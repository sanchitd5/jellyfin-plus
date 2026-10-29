import { log } from '../lib/log.js';
import { el } from '../lib/dom.js';
import { watchDetail } from '../controller/detail-page.js';

/*
 * TAP-TO-ZOOM ON THE ITEM DETAILS POSTER.
 *
 * jellyfin-web 12.1's details page (apps/legacy/controllers/itemDetails) draws the poster as a
 * card inside .detailImageContainer: a .cardImageContainer whose image is lazy-loaded from
 * data-src into an inline background-image, at a quarter of the window width. Nothing happens
 * when it is tapped. This makes a tap open the image full screen, at FULL resolution:
 *
 *  - the image URL is read from that element (data-src, else the background-image) - found by
 *    shape in the DOM, never through a global ApiClient - and the sizing parameters cardBuilder
 *    adds (fillWidth/fillHeight/maxWidth/...) are dropped so the server sends the original;
 *  - the best image already loaded shows at once: a screen-sized copy (prefetched at idle once
 *    the poster is on screen, same URL as the later open so it is a cache hit) else the thumbnail;
 *  - the ORIGINAL is fetched only when the user taps to actual size, decoded, then swapped in with
 *    the viewport centre kept;
 *  - tap the image to toggle fit-to-screen / actual size (panning by scroll or drag); pinch-zoom
 *    is the browser's own;
 *  - close with the X button, a tap on the dark backdrop, or Escape.
 *
 * One delegated click listener and one stylesheet: no observer, no per-page wiring. A zoom_in
 * badge is drawn on the poster by CSS alone so the option is visible, not just discoverable.
 */

var STYLE_ID = 'jellyfinPlusImageZoomStyle';
var OVERLAY_ID = 'jellyfinPlusImageZoom';
var POSTER = '#itemDetailPage .detailImageContainer .cardImageContainer';

// cardBuilder's sizing parameters. Removing them asks the server for the stored image.
var SIZE_PARAMS = /^(fillwidth|fillheight|maxwidth|maxheight|width|height|quality)$/i;

// An item with no primary image still gets the card, with data-src="" (seen live). Badge and
// cursor only where there is an image to open; the lazy loader removes data-src once it loads.
var HAS_IMAGE = ':not([data-src=""])';

var CSS = [
    POSTER + HAS_IMAGE + ' { cursor: zoom-in !important; }',
    '#itemDetailPage .detailImageContainer .cardScalable { position: relative; }',
    '#itemDetailPage .detailImageContainer .cardScalable:has(> .cardImageContainer' + HAS_IMAGE + ')::after {'
        + ' content: "zoom_in"; font-family: "Material Icons"; font-feature-settings: "liga";'
        + ' position: absolute; right: 0.4em; bottom: 0.4em; font-size: 1.4em; line-height: 1;'
        + ' padding: 0.25em; border-radius: 50%; color: #fff; background: rgba(0,0,0,0.55);'
        + ' pointer-events: none; }',
    '#' + OVERLAY_ID + ' { position: fixed; inset: 0; z-index: 100000; background: rgba(0,0,0,0.92);'
        + ' overflow: auto; overscroll-behavior: contain; display: flex; }',
    '#' + OVERLAY_ID + ' img { margin: auto; max-width: 100vw; max-height: 100vh; object-fit: contain;'
        + ' cursor: zoom-in; user-select: none; -webkit-user-drag: none; }',
    '#' + OVERLAY_ID + '.zoomed img { max-width: none; max-height: none; cursor: zoom-out; }',
    '#' + OVERLAY_ID + ' button { position: fixed; top: 0.6em; right: 0.6em; z-index: 1;'
        + ' width: 2.6em; height: 2.6em; border: 0; border-radius: 50%; cursor: pointer;'
        + ' color: #fff; background: rgba(0,0,0,0.6); font-family: "Material Icons";'
        + ' font-feature-settings: "liga"; font-size: 1.4em; line-height: 2.6em; padding: 0; }'
].join('\n');

