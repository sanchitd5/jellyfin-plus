import { log } from '../lib/log.js';
import { watchDetail } from '../controller/detail-page.js';
import { decode, posterHash, headerHash } from '../model/blurhash.js';

/*
 * BLURHASH PLACEHOLDER ON THE DETAILS PAGE.
 *
 * The details header (#itemBackdrop, phone) and poster (.detailImageContainer .cardImageContainer)
 * are filled by jellyfin-web's lazy loader: the element gets `lazy-hidden` (opacity 0) until the
 * picture has loaded, then fades in. The header has no blurhash in stock and the poster's is
 * drawn only when the user setting is on, so the viewer sees an empty block for the 0.4-2 s a cold
 * resize takes.
 *
 * Here, once the shared details watcher has the item DTO (ImageBlurHashes), a small canvas
 * (decoded at 32x32, scaled by CSS) is inserted as the element's PREVIOUS SIBLING, the same place
 * stock puts its own blurhash canvas. It is positioned over the element's box and sits earlier in
 * the DOM, so the real image (a later positioned sibling) always paints on top of it. When the
 * element loses `lazy-hidden` the canvas fades out and is removed; a timeout removes it anyway.
 *
 * Painted only while the element is still waiting (`lazy-hidden`, no stock blurhash canvas, has a
 * box). Skipped when the item has no hash, the decode fails, or the image already arrived. Never
 * a request: the hash comes from the DTO detail-page.js already fetched. Any failure -> stock.
 */

var MARK = 'data-jp-blurhash';
var SIZE = 32;
var GIVE_UP_MS = 12000;
var FADE_MS = 200;

function drop(canvas) {
    if (canvas.__jpDone) {
        return;
    }

    canvas.__jpDone = true;
    try {
        canvas.__jpObserver.disconnect();
        clearTimeout(canvas.__jpTimer);
        canvas.style.transition = 'opacity ' + FADE_MS + 'ms ease-out';
        canvas.style.opacity = '0';
        setTimeout(function () {
            if (canvas.parentNode) {
                canvas.parentNode.removeChild(canvas);
            }
        }, FADE_MS + 50);
    } catch (err) {
        if (canvas.parentNode) {
            canvas.parentNode.removeChild(canvas);
        }
    }
}

function clear(page) {
    Array.prototype.forEach.call(page.querySelectorAll('canvas[' + MARK + ']'), function (c) {
        c.__jpDone = false;
        try {
            c.__jpObserver.disconnect();
            clearTimeout(c.__jpTimer);
        } catch (err) { /* already gone */ }

        if (c.parentNode) {
            c.parentNode.removeChild(c);
        }
    });
}

function paint(elem, hash, fillParent) {
    if (!elem || !hash || !elem.classList.contains('lazy-hidden') || elem.classList.contains('blurhashed')) {
        return;
    }

    if (!elem.parentNode || elem.offsetWidth === 0 || elem.offsetHeight === 0) {
        return;
    }

    var px = decode(hash, SIZE, SIZE);
    if (!px) {
        return;
    }

    var canvas = document.createElement('canvas');
    canvas.width = SIZE;
    canvas.height = SIZE;
    canvas.setAttribute(MARK, '1');
    canvas.setAttribute('aria-hidden', 'true');
    var ctx = canvas.getContext('2d');
    if (!ctx) {
        return;
    }

    var img = ctx.createImageData(SIZE, SIZE);
    img.data.set(px);
    ctx.putImageData(img, 0, 0);

    var s = canvas.style;
    s.position = 'absolute';
    s.pointerEvents = 'none';
    if (fillParent) {
        s.top = '0';
        s.left = '0';
        s.width = '100%';
        s.height = '100%';
    } else {
        s.top = elem.offsetTop + 'px';
        s.left = elem.offsetLeft + 'px';
        s.width = elem.offsetWidth + 'px';
        s.height = elem.offsetHeight + 'px';
    }

    elem.parentNode.insertBefore(canvas, elem);

    canvas.__jpObserver = new MutationObserver(function () {
        if (!elem.classList.contains('lazy-hidden')) {
            drop(canvas);
        }
    });
    canvas.__jpObserver.observe(elem, { attributes: true, attributeFilter: ['class'] });
    canvas.__jpTimer = setTimeout(function () {
        drop(canvas);
    }, GIVE_UP_MS);
}

function onItem(page, item) {
    clear(page);
    paint(page.querySelector('#itemBackdrop'), headerHash(item), false);
    paint(page.querySelector('.detailImageContainer .cardImageContainer'), posterHash(item), true);
}

export function installDetailBlurhash() {
    watchDetail({
        start: function (page) {
            clear(page);
        },
        item: onItem
    });
    log('detail blurhash: installed');
}
