// Run: node web/src/model/timeline-markers.test.mjs   (no framework; exits non-zero on failure)
import assert from 'node:assert/strict';
import { timelineMarkers, markerLabel, seekTarget, stripTimes, pickTrickplay, stockTile, momentTile } from './timeline-markers.js';

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

var BODY = { markers: [
    { offsetSeconds: 500, label: 'item b', value: 100, kind: 'tip', source: 'a' },
    { offsetSeconds: 100, label: 'item a', value: 20, kind: 'tip', source: 'a' },
    { offsetSeconds: 900, label: 'scene', kind: 'whatever', source: 'b' }
] };

t('sorted, scaled against the duration', function () {
    var m = timelineMarkers(BODY, 1000);
    assert.deepEqual(m.map(function (x) { return x.offset; }), [100, 500, 900]);
    assert.deepEqual(m.map(function (x) { return x.pct; }), [10, 50, 90]);
});

t('unknown or missing kind is neutral', function () {
    var m = timelineMarkers(BODY, 1000);
    assert.deepEqual(m.map(function (x) { return x.kind; }), ['tip', 'tip', 'neutral']);
    assert.equal(timelineMarkers({ markers: [{ offsetSeconds: 1 }] }, 10)[0].kind, 'neutral');
});

t('size relative to the largest value; no value is small', function () {
    var m = timelineMarkers(BODY, 1000);
    assert.deepEqual(m.map(function (x) { return x.size; }), ['s', 'l', 's']);
    var b = { markers: [{ offsetSeconds: 1, value: 0.3 }, { offsetSeconds: 2, value: 1 }, { offsetSeconds: 3, value: -4 }] };
    assert.deepEqual(timelineMarkers(b, 10).map(function (x) { return x.size; }), ['m', 'l', 's']);
});

t('nothing for junk bodies, empty lists or no duration', function () {
    [null, undefined, '', 'x', 5, [], {}, { markers: 'no' }, { markers: [] }].forEach(function (b) {
        assert.deepEqual(timelineMarkers(b, 1000), []);
    });
    [0, -1, NaN, undefined, null, Infinity].forEach(function (d) {
        assert.deepEqual(timelineMarkers(BODY, d), []);
    });
});

t('drops markers with bad or out-of-range offsets', function () {
    var b = { markers: [{ offsetSeconds: -1 }, { offsetSeconds: 1001 }, { offsetSeconds: '5' }, null, 7,
        { offsetSeconds: NaN }, { offsetSeconds: 0 }, { offsetSeconds: 1000 }] };
    assert.deepEqual(timelineMarkers(b, 1000).map(function (x) { return x.offset; }), [0, 1000]);
});

t('label is kept as text, never interpreted', function () {
    var b = { markers: [{ offsetSeconds: 1, value: 2, label: '  <img src=x onerror=1> 💋 ' }] };
    assert.equal(timelineMarkers(b, 10)[0].label, '<img src=x onerror=1> 💋 · 2');
    assert.equal(timelineMarkers({ markers: [{ offsetSeconds: 1, label: 7 }] }, 10)[0].label, '');
});

t('markerLabel', function () {
    assert.equal(markerLabel('a', 20), 'a · 20');
    assert.equal(markerLabel('', 1.256), '1.26');
    assert.equal(markerLabel('a', null), 'a');
    assert.equal(markerLabel('', null), '');
});

t('seek target lands 3 s early, never below 0', function () {
    assert.equal(seekTarget(195), 192);
    assert.equal(seekTarget(1.5), 0);
    assert.equal(seekTarget(NaN), 0);
});

t('markerLabel with a unit: "name · 100 tokens"; null label shows the value alone', function () {
    assert.equal(markerLabel('Someone', 100, 'tokens'), 'Someone · 100 tokens');
    assert.equal(markerLabel(null, 100, 'tokens'), '100 tokens');
    assert.equal(markerLabel(undefined, 100, ' tokens '), '100 tokens');
    assert.equal(markerLabel('Someone', null, 'tokens'), 'Someone');
    assert.equal(markerLabel('Someone', undefined, 'tokens'), 'Someone');
    assert.equal(markerLabel(null, null, 'tokens'), '');
    assert.equal(markerLabel('a', 5, ''), 'a · 5');
    var b = { markers: [{ offsetSeconds: 1, label: null, value: 100, unit: 'tokens' }, { offsetSeconds: 2, label: 'x', unit: 'tokens' }] };
    assert.deepEqual(timelineMarkers(b, 10).map(function (m) { return m.label; }), ['100 tokens', 'x']);
});

