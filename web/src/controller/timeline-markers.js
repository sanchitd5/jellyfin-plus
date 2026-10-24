import { state } from '../model/state.js';
import { log } from '../lib/log.js';
import { player, activePlayer } from './live-apply.js';
import { timelineMarkers, seekTarget } from '../model/timeline-markers.js';

/*
 * TIMELINE MARKERS ON THE PLAYER'S SEEK BAR. External services configured on the server
 * (src/MarkerProviders.cs) supply points for an item; GET /JellyfinPlus/Markers/{itemId} returns them
 * merged and normalised. Each is a dot over the OSD position slider, styled by kind (unknown kinds
 * neutral), sized by value, titled "label · value"; a click seeks a few seconds before it
 * (model/timeline-markers.js).
 *
 * ZERO REQUESTS WHEN OFF. GET /JellyfinPlus/Markers/Enabled is asked once (again 5 min after a
 * failure); while it says false no per-item request is made.
 *
 * FAIL SOFT. One fetch per item (one retry after 30 s on a network failure). An error, an
 * unrecognised body or no markers: nothing is drawn and the player is stock. Markers go when the
 * item changes or the video page goes away. TV layout is left alone.
 *
 * SEAMS: #videoOsdPage, .osdPositionSlider inside .mdl-slider-container, and
 * .mdl-slider-background-flex for the track's horizontal extent (falls back to the container).
 * The playbackManager is found by shape (live-apply.js), never a global.
 */

var STYLE_ID = 'jellyfinPlusMarkerStyle';
var LAYER_CLASS = 'jp-marker-layer';
var HOST_CLASS = 'jp-marker-host';
var PAGE_ID = 'videoOsdPage';
var POLL_MS = 1000;
var RETRY_MS = 30000;
var ENABLED_RETRY_MS = 5 * 60 * 1000;
var TICKS_PER_SECOND = 10000000;

var L = '.' + LAYER_CLASS;
var CSS = [
    '.' + HOST_CLASS + ' { position: relative; }',
    L + ' { position: absolute; top: 0; bottom: 0; pointer-events: none; z-index: 2; }',
    L + ' .jp-marker { position: absolute; top: 50%; transform: translate(-50%, -50%); box-sizing: border-box;'
        + ' padding: 0; margin: 0; border: 1px solid rgba(0,0,0,0.6); border-radius: 50%; cursor: pointer;'
        + ' pointer-events: auto; width: 7px; height: 7px; background: #e0e0e0; }',
    L + ' .jp-marker[data-size="m"] { width: 10px; height: 10px; }',
    L + ' .jp-marker[data-size="l"] { width: 13px; height: 13px; }',
    L + ' .jp-marker[data-kind="tip"] { background: #ffc107; }',
    L + ' .jp-marker[data-kind="tip"][data-size="l"] { background: #ff7043; }',
    L + ' .jp-marker[data-kind="chapter"] { background: #64b5f6; border-radius: 2px; }',
    L + ' .jp-marker[data-kind="highlight"] { background: #ec407a; }',
    L + ' .jp-marker:hover, ' + L + ' .jp-marker:focus-visible { outline: 2px solid #fff; }'
].join('\n');

var enabled = null;      // null: not asked yet / asking; true/false: the server's answer
var enabledAt = 0;
var itemKey = null;      // the item the current answer belongs to
var body = null;         // its answer (null: none / not yet)
var drawn = null;        // { slider, duration } last drawn, to redraw only on change

function manager() {
    return state.playbackManagerRef || player();
}

function client() {
    var c = window.ApiClient;
    return c && typeof c.getUrl === 'function' && typeof c.ajax === 'function' ? c : null;
}

function onVideoPage() {
    var p = document.getElementById(PAGE_ID);
    return !!p && p.getClientRects().length > 0;
}

function clear() {
    Array.prototype.forEach.call(document.querySelectorAll(L), function (l) { l.remove(); });
    drawn = null;
}

function get(path) {
    var c = client();
    if (!c) { return Promise.reject(new Error('no ApiClient')); }
    return Promise.resolve(c.ajax({ type: 'GET', url: c.getUrl(path), dataType: 'json' }));
}

/* True once the server said providers exist; asks at most once per ENABLED_RETRY_MS after a failure. */
function markersEnabled() {
    if (enabled !== null || Date.now() - enabledAt < ENABLED_RETRY_MS) { return enabled === true; }
    enabledAt = Date.now();
    enabled = false; // while asking
    get('JellyfinPlus/Markers/Enabled').then(function (r) {
        enabled = !!(r && r.enabled === true);
        log('timeline markers: ' + (enabled ? 'enabled' : 'no providers on this server'));
    }, function (err) {
        enabled = null; // a server without this route (404) or offline: ask again later
        log('timeline markers: status unavailable', err && err.status);
    });
    return false;
}

