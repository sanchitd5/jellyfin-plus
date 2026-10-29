import { log } from '../lib/log.js';

/*
 * SHORTER BACKDROP FADES ON THE ITEM DETAILS PAGE.
 *
 * jellyfin-web fades the details backdrop in slowly: the desktop global backdrop
 * (components/backdrop, class .backdropImageFadeIn) takes 800ms, the narrow/mobile header
 * (#itemBackdrop, class .lazy-image-fadein, set by imageLoader) 500ms. Once the image is
 * cached that wait is pure delay. Only the duration is overridden, only while a visible
 * #itemDetailPage exists; every other page keeps the stock fade. CSS only: no observer.
 */

var STYLE_ID = 'jellyfinPlusDetailFadeStyle';
var ON = 'body:has(#itemDetailPage:not(.hide))';
var FAST = '120ms';

var CSS = [
    ON + ' .backdropImageFadeIn { animation-duration: ' + FAST + ' !important;'
        + ' transition-duration: ' + FAST + ' !important; }',
    '#itemDetailPage #itemBackdrop.lazy-image-fadein { animation-duration: ' + FAST + ' !important;'
        + ' transition-duration: ' + FAST + ' !important; }'
].join('\n');

export function installDetailFade() {
    try {
        if (!document.getElementById(STYLE_ID)) {
            var style = document.createElement('style');
            style.id = STYLE_ID;
            style.textContent = CSS;
            (document.head || document.documentElement).appendChild(style);
        }

        log('detail fade installed');
    } catch (err) {
        log('detail fade install failed', err);
    }
}