t('stripTimes: marker +-10 s at 5 s, centre flagged, clamped, de-duplicated', function () {
    assert.deepEqual(stripTimes(100, 1000), [
        { t: 90, centre: false }, { t: 95, centre: false }, { t: 100, centre: true },
        { t: 105, centre: false }, { t: 110, centre: false }]);
    assert.deepEqual(stripTimes(3, 1000).map(function (x) { return x.t; }), [0, 3, 8, 13]);
    assert.deepEqual(stripTimes(0.5, 1000).map(function (x) { return x.t; }), [0.5, 5.5, 10.5]);
    assert.deepEqual(stripTimes(998, 1000).map(function (x) { return x.t; }), [988, 993, 998, 999]);
    assert.equal(stripTimes(5, 1000).filter(function (x) { return x.centre; }).length, 1);
    assert.deepEqual(stripTimes(NaN, 10), []);
    assert.deepEqual(stripTimes(5, 0), []);
    assert.deepEqual(stripTimes(50, 10), []);
});

var INFO = { Width: 320, Height: 180, TileWidth: 10, TileHeight: 10, ThumbnailCount: 250, Interval: 10000 };

t('pickTrickplay: smallest width >= 200 of the media source, else the largest', function () {
    var tp = { abc: { 160: Object.assign({}, INFO, { Width: 160 }), 320: INFO, 640: Object.assign({}, INFO, { Width: 640 }) } };
    assert.equal(pickTrickplay(tp, 'abc').info.Width, 320);
    assert.equal(pickTrickplay(tp, 'missing').mediaSourceId, 'abc');
    assert.equal(pickTrickplay({ a: { 100: Object.assign({}, INFO, { Width: 100 }), 150: Object.assign({}, INFO, { Width: 150 }) } }).info.Width, 150);
    [null, undefined, {}, { a: null }, { a: { 1: { Width: 'x' } } }, 'x'].forEach(function (b) { assert.equal(pickTrickplay(b, 'a'), null); });
});

t('stockTile: index = floor(ms / Interval), sheet / column / row, clamped to the last thumbnail', function () {
    assert.deepEqual(stockTile(INFO, 0), { sheet: 0, x: 0, y: 0, w: 320, h: 180 });
    assert.deepEqual(stockTile(INFO, 125), { sheet: 0, x: 2 * 320, y: 1 * 180, w: 320, h: 180 });   // n=12
    assert.deepEqual(stockTile(INFO, 1005), { sheet: 1, x: 0, y: 0, w: 320, h: 180 });             // n=100
    assert.deepEqual(stockTile(INFO, 99999), { sheet: 2, x: 9 * 320, y: 4 * 180, w: 320, h: 180 }); // n=249
    assert.equal(stockTile(INFO, -1), null);
    assert.equal(stockTile({ Width: 1 }, 5), null);
});

t('momentTile: nearest listed time within 1 s, irregular times, missing frame -> null', function () {
    var mf = { status: 'ready', version: 'v', width: 320, height: 180, tileWidth: 2, tileHeight: 2, times: [0, 3, 8, 13, 400] };
    assert.deepEqual(momentTile(mf, 8), { sheet: 0, x: 0, y: 180, w: 320, h: 180, t: 8 });
    assert.deepEqual(momentTile(mf, 400), { sheet: 1, x: 0, y: 0, w: 320, h: 180, t: 400 });
    assert.equal(momentTile(mf, 0.5).t, 0);
    assert.equal(momentTile(mf, 20), null);
    assert.equal(momentTile(Object.assign({}, mf, { status: 'pending' }), 8), null);
    assert.equal(momentTile(null, 8), null);
    assert.equal(momentTile(Object.assign({}, mf, { times: 'x' }), 8), null);
});

console.log(passed + ' passed');
