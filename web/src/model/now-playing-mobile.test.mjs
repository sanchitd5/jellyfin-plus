// Run: node web/src/model/now-playing-mobile.test.mjs   (no framework; exits non-zero on failure)
import assert from 'node:assert/strict';
import { centeredScrollTop, rowVisible } from './now-playing-mobile.js';

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

t('row below the fold is pulled to the middle', function () {
    // list 400 tall, row 50 tall sitting 1000px under the list top, nothing scrolled yet
    assert.equal(centeredScrollTop(0, 1000, 400, 50), 825);
});

t('row above the fold scrolls back up', function () {
    // scrolled 2000, row 600px above the list top
    assert.equal(centeredScrollTop(2000, -600, 400, 50), 1225);
});

t('never negative', function () {
    assert.equal(centeredScrollTop(0, 10, 400, 50), 0);
});

t('rounded to whole pixels', function () {
    assert.equal(centeredScrollTop(0, 1000, 401, 50), 825);
});

t('garbage measurements leave scrollTop alone', function () {
    assert.equal(centeredScrollTop(120, NaN, 400, 50), 120);
    assert.equal(centeredScrollTop(120, 10, undefined, 50), 120);
    assert.equal(centeredScrollTop(120, 10, Infinity, 50), 120);
});

t('rowVisible is true only when the whole row is inside', function () {
    assert.equal(rowVisible(0, 50, 400), true);
    assert.equal(rowVisible(350, 50, 400), true);
    assert.equal(rowVisible(351, 50, 400), false);
    assert.equal(rowVisible(-1, 50, 400), false);
});

console.log(passed + ' passed' + (process.exitCode ? ', with failures' : ''));
