import { state } from '../model/state.js';
import { log } from '../lib/log.js';
import { player, activePlayer } from './live-apply.js';
import { timelineMarkers, seekTarget, stripTimes, pickTrickplay, stockTile, momentTile } from '../model/timeline-markers.js';

/*
 * TIMELINE MARKERS ON THE PLAYER'S SEEK BAR. External services configured on the server
 * (src/MarkerProviders.cs) supply points for an item; GET /JellyfinPlus/Markers/{itemId} returns them
 * merged and normalised. Each is a dot over the OSD position slider, styled by kind (unknown kinds
 * neutral), sized by value, titled "label · value unit"; a click seeks a few seconds before it
 * (model/timeline-markers.js).
 *
 * PREVIEW POPUP. Hover or focus a marker (touch: the first tap) and a popup above the seek bar shows
 * the label + value (textContent only) and, when there is something to show, a strip of frames: the
 * marker's own frame, highlighted, with two either side 5 s apart. Clicking a frame seeks to its time.
 * Frames come from the item's stock Jellyfin trickplay when it has any (Videos/{id}/Trickplay/...),
 * otherwise from the plugin's moment sheets (GET /JellyfinPlus/Markers/{id}/Moments, generated on the
 * server on first ask, src/MomentSheets.cs). Prewarmed once per item when its markers are drawn
 * (generation driven to ready, the strips' sheets loaded), cached per item; no preview source, or a
 * sheet that fails to load, leaves the label alone.
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
var THUMB_W = 128;              // px, each frame in the strip
var HIDE_DELAY_MS = 250;        // grace to move from the marker into the popup
var PENDING_RETRY_MS = 3000;    // moment sheets still generating: ask again while the popup is open
var PENDING_TRIES = 20;

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
    L + ' .jp-marker:hover, ' + L + ' .jp-marker:focus-visible { outline: 2px solid #fff; }',
    L + ' .jp-mpop { position: absolute; bottom: 100%; margin-bottom: 14px; pointer-events: auto; z-index: 3;'
        + ' background: rgba(20,20,20,0.92); border-radius: 6px; padding: 6px; box-shadow: 0 2px 10px rgba(0,0,0,0.6);'
        + ' color: #fff; font-size: 0.85em; line-height: 1.3; white-space: nowrap; }',
    L + ' .jp-mpop-strip { display: flex; gap: 4px; margin-bottom: 4px; }',
    L + ' .jp-mpop-strip:empty { display: none; }',
    L + ' .jp-mthumb { width: ' + THUMB_W + 'px; padding: 0; margin: 0; border: 2px solid transparent; border-radius: 3px;'
        + ' background-color: #000; background-repeat: no-repeat; cursor: pointer; opacity: 0.75; flex: none; }',
    L + ' .jp-mthumb[data-centre] { border-color: #ffc107; opacity: 1; }',
    L + ' .jp-mthumb:hover, ' + L + ' .jp-mthumb:focus-visible { opacity: 1; outline: 1px solid #fff; }',
    L + ' .jp-mpop-label { max-width: ' + (THUMB_W * 5 + 16) + 'px; overflow: hidden; text-overflow: ellipsis; text-align: center; }'
].join('\n');

var enabled = null;     // null: not asked yet / asking; true/false: the server's answer
var momentsOn = false;  // the server makes moment sheets (setting MarkerMoments)
var enabledAt = 0;
var itemKey = null;     // the item the current answer belongs to
var body = null;        // its answer (null: none / not yet)
var drawn = null;       // { slider, duration } last drawn, to redraw only on change
var preview = null;     // { key, promise } the item's preview source, loaded on first hover
var pop = null;         // { el, button, marker, hideTimer, pendingTimer, tries } the open popup
var touchArmed = null;  // the marker button a first tap opened the popup for

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
    hidePopup();
    Array.prototype.forEach.call(document.querySelectorAll(L), function (l) { l.remove(); });
    drawn = null;
}

function get(path, params) {
    var c = client();
    if (!c) { return Promise.reject(new Error('no ApiClient')); }
    return Promise.resolve(c.ajax({ type: 'GET', url: c.getUrl(path, params), dataType: 'json' }));
}

/* True once the server said providers exist; asks at most once per ENABLED_RETRY_MS after a failure. */
function markersEnabled() {
    if (enabled !== null || Date.now() - enabledAt < ENABLED_RETRY_MS) { return enabled === true; }
    enabledAt = Date.now();
    enabled = false; // while asking
    get('JellyfinPlus/Markers/Enabled').then(function (r) {
        enabled = !!(r && r.enabled === true);
        momentsOn = !!(r && r.moments === true);
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

/* Seek to a marker (a little before it) or, exact, to a frame's own time. */
function seekTo(seconds, exact) {
    try {
        var pm = manager();
        var pl = pm && activePlayer(pm);
        if (!pm || !pl || typeof pm.seek !== 'function') { return; }
        var to = exact ? Math.max(0, seconds) : seekTarget(seconds);
        var r = pm.seek(Math.round(to * TICKS_PER_SECOND), pl);
        if (r && typeof r.catch === 'function') { r.catch(function (e) { log('timeline markers: seek rejected', e); }); }
    } catch (err) {
        log('timeline markers: seek failed', err);
    }
}

function stop(e) { e.stopPropagation(); }

function hostOf(slider) {
    return slider.closest('.mdl-slider-container') || slider.parentNode;
}

/* ---- preview source: stock trickplay, else moment sheets, else nothing ---------------------- */

/*
 * Resolves to { kind:'stock', info, mediaSourceId } | { kind:'moment', manifest } | null.
 * Stock wins whenever the item has trickplay; the server answers moments "none" in that case too.
 */
function previewSource(key) {
    if (preview && preview.key === key) { return preview.promise; }
    var promise = stockSource(key).then(function (s) {
        return s || null;
    }, function () { return null; });
    preview = { key: key, promise: promise, moment: null };
    return promise;
}

function stockSource(key) {
    var pm = manager();
    var pl = pm && activePlayer(pm);
    var item = pl && typeof pm.currentItem === 'function' ? pm.currentItem(pl) : null;
    var src = null;
    try { src = pl && typeof pm.currentMediaSource === 'function' ? pm.currentMediaSource(pl) : null; } catch (e) { src = null; }
    var msid = src && src.Id ? String(src.Id) : null;
    var fromItem = function (it) {
        var t = it && pickTrickplay(it.Trickplay, msid);
        return t ? { kind: 'stock', info: t.info, mediaSourceId: t.mediaSourceId } : null;
    };
    if (item && String(item.Id) === key && item.Trickplay) {
        return Promise.resolve(fromItem(item));
    }
    var c = client();
    var uid = c && typeof c.getCurrentUserId === 'function' ? c.getCurrentUserId() : null;
    var params = { Fields: 'Trickplay' };
    if (uid) { params.userId = uid; }
    return get('Items/' + encodeURIComponent(key), params).then(fromItem, function () { return null; });
}

/* The moment manifest; 'pending' is asked again by the open popup, never cached. */
function momentManifest(key) {
    if (!momentsOn) { return Promise.resolve(null); }
    if (preview && preview.key === key && preview.moment) { return Promise.resolve(preview.moment); }
    return get('JellyfinPlus/Markers/' + encodeURIComponent(key) + '/Moments').then(function (m) {
        if (!m || typeof m !== 'object') { return null; }
        if (m.status === 'ready' || m.status === 'none') {
            if (preview && preview.key === key) { preview.moment = m; }
        }
        return m;
    }, function () { return null; });
}

function sheetUrl(key, src, sheet) {
    var c = client();
    var token = c && typeof c.accessToken === 'function' ? c.accessToken() : '';
    if (src.kind === 'stock') {
        return c.getUrl('Videos/' + encodeURIComponent(key) + '/Trickplay/' + src.info.Width + '/' + sheet + '.jpg',
            { ApiKey: token, MediaSourceId: src.mediaSourceId });
    }
    return c.getUrl('JellyfinPlus/Markers/' + encodeURIComponent(key) + '/Moments/' + sheet + '.jpg',
        { ApiKey: token, v: src.manifest.version });
}

/* ---- popup ---------------------------------------------------------------------------------- */

function hidePopup() {
    if (!pop) { return; }
    clearTimeout(pop.hideTimer);
    clearTimeout(pop.pendingTimer);
    pop.el.remove();
    pop = null;
    touchArmed = null;
}

function scheduleHide() {
    if (!pop) { return; }
    clearTimeout(pop.hideTimer);
    pop.hideTimer = setTimeout(hidePopup, HIDE_DELAY_MS);
}

function keepOpen() {
    if (pop) { clearTimeout(pop.hideTimer); }
}

function showPopup(button, marker, layer) {
    if (pop && pop.button === button) { keepOpen(); return; }
    hidePopup();
    var el = document.createElement('div');
    el.className = 'jp-mpop';
    var strip = document.createElement('div');
    strip.className = 'jp-mpop-strip';
    var label = document.createElement('div');
    label.className = 'jp-mpop-label';
    label.textContent = marker.label || ''; // untrusted text: textContent only
    if (!marker.label) {
        label.style.display = 'none';
        el.style.display = 'none'; // nothing to say until frames arrive
    }
    el.appendChild(strip);
    el.appendChild(label);
    ['pointerdown', 'mousedown', 'click'].forEach(function (t) { el.addEventListener(t, stop); });
    el.addEventListener('touchstart', stop, { passive: true });
    el.addEventListener('pointerenter', keepOpen);
    el.addEventListener('pointerleave', scheduleHide);
    layer.appendChild(el);
    pop = { el: el, button: button, marker: marker, hideTimer: 0, pendingTimer: 0, tries: 0 };
    place();
    loadStrip(pop, strip);
}

/* Centre over the marker, kept inside the layer. */
function place() {
    if (!pop) { return; }
    var layer = pop.el.parentNode;
    var lw = layer.getBoundingClientRect().width;
    var pw = pop.el.getBoundingClientRect().width;
    var x = lw * pop.marker.pct / 100 - pw / 2;
    pop.el.style.left = Math.max(0, Math.min(x, Math.max(0, lw - pw))) + 'px';
}

function loadStrip(p, strip) {
    var key = itemKey;
    var times = stripTimes(p.marker.offset, drawn ? drawn.duration : 0);
    if (!key || !times.length) { return; }
    previewSource(key).then(function (src) {
        if (src) { return src; }
        return momentManifest(key).then(function (m) {
            if (m && m.status === 'pending' && pop === p && p.tries++ < PENDING_TRIES) {
                p.pendingTimer = setTimeout(function () { if (pop === p) { loadStrip(p, strip); } }, PENDING_RETRY_MS);
            }
            return m && m.status === 'ready' ? { kind: 'moment', manifest: m } : null;
        });
    }).then(function (src) {
        if (!src || pop !== p || itemKey !== key) { return; }
        renderStrip(p, strip, key, src, times);
    }).catch(function (err) {
        log('timeline markers: preview unavailable', err);
    });
}

function renderStrip(p, strip, key, src, times) {
    var cols = src.kind === 'stock' ? src.info.TileWidth : src.manifest.tileWidth;
    var rows = src.kind === 'stock' ? src.info.TileHeight : src.manifest.tileHeight;
    var sheets = sheetCache(key);
    strip.textContent = '';
    times.forEach(function (ft) {
        var tile = src.kind === 'stock' ? stockTile(src.info, ft.t) : momentTile(src.manifest, ft.t);
        if (!tile) { return; }
        var s = THUMB_W / tile.w;
        var url = sheetUrl(key, src, tile.sheet);
        var b = document.createElement('button');
        b.type = 'button';
        b.className = 'jp-mthumb';
        if (ft.centre) { b.setAttribute('data-centre', ''); }
        b.setAttribute('aria-label', 'Seek to ' + Math.round(ft.t) + ' s');
        b.style.height = Math.round(tile.h * s) + 'px';
        b.style.backgroundSize = (cols * tile.w * s) + 'px ' + (rows * tile.h * s) + 'px';
        b.style.backgroundPosition = (-tile.x * s) + 'px ' + (-tile.y * s) + 'px';
        b.style.visibility = 'hidden';
        b.addEventListener('click', function (e) {
            e.stopPropagation();
            e.preventDefault();
            seekTo(tile.t != null ? tile.t : ft.t, true);
        });
        strip.appendChild(b);
        // A failed sheet hides only its own frames; a frame shows once its sheet has loaded.
        (sheets[url] = sheets[url] || loadImage(url)).then(function () {
            b.style.backgroundImage = 'url("' + url.replace(/["\\]/g, '\\$&') + '")';
            b.style.visibility = '';
            if (pop === p && p.el.style.display === 'none') {
                p.el.style.display = '';
                place();
            }
        }, function () {
            b.remove();
            if (pop === p) { place(); }
        });
    });
    if (pop === p) { place(); }
}

/* Sheet image loads for the current item, shared by the prewarm and every popup. */
function sheetCache(key) {
    if (!preview || preview.key !== key) { return {}; }
    preview.sheets = preview.sheets || {};
    return preview.sheets;
}

/*
 * PREWARM. Once per item, as soon as its markers are drawn (playback start): resolve the preview
 * source, drive moment-sheet generation to "ready" in the background, and load every sheet the marker
 * strips will use, so the first hover shows frames at once. Stops when the item changes.
 */
function prewarm(key, markers, duration) {
    if (!preview || preview.key !== key) { previewSource(key); }
    if (preview.warmed) { return; }
    preview.warmed = true;
    var tries = 0;
    var resolve = function () {
        return previewSource(key).then(function (src) {
            if (src) { return src; }
            return momentManifest(key).then(function (m) {
                if (m && m.status === 'pending' && itemKey === key && tries++ < PENDING_TRIES) {
                    return new Promise(function (r) { setTimeout(r, PENDING_RETRY_MS); }).then(function () {
                        return itemKey === key ? resolve() : null;
                    });
                }
                return m && m.status === 'ready' ? { kind: 'moment', manifest: m } : null;
            });
        });
    };
    resolve().then(function (src) {
        if (!src || itemKey !== key) { return; }
        var sheets = sheetCache(key);
        var n = 0;
        markers.forEach(function (m) {
            stripTimes(m.offset, duration).forEach(function (ft) {
                var tile = src.kind === 'stock' ? stockTile(src.info, ft.t) : momentTile(src.manifest, ft.t);
                if (!tile) { return; }
                var url = sheetUrl(key, src, tile.sheet);
                if (!sheets[url]) {
                    n++;
                    sheets[url] = loadImage(url);
                    sheets[url].catch(function () {}); // a failed sheet just hides its frames later
                }
            });
        });
        log('timeline markers: prewarmed ' + n + ' ' + src.kind + ' sheet(s)');
    }).catch(function (err) {
        log('timeline markers: prewarm failed', err);
    });
}

function loadImage(url) {
    return new Promise(function (resolve, reject) {
        var img = new Image();
        img.onload = resolve;
        img.onerror = reject;
        img.src = url;
    });
}

/* ---- drawing -------------------------------------------------------------------------------- */

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
    if (!hr.width) { return; } // OSD not laid out yet: drawn stays null, the next tick tries again
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
            b.title = m.label; // untrusted text: attributes only, never innerHTML
            b.setAttribute('aria-label', m.label);
        } else {
            b.setAttribute('aria-label', 'Marker');
        }
        var touch = false;
        b.addEventListener('pointerdown', function (e) { touch = e.pointerType === 'touch'; stop(e); });
        b.addEventListener('mousedown', stop);
        b.addEventListener('touchstart', function (e) { touch = true; stop(e); }, { passive: true });
        b.addEventListener('pointerenter', function (e) { if (e.pointerType !== 'touch') { showPopup(b, m, layer); } });
        b.addEventListener('pointerleave', function (e) { if (e.pointerType !== 'touch') { scheduleHide(); } });
        b.addEventListener('focus', function () { showPopup(b, m, layer); });
        b.addEventListener('blur', scheduleHide);
        b.addEventListener('contextmenu', function (e) { if (touch) { e.preventDefault(); } }); // tap-and-hold
        b.addEventListener('click', function (e) {
            e.stopPropagation();
            e.preventDefault();
            // Touch: the first tap opens the preview, a second tap on the same marker seeks.
            if (touch && touchArmed !== b) {
                showPopup(b, m, layer);
                touchArmed = b;
                return;
            }
            hidePopup();
            seekTo(m.offset);
        });
        layer.appendChild(b);
    });

    host.appendChild(layer);
    drawn = { slider: slider, duration: durationSeconds };
    if (itemKey) { prewarm(itemKey, markers, durationSeconds); }
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
            preview = null;
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