function imageUrl(node) {
    var url = node.getAttribute('data-src');
    if (!url) {
        var bg = node.style && node.style.backgroundImage;
        var m = bg && /url\(["']?(.*?)["']?\)/.exec(bg);
        url = m ? m[1] : '';
    }

    return url || null;
}

export function fullSizeUrl(url) {
    try {
        var u = new URL(url, window.location.href);
        Array.from(u.searchParams.keys()).forEach(function (k) {
            if (SIZE_PARAMS.test(k)) {
                u.searchParams.delete(k);
            }
        });
        return u.toString();
    } catch (e) {
        return url;
    }
}

// Same snapping idea as jellyfin-web's dom.getScreenWidth(): fixed buckets, so the URL repeats.
var BUCKETS = [480, 720, 1280, 1440, 1920, 2560, 3840];

// Fit-to-screen view only: the overlay is the viewport, so size each axis to it. DPR is capped at
// 1.5 - a cold server resize grows with output pixels (~0.5 s at 1280, ~2 s at 3840), and the
// actual-size tap fetches the original anyway (served untouched, no resize).
var MAX_DPR = 1.5;

function bucketOf(px) {
    return BUCKETS.find(function (b) { return b >= px; }) || BUCKETS[BUCKETS.length - 1];
}

function screenSizedUrl(url) {
    try {
        var dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
        var w = bucketOf(Math.max(window.innerWidth || 0, 1) * dpr);
        var h = bucketOf(Math.max(window.innerHeight || 0, 1) * dpr);
        var u = new URL(fullSizeUrl(url));
        u.searchParams.set('maxHeight', String(h));
        u.searchParams.set('maxWidth', String(w));
        u.searchParams.set('quality', '90');
        return u.toString();
    } catch (e) {
        return null;
    }
}

// URL -> { done, img, promise }. Image objects are held so a load is not dropped mid-flight.
var screenLoads = new Map();

function loadImage(url, low) {
    var existing = screenLoads.get(url);
    if (existing) {
        return existing.promise;
    }

    var entry = { done: false, img: new Image() };
    entry.promise = new Promise(function (resolve) {
        entry.img.onload = function () { entry.done = true; resolve(true); };
        entry.img.onerror = function () { screenLoads.delete(url); resolve(false); };
    });
    if (low) {
        entry.img.fetchPriority = 'low';
    }

    entry.img.src = url;
    screenLoads.set(url, entry);
    return entry.promise;
}

function slowLink() {
    var c = navigator.connection;
    return !!c && (c.saveData || /(^|-)2g$/.test(c.effectiveType || ''));
}

function whenIdle(fn) {
    if (typeof window.requestIdleCallback === 'function') {
        window.requestIdleCallback(fn, { timeout: 3000 });
    } else {
        setTimeout(fn, 1000);
    }
}

var prefetchVisit = 0;

// The poster is lazy-loaded after the page appears: look a few times, then give up quietly.
function prefetchScreenSized(tries) {
    var visit = prefetchVisit;
    whenIdle(function () {
        if (visit !== prefetchVisit) {
            return;
        }

        var node = document.querySelector(POSTER + HAS_IMAGE);
        var url = node && imageUrl(node);
        var sized = url && screenSizedUrl(url);
        if (sized) {
            loadImage(sized, true);
        } else if (tries > 0) {
            setTimeout(function () { prefetchScreenSized(tries - 1); }, 400);
        }
    });
}

function close() {
    var overlay = document.getElementById(OVERLAY_ID);
    if (overlay) {
        overlay.remove();
    }

    document.removeEventListener('keydown', onKey, true);
}

function onKey(e) {
    if (e.key === 'Escape' || e.key === 'Backspace') {
        // Stopped here so jellyfin does not ALSO treat it as "back" and leave the page.
        e.preventDefault();
        e.stopPropagation();
        close();
    }
}

function toggleZoom(overlay, img, e) {
    var zoomingIn = !overlay.classList.contains('zoomed');
    // Where on the image was tapped, as a fraction, so actual size opens on that spot.
    var r = img.getBoundingClientRect();
    var fx = r.width ? (e.clientX - r.left) / r.width : 0.5;
    var fy = r.height ? (e.clientY - r.top) / r.height : 0.5;

    overlay.classList.toggle('zoomed', zoomingIn);
    if (zoomingIn) {
        overlay.scrollLeft = Math.max(0, img.naturalWidth * fx - overlay.clientWidth / 2);
        overlay.scrollTop = Math.max(0, img.naturalHeight * fy - overlay.clientHeight / 2);
        swapToOriginal(overlay, img);
    }
}

// Actual size was asked for: only now fetch the original, decode it off-screen, swap it in
// keeping the same point at the viewport centre. Any failure leaves the current image in place.
function swapToOriginal(overlay, img) {
    var full = overlay.__full;
    if (!full || img.src === full || overlay.__fullPending) {
        return;
    }

    overlay.__fullPending = true;
    var loader = new Image();
    loader.src = full;
    var ready = typeof loader.decode === 'function'
        ? loader.decode()
        : new Promise(function (res, rej) { loader.onload = res; loader.onerror = rej; });
    ready.then(function () {
        overlay.__fullPending = false;
        if (!img.isConnected) {
            return;
        }

        var w = img.clientWidth || 1;
        var h = img.clientHeight || 1;
        var cx = (overlay.scrollLeft + overlay.clientWidth / 2) / w;
        var cy = (overlay.scrollTop + overlay.clientHeight / 2) / h;
        img.src = full;
        if (overlay.classList.contains('zoomed')) {
            overlay.scrollLeft = Math.max(0, img.naturalWidth * cx - overlay.clientWidth / 2);
            overlay.scrollTop = Math.max(0, img.naturalHeight * cy - overlay.clientHeight / 2);
        }
    }, function (err) {
        overlay.__fullPending = false;
        log('image zoom: original failed to load', err);
    });
}

function open(thumbUrl) {
    close();

    var overlay = el('div');
    overlay.id = OVERLAY_ID;
    overlay.setAttribute('role', 'dialog');
    overlay.setAttribute('aria-label', 'Image');

    var img = el('img');
    img.alt = '';
    // Best already-loaded image first: the screen-sized copy if the idle prefetch finished.
    var sized = screenSizedUrl(thumbUrl);
    var sizedEntry = sized && screenLoads.get(sized);
    img.src = sizedEntry && sizedEntry.done ? sized : thumbUrl;

    var closeBtn = el('button', null, 'close');
    closeBtn.type = 'button';
    closeBtn.setAttribute('aria-label', 'Close');

    overlay.appendChild(img);
    overlay.appendChild(closeBtn);
    document.body.appendChild(overlay);

    // Not loaded yet: fetch the screen-sized copy and swap it in. The original waits for zoom-in.
    var full = fullSizeUrl(thumbUrl);
    overlay.__full = full !== thumbUrl ? full : null;
    if (sized && !(sizedEntry && sizedEntry.done)) {
        loadImage(sized, false).then(function (ok) {
            if (ok && img.isConnected && img.src !== full) {
                img.src = sized;
            }
        });
    }

    // Drag to pan while zoomed (mouse). Touch pans natively through the scrolling overlay.
    var drag = null;
    var moved = false;
    overlay.addEventListener('mousedown', function (e) {
        if (overlay.classList.contains('zoomed') && e.button === 0) {
            drag = { x: e.clientX, y: e.clientY, l: overlay.scrollLeft, t: overlay.scrollTop };
            moved = false;
            e.preventDefault();
            // On window, so releasing outside the overlay still ends the drag.
            window.addEventListener('mouseup', function () { drag = null; }, { once: true });
        }
    });
    overlay.addEventListener('mousemove', function (e) {
        if (drag) {
            moved = moved || Math.abs(e.clientX - drag.x) + Math.abs(e.clientY - drag.y) > 4;
            overlay.scrollLeft = drag.l - (e.clientX - drag.x);
            overlay.scrollTop = drag.t - (e.clientY - drag.y);
        }
    });
    overlay.addEventListener('click', function (e) {
        e.stopPropagation();
        if (e.target === img) {
            if (!moved) {
                toggleZoom(overlay, img, e);
            }

            moved = false;
            return;
        }

        close();
    });

    document.addEventListener('keydown', onKey, true);
    closeBtn.focus();
    log('image zoom opened: ' + full);
}

function onClick(e) {
    var node = e.target && e.target.closest && e.target.closest(POSTER);
    if (!node) {
        return;
    }

    var url = imageUrl(node);
    if (!url) {
        return;
    }

    e.preventDefault();
    e.stopPropagation();
    open(url);
}

export function installImageZoom() {
    try {
        if (!document.getElementById(STYLE_ID)) {
            var style = document.createElement('style');
            style.id = STYLE_ID;
            style.textContent = CSS;
            (document.head || document.documentElement).appendChild(style);
        }

        document.addEventListener('click', onClick, true);
        // Leaving the page (back button, link) must not leave the overlay behind.
        window.addEventListener('hashchange', close);
        window.addEventListener('popstate', close);
        // Screen-sized poster at idle on each details visit; never on a data-saving or 2g link.
        watchDetail({
            start: function () {
                prefetchVisit++;
                if (!slowLink()) {
                    prefetchScreenSized(10);
                }
            }
        });
        log('image zoom installed');
    } catch (err) {
        log('image zoom install failed', err);
    }
}
