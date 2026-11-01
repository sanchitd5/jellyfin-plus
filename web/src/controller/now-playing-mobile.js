import { log } from '../lib/log.js';
import { centeredScrollTop, rowVisible } from '../model/now-playing-mobile.js';

/*
 * NOW PLAYING ON A PHONE: the queue / remote-control page (#/queue, `#nowPlayingPage`, legacy
 * controller `playback/queue/index` rendering `components/remotecontrol/remotecontrol.js`). This
 * is the screen the music bar's "now playing" tap and the remote-control button open; the in-video
 * OSD (`#videoOsdPage`) is a different screen and has its own module (view/portrait-osd.js).
 *
 * Almost all CSS, scoped `html:not(.jp-theme-off):not(.layout-tv)` (the off switch and TV get
 * stock), sizes under `(pointer: coarse)`, layout under the same media queries stock uses so the
 * two never disagree about which layout is live. A missing seam is an inert rule.
 *
 * What it fixes, checked against the 12.3 sources (not on a device):
 *
 *  1. THE PLUGIN'S OWN BOTTOM BAR COVERS THE PAGE'S FIXED CONTROLS. Stock mobile pins
 *     `.playlistSectionButton` (queue toggle, volume, More) to `bottom: 0`, and the open queue /
 *     context menu to `bottom: 4.2em`. view/bottom-nav.js shows on #/queue (it is a "More" route),
 *     so its 56px + safe area sat on top of them. While `html.jp-nav-on`, the fixed strip and the
 *     overlays move up by `--jp-nav-h`, and the portrait column's height budget shrinks to match.
 *  2. TAP TARGETS. The transport buttons are stock paper-icon-button-light (the theme already gives
 *     those 44px on touch tiers); the queue's drag handle and the remove button were not covered,
 *     and the strip's buttons shrink under `font-size: larger` rules. All get --jp-tap.
 *  3. THE SEEK AND VOLUME SLIDERS. Stock's thumb is 1.08em in a 1.25em container: a 20px strip to
 *     hit on a phone, where this slider is the only way to seek. Container 44px, thumb 20px, same
 *     treatment the video OSD already has.
 *  4. TITLE TRUNCATION. Portrait truncates album and artist to one line but not the song / episode
 *     name, so a long name wrapped to four lines and ate the artwork. The name (and the h2 title
 *     shown for video) clamp at two lines; album and artist keep stock's one line.
 *  5. LANDSCAPE PHONE. Stock's phone layout is portrait-only; landscape gets the desktop row with
 *     5em of top padding, a 16% artwork column and the queue overlay starting 12.2em down, which
 *     on a 375px-tall screen left ~110px for the queue. Top padding drops to the header's height,
 *     the artwork is capped to the room between header and strip, the overlay starts under the
 *     header, and the content respects the side safe areas.
 *  6. THE QUEUE SCROLLS TO THE PLAYING TRACK when opened (the one piece of JS): stock opens a long
 *     queue at the top. The scroller also stops chaining its overscroll to the page.
 *
 * Fail soft: no page or no toggle button means the listener does nothing; CSS without a match
 * styles nothing.
 */

var STYLE_ID = 'jellyfinPlusNowPlayingStyle';
var P = 'html:not(.jp-theme-off):not(.layout-tv)';
var PAGE = ' .nowPlayingPage';
var NAV = 'html.jp-nav-on.layout-mobile:not(.jp-theme-off):not(.layout-tv)' + PAGE;
// Stock's own breakpoints (remotecontrol.scss): the phone layout and the "narrow" layout.
var PORTRAIT = '@media all and (orientation: portrait) and (max-width: 43em)';
var LANDSCAPE = '@media all and (orientation: landscape) and (max-height: 500px) and (pointer: coarse)';
var STRIP = '4.2em';

