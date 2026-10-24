using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Plus
{
    /// <summary>Preview tiles for the marker moments of one item: one sprite-sheet set plus its manifest.</summary>
    public sealed class MomentManifest
    {
        /// <summary>Gets or sets "ready", "pending" or "none" (no tiles: off, no markers, stock trickplay or failed).</summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = "none";

        /// <summary>Gets or sets the cache key of this set; the client puts it in sheet URLs so a new set gets new URLs.</summary>
        [JsonPropertyName("version")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Version { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("tileWidth")]
        public int TileWidth { get; set; }

        [JsonPropertyName("tileHeight")]
        public int TileHeight { get; set; }

        /// <summary>Gets or sets the time in seconds of each tile, in tile order (sheet = i / (cols*rows)).</summary>
        [JsonPropertyName("times")]
        public List<double> Times { get; set; } = new List<double>();
    }

    /// <summary>
    /// Generates preview frames for the marker moments of an item that has no stock trickplay: the
    /// frame at each marker plus neighbours, tiled into sprite sheets. Contract:
    /// docs/architecture/TIMELINE_MARKERS.md ("Moment previews").
    ///
    /// The times always come from the server's own marker list (MomentTimes), never from a request.
    /// Frames are decoded with the ffmpeg Jellyfin is configured with, input-side seek, one frame per
    /// process, ThumbWidth x ThumbHeight JPEG at -q:v Quality (~1.3 KB a frame in a sheet, measured on
    /// CT114). KEPT FOREVER: output lives under Jellyfin's DATA path (jellyfin-plus-moments/) - not the
    /// cache path, whose built-in "Clean Cache Directory" task deletes files by age - keyed by format
    /// version + item + file mtime/size + times, written to a temp dir and moved into place whole. Only
    /// orphans are deleted: an item's other sets when a newer set replaces them, strips of a replaced
    /// file, and (nightly, MomentPrecompute) every set of an item that no longer exists, has stock
    /// trickplay or has no markers any more. No size cap, no LRU. Nothing is ever written beside the media.
    ///
    /// TWO LANES, one generation per key. Live (a viewer's GetMoments miss): up to two ffmpeg at a time
    /// server-wide, below normal priority. Background (MomentPrecompute): one ffmpeg at a time, idle
    /// priority, frame by frame, and before every frame it waits while any live generation runs or
    /// any session is transcoding. A viewer asking for a key the background is already making
    /// promotes that generation to the live lane for its remaining frames.
    /// </summary>
    public sealed class MomentSheets
    {
        /// <summary>Frame size: the popup draws 128 CSS px thumbs. Changing size or quality: bump FormatVersion.</summary>
        public const int ThumbWidth = 160;
        public const int ThumbHeight = 90;

        /// <summary>Part of every key: a new format turns old sets into orphans, re-made on the next request or run.</summary>
        public const string FormatVersion = "2";

        /// <summary>
        /// JPEG -q:v of every stored image (2 best .. 31 worst). 10, measured on CT114 at 160x90: ~1.5 KB a
        /// lone frame, ~1.26 KB a frame in a sheet; WebP q50 was only ~19% smaller in a sheet, not worth a
        /// second format.
        /// </summary>
        private const string Q = "10";

        public const int Cols = 10;
        public const int Rows = 10;

        /// <summary>Neighbour spacing in seconds: marker -10, -5, 0, +5, +10. Same constant as the client.</summary>
        public const double Spacing = 5;
        public const int Neighbours = 2;

        /// <summary>Frames per item at most (40 markers x 5); ~375 ms each measured on CT114.</summary>
        public const int MaxFrames = 200;

        /// <summary>Times closer than this collapse into one frame.</summary>
        public const double MinGap = 1.0;

        private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FailTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan YieldPoll = TimeSpan.FromSeconds(5);
        private const string ManifestName = "manifest.json";

        private static readonly SemaphoreSlim FfmpegGate = new SemaphoreSlim(2, 2);
        private static readonly SemaphoreSlim BackgroundGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim StripGate = new SemaphoreSlim(2, 2);
        private static readonly TimeSpan StripTimeout = TimeSpan.FromSeconds(10);
        private const string StripDir = "strips";
        private readonly ConcurrentDictionary<string, Lazy<Task>> _strips = new ConcurrentDictionary<string, Lazy<Task>>(StringComparer.Ordinal);

        private readonly IMediaEncoder _encoder;
        private readonly IApplicationPaths _paths;
        private readonly ISessionManager _sessions;
        private readonly ILogger<MomentSheets> _logger;
        private readonly ConcurrentDictionary<string, Lazy<Generation>> _inFlight = new ConcurrentDictionary<string, Lazy<Generation>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _failed = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private int _liveGenerations;
        private long _transcodeCheckedTicks;
        private bool _transcodeActive;

        public MomentSheets(IMediaEncoder encoder, IApplicationPaths paths, ISessionManager sessions, ILogger<MomentSheets> logger)
        {
            _encoder = encoder;
            _paths = paths;
            _sessions = sessions;
            _logger = logger;
        }

        /// <summary>The store root (one directory per item id, one set directory per key inside, plus strips/).</summary>
        public string Root => Path.Combine(_paths.DataPath, "jellyfin-plus-moments");

        /// <summary>Where the first version kept its 320 px sets (cache path); removed by the nightly run.</summary>
        public string LegacyRoot => Path.Combine(_paths.CachePath, "jellyfin-plus-moments");

        /// <summary>One generation of one key; Live flips once (never back) when a viewer joins it.</summary>
        private sealed class Generation
        {
            public Task Task;
            public volatile bool Live;
            public bool Done;
        }

        /// <summary>Moves a generation to the live lane (counted while it runs, so the background yields).</summary>
        private void MakeLive(Generation g)
        {
            lock (g)
            {
                if (g.Done || g.Live)
                {
                    return;
                }

                g.Live = true;
                Interlocked.Increment(ref _liveGenerations);
            }
        }

        private void Finish(Generation g)
        {
            lock (g)
            {
                g.Done = true;
                if (g.Live)
                {
                    Interlocked.Decrement(ref _liveGenerations);
                }
            }
        }

        /// <summary>Starts (or joins) the one generation for this key.</summary>
        private Generation Start(string key, string mediaPath, string itemDir, string dir, IReadOnlyList<double> times, bool live)
        {
            var list = times.ToList();
            // Lazy: GetOrAdd may run its factory twice under a race; only the stored Lazy ever starts.
            var g = _inFlight.GetOrAdd(key, k => new Lazy<Generation>(() =>
            {
                var gen = new Generation();
                if (live)
                {
                    MakeLive(gen); // before the first frame picks its lane
                }

                gen.Task = Task.Run(() => GenerateAsync(gen, k, mediaPath, itemDir, dir, list));
                return gen;
            })).Value;
            if (live)
            {
                MakeLive(g); // joined a background generation: promote it (idempotent)
            }

            return g;
        }

        /// <summary>Whether a session is transcoding; cached for a few seconds (in memory, cheap).</summary>
        private bool TranscodeActive()
        {
            var now = DateTime.UtcNow.Ticks;
            if (now - Interlocked.Read(ref _transcodeCheckedTicks) < YieldPoll.Ticks)
            {
                return _transcodeActive;
            }

            try
            {
                _transcodeActive = _sessions.Sessions.Any(s => s.TranscodingInfo != null);
            }
            catch (Exception)
            {
                _transcodeActive = false;
            }

            Interlocked.Exchange(ref _transcodeCheckedTicks, now);
            return _transcodeActive;
        }

        /// <summary>
        /// The moment times for markers (offsets in seconds) of an item of this duration: each marker and
        /// its neighbours at Spacing, clamped to [0, duration), sorted, near-duplicates (MinGap) collapsed.
        /// When the total would pass MaxFrames, markers with the highest value are kept first. Pure.
        /// </summary>
        public static List<double> MomentTimes(IEnumerable<TimelineMarker> markers, double durationSeconds)
        {
            var result = new List<double>();
            if (markers == null || !(durationSeconds > 0) || double.IsInfinity(durationSeconds))
            {
                return result;
            }

            var last = Math.Max(0, durationSeconds - 1);
            var ordered = markers
                .Where(m => m != null && double.IsFinite(m.OffsetSeconds) && m.OffsetSeconds >= 0 && m.OffsetSeconds <= durationSeconds)
                .OrderByDescending(m => m.Value ?? double.MinValue)
                .ThenBy(m => m.OffsetSeconds);
            var chosen = new List<double>();
            foreach (var m in ordered)
            {
                var add = new List<double>();
                for (var k = -Neighbours; k <= Neighbours; k++)
                {
                    var t = Math.Round(Math.Clamp(m.OffsetSeconds + (k * Spacing), 0, last), 1);
                    if (!chosen.Any(c => Math.Abs(c - t) < MinGap) && !add.Any(c => Math.Abs(c - t) < MinGap))
                    {
                        add.Add(t);
                    }
                }

                if (chosen.Count + add.Count > MaxFrames)
                {
                    break;
                }

                chosen.AddRange(add);
            }

            chosen.Sort();
            return chosen;
        }

        /// <summary>
        /// The marker a strip request names: the one at <paramref name="index"/> of the server's merged
        /// list; when <paramref name="offset"/> is given it must agree within half a second, else the
        /// list's marker nearest that offset (within half a second) is used. Null when none fits. Pure.
        /// </summary>
        public static TimelineMarker PickMarker(IReadOnlyList<TimelineMarker> markers, int index, double? offset)
        {
            if (markers == null || markers.Count == 0)
            {
                return null;
            }

            var at = index >= 0 && index < markers.Count ? markers[index] : null;
            if (offset is not double t || !double.IsFinite(t))
            {
                return at;
            }

            if (at != null && Math.Abs(at.OffsetSeconds - t) <= 0.5)
            {
                return at;
            }

            var near = markers.Where(m => m != null && Math.Abs(m.OffsetSeconds - t) <= 0.5)
                .OrderBy(m => Math.Abs(m.OffsetSeconds - t))
                .FirstOrDefault();
            return near;
        }

        /// <summary>The cache key: item, the media file's identity, and the exact times. Pure.</summary>
        public static string Key(Guid itemId, string path, long length, DateTime mtimeUtc, IReadOnlyList<double> times)
        {
            var text = FormatVersion + "|" + itemId.ToString("N", CultureInfo.InvariantCulture) + "|" + path + "|" + length.ToString(CultureInfo.InvariantCulture)
                + "|" + mtimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|"
                + string.Join(",", times.Select(t => t.ToString("R", CultureInfo.InvariantCulture)));
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>
        /// The manifest for these times: "ready" when on disk, otherwise starts (or joins) the one
        /// generation for the item and waits up to <paramref name="wait"/>, then answers "pending".
        /// Failures are remembered for FailTtl and answer "none".
        /// </summary>
        public async Task<MomentManifest> GetAsync(Guid itemId, string mediaPath, IReadOnlyList<double> times, TimeSpan wait, CancellationToken requestToken)
        {
            if (times.Count == 0 || string.IsNullOrEmpty(mediaPath))
            {
                return new MomentManifest();
            }

            FileInfo media;
            try
            {
                media = new FileInfo(mediaPath);
                if (!media.Exists)
                {
                    return new MomentManifest();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return new MomentManifest();
            }

            var key = Key(itemId, mediaPath, media.Length, media.LastWriteTimeUtc, times);
            var itemDir = Path.Combine(Root, itemId.ToString("N", CultureInfo.InvariantCulture));
            var dir = Path.Combine(itemDir, key);
            var ready = ReadManifest(dir);
            if (ready != null)
            {
                return ready;
            }

            if (RecentlyFailed(key))
            {
                return new MomentManifest();
            }

            var g = Start(key, mediaPath, itemDir, dir, times, live: true);
            try
            {
                await g.Task.WaitAsync(wait, requestToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new MomentManifest { Status = "pending", Version = key };
            }

            return ReadManifest(dir) ?? new MomentManifest();
        }

        /// <summary>
        /// Background lane: the ready manifest for these times, generating it first (idle priority,
        /// yielding to viewers) when missing; "none" when the file is gone or generation failed. Does
        /// Cancelling stops the wait, not a started generation.
        /// </summary>
        public async Task<MomentManifest> EnsureAsync(Guid itemId, string mediaPath, IReadOnlyList<double> times, CancellationToken cancellationToken)
        {
            if (times.Count == 0 || string.IsNullOrEmpty(mediaPath))
            {
                return new MomentManifest();
            }

            string key;
            try
            {
                var media = new FileInfo(mediaPath);
                if (!media.Exists)
                {
                    return new MomentManifest();
                }

                key = Key(itemId, mediaPath, media.Length, media.LastWriteTimeUtc, times);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return new MomentManifest();
            }

            var itemDir = Path.Combine(Root, itemId.ToString("N", CultureInfo.InvariantCulture));
            var dir = Path.Combine(itemDir, key);
            var ready = ReadManifest(dir);
            if (ready != null || RecentlyFailed(key))
            {
                return ready ?? new MomentManifest();
            }

            var g = Start(key, mediaPath, itemDir, dir, times, live: false);
            await g.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return ReadManifest(dir) ?? new MomentManifest();
        }

        /// <summary>The full set for these times when it is on disk, else null.</summary>
        public MomentManifest ReadySet(Guid itemId, string mediaPath, IReadOnlyList<double> times)
        {
            if (times.Count == 0 || string.IsNullOrEmpty(mediaPath))
            {
                return null;
            }

            try
            {
                var media = new FileInfo(mediaPath);
                if (!media.Exists)
                {
                    return null;
                }

                var key = Key(itemId, mediaPath, media.Length, media.LastWriteTimeUtc, times);
                return ReadManifest(Path.Combine(Root, itemId.ToString("N", CultureInfo.InvariantCulture), key));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// The one-marker strip: the frames at <paramref name="times"/> (one marker's StripTimes, at most
        /// 2*Neighbours+1) side by side in one JPEG of ThumbWidth*n x ThumbHeight, made by ONE ffmpeg
        /// process with an input-side seek per frame. Live lane ahead of everything (its own gate; the
        /// background yields while it runs), de-duplicated per key, StripTimeout, failures remembered for
        /// FailTtl. Kept under the item's directory (strips/) until the item's full set supersedes it, the
        /// file is replaced, or the item goes.
        /// Null when it could not be made in time.
        /// </summary>
        public async Task<string> StripAsync(Guid itemId, string mediaPath, IReadOnlyList<double> times, CancellationToken requestToken)
        {
            if (times.Count == 0 || times.Count > (2 * Neighbours) + 1 || string.IsNullOrEmpty(mediaPath))
            {
                return null;
            }

            string key;
            try
            {
                var media = new FileInfo(mediaPath);
                if (!media.Exists)
                {
                    return null;
                }

                // "<file identity>-<times>": a strip of a replaced file is recognisable as an orphan.
                key = Key(itemId, mediaPath, media.Length, media.LastWriteTimeUtc, Array.Empty<double>())
                    + "-" + Key(itemId, mediaPath, media.Length, media.LastWriteTimeUtc, times);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return null;
            }

            var file = Path.Combine(Root, itemId.ToString("N", CultureInfo.InvariantCulture), StripDir, key + ".jpg");
            if (File.Exists(file))
            {
                return file;
            }

            if (RecentlyFailed(key))
            {
                return null;
            }

            var list = times.ToList();
            var task = _strips.GetOrAdd(key, k => new Lazy<Task>(() => Task.Run(() => MakeStripAsync(k, mediaPath, file, list)))).Value;
            try
            {
                await task.WaitAsync(StripTimeout, requestToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }

            return File.Exists(file) ? file : null;
        }

        private async Task MakeStripAsync(string key, string mediaPath, string file, List<double> times)
        {
            var tmp = Path.Combine(Root, ".tmp-" + key + "-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".jpg");
            Interlocked.Increment(ref _liveGenerations); // the background yields while a viewer waits
            var sw = Stopwatch.StartNew();
            try
            {
                var ffmpeg = _encoder.EncoderPath;
                if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg))
                {
                    throw new InvalidOperationException("no ffmpeg configured");
                }

                Directory.CreateDirectory(Root);
                await StripGate.WaitAsync().ConfigureAwait(false);
                if (!await RunProcessAsync(ffmpeg, StripArgs(mediaPath, times, tmp), ProcessPriorityClass.BelowNormal, StripGate, StripTimeout).ConfigureAwait(false)
                    || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
                {
                    throw new InvalidOperationException("strip failed");
                }

                var stripDir = Path.GetDirectoryName(file);
                Directory.CreateDirectory(stripDir);
                File.Move(tmp, file, true);

                // Orphans: strips of an earlier version of the file (or an earlier format).
                var fileId = key.Substring(0, key.IndexOf('-', StringComparison.Ordinal) + 1);
                foreach (var old in Directory.GetFiles(stripDir, "*.jpg"))
                {
                    if (!Path.GetFileName(old).StartsWith(fileId, StringComparison.Ordinal))
                    {
                        File.Delete(old);
                    }
                }

                _logger.LogDebug("Moments: strip {Key} ({Frames} frames) in {Ms} ms", key, times.Count, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _failed[key] = DateTime.UtcNow + FailTtl;
                _logger.LogWarning("Moments: strip failed for {Key}: {Message}", key, ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _liveGenerations);
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }

                _strips.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// One process, one input per frame (each "-threads 2 -ss T -i file"), scaled and padded to the
        /// thumb size, hstacked. Measured on CT114 (8-bit 1080p mkv): ~330-350 ms for five frames, against
        /// ~440 ms with the default threads and ~450 ms with one. Pure.
        /// </summary>
        public static List<string> StripArgs(string mediaPath, IReadOnlyList<double> times, string output)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
            var graph = new StringBuilder();
            var labels = new StringBuilder();
            for (var i = 0; i < times.Count; i++)
            {
                args.AddRange(new[] { "-threads", "2", "-ss", times[i].ToString("0.###", CultureInfo.InvariantCulture), "-i", mediaPath });
                var n = i.ToString(CultureInfo.InvariantCulture);
                graph.Append('[').Append(n).Append(":v:0]scale=").Append(ThumbWidth).Append(':').Append(ThumbHeight)
                    .Append(":force_original_aspect_ratio=decrease,pad=").Append(ThumbWidth).Append(':').Append(ThumbHeight)
                    .Append(":-1:-1,setsar=1[v").Append(n).Append("];");
                labels.Append("[v").Append(n).Append(']');
            }

            graph.Append(labels);
            graph.Append(times.Count > 1 ? "hstack=inputs=" + times.Count.ToString(CultureInfo.InvariantCulture) : "null").Append("[out]");
            args.AddRange(new[] { "-filter_complex", graph.ToString(), "-map", "[out]", "-frames:v", "1", "-q:v", Q, output });
            return args;
        }

        /// <summary>Removes every set of an item (it is gone, has stock trickplay, or has no markers any more).</summary>
        public void Forget(Guid itemId)
        {
            TryDelete(Path.Combine(Root, itemId.ToString("N", CultureInfo.InvariantCulture)));
        }

        /// <summary>The item ids that have a set directory on disk.</summary>
        public List<Guid> CachedItemIds()
        {
            var ids = new List<Guid>();
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (var d in Directory.GetDirectories(Root))
                    {
                        if (Guid.TryParseExact(Path.GetFileName(d), "N", out var id))
                        {
                            ids.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Moments: listing the cache failed");
            }

            return ids;
        }

        private bool RecentlyFailed(string key)
        {
            if (_failed.TryGetValue(key, out var until))
            {
                if (DateTime.UtcNow < until)
                {
                    return true;
                }

                _failed.TryRemove(key, out _);
            }

            return false;
        }

        /// <summary>Path of sheet <paramref name="index"/> of the ready set for these times, or null.</summary>
        public string SheetPath(Guid itemId, string mediaPath, IReadOnlyList<double> times, int index)
        {
            if (times.Count == 0 || index < 0 || index * Cols * Rows >= times.Count)
            {
                return null;
            }

            try
            {
                var media = new FileInfo(mediaPath);
                if (!media.Exists)
                {
                    return null;
                }

                var key = Key(itemId, mediaPath, media.Length, media.LastWriteTimeUtc, times);
                var file = Path.Combine(Root, itemId.ToString("N", CultureInfo.InvariantCulture), key, index.ToString(CultureInfo.InvariantCulture) + ".jpg");
                return File.Exists(file) ? file : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return null;
            }
        }

        private static MomentManifest ReadManifest(string dir)
        {
            var file = Path.Combine(dir, ManifestName);
            try
            {
                if (!File.Exists(file))
                {
                    return null;
                }

                var m = JsonSerializer.Deserialize<MomentManifest>(File.ReadAllText(file));
                return m != null && m.Status == "ready" ? m : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return null;
            }
        }

        private async Task GenerateAsync(Generation g, string key, string mediaPath, string itemDir, string dir, List<double> times)
        {
            var tmp = Path.Combine(Root, ".tmp-" + key + "-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            var sw = Stopwatch.StartNew();
            try
            {
                var ffmpeg = _encoder.EncoderPath;
                if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg))
                {
                    throw new InvalidOperationException("no ffmpeg configured");
                }

                Directory.CreateDirectory(tmp);
                var ok = new bool[times.Count];
                string FramePath(int i) => Path.Combine(tmp, "f" + i.ToString("D4", CultureInfo.InvariantCulture) + ".jpg");

                // Background: frame by frame, so a promotion takes effect at once; live: all at once
                // under the two-slot gate.
                var next = 0;
                while (next < times.Count && !g.Live)
                {
                    ok[next] = await ExtractAsync(ffmpeg, mediaPath, times[next], FramePath(next), g).ConfigureAwait(false);
                    next++;
                }

                await Task.WhenAll(Enumerable.Range(next, times.Count - next).Select(async i =>
                {
                    ok[i] = await ExtractAsync(ffmpeg, mediaPath, times[i], FramePath(i), g).ConfigureAwait(false);
                })).ConfigureAwait(false);

                // Renumber the successes densely so the tiler reads a gapless sequence; a failed frame
                // simply has no tile (and no time in the manifest).
                var kept = new List<double>();
                for (var i = 0; i < times.Count; i++)
                {
                    if (ok[i])
                    {
                        File.Move(Path.Combine(tmp, "f" + i.ToString("D4", CultureInfo.InvariantCulture) + ".jpg"),
                            Path.Combine(tmp, "s" + kept.Count.ToString("D4", CultureInfo.InvariantCulture) + ".jpg"));
                        kept.Add(times[i]);
                    }
                }

                if (kept.Count == 0)
                {
                    throw new InvalidOperationException("no frame could be decoded");
                }

                var perSheet = Cols * Rows;
                for (var s = 0; s * perSheet < kept.Count; s++)
                {
                    var args = new List<string>
                    {
                        "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                        "-framerate", "1", "-start_number", (s * perSheet).ToString(CultureInfo.InvariantCulture),
                        "-i", Path.Combine(tmp, "s%04d.jpg"),
                        "-vf", "tile=" + Cols.ToString(CultureInfo.InvariantCulture) + "x" + Rows.ToString(CultureInfo.InvariantCulture),
                        "-frames:v", "1", "-q:v", Q,
                        Path.Combine(tmp, s.ToString(CultureInfo.InvariantCulture) + ".jpg"),
                    };
                    if (!await RunAsync(ffmpeg, args, g).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("tiling failed");
                    }
                }

                foreach (var f in Directory.GetFiles(tmp, "s*.jpg"))
                {
                    File.Delete(f);
                }

                var manifest = new MomentManifest
                {
                    Status = "ready",
                    Version = key,
                    Width = ThumbWidth,
                    Height = ThumbHeight,
                    TileWidth = Cols,
                    TileHeight = Rows,
                    Times = kept,
                };
                File.WriteAllText(Path.Combine(tmp, ManifestName), JsonSerializer.Serialize(manifest));

                // Orphans: older sets for this item (other markers, a replaced file, an older format) and
                // its strips (the full set supersedes them) go; then the new set appears whole.
                if (Directory.Exists(itemDir))
                {
                    foreach (var old in Directory.GetDirectories(itemDir))
                    {
                        TryDelete(old);
                    }
                }

                Directory.CreateDirectory(itemDir);
                Directory.Move(tmp, dir);
                _logger.LogInformation("Moments: {Frames}/{Asked} frames for {Key} in {Ms} ms ({Lane})", kept.Count, times.Count, key, sw.ElapsedMilliseconds, g.Live ? "live" : "background");
            }
            catch (Exception ex)
            {
                _failed[key] = DateTime.UtcNow + FailTtl;
                _logger.LogWarning("Moments: generation failed for {Key}: {Message}", key, ex.Message);
            }
            finally
            {
                TryDelete(tmp);
                Finish(g);
                _inFlight.TryRemove(key, out _);
            }
        }

        private async Task<bool> ExtractAsync(string ffmpeg, string mediaPath, double t, string output, Generation g)
        {
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1",
                "-ss", t.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", mediaPath,
                "-an", "-sn", "-dn", "-frames:v", "1",
                "-vf", "scale=" + ThumbWidth + ":" + ThumbHeight + ":force_original_aspect_ratio=decrease,pad=" + ThumbWidth + ":" + ThumbHeight + ":-1:-1",
                "-q:v", "2", // temp frame, near lossless: the sheet is the one lossy encode
                output,
            };
            return await RunAsync(ffmpeg, args, g).ConfigureAwait(false) && File.Exists(output) && new FileInfo(output).Length > 0;
        }

        /// <summary>
        /// One ffmpeg process in the generation's lane, killed at FrameTimeout. Live: the two-slot gate,
        /// below normal priority. Background: first waits (outside any gate) while a live generation
        /// runs or a session transcodes, then the one-slot gate, idle priority; a promotion while waiting
        /// switches to the live lane.
        /// </summary>
        private async Task<bool> RunAsync(string ffmpeg, List<string> args, Generation g)
        {
            SemaphoreSlim gate = null;
            while (gate == null)
            {
                if (g == null || g.Live)
                {
                    await FfmpegGate.WaitAsync().ConfigureAwait(false);
                    gate = FfmpegGate;
                }
                else if (Volatile.Read(ref _liveGenerations) > 0 || TranscodeActive())
                {
                    await Task.Delay(YieldPoll).ConfigureAwait(false);
                }
                else if (await BackgroundGate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
                {
                    gate = BackgroundGate;
                }
            }

            return await RunProcessAsync(ffmpeg, args, gate == FfmpegGate ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Idle, gate, FrameTimeout).ConfigureAwait(false);
        }

        /// <summary>Runs one ffmpeg (the caller already holds <paramref name="gate"/>; released here), killed at <paramref name="timeout"/>.</summary>
        private static async Task<bool> RunProcessAsync(string ffmpeg, List<string> args, ProcessPriorityClass priority, SemaphoreSlim gate, TimeSpan timeout)
        {
            try
            {
                var psi = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                foreach (var a in args)
                {
                    psi.ArgumentList.Add(a);
                }

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return false;
                }

                try
                {
                    proc.PriorityClass = priority;
                }
                catch (Exception)
                {
                    // Priority is best effort.
                }

                var drainErr = proc.StandardError.ReadToEndAsync();
                var drainOut = proc.StandardOutput.ReadToEndAsync();
                using var cts = new CancellationTokenSource(timeout);
                try
                {
                    await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        proc.Kill(true);
                    }
                    catch (Exception)
                    {
                    }

                    return false;
                }

                await Task.WhenAll(drainErr, drainOut).ConfigureAwait(false);
                return proc.ExitCode == 0;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Store size for the nightly log: total bytes, full sets, strips. Also deletes temp leftovers
        /// of an interrupted generation (older than an hour) and the first version's cache-path store.
        /// </summary>
        public (long Bytes, int Sets, int Strips) Housekeep()
        {
            long bytes = 0;
            int sets = 0, strips = 0;
            TryDelete(LegacyRoot);
            try
            {
                if (!Directory.Exists(Root))
                {
                    return (0, 0, 0);
                }

                foreach (var f in new DirectoryInfo(Root).EnumerateFileSystemInfos(".tmp-*"))
                {
                    if (DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromHours(1))
                    {
                        if (f is DirectoryInfo d)
                        {
                            TryDelete(d.FullName);
                        }
                        else
                        {
                            try
                            {
                                f.Delete();
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                            {
                            }
                        }
                    }
                }

                foreach (var f in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    bytes += f.Length;
                    if (f.Name == ManifestName)
                    {
                        sets++;
                    }
                    else if (f.Directory?.Name == StripDir)
                    {
                        strips++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Moments: housekeeping failed");
            }

            return (bytes, sets, strips);
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Registers MomentSheets and MarkerSource as singletons (one in-flight table, cache and upstream
    /// rate limit per server), and the precompute queue for new items.
    /// </summary>
    public class MomentSheetsServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<MomentSheets>();
            serviceCollection.AddSingleton<MarkerSource>();
            serviceCollection.AddSingleton<MomentPrecompute>();
            serviceCollection.AddHostedService<MomentPrecomputeQueue>();
        }
    }
}