function fetchMarkers(key, retried) {
    get('JellyfinPlus/Markers/' + encodeURIComponent(key)).then(function (b) {
        if (itemKey !== key) { return; }
        body = b;
        drawn = null;
    }, function (err) {
        var status = err && typeof err.status === 'number' ? err.status : 0;
        if (status === 0 && !retried) {
            setTimeout(function () { if (itemKey === key) { fetchMarkers(key, true); } }, RETRY_MS);
        }
        log('timeline markers: unavailable', status || err);
    });
}

function seekTo(seconds) {
    try {
        var pm = manager();
        var pl = pm && activePlayer(pm);
        if (!pm || !pl || typeof pm.seek !== 'function') { return; }
        var r = pm.seek(Math.round(seekTarget(seconds) * TICKS_PER_SECOND), pl);
        if (r && typeof r.catch === 'function') { r.catch(function (e) { log('timeline markers: seek rejected', e); }); }
    } catch (err) {
        log('timeline markers: seek failed', err);
    }
}

function stop(e) { e.stopPropagation(); }

function hostOf(slider) {
    return slider.closest('.mdl-slider-container') || slider.parentNode;
}

function draw(slider, durationSeconds) {
    clear();
    var markers = timelineMarkers(body, durationSeconds);
    if (!markers.length) {
        drawn = { slider: slider, duration: durationSeconds };
        return;
    }

    var host = hostOf(slider);
    if (!host) { return; }

    // The track's horizontal extent inside the host (stock pads it), so 0% and 100% sit under the
    // slider's own ends.
    var track = host.querySelector('.mdl-slider-background-flex') || host;
    var hr = host.getBoundingClientRect();
    var tr = track.getBoundingClientRect();
    if (!hr.width) { return; }   // OSD not laid out yet: drawn stays null, the next tick tries again
    host.classList.add(HOST_CLASS);

    var layer = document.createElement('div');
    layer.className = LAYER_CLASS;
    layer.style.left = Math.max(0, tr.left - hr.left) + 'px';
    layer.style.width = (tr.width || hr.width) + 'px';

    markers.forEach(function (m) {
        var b = document.createElement('button');
        b.type = 'button';
        b.className = 'jp-marker';
        b.setAttribute('data-kind', m.kind);
        b.setAttribute('data-size', m.size);
        b.style.left = m.pct + '%';
        if (m.label) {
            b.title = m.label;                 // untrusted text: attributes only, never innerHTML
            b.setAttribute('aria-label', m.label);
        } else {
            b.setAttribute('aria-label', 'Marker');
        }
        b.addEventListener('pointerdown', stop);
        b.addEventListener('mousedown', stop);
        b.addEventListener('touchstart', stop, { passive: true });
        b.addEventListener('click', function (e) {
            e.stopPropagation();
            e.preventDefault();
            seekTo(m.offset);
        });
        layer.appendChild(b);
    });
    host.appendChild(layer);
    drawn = { slider: slider, duration: durationSeconds };
}

function tick() {
    try {
        if (document.documentElement.classList.contains('layout-tv') || !onVideoPage()) {
            itemKey = null;
            body = null;
            clear();
            return;
        }

        if (!markersEnabled()) { return; }

        var pm = manager();
        var pl = pm && typeof pm.currentItem === 'function' ? activePlayer(pm) : null;
        var item = pl ? pm.currentItem(pl) : null;
        var key = item && item.Id ? String(item.Id) : null;
        if (key !== itemKey) {
            itemKey = key;
            body = null;
            clear();
            if (key) { fetchMarkers(key, false); }
            return;
        }

        if (!body) { return; }
        var slider = document.querySelector('#' + PAGE_ID + ' .osdPositionSlider');
        if (!slider) { clear(); return; }

        var ticks = typeof pm.duration === 'function' ? pm.duration(pl) : 0;
        var duration = ticks > 0 ? ticks / TICKS_PER_SECOND : 0;
        if (!duration) {
            var v = document.querySelector('#' + PAGE_ID + ' video') || document.querySelector('video');
            duration = v && isFinite(v.duration) ? v.duration : 0;
        }

        var host = hostOf(slider);
        var layerGone = !host || !host.querySelector(L);
        if (!drawn || drawn.slider !== slider || Math.abs(drawn.duration - duration) > 0.5
            || (layerGone && timelineMarkers(body, duration).length)) {
            draw(slider, duration);
        }
    } catch (err) {
        // currentItem() throws when there is no active player: nothing to draw.
        clear();
    }
}

export function installTimelineMarkers() {
    try {
        if (!document.getElementById(STYLE_ID)) {
            var style = document.createElement('style');
            style.id = STYLE_ID;
            style.textContent = CSS;
            (document.head || document.documentElement).appendChild(style);
        }

        setInterval(tick, POLL_MS);
        window.addEventListener('resize', function () { drawn = null; });
        log('timeline markers installed');
    } catch (err) {
        log('timeline markers install failed', err);
    }
}
