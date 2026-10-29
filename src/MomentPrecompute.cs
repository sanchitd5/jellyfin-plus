using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Plus
{
    /// <summary>What the precompute last concluded for one item.</summary>
    public sealed class MomentCheck
    {
        /// <summary>Gets or sets the file key the check was for (path + size + mtime as the library knows them).</summary>
        [JsonPropertyName("k")]
        public string FileKey { get; set; }

        [JsonPropertyName("at")]
        public DateTime CheckedUtc { get; set; }

        /// <summary>Gets or sets "ready", "none" (no markers) or "trickplay" (stock trickplay: nothing to make).</summary>
        [JsonPropertyName("r")]
        public string Result { get; set; }
    }

    /// <summary>
    /// Server-side precompute of moment sheets, so a viewer who presses play and hovers a marker at once
    /// already has frames. Contract: docs/architecture/TIMELINE_MARKERS.md ("Precompute").
    ///
    /// One per-item routine (Process), used by the nightly scheduled task and by the queue for new items:
    /// skip items no provider matches, without a duration, or with stock trickplay; skip items the index
    /// says were checked recently for the same file (NeedsCheck); otherwise fetch the merged markers with
    /// no user (MarkerSource, background: rate-limited, never filling the viewers' cache) and generate
    /// through MomentSheets.EnsureAsync - the same gate, in-flight map and keys as a viewer's request,
    /// in the background lane (one idle-priority ffmpeg, yielding to viewers and transcodes).
    ///
    /// The index (precompute-index.json beside the sets, in Jellyfin's cache path) remembers "checked
    /// at T for this file key", so a nightly run does not re-ask the provider for every unchanged file.
    /// Off (MarkerMomentsPrecompute = false, or MarkerMoments off, or no provider): does nothing.
    /// </summary>
    public sealed class MomentPrecompute
    {
        /// <summary>A checked item is asked again after this many days (markers may be added upstream later).</summary>
        public const int RecheckDays = 14;

        /// <summary>Items added this recently are asked again on every nightly run (markers arrive late).</summary>
        public const int RecentDays = 2;

        private const string IndexName = "precompute-index.json";
        private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(2);

        private readonly MomentSheets _sheets;
        private readonly MarkerSource _source;
        private readonly ILibraryManager _library;
        private readonly ILogger<MomentPrecompute> _logger;
        private readonly object _indexLock = new object();
        private ConcurrentDictionary<Guid, MomentCheck> _index;
        private int _dirty;
        private DateTime _savedAt = DateTime.UtcNow;

        public MomentPrecompute(MomentSheets sheets, MarkerSource source, ILibraryManager library, ILogger<MomentPrecompute> logger)
        {
            _sheets = sheets;
            _source = source;
            _library = library;
            _logger = logger;
        }

        /// <summary>Outcome of one item, for counting.</summary>
        public enum Outcome
        {
            Skipped,
            Unchanged,
            Ready,
            NoMarkers,
            Trickplay,
            Failed,
        }

        /// <summary>Gets a value indicating whether precompute is on (and has something to work with).</summary>
        public bool Enabled => (Plugin.Instance?.Configuration?.MarkerMomentsPrecompute ?? true) && MarkerSource.MomentsOn && _source.Providers().Count > 0;

        /// <summary>The file key for an item, from library metadata only (no file system access). Pure.</summary>
        public static string FileKey(string path, long? size, DateTime modifiedUtc)
        {
            var text = (path ?? string.Empty) + "|" + (size ?? -1).ToString(CultureInfo.InvariantCulture) + "|" + modifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
        }

        /// <summary>
        /// Whether an item should be checked now. Never checked, or its file changed: yes. Added within
        /// RecentDays: when the last check is at least <paramref name="recentEvery"/> old (zero for the
        /// nightly run: every run). Otherwise when the last check is RecheckDays old. Pure.
        /// </summary>
        public static bool NeedsCheck(MomentCheck last, string fileKey, DateTime nowUtc, DateTime createdUtc, TimeSpan recentEvery)
        {
            if (last == null || !string.Equals(last.FileKey, fileKey, StringComparison.Ordinal))
            {
                return true;
            }

            var age = nowUtc - last.CheckedUtc;
            if (nowUtc - createdUtc < TimeSpan.FromDays(RecentDays))
            {
                return age >= recentEvery;
            }

            return age >= TimeSpan.FromDays(RecheckDays);
        }

        /// <summary>Cheap, in-memory pre-check for library events: false when the event cannot lead to work.</summary>
        public bool WorthQueueing(BaseItem item, TimeSpan recentEvery)
        {
            if (item is not Video || item.IsVirtualItem || string.IsNullOrEmpty(item.Path) || !Enabled || !_source.AnyProviderMatches(item.Path))
            {
                return false;
            }

            Index().TryGetValue(item.Id, out var last);
            return NeedsCheck(last, FileKey(item.Path, item.Size, item.DateModified), DateTime.UtcNow, item.DateCreated, recentEvery);
        }

        /// <summary>The per-item routine. Never throws except on cancellation.</summary>
        public async Task<Outcome> ProcessAsync(Video item, TimeSpan recentEvery, CancellationToken ct)
        {
            if (item == null || item.IsVirtualItem || string.IsNullOrEmpty(item.Path) || item.RunTimeTicks is not long ticks || ticks <= 0
                || !_source.AnyProviderMatches(item.Path))
            {
                return Outcome.Skipped;
            }

            var fileKey = FileKey(item.Path, item.Size, item.DateModified);
            var index = Index();
            index.TryGetValue(item.Id, out var last);
            if (!NeedsCheck(last, fileKey, DateTime.UtcNow, item.DateCreated, recentEvery))
            {
                return Outcome.Unchanged;
            }

            try
            {
                var stock = await _source.HasStockTrickplayAsync(item.Id).ConfigureAwait(false);
                if (stock == null)
                {
                    return Outcome.Failed;
                }

                if (stock == true)
                {
                    _sheets.Forget(item.Id);
                    Record(item.Id, fileKey, "trickplay");
                    return Outcome.Trickplay;
                }

                var merged = await _source.MergedAsync(item, true, ct).ConfigureAwait(false);
                if (merged.AnyFailed && merged.Markers.Count == 0)
                {
                    return Outcome.Failed; // upstream down: not "no markers"; try again next time
                }

                var times = MomentSheets.MomentTimes(merged.Markers, ticks / (double)TimeSpan.TicksPerSecond);
                if (times.Count == 0)
                {
                    _sheets.Forget(item.Id);
                    Record(item.Id, fileKey, "none");
                    return Outcome.NoMarkers;
                }

                var m = await _sheets.EnsureAsync(item.Id, item.Path, times, ct).ConfigureAwait(false);
                if (m.Status != "ready")
                {
                    return Outcome.Failed;
                }

                Record(item.Id, fileKey, "ready");
                return Outcome.Ready;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Moments precompute: item {Id} failed", item.Id);
                return Outcome.Failed;
            }
        }

        /// <summary>Drops the sets and index entries of items that no longer exist.</summary>
        public int PruneRemoved(Func<Guid, bool> exists)
        {
            var removed = 0;
            foreach (var id in _sheets.CachedItemIds())
            {
                if (!exists(id))
                {
                    _sheets.Forget(id);
                    removed++;
                }
            }

            var index = Index();
            foreach (var id in index.Keys.ToList())
            {
                if (!exists(id) && index.TryRemove(id, out _))
                {
                    Interlocked.Increment(ref _dirty);
                }
            }

            return removed;
        }

        /// <summary>An item was removed from the library: forget it now.</summary>
        public void Forget(Guid id)
        {
            _sheets.Forget(id);
            if (Index().TryRemove(id, out _))
            {
                Interlocked.Increment(ref _dirty);
            }
        }

        private void Record(Guid id, string fileKey, string result)
        {
            Index()[id] = new MomentCheck { FileKey = fileKey, CheckedUtc = DateTime.UtcNow, Result = result };
            Interlocked.Increment(ref _dirty);
            if (DateTime.UtcNow - _savedAt > SaveEvery)
            {
                Save();
            }
        }

        private ConcurrentDictionary<Guid, MomentCheck> Index()
        {
            if (_index != null)
            {
                return _index;
            }

            lock (_indexLock)
            {
                if (_index == null)
                {
                    var loaded = new ConcurrentDictionary<Guid, MomentCheck>();
                    try
                    {
                        var file = Path.Combine(_sheets.Root, IndexName);
                        if (File.Exists(file))
                        {
                            var d = JsonSerializer.Deserialize<Dictionary<Guid, MomentCheck>>(File.ReadAllText(file));
                            foreach (var kv in d ?? new Dictionary<Guid, MomentCheck>())
                            {
                                if (kv.Value != null)
                                {
                                    loaded[kv.Key] = kv.Value;
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                    {
                        _logger.LogWarning("Moments precompute: index unreadable, starting afresh: {Message}", ex.Message);
                    }

                    _index = loaded;
                }

                return _index;
            }
        }

        /// <summary>Writes the index when it changed (temp file, then replace).</summary>
        public void Save()
        {
            lock (_indexLock)
            {
                if (_index == null || Interlocked.Exchange(ref _dirty, 0) == 0)
                {
                    return;
                }

                _savedAt = DateTime.UtcNow;
                var file = Path.Combine(_sheets.Root, IndexName);
                var tmp = file + ".tmp";
                try
                {
                    Directory.CreateDirectory(_sheets.Root);
                    File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<Guid, MomentCheck>(_index)));
                    File.Move(tmp, file, true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref _dirty);
                    _logger.LogWarning("Moments precompute: index not saved: {Message}", ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// New items: ItemAdded / ItemUpdated put a matching Video on ONE bounded, de-duplicated queue that a
    /// single worker drains, each item no earlier than QueueDelay after it arrived (so the file is complete
    /// and the provider has had time). A scan adding thousands of items costs one dictionary entry each, at
    /// most QueueCap; overflow is dropped and the nightly task picks it up.
    /// </summary>
    public sealed class MomentPrecomputeQueue : IHostedService
    {
        public const int QueueCap = 2000;
        public static readonly TimeSpan QueueDelay = TimeSpan.FromMinutes(2);

        /// <summary>A recent item re-announced by ItemUpdated is re-checked at most this often.</summary>
        public static readonly TimeSpan EventRecheck = TimeSpan.FromHours(6);

        private readonly ILibraryManager _library;
        private readonly MomentPrecompute _precompute;
        private readonly ILogger<MomentPrecomputeQueue> _logger;
        private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(QueueCap) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        private readonly ConcurrentDictionary<Guid, DateTime> _queued = new ConcurrentDictionary<Guid, DateTime>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _worker;

        public MomentPrecomputeQueue(ILibraryManager library, MomentPrecompute precompute, ILogger<MomentPrecomputeQueue> logger)
        {
            _library = library;
            _precompute = precompute;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _library.ItemAdded += OnItem;
            _library.ItemUpdated += OnItem;
            _library.ItemRemoved += OnRemoved;
            _worker = Task.Run(() => DrainAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _library.ItemAdded -= OnItem;
            _library.ItemUpdated -= OnItem;
            _library.ItemRemoved -= OnRemoved;
            _cts.Cancel();
            if (_worker != null)
            {
                try
                {
                    await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            _precompute.Save();
        }

        private void OnRemoved(object sender, ItemChangeEventArgs e)
        {
            if (e?.Item is Video v)
            {
                try
                {
                    _precompute.Forget(v.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Moments precompute: forget failed");
                }
            }
        }

        private void OnItem(object sender, ItemChangeEventArgs e)
        {
            // A full scan fires this for every item: in-memory checks only.
            try
            {
                if (e?.Item is Video v && !_queued.ContainsKey(v.Id) && _queued.Count < QueueCap
                    && _precompute.WorthQueueing(v, EventRecheck) && _queued.TryAdd(v.Id, DateTime.UtcNow + QueueDelay))
                {
                    if (!_queue.Writer.TryWrite(v.Id))
                    {
                        _queued.TryRemove(v.Id, out _); // full: the nightly task covers it
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Moments precompute: event ignored");
            }
        }

        private async Task DrainAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var id in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (_queued.TryGetValue(id, out var due) && due > DateTime.UtcNow)
                    {
                        await Task.Delay(due - DateTime.UtcNow, ct).ConfigureAwait(false);
                    }

                    _queued.TryRemove(id, out _);
                    if (!_precompute.Enabled)
                    {
                        continue;
                    }

                    try
                    {
                        if (_library.GetItemById(id) is Video v)
                        {
                            var r = await _precompute.ProcessAsync(v, EventRecheck, ct).ConfigureAwait(false);
                            _logger.LogDebug("Moments precompute: new item {Id}: {Outcome}", id, r);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Moments precompute: new item {Id} failed", id);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Dashboard > Scheduled Tasks > Jellyfin Plus: precomputes moment sheets for every matching video.</summary>
    public sealed class MomentPrecomputeTask : IScheduledTask
    {
        private readonly ILibraryManager _library;
        private readonly MomentPrecompute _precompute;
        private readonly MomentSheets _sheets;
        private readonly ILogger<MomentPrecomputeTask> _logger;
        private readonly ISessionManager _sessions;

        /// <summary>Whether anyone is transcoding or playing; true when it cannot be told (stay at one).</summary>
        private bool ViewerActive()
        {
            try
            {
                return _sessions.Sessions.Any(s => s.TranscodingInfo != null || s.NowPlayingItem != null);
            }
            catch (Exception)
            {
                return true;
            }
        }

        public MomentPrecomputeTask(ILibraryManager library, MomentPrecompute precompute, MomentSheets sheets, ISessionManager sessions, ILogger<MomentPrecomputeTask> logger)
        {
            _sessions = sessions;
            _library = library;
            _precompute = precompute;
            _sheets = sheets;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Precompute marker previews";

        /// <inheritdoc />
        public string Key => "JellyfinPlusMomentPrecompute";

        /// <inheritdoc />
        public string Description => "Makes the timeline-marker preview frames for every video a marker provider covers and that has no Jellyfin trickplay, so they are ready before playback. One low-priority ffmpeg at a time, paused while anyone is transcoding. Unchanged videos checked in the last "
            + MomentPrecompute.RecheckDays + " days are skipped. Setting: Moment previews > Precompute.";

        /// <inheritdoc />
        public string Category => "Jellyfin Plus";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!_precompute.Enabled)
            {
                _logger.LogInformation("Moments precompute: off (setting, moment previews or no provider); nothing done");
                progress.Report(100);
                return;
            }

            var ids = _library.GetItemIds(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Video, BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo },
                Recursive = true,
                IsVirtualItem = false,
            });

            var counts = new Dictionary<MomentPrecompute.Outcome, int>();
            var total = ids.Count;
            try
            {
                // Worker 1 always runs. Worker 2 takes an item only while nobody is transcoding or playing
                // (checked before every item) and otherwise waits, so it is back to one the moment a viewer
                // appears. The ffmpeg lane is unchanged (MomentSheets.BackgroundGate admits one at a time):
                // the second worker overlaps the provider and library work of the items that need no ffmpeg.
                var next = -1;
                async Task WorkerAsync(bool second)
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (second)
                        {
                            while (ViewerActive())
                            {
                                if (Volatile.Read(ref next) + 1 >= total)
                                {
                                    return;
                                }

                                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                            }
                        }

                        var i = Interlocked.Increment(ref next);
                        if (i >= total)
                        {
                            return;
                        }

                        var outcome = _library.GetItemById(ids[i]) is Video v
                            ? await _precompute.ProcessAsync(v, TimeSpan.Zero, cancellationToken).ConfigureAwait(false)
                            : MomentPrecompute.Outcome.Skipped;
                        lock (counts)
                        {
                            counts[outcome] = counts.TryGetValue(outcome, out var c) ? c + 1 : 1;
                        }

                        if (i % 50 == 0)
                        {
                            progress.Report(99.0 * i / Math.Max(1, total));
                        }
                    }
                }

                await Task.WhenAll(WorkerAsync(false), WorkerAsync(true)).ConfigureAwait(false);

                var pruned = _precompute.PruneRemoved(id => _library.GetItemById(id) != null);
                var store = _sheets.Housekeep();
                progress.Report(100);
                _logger.LogInformation(
                    "Moments precompute: {Total} videos: {Counts}; {Pruned} removed items pruned; store {MB:0.0} MB in {Sets} sets + {Strips} strips",
                    total,
                    string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value)),
                    pruned,
                    store.Bytes / 1048576.0,
                    store.Sets,
                    store.Strips);
            }
            finally
            {
                _precompute.Save();
            }
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks },
            };
        }
    }
}
