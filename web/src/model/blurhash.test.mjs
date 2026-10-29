// Run: node web/src/model/blurhash.test.mjs   (no framework; exits non-zero on failure)
import assert from 'node:assert/strict';
import { decode, posterHash, headerHash } from './blurhash.js';

var CHARS = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~';
function enc(v, n) {
    var s = '';
    for (var i = 0; i < n; i++) {
        s = CHARS[v % 83] + s;
        v = Math.floor(v / 83);
    }
    return s;
}

var passed = 0;
function t(name, fn) {
    try {
        fn();
        passed++;
        console.log('ok   ' + name);
    } catch (e) {
        console.log('FAIL ' + name + '\n' + e.message);
        process.exitCode = 1;
    }
}

t('1x1 hash decodes to its flat colour', function () {
    var px = decode('00' + enc(0xff8000, 4), 4, 4);
    assert.equal(px.length, 64);
    for (var i = 0; i < 16; i++) {
        assert.deepEqual(Array.from(px.slice(i * 4, i * 4 + 4)), [255, 128, 0, 255]);
    }
});

t('multi-component hash gives full opaque bitmap that is not flat', function () {
    var px = decode('LEHV6nWB2yk8pyo0adR*.7kCMdnj', 32, 32);
    assert.equal(px.length, 32 * 32 * 4);
    var distinct = new Set();
    for (var i = 0; i < px.length; i += 4) {
        assert.equal(px[i + 3], 255);
        distinct.add(px[i] + ',' + px[i + 1] + ',' + px[i + 2]);
    }
    assert.ok(distinct.size > 10);
});

t('malformed input returns null, never throws', function () {
    assert.equal(decode('', 4, 4), null);
    assert.equal(decode(null, 4, 4), null);
    assert.equal(decode('LEHV6n', 4, 4), null);
    assert.equal(decode('00!!!!!!', 4, 4), null);
    assert.equal(decode('00' + enc(1, 4), 0, 4), null);
});

t('posterHash and headerHash pick by tag', function () {
    var item = {
        ImageTags: { Primary: 'tp' },
        BackdropImageTags: ['tb'],
        ImageBlurHashes: { Primary: { tp: 'P' }, Backdrop: { tb: 'B' } }
    };
    assert.equal(posterHash(item), 'P');
    assert.equal(headerHash(item), 'B');
    assert.equal(headerHash({ ImageTags: item.ImageTags, ImageBlurHashes: item.ImageBlurHashes }), 'P');
    assert.equal(posterHash({ ImageTags: { Primary: 'x' }, ImageBlurHashes: item.ImageBlurHashes }), null);
    assert.equal(posterHash({}), null);
    assert.equal(headerHash(null), null);
});

console.log(passed + ' passed');