var CSS = [
    // 1. room for the plugin's bottom bar (stock pads the strip with the safe area; the bar already does).
    NAV + ' .playlistSectionButton { bottom: var(--jp-nav-h, 0px); padding-bottom: 0; }',
    NAV + ' .playlistSection .playlist, ' + NAV + ' .playlistSection .contextMenu {'
        + ' bottom: calc(' + STRIP + ' + var(--jp-nav-h, 0px)); }',
    NAV + ' .remoteControlSection { padding-bottom: calc(' + STRIP + ' + var(--jp-nav-h, 0px)); }',
    PORTRAIT + ' {',
    '  ' + NAV + ' .nowPlayingInfoContainer { height: calc(100% - ' + STRIP + ' - var(--jp-nav-h, 0px)); }',
    '}',

    // 2 + 3. sizes, touch only
    '@media (pointer: coarse) {',
    '  ' + P + PAGE + ' .paper-icon-button-light, ' + P + PAGE + ' .playlistSectionButton button,',
    '  ' + P + PAGE + ' .playlist .listViewDragHandle { min-width: var(--jp-tap, 44px); min-height: var(--jp-tap, 44px); }',
    '  ' + P + PAGE + ' .playlist .listItem { min-height: var(--jp-tap, 44px); }',
    '  ' + P + PAGE + ' .nowPlayingInfoButtons { flex-wrap: nowrap; min-width: 0; }',
    '  ' + P + PAGE + ' .sliderContainer .mdl-slider-container { height: var(--jp-tap, 44px); }',
    '  ' + P + PAGE + ' .sliderContainer .mdl-slider-container .mdl-slider { height: 100%; }',
    '  ' + P + PAGE + ' .sliderContainer .mdl-slider-background-flex-container { padding: 0 10px; }',
    '  ' + P + PAGE + ' .sliderContainer .mdl-slider::-webkit-slider-thumb { width: 20px; height: 20px; }',
    '  ' + P + PAGE + ' .sliderContainer .mdl-slider::-moz-range-thumb { width: 20px; height: 20px; }',
    '  ' + P + PAGE + ' .positionTime, ' + P + PAGE + ' .runtime {'
        + ' font-size: var(--jp-font-small, 0.9em); font-variant-numeric: tabular-nums; }',
    '}',

    // 4. the name clamps at two lines (stock: album/artist one line, name unbounded)
    P + PAGE + ' .nowPlayingSongName:not(.hide), ' + P + PAGE + ' .nowPlayingPageTitle:not(.hide) {'
        + ' display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; overflow: hidden;'
        + ' overflow-wrap: anywhere; line-height: 1.25; }',

    // 6. the queue scroller keeps its scroll to itself
    P + PAGE + ' .playlistSection .playlist { overscroll-behavior: contain; -webkit-overflow-scrolling: touch;'
        + ' overflow-x: hidden; overflow-y: auto; scrollbar-width: thin; }',

    // 5. landscape phone: stock gives it the desktop row
    LANDSCAPE + ' {',
    // header is .headerTop (0.54em padding + a 2.54em button row); 4em clears it with no gap worth a strip
    '  ' + P + PAGE + ' { padding-top: 4em !important; }',
    '  ' + P + PAGE + ' .remoteControlContent { padding-left: max(3.3%, env(safe-area-inset-left, 0px)) !important;'
        + ' padding-right: max(3.3%, env(safe-area-inset-right, 0px)) !important; }',
    '  ' + P + PAGE + ' .nowPlayingPageImageContainer { width: auto; max-width: 34%; }',
    '  ' + P + PAGE + ' .nowPlayingPageImage { width: auto; max-width: 100%;'
        + ' max-height: calc(100vh - 4em - ' + STRIP + ' - 1em - var(--jp-nav-h, 0px)); }',
    '  ' + P + PAGE + ' .nowPlayingSecondaryButtons { flex-wrap: nowrap; }',
    '  html.layout-mobile:not(.jp-theme-off):not(.layout-tv)' + PAGE + ' .playlistSection .playlist,',
    '  html.layout-mobile:not(.jp-theme-off):not(.layout-tv)' + PAGE + ' .playlistSection .contextMenu { top: 4em; }',
    '}'
].join('\n');

function ensureStyle() {
    if (document.getElementById(STYLE_ID)) { return; }
    var style = document.createElement('style');
    style.id = STYLE_ID;
    style.textContent = CSS;
    (document.head || document.documentElement).appendChild(style);
}

