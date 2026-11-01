// Run: node web/src/model/detail-tracks.test.mjs   (no framework; exits non-zero on failure)
import assert from 'node:assert/strict';
import { trackSummary, clip, EMPTY } from './detail-tracks.js';

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

t('spec example: video, long audio, subtitles', function () {
    assert.equal(trackSummary([
        { label: 'Video', text: '1080p H264', shown: true },
        { label: 'Audio', text: 'English - AAC - 5.1 (Default)', shown: true },
        { label: 'Subtitles', text: 'Off', shown: true }
    ]), '1080p H264 · English - AAC - 5.1… · Off');
});

t('all hidden -> Default', function () {
    assert.equal(trackSummary([
        { label: 'Video', text: '1080p H264', shown: false },
        { label: 'Audio', text: 'English', shown: false }
    ]), EMPTY);
    assert.equal(EMPTY, 'Default');
});

t('empty / missing input -> Default', function () {
    assert.equal(trackSummary([]), 'Default');
    assert.equal(trackSummary(undefined), 'Default');
    assert.equal(trackSummary(null), 'Default');
});

t('version shown first leads', function () {
    assert.equal(trackSummary([
        { label: 'Version', text: 'Main cut', shown: true },
        { label: 'Video', text: '720p VP9', shown: true },
        { label: 'Audio', text: 'Stereo', shown: false }
    ]), 'Main cut · 720p VP9');
});

t('trims whitespace, skips blank text', function () {
    assert.equal(trackSummary([
        { label: 'Video', text: '  720p VP9  ', shown: true },
        { label: 'Audio', text: '   ', shown: true },
        { label: 'Subtitles', text: '', shown: true }
    ]), '720p VP9');
});

t('exactly 28 chars is kept; 29 is cut', function () {
    var s28 = 'abcdefghi jklmnopqr stuvwxyz';
    assert.equal(s28.length, 28);
    assert.equal(clip(s28), s28);
    assert.equal(clip(s28 + 'x'), 'abcdefghi jklmnopqr…');
});

t('no space before the limit -> hard cut at 28', function () {
    assert.equal(clip('x'.repeat(40)), 'x'.repeat(28) + '…');
});
console.log(passed + ' passed');
