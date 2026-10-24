/*
 * TIMELINE MARKERS, PURE PART. The server (src/MarkersController.cs, GET /JellyfinPlus/Markers/{itemId})
 * answers with its own normalised contract, merged from every configured provider:
 *
 *   { markers: [{ offsetSeconds, label, value?, unit?, kind?, source }] }
 *
 * The body is still treated as untrusted: any shape not recognised is "no markers", never an error.
 * `label` and `unit` are free text from an external service and only ever reach textContent / title.
 *
 * Also the preview-strip maths: which frames to show around a marker, and where each one sits in a
 * sprite sheet - stock Jellyfin trickplay (fixed Interval) or the plugin's moment sheets (a per-tile
 * time list, MomentSheets.cs).
 */

// Seek a little before the marker, so what it marks is seen arriving.
export var SEEK_LEAD_SECONDS = 3;

// Kinds with their own style; any other kind (or none) is drawn neutral.
export var KNOWN_KINDS = ['tip', 'chapter', 'highlight'];

// Preview strip: the marker frame plus NEIGHBOURS either side, SPACING seconds apart. Same
// constants as the server's MomentSheets (Neighbours, Spacing), so moment sheets hold these frames.
export var STRIP_NEIGHBOURS = 2;
export var STRIP_SPACING = 5;

function finite(n) {
    return typeof n === 'number' && isFinite(n);
}

/*
 * Drawable markers for a body and a duration in seconds (the player's own). Returns [] for anything
 * unusable. Each: { offset, pct (0..100), kind ('neutral' when unknown), size ('s'|'m'|'l'), label }.
 * Size is by value relative to the largest value in the list, so it works whatever the unit.
 */
export function timelineMarkers(body, durationSeconds) {
    if (!body || typeof body !== 'object' || !Array.isArray(body.markers)
        || !finite(durationSeconds) || durationSeconds <= 0) {
        return [];
    }

    var max = 0;
    body.markers.forEach(function (m) {
        if (m && finite(m.value) && m.value > max) { max = m.value; }
    });

    var out = [];
    body.markers.forEach(function (m) {
        if (!m || typeof m !== 'object' || !finite(m.offsetSeconds) || m.offsetSeconds < 0
            || m.offsetSeconds > durationSeconds) {
            return;
        }
        var value = finite(m.value) ? m.value : null;
        var label = typeof m.label === 'string' ? m.label.trim().slice(0, 200) : '';
        var unit = typeof m.unit === 'string' ? m.unit.trim().slice(0, 24) : '';
        var kind = typeof m.kind === 'string' && KNOWN_KINDS.indexOf(m.kind) >= 0 ? m.kind : 'neutral';
        var rel = value != null && max > 0 ? value / max : 0;
        out.push({
            offset: m.offsetSeconds,
            pct: Math.min(100, m.offsetSeconds / durationSeconds * 100),
            kind: kind,
            size: rel > 0.66 ? 'l' : (rel > 0.25 ? 'm' : 's'),
            label: markerLabel(label, value, unit)
        });
    });
    out.sort(function (a, b) { return a.offset - b.offset; });
    return out;
}

/* "label · value unit", either part alone when the other is missing. Plain text. */
export function markerLabel(label, value, unit) {
    var v = finite(value) ? String(Math.round(value * 100) / 100) : '';
    if (v && typeof unit === 'string' && unit.trim()) { v += ' ' + unit.trim(); }
    label = typeof label === 'string' ? label.trim() : '';
    if (label && v) { return label + ' · ' + v; }
    return label || v;
}

/* Where a click on a marker seeks to, in seconds. */
export function seekTarget(offsetSeconds) {
    return finite(offsetSeconds) ? Math.max(0, offsetSeconds - SEEK_LEAD_SECONDS) : 0;
}

/*
 * The strip's frame times for a marker: offset + k*STRIP_SPACING for k in -N..N, clamped to
 * [0, duration - 1] (the server's clamp), near-duplicates (< 1 s) collapsed. Each:
 * { t, centre } - centre is the marker's own frame. [] when unusable.
 */
