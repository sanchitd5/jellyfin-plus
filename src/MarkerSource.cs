using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Trickplay;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Plus
{
    /// <summary>The merged marker list for an item, and whether every matching provider answered.</summary>
    public sealed class MergedMarkers
    {
        public List<TimelineMarker> Markers { get; set; } = new List<TimelineMarker>();

        /// <summary>Gets or sets a value indicating whether a provider failed (so an empty list is not "no markers").</summary>
        public bool AnyFailed { get; set; }
    }

    /// <summary>
    /// Fetches, caches and merges provider markers for an item, and derives its moment times. Shared by
    /// MarkersController (per user, after its access check) and the background precompute (no user: never
    /// exposed over HTTP). Contract: docs/architecture/TIMELINE_MARKERS.md.
    ///
    /// CACHE. In memory per provider + URL: a success for the provider's cacheMinutes, a failure for one
    /// minute. Background lookups read the cache but never fill it (a 48k-item sweep would evict what
    /// viewers use), and their upstream calls are spaced to at most BackgroundCallsPerSecond.
    /// </summary>
    public sealed class MarkerSource
    {
        public const int BackgroundCallsPerSecond = 4;

        private static readonly TimeSpan FailTtl = TimeSpan.FromMinutes(1);
        private const int MaxEntries = 1000;
        private const int MaxBodyBytes = 2 * 1024 * 1024;

        private static readonly ConcurrentDictionary<string, (DateTime At, TimeSpan Ttl, List<TimelineMarker> Markers)> Cache =
            new ConcurrentDictionary<string, (DateTime, TimeSpan, List<TimelineMarker>)>(StringComparer.Ordinal);

        private static readonly SemaphoreSlim BackgroundRate = new SemaphoreSlim(1, 1);
        private static DateTime _nextBackgroundCall = DateTime.MinValue;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ITrickplayManager _trickplay;
        private readonly ILogger<MarkerSource> _logger;

        public MarkerSource(IHttpClientFactory httpClientFactory, ITrickplayManager trickplay, ILogger<MarkerSource> logger)
        {
            _httpClientFactory = httpClientFactory;
            _trickplay = trickplay;
            _logger = logger;
        }

        public static bool MomentsOn => Plugin.Instance?.Configuration?.MarkerMoments ?? true;

        public IReadOnlyList<MarkerProvider> Providers()
        {
            return MarkerProviders.Current(Plugin.Instance?.Configuration?.MarkerProviders, w => _logger.LogWarning("{Warning}", w));
        }

        /// <summary>Whether any enabled provider's filter matches this path. In memory only.</summary>
        public bool AnyProviderMatches(string path)
        {
            return Providers().Any(p => MarkerProviders.Matches(p, path));
        }

        /// <summary>Whether the item has stock (or sheet) trickplay; null when the lookup failed.</summary>
        public async Task<bool?> HasStockTrickplayAsync(Guid itemId)
        {
            try
            {
                var stock = await _trickplay.GetTrickplayResolutions(itemId).ConfigureAwait(false);
                return stock != null && stock.Count > 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Moments: trickplay lookup failed for {Id}", itemId);
                return null;
            }
        }

        /// <summary>The frame times for an item, or none when moments are off or it has stock trickplay.</summary>
        public async Task<List<double>> MomentTimesAsync(BaseItem item, CancellationToken cancellationToken)
        {
            if (!MomentsOn || item is not Video || item.RunTimeTicks is not long ticks || ticks <= 0)
            {
                return new List<double>();
            }

            if (await HasStockTrickplayAsync(item.Id).ConfigureAwait(false) != false)
            {
                return new List<double>(); // stock trickplay wins (the client uses it), or the lookup failed
            }

            var duration = ticks / (double)TimeSpan.TicksPerSecond;
            var markers = await MergedAsync(item, false, cancellationToken).ConfigureAwait(false);
            return MomentSheets.MomentTimes(markers.Markers, duration);
        }

        /// <summary>Markers from every matching provider, sorted by offset.</summary>
        public async Task<MergedMarkers> MergedAsync(BaseItem item, bool background, CancellationToken cancellationToken)
        {
            var providers = Providers().Where(p => MarkerProviders.Matches(p, item.Path)).ToList();
            if (providers.Count == 0)
            {
                return new MergedMarkers();
            }

            var duration = item.RunTimeTicks is long t && t > 0 ? t / (double)TimeSpan.TicksPerSecond : 0;
            List<TimelineMarker>[] lists;
            if (background)
            {
                lists = new List<TimelineMarker>[providers.Count];
                for (var i = 0; i < providers.Count; i++)
                {
                    lists[i] = await GetCachedAsync(providers[i], item, duration, true, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                lists = await Task.WhenAll(providers.Select(p => GetCachedAsync(p, item, duration, false, cancellationToken))).ConfigureAwait(false);
            }

            return new MergedMarkers
            {
                Markers = lists.Where(l => l != null).SelectMany(l => l).OrderBy(m => m.OffsetSeconds).ToList(),
                AnyFailed = lists.Any(l => l == null),
            };
        }

        private async Task<List<TimelineMarker>> GetCachedAsync(MarkerProvider p, BaseItem item, double duration, bool background, CancellationToken requestToken)
        {
            var url = MarkerProviders.BuildUrl(p, item.Path, item.Id, item.Name);
            if (url == null)
            {
                return null;
            }

            var key = p.Id + "|" + p.UrlTemplate + "|" + url.AbsoluteUri;
            if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < hit.Ttl)
            {
                return hit.Markers;
            }

            if (background)
            {
                await WaitBackgroundSlotAsync(requestToken).ConfigureAwait(false);
            }

            var now = DateTime.UtcNow;
            List<TimelineMarker> markers;
            try
            {
                markers = await FetchAsync(p, url, duration, requestToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            {
                if (background)
                {
                    throw;
                }

                return null; // the viewer went away: not a provider failure, cache nothing
            }

            if (background)
            {
                return markers;
            }

            if (Cache.Count >= MaxEntries)
            {
                Cache.Clear();
            }

            var ttl = markers == null ? FailTtl : TimeSpan.FromMinutes(p.CacheMinutes);
            if (ttl > TimeSpan.Zero)
            {
                Cache[key] = (now, ttl, markers);
            }

            return markers;
        }

        /// <summary>Spaces background upstream calls to at most BackgroundCallsPerSecond (it is someone else's service).</summary>
        private static async Task WaitBackgroundSlotAsync(CancellationToken ct)
        {
            await BackgroundRate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var wait = _nextBackgroundCall - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                _nextBackgroundCall = DateTime.UtcNow + TimeSpan.FromSeconds(1.0 / BackgroundCallsPerSecond);
            }
            finally
            {
                BackgroundRate.Release();
            }
        }

        /// <summary>The provider's markers, or null on any failure.</summary>
        private async Task<List<TimelineMarker>> FetchAsync(MarkerProvider p, Uri url, double duration, CancellationToken requestToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
            cts.CancelAfter(TimeSpan.FromSeconds(p.TimeoutSeconds));
            try
            {
                var client = _httpClientFactory.CreateClient();
                using var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Markers: provider {Id} answered {Status}", p.Id, (int)res.StatusCode);
                    return null;
                }

                if (res.Content.Headers.ContentLength > MaxBodyBytes)
                {
                    return null;
                }

                // Read at most MaxBodyBytes + 1, whatever the headers claimed.
                using var stream = await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int n;
                while ((n = await stream.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, n);
                    if (buffer.Length > MaxBodyBytes)
                    {
                        return null;
                    }
                }

                using var doc = JsonDocument.Parse(buffer.ToArray());
                var markers = MarkerProviders.Map(p, doc.RootElement, duration);
                if (markers == null)
                {
                    _logger.LogDebug("Markers: provider {Id} answered an unexpected shape", p.Id);
                }

                return markers;
            }
            catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
            {
                _logger.LogDebug("Markers: provider {Id} timed out", p.Id);
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is JsonException || ex is IOException)
            {
                _logger.LogDebug(ex, "Markers: provider {Id} unavailable", p.Id);
                return null;
            }
        }
    }
}
