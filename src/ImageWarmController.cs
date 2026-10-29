using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Plus
{
    /// <summary>
    /// Pre-resizes card images ahead of the viewport. Stock encodes one image per request and does not
    /// dedupe concurrent identical requests, and a browser only opens 6 connections per host, so a cold
    /// screenful trickles in. The client posts the look-ahead URLs here once; the server fetches them from
    /// itself with bounded concurrency and discards the bodies, which fills Jellyfin's own image cache.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("JellyfinPlus")]
    public class ImageWarmController : ControllerBase
    {
        private const int MaxUrls = 48;
        private const int MaxQuery = 400;
        private const int MaxPending = 256;

        private static readonly Regex UrlShape = new Regex(
            @"^/Items/(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/Images/[A-Za-z]{3,24}(?:/[0-9]{1,3})?\?[A-Za-z0-9=&%._-]{1,400}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

        // Shared by every caller, so warming can never take more than half the cores from live requests.
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));
        // Shared in-flight work, so a second caller for the same URL waits on the first encode instead of
        // starting another one (stock does not dedupe identical concurrent resizes).
        private static readonly ConcurrentDictionary<string, Task> InFlight = new ConcurrentDictionary<string, Task>();

        // The POST answers when its batch is cached, so the browser's own fetch that follows is a cache hit.
        // Capped: a slow batch answers anyway and the remaining resizes finish in the background.
        private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(8);
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private readonly ILogger<ImageWarmController> _log;

        /// <summary>Initializes a new instance of the <see cref="ImageWarmController"/> class.</summary>
        public ImageWarmController(ILogger<ImageWarmController> log)
        {
            _log = log;
        }

        /// <summary>Resizes and caches image URLs, answering once they are cached (at most 8 s). Best effort.</summary>
        /// <param name="body">Relative image paths.</param>
        /// <returns>200 with the number queued, or 400 when any entry is not an image path.</returns>
        [HttpPost("Images/Warm")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Warm([FromBody] WarmRequest body)
        {
            var urls = body?.Urls;
            if (urls == null || urls.Count == 0 || urls.Count > MaxUrls)
            {
                return BadRequest();
            }

            foreach (var u in urls)
            {
                if (!IsAllowed(u))
                {
                    return BadRequest();
                }
            }

            var prefix = "http://127.0.0.1:" + HttpContext.Connection.LocalPort + Request.PathBase.Value;
            var queued = 0;
            var waits = new List<Task>(urls.Count);
            foreach (var u in urls)
            {
                if (InFlight.TryGetValue(u, out var running))
                {
                    waits.Add(running);
                    continue;
                }

                if (InFlight.Count >= MaxPending)
                {
                    break;
                }

                // Not started under the request's token: a closed tab must not abandon a half-done batch.
                var key = u;
                var full = prefix + u;
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!InFlight.TryAdd(key, gate.Task))
                {
                    if (InFlight.TryGetValue(key, out running))
                    {
                        waits.Add(running);
                    }

                    continue;
                }

                queued++;
                waits.Add(gate.Task);
                _ = Task.Run(async () =>
                {
                    await FetchAsync(key, full).ConfigureAwait(false);
                    gate.TrySetResult();
                });
            }

            try
            {
                await Task.WhenAll(waits).WaitAsync(MaxWait, HttpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // timed out or the browser went away: the resizes carry on, the answer goes out now
            }

            return Ok(new { queued });
        }

        internal static bool IsAllowed(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url) || url.Length > MaxQuery + 120 || !UrlShape.IsMatch(url))
                {
                    return false;
                }

                // The query is capped on its own, not just through the total length.
                return url.Length - url.IndexOf('?') - 1 <= MaxQuery;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private async Task FetchAsync(string key, string fullUrl)
        {
            try
            {
                await Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                    using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                    // Headers arrive once the image is encoded and cached; the body is never needed.
                    _log.LogDebug("Image warm {Status}", (int)res.StatusCode);
                }
                finally
                {
                    Gate.Release();
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Image warm failed");
            }
            finally
            {
                InFlight.TryRemove(key, out _);
            }
        }

        /// <summary>Request body.</summary>
        public class WarmRequest
        {
            /// <summary>Gets or sets relative image paths, each starting with /Items/.</summary>
            public List<string> Urls { get; set; }
        }
    }
}
