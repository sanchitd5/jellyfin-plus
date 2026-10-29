/*
 * BLURHASH DECODER AND HASH PICKER (pure; unit-tested by model/blurhash.test.mjs).
 *
 * The public Blurhash algorithm (https://blurha.sh), decode side only: a short base83 string holds
 * a handful of DCT components of a picture; decoding renders them to a small RGBA bitmap.
 * jellyfin-web bundles the same algorithm inside a worker, which is not reachable by shape, so
 * this is a self-contained copy. decode() returns null on any malformed hash (never throws).
 */

var CHARS = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~';
var LOOKUP = {};
for (var c = 0; c < CHARS.length; c++) {
    LOOKUP[CHARS[c]] = c;
}

function base83(str, from, to) {
    var v = 0;
    for (var i = from; i < to; i++) {
        var d = LOOKUP[str[i]];
        if (d === undefined) {
            return -1;
        }

        v = v * 83 + d;
    }

    return v;
}

function toLinear(v) {
    v /= 255;
    return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
}

function toSrgb(v) {
    v = Math.max(0, Math.min(1, v));
    return Math.trunc(v <= 0.0031308 ? v * 12.92 * 255 + 0.5 : (1.055 * Math.pow(v, 1 / 2.4) - 0.055) * 255 + 0.5);
}

function signPow(v, e) {
    return (v < 0 ? -1 : 1) * Math.pow(Math.abs(v), e);
}

/* RGBA Uint8ClampedArray of width*height, or null when the hash or size is not usable. */
export function decode(hash, width, height, punch) {
    if (typeof hash !== 'string' || hash.length < 6 || !(width > 0) || !(height > 0)) {
        return null;
    }

    var flag = base83(hash, 0, 1);
    if (flag < 0) {
        return null;
    }

    var numY = Math.floor(flag / 9) + 1;
    var numX = (flag % 9) + 1;
    if (hash.length !== 4 + 2 * numX * numY) {
        return null;
    }

    var q = base83(hash, 1, 2);
    var dc = base83(hash, 2, 6);
    if (q < 0 || dc < 0) {
        return null;
    }

    var maxVal = ((q + 1) / 166) * (punch || 1);
    var colors = [[toLinear(dc >> 16), toLinear((dc >> 8) & 255), toLinear(dc & 255)]];
    for (var i = 1; i < numX * numY; i++) {
        var v = base83(hash, 4 + i * 2, 6 + i * 2);
        if (v < 0) {
            return null;
        }

        colors.push([
            signPow((Math.floor(v / 361) - 9) / 9, 2) * maxVal,
            signPow(((Math.floor(v / 19) % 19) - 9) / 9, 2) * maxVal,
            signPow(((v % 19) - 9) / 9, 2) * maxVal
        ]);
    }

    var out = new Uint8ClampedArray(width * height * 4);
    for (var y = 0; y < height; y++) {
        for (var x = 0; x < width; x++) {
            var r = 0;
            var g = 0;
            var b = 0;
            for (var j = 0; j < numY; j++) {
                for (var k = 0; k < numX; k++) {
                    var basis = Math.cos((Math.PI * x * k) / width) * Math.cos((Math.PI * y * j) / height);
                    var col = colors[k + j * numX];
                    r += col[0] * basis;
                    g += col[1] * basis;
                    b += col[2] * basis;
                }
            }

            var p = 4 * (x + y * width);
            out[p] = toSrgb(r);
            out[p + 1] = toSrgb(g);
            out[p + 2] = toSrgb(b);
            out[p + 3] = 255;
        }
    }

    return out;
}

function hashFor(item, type, tag) {
    var map = item && item.ImageBlurHashes && item.ImageBlurHashes[type];
    var h = map && tag ? map[tag] : null;
    return typeof h === 'string' && h ? h : null;
}

/* The item's own Primary hash (the details poster), else null. */
export function posterHash(item) {
    return hashFor(item, 'Primary', item && item.ImageTags && item.ImageTags.Primary);
}

/*
 * The hash for the details header: the own first Backdrop, else Primary (the phone header falls
 * back to Primary when there is no backdrop). A parent's backdrop has no hash on this DTO, so
 * that case returns the Primary hash, a rough colour match at worst, or null.
 */
export function headerHash(item) {
    var tags = item && item.BackdropImageTags;
    return (tags && tags.length && hashFor(item, 'Backdrop', tags[0])) || posterHash(item);
}
