using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Plus
{
    /// <summary>The markers answer: always an object, never upstream detail.</summary>
    public sealed class TimelineMarkers
    {
        /// <summary>Gets or sets the markers from every matching provider, sorted by offset. Empty: draw nothing.</summary>
        [JsonPropertyName("markers")]
        public List<TimelineMarker> Markers { get; set; } = new List<TimelineMarker>();
    }

    /// <summary>Whether any marker provider is configured, so a client can skip the per-item request.</summary>
    public sealed class TimelineMarkersStatus
    {
        /// <summary>Gets or sets a value indicating whether at least one provider is enabled and valid.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// Timeline markers: points on the player's seek bar supplied by external services ("providers",
    /// MarkerProviders.cs), merged into one normalised list. The server makes every upstream call, so
    /// provider hosts are never exposed to clients and an HTTPS-served Jellyfin never makes a
    /// mixed-content call.
    ///
    ///   GET /JellyfinPlus/Markers/Enabled      -> { enabled }
    ///   GET /JellyfinPlus/Markers/{itemId}     -> { markers: [{ offsetSeconds, label, value?, kind?, source }] }
    ///
    /// ACCESS. The item is looked up for the authenticated user, so an item the user cannot see is
    /// 404 exactly like a missing one; its path never leaves the server.
    ///
    /// FAIL SOFT. A provider that fails (non-2xx, timeout, oversized or wrongly shaped body)
    /// contributes nothing; the answer is still 200 with whatever the others gave.
    ///
    /// CACHE. In memory per provider + URL: a success for the provider's cacheMinutes, a failure
    /// for one minute. Access is checked before the cache.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("JellyfinPlus/Markers")]
    public class MarkersController : ControllerBase
    {
        private static readonly TimeSpan FailTtl = TimeSpan.FromMinutes(1);
        private const int MaxEntries = 1000;
        private const int MaxBodyBytes = 2 * 1024 * 1024;

        private static readonly ConcurrentDictionary<string, (DateTime At, TimeSpan Ttl, List<TimelineMarker> Markers)> Cache =
            new ConcurrentDictionary<string, (DateTime, TimeSpan, List<TimelineMarker>)>(StringComparer.Ordinal);

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<MarkersController> _logger;

        /// <summary>Initializes a new instance of the <see cref="MarkersController"/> class.</summary>
        public MarkersController(ILibraryManager libraryManager, IUserManager userManager, IHttpClientFactory httpClientFactory, ILogger<MarkersController> logger)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>Gets whether any provider is configured.</summary>
        [HttpGet("Enabled")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<TimelineMarkersStatus> GetEnabled()
        {
            return new TimelineMarkersStatus { Enabled = Providers().Count > 0 };
        }

        /// <summary>Gets the markers for an item, merged across every matching provider.</summary>
        [HttpGet("{itemId:guid}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<TimelineMarkers>> GetMarkers([FromRoute] Guid itemId, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId) || userId == Guid.Empty)
            {
                return NotFound();
            }

            var user = _userManager.GetUserById(userId);
            var item = user == null || itemId == Guid.Empty ? null : _libraryManager.GetItemById<BaseItem>(itemId, user);
            if (item == null)
            {
                return NotFound(); // missing or not visible to this user: the same answer
            }

            var result = new TimelineMarkers();
            var providers = Providers().Where(p => MarkerProviders.Matches(p, item.Path)).ToList();
            if (providers.Count == 0)
            {
                return result;
            }

            var duration = item.RunTimeTicks is long t && t > 0 ? t / (double)TimeSpan.TicksPerSecond : 0;
            var lists = await Task.WhenAll(providers.Select(p => GetCachedAsync(p, item, duration, cancellationToken))).ConfigureAwait(false);
            result.Markers = lists.Where(l => l != null).SelectMany(l => l).OrderBy(m => m.OffsetSeconds).ToList();
            return result;
        }

        private IReadOnlyList<MarkerProvider> Providers()
        {
            return MarkerProviders.Current(Plugin.Instance?.Configuration?.MarkerProviders, w => _logger.LogWarning("{Warning}", w));
        }

        private async Task<List<TimelineMarker>> GetCachedAsync(MarkerProvider p, BaseItem item, double duration, CancellationToken requestToken)
        {
            var url = MarkerProviders.BuildUrl(p, item.Path, item.Id, item.Name);
            if (url == null)
            {
                return null;
            }

            var key = p.Id + "|" + p.UrlTemplate + "|" + url.AbsoluteUri;
            var now = DateTime.UtcNow;
            if (Cache.TryGetValue(key, out var hit) && now - hit.At < hit.Ttl)
            {
                return hit.Markers;
            }

            List<TimelineMarker> markers;
            try
            {
                markers = await FetchAsync(p, url, duration, requestToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            {
                return null; // the viewer went away: not a provider failure, cache nothing
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
