// Run: node web/src/model/backdrop-url.test.mjs   (no framework; exits non-zero on failure)
import assert from 'node:assert/strict';
import { stockBackdropUrl, screenWidth, backdropFields, backdropEnabled, slowConnection, BACKDROP_PREF_KEY } from './backdrop-url.js';

function t(name, fn) {
    try { fn(); console.log('ok   ' + name); } catch (e) { console.log('FAIL ' + name + '\n' + e.message); process.exitCode = 1; }
}
var fake = function (id, o) { return 'U/' + id + '/' + o.type + '/' + (o.index === undefined ? '' : o.index) + '?tag=' + o.tag + '&w=' + o.maxWidth; };
var desk = { innerWidth: 1900, innerHeight: 900, mobileLayout: false };
var phone = { innerWidth: 390, innerHeight: 844, mobileLayout: true };
var own = backdropFields({ Id: 'a', BackdropImageTags: ['t1', 't2'], ImageTags: { Primary: 'p' } });
var ep = backdropFields({ Id: 'e', Type: 'Episode', ParentBackdropItemId: 's', ParentBackdropImageTags: ['st'], ImageTags: { Primary: 'p' } });
var bare = backdropFields({ Id: 'b', ImageTags: { Primary: 'p' } });

t('screenWidth snaps to the nearest standard width', function () {
    assert.equal(screenWidth({ innerWidth: 1900, innerHeight: 900 }), 1920);
    assert.equal(screenWidth({ innerWidth: 1100, innerHeight: 700 }), 1280);
    assert.equal(screenWidth({ innerWidth: 900, innerHeight: 600 }), 720);
});
t('portrait phone uses height*16/9', function () {
    assert.equal(screenWidth(phone), 1440); // 844*16/9 = 1500.4
});
t('desktop: own backdrop, first tag, index 0', function () {
    assert.equal(stockBackdropUrl(own, desk, fake), 'U/a/Backdrop/0?tag=t1&w=1920');
});
t('desktop: BackdropItemId overrides the item id', function () {
    var i = backdropFields({ Id: 'a', BackdropItemId: 'x', BackdropImageTags: ['t'] });
    assert.equal(stockBackdropUrl(i, desk, fake), 'U/x/Backdrop/0?tag=t&w=1920');
});
t('desktop: parent backdrop fallback', function () {
    assert.equal(stockBackdropUrl(ep, desk, fake), 'U/s/Backdrop/0?tag=st&w=1920');
});
t('desktop: no Primary fallback', function () {
    assert.equal(stockBackdropUrl(bare, desk, fake), '');
});
t('phone: own, parent, then Primary', function () {
    assert.equal(stockBackdropUrl(own, phone, fake), 'U/a/Backdrop/?tag=t1&w=1440');
    assert.equal(stockBackdropUrl(ep, phone, fake), 'U/s/Backdrop/?tag=st&w=1440');
    assert.equal(stockBackdropUrl(bare, phone, fake), 'U/b/Primary/?tag=p&w=1440');
});
t('narrow non-mobile window takes the header path', function () {
    assert.equal(stockBackdropUrl(bare, { innerWidth: 900, innerHeight: 600, mobileLayout: false }, fake), 'U/b/Primary/?tag=p&w=720');
});
t('people and books get no header backdrop', function () {
    assert.equal(stockBackdropUrl(backdropFields({ Id: 'p', Type: 'Person', ImageTags: { Primary: 'x' } }), phone, fake), '');
});
t('odd input yields empty string', function () {
    assert.equal(stockBackdropUrl(null, desk, fake), '');
    assert.equal(stockBackdropUrl(own, desk, null), '');
    assert.equal(backdropFields({}), null);
    assert.deepEqual(backdropFields({ Id: 'z', BackdropImageTags: [1, 'ok'] }).BackdropImageTags, ['ok']);
});
t('switches', function () {
    assert.equal(backdropEnabled({ getItem: function (k) { return k === BACKDROP_PREF_KEY ? 'off' : null; } }), false);
    assert.equal(backdropEnabled({ getItem: function () { throw new Error('x'); } }), true);
    assert.equal(slowConnection({ saveData: true }), true);
    assert.equal(slowConnection({ effectiveType: 'slow-2g' }), true);
    assert.equal(slowConnection({ effectiveType: '4g' }), false);
    assert.equal(slowConnection(undefined), false);
});
