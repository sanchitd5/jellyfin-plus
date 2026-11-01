/*
 * PURE SUMMARY OF THE DETAILS PAGE'S TRACK SELECTS (view/detail-tracks.js draws it).
 *
 * Input is one entry per stock .selectContainer in DOM order: { label, text, shown } where text is
 * the selected <option>'s text and shown is "the container does not carry .hide". Output is the one
 * line the "Watch options" row shows. No DOM, no storage: unit-tested by detail-tracks.test.mjs.
 */

export var MAX_LEN = 28;
export var EMPTY = 'Default';
export var SEP = ' · ';

/* Trim; a text longer than MAX_LEN is cut at the last space before MAX_LEN (hard cut if none) and gets an ellipsis. */
export function clip(text) {
    var t = String(text == null ? '' : text).trim();
    if (t.length <= MAX_LEN) {
        return t;
    }

    var cut = t.lastIndexOf(' ', MAX_LEN - 1);
    return t.slice(0, cut > 0 ? cut : MAX_LEN).trimEnd() + '…';
}

export function trackSummary(entries) {
    var parts = (Array.isArray(entries) ? entries : [])
        .filter(function (e) { return e && e.shown && e.text && String(e.text).trim(); })
        .map(function (e) { return clip(e.text); });

    return parts.length ? parts.join(SEP) : EMPTY;
}
