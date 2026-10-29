// Run: node web/src/model/link-profile.test.mjs   (no framework; exits non-zero on failure)
import assert from 'node:assert/strict';
import { classify, linkPref, slowBody, slowUrl, MIN_KBPS, MAX_KBPS } from './link-profile.js';

var passed = 0;
function t(name, fn) {
    try { fn(); passed++; console.log('ok   ' + name); } catch (e) { console.log('FAIL ' + name + '\n' + e.message); process.exitCode = 1; }
}

t('pref parsing', function () {
    assert.equal(linkPref(null), 'auto');
    assert.equal(linkPref(' Slow '), 'slow');
    assert.equal(linkPref('remote'), 'slow');
    assert.equal(linkPref('off'), 'fast');
    assert.equal(linkPref('lan'), 'fast');
    assert.equal(linkPref('junk'), 'auto');
});

t('classify: measured remote link (272 ms, 2.6 Mbps) is slow, cap 60% of throughput', function () {
    assert.deepEqual(classify(272, 2.6, null), { slow: true, kbps: 1560 });
});

t('classify: LAN is fast', function () {
    assert.equal(classify(3, 400, null).slow, false);
});

t('classify: either signal alone makes it slow', function () {
    assert.equal(classify(200, 500, null).slow, true);
    assert.equal(classify(5, 4, null).slow, true);
});

t('classify: prefs override the measurement', function () {
    assert.equal(classify(272, 2.6, 'fast').slow, false);
    assert.equal(classify(3, 400, 'slow').slow, true);
});

t('classify: cap clamped, default when throughput unknown', function () {
    assert.equal(classify(300, 0.5, null).kbps, MIN_KBPS);
    assert.equal(classify(300, 9.9, null).kbps, MAX_KBPS);
    assert.equal(classify(300, null, null).kbps, MIN_KBPS * 2);
});

t('slowBody: no direct play, cap lowered but never raised', function () {
    var out = JSON.parse(slowBody(JSON.stringify({ MaxStreamingBitrate: 120000000, EnableDirectStream: true }), 1500));
    assert.equal(out.EnableDirectPlay, false);
    assert.equal(out.EnableDirectStream, true);
    assert.equal(out.MaxStreamingBitrate, 1500000);
    assert.equal(slowBody(JSON.stringify({ MaxStreamingBitrate: 1000000, EnableDirectPlay: false }), 1500), null);
    assert.equal(slowBody('not json', 1500), null);
    assert.equal(slowBody('', 1500), null);
});

t('slowUrl: query form', function () {
    assert.equal(slowUrl('/Items/x/PlaybackInfo?UserId=u', 1500),
        '/Items/x/PlaybackInfo?UserId=u&enableDirectPlay=false&maxStreamingBitrate=1500000');
    assert.equal(slowUrl('/Items/x/PlaybackInfo?MaxStreamingBitrate=900000&EnableDirectPlay=true', 1500),
        '/Items/x/PlaybackInfo?MaxStreamingBitrate=900000&EnableDirectPlay=false');
    assert.equal(slowUrl('/p?maxStreamingBitrate=8000000', 1500), '/p?maxStreamingBitrate=1500000&enableDirectPlay=false');
});

console.log(passed + ' passed');
