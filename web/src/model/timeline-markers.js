/*
 * TIMELINE MARKERS, PURE PART. The server (src/MarkersController.cs, GET /JellyfinPlus/Markers/{itemId})
 * answers with its own normalised contract, merged from every configured provider:
 *
 *   { markers: [{ offsetSeconds, label, value?, kind?, source }] }
 *
 * The body is still treated as untrusted: any shape not recognised is "no markers", never an error.
 * `label` is free text from an external service and only ever reaches textContent / title.
 */

// Seek a little before the marker, so what it marks is seen arriving.
export var SEEK_LEAD_SECONDS = 3;

// Kinds with their own style; any other kind (or none) is drawn neutral.
export var KNOWN_KINDS = ['tip', 'chapter', 'highlight'];

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
        var kind = typeof m.kind === 'string' && KNOWN_KINDS.indexOf(m.kind) >= 0 ? m.kind : 'neutral';
        var rel = value != null && max > 0 ? value / max : 0;
        out.push({
            offset: m.offsetSeconds,
            pct: Math.min(100, m.offsetSeconds / durationSeconds * 100),
            kind: kind,
            size: rel >= 0.66 ? 'l' : (rel >= 0.25 ? 'm' : 's'),
            label: markerLabel(label, value)
        });
    });
    out.sort(function (a, b) { return a.offset - b.offset; });
    return out;
}

/* "label · value", either alone when the other is missing. Plain text. */
export function markerLabel(label, value) {
    var v = value != null ? String(Math.round(value * 100) / 100) : '';
    if (label && v) { return label + ' · ' + v; }
    return label || v;
}

/* Where a click on a marker seeks to, in seconds. */
export function seekTarget(offsetSeconds) {
    return finite(offsetSeconds) ? Math.max(0, offsetSeconds - SEEK_LEAD_SECONDS) : 0;
}
