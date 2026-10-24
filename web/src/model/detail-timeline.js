/*
 * A PERSON'S TIMELINE ON A CLIP'S DETAILS PAGE: THE PURE PART (controller/detail-timeline.js is the
 * DOM and network side).
 *
 * A person's videos are put in timeline order - the date the clip was made (PremiereDate), falling
 * back to when it was added (DateCreated) for a clip that has none - and the current clip is
 * located in that order. The section shows a window of its neighbours with the current clip in the
 * middle. Undated clips go last, in name order, so they never sit between two dated ones.
 */

import { normId } from './detail-nav.js';

// Clips shown on each side of the current one.
export var SIDE = 8;

/* Milliseconds for the clip's place on the timeline, or Infinity when it carries no date. */
export function timelineKey(item) {
    var t = item && Date.parse(item.PremiereDate || item.DateCreated || '');
    return typeof t === 'number' && isFinite(t) ? t : Infinity;
}

function nameOf(item) {
    return String((item && (item.SortName || item.Name)) || '').toLowerCase();
}

/* A copy of `items` in timeline order; ties by name, then id, so the order is the same on every visit. */
export function orderTimeline(items) {
    return (items || []).filter(function (it) {
        return it && it.Id;
    }).map(function (it) {
        return { it: it, k: timelineKey(it), n: nameOf(it) };
    }).sort(function (a, b) {
        if (a.k !== b.k) {
            return a.k < b.k ? -1 : 1;
        }

        if (a.n !== b.n) {
            return a.n < b.n ? -1 : 1;
        }

        return a.it.Id < b.it.Id ? -1 : (a.it.Id > b.it.Id ? 1 : 0);
    }).map(function (x) {
        return x.it;
    });
}

/*
 * { before, current, after, index, total } around `id` in the ordered list, at most `side` on each
 * side; null when `id` is not in the list or it has no neighbour at all.
 */
export function windowAround(ordered, id, side) {
    var n = side == null ? SIDE : side;
    var want = normId(id);
    var list = ordered || [];
    var at = -1;
    for (var i = 0; i < list.length; i++) {
        if (normId(list[i].Id) === want) {
            at = i;
            break;
        }
    }

    if (at === -1 || list.length < 2) {
        return null;
    }

    return {
        before: list.slice(Math.max(0, at - n), at),
        current: list[at],
        after: list.slice(at + 1, at + 1 + n),
        index: at,
        total: list.length
    };
}
