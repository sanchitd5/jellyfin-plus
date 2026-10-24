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
    /// Generates, on demand, preview frames for the marker moments of an item that has no stock
    /// trickplay: the frame at each marker plus neighbours, tiled into sprite sheets. Contract:
    /// docs/architecture/TIMELINE_MARKERS.md ("Moment previews").
    ///
    /// The times always come from the server's own marker list (MomentTimes), never from a request.
    /// Frames are decoded with the ffmpeg Jellyfin is configured with, input-side seek, one frame per
    /// process, at most two processes at a time server-wide, below normal priority. Output lives only
    /// under Jellyfin's cache path (jellyfin-plus-moments/), keyed by item + file mtime/size + times,
    /// written to a temp dir and moved into place whole, and pruned oldest-first above MaxCacheBytes.
    /// Nothing is ever written beside the media.
    /// </summary>
    public sealed class MomentSheets
    {
        public const int ThumbWidth = 320;
        public const int ThumbHeight = 180;
        public const int Cols = 10;
        public const int Rows = 10;

        /// <summary>Neighbour spacing in seconds: marker -10, -5, 0, +5, +10. Same constant as the client.</summary>
        public const double Spacing = 5;
        public const int Neighbours = 2;

        /// <summary>Frames per item at most (40 markers x 5); ~375 ms each measured on CT114.</summary>
        public const int MaxFrames = 200;

        /// <summary>Times closer than this collapse into one frame.</summary>
        public const double MinGap = 1.0;

        private const long MaxCacheBytes = 512L * 1024 * 1024;
        private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FailTtl = TimeSpan.FromMinutes(10);
        private const string ManifestName = "manifest.json";

        private static readonly SemaphoreSlim FfmpegGate = new SemaphoreSlim(2, 2);
        private static readonly object PruneLock = new object();

        private readonly IMediaEncoder _encoder;
        private readonly IApplicationPaths _paths;
        private readonly ILogger<MomentSheets> _logger;
        private readonly ConcurrentDictionary<string, Lazy<Task>> _inFlight = new ConcurrentDictionary<string, Lazy<Task>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _failed = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public MomentSheets(IMediaEncoder encoder, IApplicationPaths paths, ILogger<MomentSheets> logger)
        {
            _encoder = encoder;
            _paths = paths;
            _logger = logger;
        }

        private string Root => Path.Combine(_paths.CachePath, "jellyfin-plus-moments");

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

        /// <summary>The cache key: item, the media file's identity, and the exact times. Pure.</summary>
        public static string Key(Guid itemId, string path, long length, DateTime mtimeUtc, IReadOnlyList<double> times)
        {
            var text = itemId.ToString("N", CultureInfo.InvariantCulture) + "|" + path + "|" + length.ToString(CultureInfo.InvariantCulture)
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

            if (_failed.TryGetValue(key, out var until))
            {
                if (DateTime.UtcNow < until)
                {
                    return new MomentManifest();
                }

                _failed.TryRemove(key, out _);
            }

            // Lazy: GetOrAdd may run its factory twice under a race; only the stored Lazy ever starts.
            var task = _inFlight.GetOrAdd(key, k => new Lazy<Task>(() => Task.Run(() => GenerateAsync(k, mediaPath, itemDir, dir, times.ToList())))).Value;
            try
            {
                await task.WaitAsync(wait, requestToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new MomentManifest { Status = "pending", Version = key };
            }

            return ReadManifest(dir) ?? new MomentManifest();
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
                try
                {
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // LRU: last use, for pruning
                }
                catch (IOException)
                {
                }

                return m != null && m.Status == "ready" ? m : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return null;
            }
        }

        private async Task GenerateAsync(string key, string mediaPath, string itemDir, string dir, List<double> times)
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
                await Task.WhenAll(times.Select(async (t, i) =>
                {
                    ok[i] = await ExtractAsync(ffmpeg, mediaPath, t, Path.Combine(tmp, "f" + i.ToString("D4", CultureInfo.InvariantCulture) + ".jpg")).ConfigureAwait(false);
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
                        "-frames:v", "1", "-q:v", "4",
                        Path.Combine(tmp, s.ToString(CultureInfo.InvariantCulture) + ".jpg"),
                    };
                    if (!await RunAsync(ffmpeg, args).ConfigureAwait(false))
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

                // Older sets for this item (other markers or a replaced file) go; then the new set
                // appears whole.
                if (Directory.Exists(itemDir))
                {
                    foreach (var old in Directory.GetDirectories(itemDir))
                    {
                        TryDelete(old);
                    }
                }

                Directory.CreateDirectory(itemDir);
                Directory.Move(tmp, dir);
                _logger.LogInformation("Moments: {Frames}/{Asked} frames for {Key} in {Ms} ms", kept.Count, times.Count, key, sw.ElapsedMilliseconds);
                Prune();
            }
            catch (Exception ex)
            {
                _failed[key] = DateTime.UtcNow + FailTtl;
                _logger.LogWarning("Moments: generation failed for {Key}: {Message}", key, ex.Message);
            }
            finally
            {
                TryDelete(tmp);
                _inFlight.TryRemove(key, out _);
            }
        }

        private static async Task<bool> ExtractAsync(string ffmpeg, string mediaPath, double t, string output)
        {
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1",
                "-ss", t.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", mediaPath,
                "-an", "-sn", "-dn", "-frames:v", "1",
                "-vf", "scale=" + ThumbWidth + ":" + ThumbHeight + ":force_original_aspect_ratio=decrease,pad=" + ThumbWidth + ":" + ThumbHeight + ":-1:-1",
                "-q:v", "4",
                output,
            };
            return await RunAsync(ffmpeg, args).ConfigureAwait(false) && File.Exists(output) && new FileInfo(output).Length > 0;
        }

        /// <summary>One ffmpeg process under the global gate, below normal priority, killed at FrameTimeout.</summary>
        private static async Task<bool> RunAsync(string ffmpeg, List<string> args)
        {
            await FfmpegGate.WaitAsync().ConfigureAwait(false);
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
                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (Exception)
                {
                    // Priority is best effort.
                }

                var drainErr = proc.StandardError.ReadToEndAsync();
                var drainOut = proc.StandardOutput.ReadToEndAsync();
                using var cts = new CancellationTokenSource(FrameTimeout);
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
                FfmpegGate.Release();
            }
        }

        /// <summary>Oldest-used sets go first until the cache is under MaxCacheBytes.</summary>
        private void Prune()
        {
            lock (PruneLock)
            {
                try
                {
                    var sets = new List<(string Dir, DateTime Used, long Bytes)>();
                    foreach (var itemDir in Directory.GetDirectories(Root))
                    {
                        if (Path.GetFileName(itemDir).StartsWith(".tmp-", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        foreach (var set in Directory.GetDirectories(itemDir))
                        {
                            var manifest = new FileInfo(Path.Combine(set, ManifestName));
                            var bytes = new DirectoryInfo(set).GetFiles().Sum(f => f.Length);
                            sets.Add((set, manifest.Exists ? manifest.LastWriteTimeUtc : DateTime.MinValue, bytes));
                        }
                    }

                    var total = sets.Sum(s => s.Bytes);
                    foreach (var s in sets.OrderBy(s => s.Used))
                    {
                        if (total <= MaxCacheBytes)
                        {
                            break;
                        }

                        TryDelete(s.Dir);
                        total -= s.Bytes;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Moments: prune failed");
                }
            }
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

    /// <summary>Registers MomentSheets as a singleton (one in-flight table and cache per server).</summary>
    public class MomentSheetsServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<MomentSheets>();
        }
    }
}
