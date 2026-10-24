// Run: node web/src/model/detail-timeline.test.mjs   (no framework; exits non-zero on failure)
import assert from 'node:assert/strict';
import { timelineKey, orderTimeline, windowAround } from './detail-timeline.js';

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

function id(n) {
    return ('0'.repeat(32) + n.toString(16)).slice(-32);
}

function ids(list) {
    return list.map(function (x) { return x.Id; });
}

t('timelineKey prefers PremiereDate, falls back to DateCreated, else Infinity', function () {
    assert.equal(timelineKey({ PremiereDate: '2020-01-01T00:00:00Z', DateCreated: '2024-01-01T00:00:00Z' }), Date.parse('2020-01-01T00:00:00Z'));
    assert.equal(timelineKey({ DateCreated: '2024-01-01T00:00:00Z' }), Date.parse('2024-01-01T00:00:00Z'));
    assert.equal(timelineKey({}), Infinity);
    assert.equal(timelineKey({ PremiereDate: 'garbage' }), Infinity);
    assert.equal(timelineKey(null), Infinity);
});

t('orderTimeline sorts by date, undated last, ties by name then id', function () {
    var a = { Id: id(1), Name: 'b', PremiereDate: '2021-01-01' };
    var b = { Id: id(2), Name: 'a', PremiereDate: '2021-01-01' };
    var c = { Id: id(3), Name: 'z', DateCreated: '2019-05-05' };
    var d = { Id: id(4), Name: 'a' };
    var e = { Id: id(5), Name: 'a' };
    assert.deepEqual(ids(orderTimeline([d, a, e, c, b, null, { Name: 'no id' }])), [id(3), id(2), id(1), id(4), id(5)]);
});

t('windowAround centres on the item and clips at the ends', function () {
    var list = [];
    for (var i = 1; i <= 20; i++) {
        list.push({ Id: id(i) });
    }

    var w = windowAround(list, id(10), 3);
    assert.deepEqual(ids(w.before), [id(7), id(8), id(9)]);
    assert.equal(w.current.Id, id(10));
    assert.deepEqual(ids(w.after), [id(11), id(12), id(13)]);
    assert.equal(w.index, 9);
    assert.equal(w.total, 20);

    var first = windowAround(list, id(1), 3);
    assert.deepEqual(first.before, []);
    assert.equal(first.after.length, 3);

    var last = windowAround(list, id(20), 3);
    assert.equal(last.before.length, 3);
    assert.deepEqual(last.after, []);
});

t('windowAround matches dashed / upper-case ids and refuses missing or lonely items', function () {
    var list = [{ Id: '0123abcd0123456789abcdef01234567' }, { Id: id(2) }];
    assert.equal(windowAround(list, '0123ABCD-0123-4567-89AB-CDEF01234567', 2).index, 0);
    assert.equal(windowAround(list, id(9), 2), null);
    assert.equal(windowAround([{ Id: id(1) }], id(1), 2), null);
    assert.equal(windowAround(null, id(1), 2), null);
});

console.log(passed + ' passed');