function reducedMotion() {
    try { return window.matchMedia('(prefers-reduced-motion: reduce)').matches; } catch (e) { return false; }
}

/* 6. Centre the playing row in the queue once it is open. Measured, never guessed from indexes. */
/*
 * Returns 'done' (centred, already visible, or nothing to do), 'wait' (open but the playing row is
 * not rendered yet) or 'closed'. The scroller is `.playlist` itself (stock: absolute, overflow
 * scroll, remotecontrol.scss:170-181), so element scrollTo never moves the page; the row is
 * measured with getBoundingClientRect against it, which sidesteps offsetParent entirely.
 * Stock marks the playing row by adding `playlistIndexIndicatorImage` to its `.listItemImage`
 * inside loadPlaylist (remotecontrol.js:490-499), in the same tick as the innerHTML that builds
 * the rows, and rebuilds the whole list on every playlist change / playback start.
 */
function scrollToCurrent(page) {
    var list = page.querySelector('.playlistSection .playlist');
    if (!list || list.classList.contains('hide') || list.getClientRects().length === 0) { return 'closed'; }
    var mark = list.querySelector('.playlistIndexIndicatorImage');
    var row = mark && mark.closest('.listItem');
    if (!row) { return 'wait'; }
    var lr = list.getBoundingClientRect();
    var rr = row.getBoundingClientRect();
    var top = rr.top - lr.top;
    if (rowVisible(top, rr.height, list.clientHeight)) { return 'done'; }
    var target = centeredScrollTop(list.scrollTop, top, list.clientHeight, rr.height);
    if (!reducedMotion() && typeof list.scrollTo === 'function') {
        list.scrollTo({ top: target, behavior: 'smooth' });
    } else {
        list.scrollTop = target;
    }
    return 'done';
}

var WAIT_MS = 2000;
var pending = null; // { obs, timer } for the one in-flight wait; a new open replaces it

function stopWaiting() {
    if (!pending) { return; }
    try { pending.obs.disconnect(); } catch (e) { /* gone */ }
    clearTimeout(pending.timer);
    pending = null;
}

/* Try now; if the rows are not there yet, watch the list for them (<= WAIT_MS, then give up). */
function scrollWhenReady(page) {
    stopWaiting();
    var state = scrollToCurrent(page);
    if (state !== 'wait') { return; }
    var list = page.querySelector('.playlistSection .playlist');
    if (!list || typeof MutationObserver !== 'function') { return; }
    var obs = new MutationObserver(function () {
        try {
            if (scrollToCurrent(page) !== 'wait') { stopWaiting(); }
        } catch (err) { stopWaiting(); log('now playing: queue scroll failed', err); }
    });
    obs.observe(list, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
    pending = { obs: obs, timer: setTimeout(stopWaiting, WAIT_MS) };
}

function onClick(e) {
    try {
        var html = document.documentElement;
        if (html.classList.contains('layout-tv') || html.classList.contains('jp-theme-off')) { return; }
        var t = e.target && e.target.closest && e.target.closest('.btnTogglePlaylist');
        var page = t && t.closest('.nowPlayingPage');
        if (!page) { return; }
        // Capture phase sees the state BEFORE stock's toggle (remotecontrol.js:813): hidden now = opening.
        // Closing (or any other state) cancels a pending wait and does nothing.
        var list = page.querySelector('.playlistSection .playlist');
        if (!list || !list.classList.contains('hide')) { stopWaiting(); return; }
        // Stock's own handler (bubble phase) removes .hide after this; one frame later it has laid out.
        requestAnimationFrame(function () {
            try { scrollWhenReady(page); } catch (err) { log('now playing: queue scroll failed', err); }
        });
    } catch (err) {
        log('now playing: click failed', err);
    }
}

export function installNowPlayingMobile() {
    ensureStyle();
    // Same function reference + capture flag: a second install is a no-op, listeners cannot stack.
    document.addEventListener('click', onClick, true);
    log('now playing mobile: installed');
}
