// Run: node web/src/model/timeline-markers.test.mjs   (no framework; exits non-zero on failure)
import assert from 'node:assert/strict';
import { timelineMarkers, markerLabel, seekTarget } from './timeline-markers.js';

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

console.log(passed + ' passed');
