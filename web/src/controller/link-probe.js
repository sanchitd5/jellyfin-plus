import { state } from '../model/state.js';
import { log } from '../lib/log.js';
import { classify, PREF_KEY } from '../model/link-profile.js';

/*
 * LINK PROBE. Measures this browser's link to the server (model/link-profile.js says why):
 *   - round trip: the fastest of three GET System/Ping (a few bytes each),
 *   - throughput: one 256 KB range of this script's own bundle, timed from first byte to last.
 * Once ~3 s after load, then every 10 min while the page is visible. Result in state.link
 * ({ rtt, mbps, at }); network.js asks linkProfile() on every PlaybackInfo request.
 * ~260 KB per probe. Off switch: localStorage jellyfinPlus.linkProfile = fast (never slow) /
 * slow (always slow, no probe needed). TV layout and failures: no profile, stock behaviour.
 */

var FIRST_MS = 3000;
var EVERY_MS = 10 * 60 * 1000;
var SAMPLE_BYTES = 256 * 1024;

function pref() {
    try { return window.localStorage.getItem(PREF_KEY); } catch (e) { return null; }
}

/* The profile in force now: { slow, kbps }. Never throws. */
export function linkProfile() {
    try {
        var l = state.link;
        return classify(l ? l.rtt : null, l ? l.mbps : null, pref());
    } catch (e) {
        return { slow: false, kbps: 0 };
    }
}

function client() {
    var c = window.ApiClient;
    return c && typeof c.getUrl === 'function' ? c : null;
}

function bundleUrl() {
    var s = Array.prototype.find.call(document.scripts, function (x) { return /jellyfin-plus\.js/.test(x.src || ''); });
    return s ? s.src : null;
}

function rtt(c) {
    var url = c.getUrl('System/Ping');
    var best = Infinity;
    var i = 0;
    function one() {
        var t0 = performance.now();
        return fetch(url, { cache: 'no-store' }).then(function (r) { return r.text(); }).then(function () {
            best = Math.min(best, performance.now() - t0);
            return ++i < 3 ? one() : best;
        });
    }
    return one();
}

function throughput() {
    var url = bundleUrl();
    if (!url) { return Promise.resolve(null); }
    var t0 = performance.now();
    var tHead = 0;
    return fetch(url + (url.indexOf('?') < 0 ? '?' : '&') + 'jpprobe=' + Date.now(), {
        cache: 'no-store',
        headers: { Range: 'bytes=0-' + (SAMPLE_BYTES - 1) }
    }).then(function (r) {
        tHead = performance.now();
        return r.arrayBuffer();
    }).then(function (buf) {
        var secs = (performance.now() - tHead) / 1000;
        // Too small or too quick to time (LAN): call it fast without dividing by ~0.
        if (buf.byteLength < 32 * 1024 || secs < 0.02) { return 1000; }
        return (buf.byteLength * 8 / 1e6) / secs;
    }).catch(function () { return null; }).then(function (mbps) {
        return { mbps: mbps, total: performance.now() - t0 };
    });
}

function probe() {
    var c = client();
    if (!c || document.hidden || document.documentElement.classList.contains('layout-tv')) { return; }
    rtt(c).then(function (ms) {
        return throughput().then(function (tp) {
            state.link = { rtt: Math.round(ms), mbps: tp.mbps == null ? null : Math.round(tp.mbps * 10) / 10, at: Date.now() };
            var p = linkProfile();
            log('link probe: rtt ' + state.link.rtt + ' ms, ' + state.link.mbps + ' Mbps -> '
                + (p.slow ? 'slow, cap ' + p.kbps + ' kbps' : 'fast'));
        });
    }).catch(function (err) {
        log('link probe failed', err);
    });
}

export function installLinkProbe() {
    setTimeout(probe, FIRST_MS);
    setInterval(probe, EVERY_MS);
    document.addEventListener('visibilitychange', function () {
        if (!document.hidden && (!state.link || Date.now() - state.link.at > EVERY_MS)) { probe(); }
    });
    log('link probe installed');
}
