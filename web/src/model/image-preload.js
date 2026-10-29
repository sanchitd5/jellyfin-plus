/*
 * Image look-ahead for card grids and rows (pure logic; the DOM side is controller/image-preload.js).
 *
 * Stock jellyfin-web (components/lazyLoader/lazyLoaderIntersectionObserver.js, unchanged 12.1 -> 12.3)
 * starts an image only when its card is within half a viewport (rootMargin 50%), and empties it again
 * when it scrolls back out, so returning to a card fades it in a second time. A Jellyfin server pays
 * about 0.3 s per image the first time a size is asked for (it resizes and caches; measured on CT114:
 * 291 ms mean cold, 1 ms warm), so every new screenful of cards arrives late.
 *
 * The fix keeps stock's loader for what is on screen and adds a second, wider, low-priority pass that
 * only requests the same URL early: the server resizes it ahead of time and the browser caches it
 * (tagged image URLs are served `max-age=31536000, immutable`), so the stock fill is instant.
 */

export var PRELOAD_MARGIN = '250%';
export var MAX_IN_FLIGHT = 4;
export var REMEMBER = 4000;
export var PREF_KEY = 'jellyfinPlus.imagePreload';

/* A LazyLoader-shaped class: what both copies in the 12.1 and 12.3 builds look like. */
export function isLazyLoaderClass(fn) {
    var p = typeof fn === 'function' && fn.prototype;
    return !!p && typeof p.createObserver === 'function' && typeof p.addElements === 'function'
        && typeof p.destroyObserver === 'function';
}

/* Only Jellyfin image URLs are worth fetching early; anything else the loader handles is left alone. */
export function isImageUrl(url) {
    return typeof url === 'string' && /\/Images\/[A-Za-z]+/.test(url);
}

export function enabled(storage) {
    try {
        return (storage && storage.getItem(PREF_KEY)) !== 'off';
    } catch (e) {
        return true;
    }
}

/*
 * A nearest-first fetch queue. `load(url)` returns a promise; at most `max` run at once. A URL is
 * fetched once per page load (remembered up to `remember` URLs). `drop(url)` forgets a URL still
 * waiting, for a card that scrolled out of the look-ahead zone before its turn.
 */
export function createQueue(load, max, remember) {
    max = max || MAX_IN_FLIGHT;
    remember = remember || REMEMBER;
    var waiting = new Map();      // url -> distance
    var seen = new Set();         // started or finished
    var running = 0;

    function pump() {
        while (running < max && waiting.size) {
            var best = null;
            var bestD = Infinity;
            waiting.forEach(function (d, url) {
                if (d < bestD) {
                    bestD = d;
                    best = url;
                }
            });
            waiting.delete(best);
            if (seen.size >= remember) {
                seen.delete(seen.values().next().value);
            }
            seen.add(best);
            running++;
            Promise.resolve().then(function (u) { return load(u); }.bind(null, best))
                .catch(function () {})
                .then(function () {
                    running--;
                    pump();
                });
        }
    }

    return {
        add: function (url, distance) {
            if (!url || seen.has(url)) {
                return;
            }
            var d = typeof distance === 'number' && isFinite(distance) ? distance : 0;
            if (!waiting.has(url) || d < waiting.get(url)) {
                waiting.set(url, d);
            }
            pump();
        },
        drop: function (url) {
            waiting.delete(url);
        },
        stats: function () {
            return { waiting: waiting.size, running: running, seen: seen.size };
        }
    };
}

/*
 * Server warm batch (POST /JellyfinPlus/Images/Warm). The server resizes the look-ahead URLs with
 * bounded parallelism before the browser's own 6 connections ask for them. The route only accepts a
 * relative `/Items/<guid>/Images/<Type>[/<n>]?<query>` path (its own PathBase is prepended), so a
 * URL that does not fit is simply not warmed.
 */
export var WARM_MAX = 48;
export var WARM_DELAY_MS = 100;
export var WARM_PATH = '/JellyfinPlus/Images/Warm';

/* The path to send for `url`, or '' when it is not same-origin or not the shape the route accepts. */
export function warmPath(url, origin) {
    try {
        var u = new URL(url, origin);
        if (u.origin !== new URL(origin).origin) {
            return '';
        }
        var m = /(\/Items\/(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\/Images\/[A-Za-z]{3,24}(?:\/[0-9]{1,3})?)$/.exec(u.pathname);
        var q = u.search.slice(1);
        return m && q && q.length <= 400 && /^[A-Za-z0-9=&%._-]+$/.test(q) ? m[1] + '?' + q : '';
    } catch (e) {
        return '';
    }
}

/*
 * Collects (url, distance) pairs for `delay` ms, then calls `send(paths)` once with at most WARM_MAX of
 * them, nearest first, and `done(urls)` when that settles (success or not) so the caller can start the
 * browser fetch. Each path is sent once. A rejected send (404 on an older server) stops all posting:
 * later URLs go straight to `done`. `schedule` is setTimeout, injectable for tests.
 */
export function createWarmer(send, done, delay, schedule) {
    schedule = schedule || setTimeout;
    var pending = new Map();   // path -> { url, d }
    var sent = new Set();
    var timer = false;
    var dead = false;

    function flush() {
        timer = false;
        var items = Array.from(pending.values()).sort(function (a, b) { return a.d - b.d; });
        pending.clear();
        var batch = items.slice(0, WARM_MAX);
        var rest = items.slice(WARM_MAX);
        if (rest.length) {
            done(rest.map(function (x) { return x.url; }));
        }
        if (!batch.length) {
            return;
        }
        var urls = batch.map(function (x) { return x.url; });
        Promise.resolve().then(function () { return send(batch.map(function (x) { return x.path; })); })
            .catch(function () { dead = true; })
            .then(function () { done(urls); });
    }

    return {
        /* True when the URL was taken (the caller must wait for `done`); false means fetch it now. */
        add: function (url, path, d) {
            if (dead || !path || sent.has(path)) {
                return false;
            }
            if (sent.size > REMEMBER) {
                sent.clear();
            }
            sent.add(path);
            pending.set(path, { url: url, path: path, d: d });
            if (!timer) {
                timer = true;
                schedule(flush, typeof delay === 'number' ? delay : WARM_DELAY_MS);
            }
            return true;
        },
        isDead: function () { return dead; }
    };
}
