/*
 * Pure helpers for the Now Playing (queue / remote control) page on phones. No DOM here; the
 * controller measures and this decides. Tested by now-playing-mobile.test.mjs (node).
 */

/*
 * The scrollTop that puts a row in the middle of its scroller, from the measurements the
 * controller takes with getBoundingClientRect: the scroller's current scrollTop and client
 * height, the row's top relative to the scroller's top (viewport coordinates, may be negative
 * when the row is scrolled above) and the row's height. Clamped at 0; the browser clamps the
 * far end itself. Non-finite input gives back the current scrollTop (no jump).
 */
export function centeredScrollTop(scrollTop, rowTopRelative, listHeight, rowHeight) {
    var args = [scrollTop, rowTopRelative, listHeight, rowHeight];
    for (var i = 0; i < args.length; i++) {
        if (typeof args[i] !== 'number' || !isFinite(args[i])) { return scrollTop; }
    }
    var target = scrollTop + rowTopRelative - (listHeight - rowHeight) / 2;
    return target < 0 ? 0 : Math.round(target);
}

/* True when the row is already fully inside the scroller: no scroll needed, nothing moves. */
export function rowVisible(rowTopRelative, rowHeight, listHeight) {
    return rowTopRelative >= 0 && rowTopRelative + rowHeight <= listHeight;
}
