using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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

        /// <summary>Gets or sets a value indicating whether the server makes moment previews (setting MarkerMoments).</summary>
        [JsonPropertyName("moments")]
        public bool Moments { get; set; }
    }

    /// <summary>
    /// Timeline markers: points on the player's seek bar supplied by external services ("providers",
    /// MarkerProviders.cs), merged into one normalised list (MarkerSource.cs). The server makes every
    /// upstream call, so provider hosts are never exposed to clients and an HTTPS-served Jellyfin never
    /// makes a mixed-content call.
    ///
    ///   GET /JellyfinPlus/Markers/Enabled      -> { enabled, moments }
    ///   GET /JellyfinPlus/Markers/{itemId}     -> { markers: [{ offsetSeconds, label, value?, unit?, kind?, source }] }
    ///   GET /JellyfinPlus/Markers/{itemId}/Moments          -> MomentManifest (200 ready/none, 202 pending)
    ///   GET /JellyfinPlus/Markers/{itemId}/Moments/{n}.jpg  -> sheet n of the ready set (404 otherwise)
    ///   GET /JellyfinPlus/Markers/{itemId}/Moments/Marker/{i}.jpg?t=  -> one marker's strip, made on the
    ///       spot when the full set is not ready (X-Moment-Strip: { width, height, times }; 409 + manifest
    ///       when the set is ready; 503 when it could not be made in time)
    ///
    /// Moment previews (MomentSheets.cs) are for items without stock trickplay; their frame times are
    /// derived from this same merged marker list, never from the request. Usually precomputed
    /// (MomentPrecompute.cs); a miss here generates at once, ahead of any background work.
    ///
    /// ACCESS. The item is looked up for the authenticated user, so an item the user cannot see is
    /// 404 exactly like a missing one; its path never leaves the server.
    ///
    /// FAIL SOFT. A provider that fails (non-2xx, timeout, oversized or wrongly shaped body)
    /// contributes nothing; the answer is still 200 with whatever the others gave.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("JellyfinPlus/Markers")]
    public class MarkersController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly MomentSheets _moments;
        private readonly MarkerSource _source;

        /// <summary>Initializes a new instance of the <see cref="MarkersController"/> class.</summary>
        public MarkersController(ILibraryManager libraryManager, IUserManager userManager, MomentSheets moments, MarkerSource source)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _moments = moments;
            _source = source;
        }

        /// <summary>Gets whether any provider is configured.</summary>
        [HttpGet("Enabled")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<TimelineMarkersStatus> GetEnabled()
        {
            var enabled = _source.Providers().Count > 0;
            return new TimelineMarkersStatus { Enabled = enabled, Moments = enabled && MarkerSource.MomentsOn };
        }

        /// <summary>
        /// Gets the moment-preview manifest for an item. Waits briefly for a fresh generation, then
        /// answers 202 "pending"; the client asks again. "none" when off, no markers, stock trickplay
        /// exists, or generation failed recently.
        /// </summary>
        [HttpGet("{itemId:guid}/Moments")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<MomentManifest>> GetMoments([FromRoute] Guid itemId, CancellationToken cancellationToken)
        {
            var item = VisibleItem(itemId);
            if (item == null)
            {
                return NotFound();
            }

            var times = await _source.MomentTimesAsync(item, cancellationToken).ConfigureAwait(false);
            if (times.Count == 0)
            {
                return new MomentManifest();
            }

            var m = await _moments.GetAsync(item.Id, item.Path, times, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            Response.Headers["Cache-Control"] = "no-store"; // pending turns ready; ready changes with the markers
            if (m.Status == "pending")
            {
                return StatusCode(StatusCodes.Status202Accepted, m);
            }

            return m;
        }

        /// <summary>Gets one sheet of the item's ready moment-preview set.</summary>
        [HttpGet("{itemId:guid}/Moments/{index:int}.jpg")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetMomentSheet([FromRoute] Guid itemId, [FromRoute] int index, [FromQuery] string v, CancellationToken cancellationToken)
        {
            var item = VisibleItem(itemId);
            if (item == null || index < 0 || index > 100)
            {
                return NotFound();
            }

            var times = await _source.MomentTimesAsync(item, cancellationToken).ConfigureAwait(false);
            var file = _moments.SheetPath(item.Id, item.Path, times, index);
            var version = file == null ? null : Path.GetFileName(Path.GetDirectoryName(file));
            if (file == null || (!string.IsNullOrEmpty(v) && !string.Equals(v, version, StringComparison.Ordinal)))
            {
                return NotFound(); // a stale version must never be cached under its URL
            }

            // The URL carries the set's version (its content key), so the bytes behind it never change.
            Response.Headers["Cache-Control"] = string.IsNullOrEmpty(v) ? "private, max-age=86400" : "private, max-age=31536000, immutable";
            return PhysicalFile(file, "image/jpeg");
        }

        /// <summary>
        /// Gets one marker's strip (its frame and neighbours side by side), for an item whose full set is
        /// not ready yet: made on the spot in well under a second, then cached. <paramref name="index"/> is
        /// the marker's position in this item's merged list (GetMarkers order); <paramref name="t"/>, when
        /// given, is its offset and wins if the list moved (it must still name one of the server's own
        /// markers - frame times never come from the request). The tile times travel in X-Moment-Strip
        /// (JSON { width, height, times }). 409 with the manifest when the full set is ready (use it).
        /// </summary>
        [HttpGet("{itemId:guid}/Moments/Marker/{index:int}.jpg")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult> GetMarkerStrip([FromRoute] Guid itemId, [FromRoute] int index, [FromQuery] double? t, CancellationToken cancellationToken)
        {
            var item = VisibleItem(itemId);
            if (item == null || index < 0 || !MarkerSource.MomentsOn || item is not Video || item.RunTimeTicks is not long ticks || ticks <= 0)
            {
                return NotFound();
            }

            if (await _source.HasStockTrickplayAsync(item.Id).ConfigureAwait(false) != false)
            {
                return NotFound(); // stock trickplay wins
            }

            var duration = ticks / (double)TimeSpan.TicksPerSecond;
            var markers = (await _source.MergedAsync(item, false, cancellationToken).ConfigureAwait(false)).Markers;
            var marker = MomentSheets.PickMarker(markers, index, t);
            if (marker == null)
            {
                return NotFound();
            }

            var ready = _moments.ReadySet(item.Id, item.Path, MomentSheets.MomentTimes(markers, duration));
            if (ready != null)
            {
                return StatusCode(StatusCodes.Status409Conflict, ready);
            }

            var times = MomentSheets.MomentTimes(new[] { marker }, duration);
            var file = await _moments.StripAsync(item.Id, item.Path, times, cancellationToken).ConfigureAwait(false);
            if (file == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            Response.Headers["X-Moment-Strip"] = JsonSerializer.Serialize(new { width = MomentSheets.ThumbWidth, height = MomentSheets.ThumbHeight, times });
            // Not immutable: the URL names a marker, not a file version (a replaced file gives new frames).
            Response.Headers["Cache-Control"] = "private, max-age=86400";
            return PhysicalFile(file, "image/jpeg");
        }

        /// <summary>Gets the markers for an item, merged across every matching provider.</summary>
        [HttpGet("{itemId:guid}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<TimelineMarkers>> GetMarkers([FromRoute] Guid itemId, CancellationToken cancellationToken)
        {
            var item = VisibleItem(itemId);
            if (item == null)
            {
                return NotFound(); // missing or not visible to this user: the same answer
            }

            var merged = await _source.MergedAsync(item, false, cancellationToken).ConfigureAwait(false);
            return new TimelineMarkers { Markers = merged.Markers };
        }

        /// <summary>The item for the authenticated user, or null (missing and not visible look the same).</summary>
        private BaseItem VisibleItem(Guid itemId)
        {
            if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId) || userId == Guid.Empty)
            {
                return null;
            }

            var user = _userManager.GetUserById(userId);
            return user == null || itemId == Guid.Empty ? null : _libraryManager.GetItemById<BaseItem>(itemId, user);
        }
    }
}
