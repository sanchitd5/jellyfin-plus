/*
 * LINK PROFILE, PURE PART (controller/link-probe.js measures; network.js applies).
 *
 * A remote viewer reached over a long or thin link (measured: Tailscale, ~272 ms round trip,
 * 0.6-2.6 Mbps) pays for every request a seek makes. Direct play is the worst case there: the
 * browser hunts for each seek target with range requests through a 2 GB file (3-8 s per seek
 * measured). The server cannot tell such a viewer apart by address (subnet routers SNAT, so it
 * sees a LAN IP), so the browser measures its own link and, when it is slow:
 *   - asks PlaybackInfo for no direct play (seeks become HLS segment fetches),
 *   - caps MaxStreamingBitrate below what the link carries (Jellyfin then transcodes or remuxes),
 *   - sends every enhancement axis as off: upscaling to 1440p before squeezing into ~1.5 Mbps
 *     costs GPU and seek time and shows nothing.
 * Only stock request fields and the existing off values travel; nothing new on the wire.
 */

export var PREF_KEY = 'jellyfinPlus.linkProfile';   // auto (unset) | fast | slow | off
export var SLOW_RTT_MS = 120;
export var SLOW_MBPS = 10;
export var MIN_KBPS = 800;
export var MAX_KBPS = 4000;
export var SHARE = 0.6;                              // leave headroom for audio and jitter

function finite(n) {
    return typeof n === 'number' && isFinite(n);
}

/* The pref: 'auto' (default), 'fast' (never treat as slow), 'slow' (always), 'off' (= fast). */
export function linkPref(raw) {
    var s = raw == null ? '' : String(raw).trim().toLowerCase();
    if (s === 'fast' || s === 'off' || s === 'lan') { return 'fast'; }
    if (s === 'slow' || s === 'remote') { return 'slow'; }
    return 'auto';
}

/*
 * { slow, kbps } from a measurement (rttMs, mbps; either may be missing) and the pref.
 * kbps is the bitrate cap to ask for when slow: SHARE of the measured throughput, clamped to
 * [MIN_KBPS, MAX_KBPS]; MIN_KBPS*2 when throughput is unknown.
 */
export function classify(rttMs, mbps, pref) {
    pref = linkPref(pref);
    var measuredSlow = (finite(rttMs) && rttMs > SLOW_RTT_MS) || (finite(mbps) && mbps < SLOW_MBPS);
    var slow = pref === 'slow' || (pref === 'auto' && measuredSlow);
    var kbps = finite(mbps) && mbps > 0
        ? Math.round(Math.min(MAX_KBPS, Math.max(MIN_KBPS, mbps * 1000 * SHARE)))
        : MIN_KBPS * 2;
    return { slow: slow, kbps: kbps };
}

/* A PlaybackInfo JSON body rewritten for a slow link, or null when nothing changes. */
export function slowBody(bodyText, kbps) {
    if (typeof bodyText !== 'string' || !bodyText) { return null; }
    var body;
    try { body = JSON.parse(bodyText); } catch (e) { return null; }
    if (!body || typeof body !== 'object') { return null; }
    var cap = kbps * 1000;
    var changed = false;
    if (body.EnableDirectPlay !== false) { body.EnableDirectPlay = false; changed = true; }
    if (!finite(body.MaxStreamingBitrate) || body.MaxStreamingBitrate > cap) {
        body.MaxStreamingBitrate = cap;
        changed = true;
    }
    return changed ? JSON.stringify(body) : null;
}

/* The query-string form of the same (GET PlaybackInfo, and the POST's own query). */
export function slowUrl(url, kbps) {
    if (!url) { return url; }
    var cap = kbps * 1000;
    var out = url;
    function set(k, v) {
        var re = new RegExp('([?&])(' + k + ')=([^&]*)', 'i');
        var m = re.exec(out);
        if (m) {
            if (k === 'maxStreamingBitrate' && /^\d+$/.test(m[3]) && Number(m[3]) <= cap) { return; }
            out = out.replace(re, '$1$2=' + v); // keep the caller's spelling of the key
        } else {
            out += (out.indexOf('?') === -1 ? '?' : '&') + k + '=' + v;
        }
    }
    set('enableDirectPlay', 'false');
    set('maxStreamingBitrate', String(cap));
    return out;
}