export function stripTimes(offsetSeconds, durationSeconds) {
    if (!finite(offsetSeconds) || !finite(durationSeconds) || durationSeconds <= 0
        || offsetSeconds < 0 || offsetSeconds > durationSeconds) {
        return [];
    }
    var last = Math.max(0, durationSeconds - 1);
    function at(k) { return Math.round(Math.min(last, Math.max(0, offsetSeconds + k * STRIP_SPACING)) * 10) / 10; }
    var out = [{ t: at(0), centre: true }];
    for (var k = -STRIP_NEIGHBOURS; k <= STRIP_NEIGHBOURS; k++) {
        var t = at(k);
        if (k !== 0 && !out.some(function (o) { return Math.abs(o.t - t) < 1; })) { out.push({ t: t, centre: false }); }
    }
    out.sort(function (a, b) { return a.t - b.t; });
    return out;
}

/*
 * The best stock trickplay resolution from an item's Trickplay map
 * ({ [mediaSourceId]: { [width]: { Width, Height, TileWidth, TileHeight, ThumbnailCount, Interval } } }):
 * the smallest width >= 200 (else the largest). { mediaSourceId, info } or null.
 */
export function pickTrickplay(trickplay, mediaSourceId) {
    if (!trickplay || typeof trickplay !== 'object') { return null; }
    var id = mediaSourceId && trickplay[mediaSourceId] ? mediaSourceId : Object.keys(trickplay)[0];
    var res = id ? trickplay[id] : null;
    if (!res || typeof res !== 'object') { return null; }
    var best = null;
    Object.keys(res).forEach(function (k) {
        var i = res[k];
        if (!validInfo(i)) { return; }
        if (!best) { best = i; return; }
        var big = i.Width >= 200, bestBig = best.Width >= 200;
        if ((big && (!bestBig || i.Width < best.Width)) || (!big && !bestBig && i.Width > best.Width)) {
            best = i;
        }
    });
    return best ? { mediaSourceId: id, info: best } : null;
}

function validInfo(i) {
    return !!i && typeof i === 'object' && finite(i.Width) && i.Width > 0 && finite(i.Height) && i.Height > 0
        && finite(i.TileWidth) && i.TileWidth > 0 && finite(i.TileHeight) && i.TileHeight > 0
        && finite(i.Interval) && i.Interval > 0 && finite(i.ThumbnailCount) && i.ThumbnailCount > 0;
}

/*
 * Stock trickplay tile for a time: thumbnail n = floor(ms / Interval), clamped to the last one;
 * sheet floor(n / (TileWidth*TileHeight)), at column/row inside it - jellyfin-web's own maths.
 * { sheet, x, y, w, h } in pixels of the sheet, or null.
 */
export function stockTile(info, seconds) {
    if (!validInfo(info) || !finite(seconds) || seconds < 0) { return null; }
    var n = Math.min(info.ThumbnailCount - 1, Math.floor(seconds * 1000 / info.Interval));
    return tileAt(n, info.TileWidth, info.TileHeight, info.Width, info.Height);
}

/*
 * Moment-sheet tile for a time, from the server's manifest
 * ({ status:'ready', version, width, height, tileWidth, tileHeight, times:[...] }): the tile whose
 * time is nearest, if within 1 s (a missing frame has no tile). { sheet, x, y, w, h, t } or null.
 */
export function momentTile(manifest, seconds) {
    if (!manifest || manifest.status !== 'ready' || !Array.isArray(manifest.times) || !finite(seconds)
        || !(manifest.width > 0) || !(manifest.height > 0) || !(manifest.tileWidth > 0) || !(manifest.tileHeight > 0)) {
        return null;
    }
    var best = -1, gap = Infinity;
    manifest.times.forEach(function (t, i) {
        if (finite(t) && Math.abs(t - seconds) < gap) { gap = Math.abs(t - seconds); best = i; }
    });
    if (best < 0 || gap >= 1) { return null; }
    var tile = tileAt(best, manifest.tileWidth, manifest.tileHeight, manifest.width, manifest.height);
    tile.t = manifest.times[best];
    return tile;
}

function tileAt(n, cols, rows, w, h) {
    var per = cols * rows;
    var i = n % per;
    return { sheet: Math.floor(n / per), x: (i % cols) * w, y: Math.floor(i / cols) * h, w: w, h: h };
}
