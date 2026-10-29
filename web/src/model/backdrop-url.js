/*
 * The details-page backdrop URL stock jellyfin-web will ask for, computed ahead of time so the image
 * can be fetched (and the server's resize cache warmed) before the page opens. Pure; the DOM side is
 * controller/image-preload.js.
 *
 * Sources (release-12.z): apps/legacy/controllers/itemDetails/index.js renderBackdrop /
 * renderHeaderBackdrop, components/backdrop/backdrop.js getItemImageUrls, utils/dom.js getScreenWidth,
 * utils/jellyfin-apiclient/backdropImage.ts. backdropImage.ts was NOT available: the header path's
 * option object is reconstructed (marked below), so compare the prefetched URL with the one the page
 * really requests before trusting the header path. The desktop path and getScreenWidth are verified.
 *
 * Wire-contract note: the cache key is the exact URL string, so the options object handed to
 * ApiClient.getScaledImageUrl keeps stock's key ORDER (the apiclient serialises in insertion order).
 * devicePixelRatio and quality are applied by getScaledImageUrl itself, so they are not modelled here.
 */

export var BACKDROP_PREF_KEY = 'jellyfinPlus.backdropPreload';
export var DESKTOP_MIN_WIDTH = 1000;
var STANDARD_WIDTHS = [480, 720, 1280, 1440, 1920, 2560, 3840, 5120, 7680];

export function backdropEnabled(storage) {
    try {
        return (storage && storage.getItem(BACKDROP_PREF_KEY)) !== 'off';
    } catch (e) {
        return true;
    }
}

/* Data-saver or a 2g link: look-ahead is not worth the bytes. conn = navigator.connection. */
export function slowConnection(conn) {
    return !!conn && (conn.saveData === true || conn.effectiveType === '2g' || conn.effectiveType === 'slow-2g');
}

/* dom.getScreenWidth() (verified against dom.js:187-204): portrait uses height * 16/9, then snaps to the nearest standard width. */
export function screenWidth(env) {
    var w = isFinite(env.innerWidth) ? env.innerWidth : 3840;
    var h = isFinite(env.innerHeight) ? env.innerHeight : 2160;
    if (h > w) {
        w = h * (16 / 9);
    }
    var best = STANDARD_WIDTHS[0];
    STANDARD_WIDTHS.forEach(function (c) {
        if (Math.abs(w - c) < Math.abs(w - best)) {
            best = c;
        }
    });
    return best;
}

/* The few fields of an item DTO the URL depends on; this is what card-data keeps per card. */
export function backdropFields(dto) {
    if (!dto || !dto.Id) {
        return null;
    }
    var tags = function (a) { return Array.isArray(a) ? a.filter(function (t) { return typeof t === 'string'; }) : []; };
    return {
        Id: dto.Id,
        Type: dto.Type || '',
        BackdropItemId: dto.BackdropItemId || null,
        BackdropImageTags: tags(dto.BackdropImageTags),
        ParentBackdropItemId: dto.ParentBackdropItemId || null,
        ParentBackdropImageTags: tags(dto.ParentBackdropImageTags),
        PrimaryTag: dto.ImageTags && typeof dto.ImageTags.Primary === 'string' ? dto.ImageTags.Primary : null
    };
}

/* Desktop (not mobile layout, window >= 1000px) uses the global backdrop; otherwise the page header. */
export function usesGlobalBackdrop(env) {
    return !env.mobileLayout && env.innerWidth >= DESKTOP_MIN_WIDTH;
}

/*
 * item = backdropFields() output; getScaledImageUrl(id, options) = the live ApiClient's, bound.
 * env = { innerWidth, innerHeight, mobileLayout }.
 * Returns the URL string stock requests, or '' when it would show no backdrop.
 */
export function stockBackdropUrl(item, env, getScaledImageUrl) {
    if (!item || !item.Id || typeof getScaledImageUrl !== 'function') {
        return '';
    }
    var maxWidth = screenWidth(env);

    if (usesGlobalBackdrop(env)) {
        // components/backdrop getItemImageUrls: own backdrop, else parent's; first tag; no Primary fallback.
        if (item.BackdropImageTags.length) {
            return getScaledImageUrl(item.BackdropItemId || item.Id,
                { type: 'Backdrop', tag: item.BackdropImageTags[0], maxWidth: maxWidth, index: 0 });
        }
        if (item.ParentBackdropItemId && item.ParentBackdropImageTags.length) {
            return getScaledImageUrl(item.ParentBackdropItemId,
                { type: 'Backdrop', tag: item.ParentBackdropImageTags[0], maxWidth: maxWidth, index: 0 });
        }
        return '';
    }

    // itemDetails reloadFromItem: the header backdrop renders only on the mobile layout; a narrow
    // desktop window gets clearBackdrop() and no backdrop at all.
    if (!env.mobileLayout) {
        return '';
    }
    // Header path (backdropImage.ts getItemBackdropImageUrl, release-12.z): own, parent, then Primary,
    // options spread last ({ maxWidth }). People and books have no header backdrop.
    if (item.Type === 'Person' || item.Type === 'Book') {
        return '';
    }
    if (item.BackdropImageTags.length) {
        return getScaledImageUrl(item.Id,
            { type: 'Backdrop', index: 0, tag: item.BackdropImageTags[0], maxWidth: maxWidth });
    }
    if (item.ParentBackdropItemId && item.ParentBackdropImageTags.length) {
        return getScaledImageUrl(item.ParentBackdropItemId,
            { type: 'Backdrop', index: 0, tag: item.ParentBackdropImageTags[0], maxWidth: maxWidth });
    }
    if (item.PrimaryTag) {
        return getScaledImageUrl(item.Id, { type: 'Primary', tag: item.PrimaryTag, maxWidth: maxWidth });
    }
    return '';
}

/*
 * True when the item (or its parent) carries a real Backdrop image, i.e. stockBackdropUrl is not the
 * large Primary fallback. Touch dwell prefetches only these; the fallback is fetched on hover only.
 */
export function isRealBackdrop(item) {
    return !!item && (item.BackdropImageTags.length > 0
        || (!!item.ParentBackdropItemId && item.ParentBackdropImageTags.length > 0));
}
