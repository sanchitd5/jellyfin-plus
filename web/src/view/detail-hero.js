import { log } from '../lib/log.js';

/*
 * HERO, TITLE BLOCK AND TYPOGRAPHY OF THE ITEM DETAILS PAGE ON A PHONE (docs/tasks/mobile-detail-page.md,
 * Task A). CSS only: no DOM writes, no observers, no requests, one <style id="jpDetailHeroStyle">.
 *
 * Stock (jellyfin-web 12.3, librarybrowser.scss) on a portrait phone: a 30vh backdrop with a hard
 * lower edge; the poster card sits INSIDE .infoWrapper (the .detailImageContainer.hide-desktop copy;
 * the other one is hidden on mobile), absolutely positioned at the wrapper's bottom-left, while the
 * wrapper itself pads its left by 37.5 % so every header line is centred in the right-hand 62 %.
 * Titles wrap to three lines, the meta row to two, the buttons are unlabelled glyphs, genres are a
 * comma list and the details rows put a label beside the value.
 *
 * This restyles those same nodes, never replaces them:
 *  - the backdrop gets a taller box and a bottom scrim (::after; stock uses ::before for the app-bar
 *    gradient and keeps it);
 *  - the poster keeps its stock anchoring (bottom of .infoWrapper, so it already rises over the
 *    backdrop); .infoWrapper gets a min-height so the overlap is about 40 % of the poster. No
 *    transform: the poster stays the image-zoom.js tap target and the detail-blurhash.js canvas is
 *    placed from offsetTop/offsetLeft, which a transform would not move;
 *  - title, meta line, original title and series name are left-aligned, clamped, muted;
 *  - every button but the primary Play gets a label drawn from its own stock `title` (::after);
 *  - the Genre row of the details group (12.3 renders genres there, NOT in the empty .itemGenres <p>)
 *    becomes chips: the ', ' separators are text nodes inside each <span>, zeroed with font-size:0
 *    while the anchors restore their size; the group is reordered so genres come first;
 *  - label-above-value details rows and tighter section headings.
 *
 * Scope: every rule is inside @media (orientation: portrait) and (max-width: 40em) and under
 * html.layout-mobile:not(.jp-theme-off):not(.layout-tv) #itemDetailPage, so the off switch, the TV
 * layout, landscape phones and tablets keep the stock page. Logical properties (inline-start)
 * keep RTL working. Tokens are --jp-* with stock-equivalent fallbacks. Fail soft: no style tag,
 * stock page.
 */

var STYLE_ID = 'jpDetailHeroStyle';

var S = 'html.layout-mobile:not(.jp-theme-off):not(.layout-tv) #itemDetailPage';
var POSTER_W = '27vw';
// Poster is portrait (padder 150 %): height 1.5 x width; 60 % of it stays beside the text.
var WRAP_MIN = 'calc(' + POSTER_W + ' * 1.5 * 0.6)';
var HAS_POSTER = ':has(.card .cardImageContainer:not([data-src=""]))';
var GENRES = ' .itemDetailsGroup > div:last-child';
var MUTED = 'color:var(--jp-text-muted,rgba(255,255,255,.6))';
var SMALL = 'font-size:var(--jp-font-small,.85rem)';

function rule(sel, decl) {
    return S + sel + '{' + decl + '}';
}

var RULES = [
    // Hero
    rule(' #itemBackdrop', 'height:38vh;min-height:220px;max-height:360px'),
    rule(' #itemBackdrop::after', 'content:"";position:absolute;inset:auto 0 0 0;height:45%;pointer-events:none;'
        + 'background:linear-gradient(180deg,transparent,var(--jp-bg,#101010))'),

    // Poster beside the title block
    rule(' .infoWrapper', 'position:relative;padding-inline-start:0'),
    rule(' .infoWrapper' + HAS_POSTER, 'padding-inline-start:calc(' + POSTER_W + ' + var(--jp-space-3,.75em))'),
    rule(' .infoWrapper:has(.portraitCard .cardImageContainer:not([data-src=""]))', 'min-height:' + WRAP_MIN),
    rule(' .infoWrapper .card:has(.cardImageContainer[data-src=""])', 'display:none'),
    rule(' .infoWrapper .detailImageContainer .card', 'position:absolute;top:auto;bottom:0;inset-inline-start:0;'
        + 'width:' + POSTER_W + ';max-width:' + POSTER_W + ';margin:0;filter:none;overflow:hidden;z-index:3;'
        + 'border-radius:var(--jp-radius-m,8px);box-shadow:var(--jp-elev-1,0 2px 6px rgba(0,0,0,.4))'),

    // Title block
    rule(' .parentName,' + S + ' .itemName,' + S + ' .itemMiscInfo', 'text-align:start;justify-content:flex-start;padding-inline-start:0!important'),
    rule(' .parentName', 'margin:0;' + SMALL + ';' + MUTED + ';font-weight:400;white-space:nowrap;overflow:hidden;text-overflow:ellipsis'),
    rule(' .itemName', 'margin:2px 0 0'),
    rule(' .itemName:not(.originalTitle)', 'line-height:1.2;font-weight:700;display:-webkit-box;-webkit-box-orient:vertical;'
        + '-webkit-line-clamp:2;overflow:hidden;white-space:normal'),
    rule(' h1.itemName', 'font-size:clamp(1.25rem,5.5vw,1.6rem)'),
    rule(' .itemName.originalTitle', SMALL + ';' + MUTED + ';font-weight:400;white-space:nowrap;overflow:hidden;text-overflow:ellipsis'),
    rule(' .itemMiscInfo-primary', 'flex-wrap:nowrap;overflow:hidden;white-space:nowrap;margin-top:var(--jp-space-1,4px);'
        + '-webkit-mask-image:linear-gradient(90deg,#000 85%,transparent);mask-image:linear-gradient(90deg,#000 85%,transparent)'),
    'html.layout-mobile:not(.jp-theme-off):not(.layout-tv) [dir="rtl"] #itemDetailPage .itemMiscInfo-primary'
        + '{-webkit-mask-image:linear-gradient(270deg,#000 85%,transparent);mask-image:linear-gradient(270deg,#000 85%,transparent)}',
    rule(' .itemMiscInfo .mediaInfoItem', 'margin:0;margin-inline-end:.5em;flex:0 0 auto;' + SMALL + ';' + MUTED),
    rule(' .itemMiscInfo .endsAt', 'display:block'),

    // Button row (jpPrimaryPlay and the sticky bar belong to detail-actions.js)
    rule(' .mainDetailButtons', 'justify-content:flex-start;gap:var(--jp-space-1,4px);padding:0'),
    rule(' .mainDetailButtons .detailButton:not(.jpPrimaryPlay)', 'flex:1 1 0;max-width:72px;flex-direction:column;'
        + 'min-height:56px;padding:.45em .15em!important'),
    rule(' .mainDetailButtons .detailButton:not(.jpPrimaryPlay)::after', 'content:attr(title);display:block;font-size:11px;line-height:1.2;'
        + 'margin-top:2px;max-width:100%;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;' + MUTED),
    rule(' .mainDetailButtons .detailButton:not(.jpPrimaryPlay):not([title])::after,' + S
        + ' .mainDetailButtons .detailButton:not(.jpPrimaryPlay)[title=""]::after', 'display:none'),

    // Primary content
    rule(' .tagline', 'font-style:italic;font-weight:400;' + MUTED + ';margin:var(--jp-space-3,.75em) 0 var(--jp-space-2,.5em)'),
    rule(' .overview', 'font-size:var(--jp-font-body,1rem);line-height:1.5;color:var(--jp-text,inherit)'),

    // Details group: genres first, as chips; other rows label-above-value
    rule(' .itemDetailsGroup', 'display:flex;flex-direction:column'),
    rule(GENRES, 'order:-1'),
    rule(' .detailsGroupItem', 'flex-direction:column;gap:2px;margin:0 0 var(--jp-space-3,.75em)!important'),
    rule(' .detailsGroupItem .label', 'flex:0 0 auto;flex-basis:auto;margin:0;' + SMALL + ';' + MUTED
        + ';text-transform:uppercase;letter-spacing:.04em'),
    rule(GENRES + ' .detailsGroupItem .label', 'display:none'),
    rule(GENRES + ' .detailsGroupItem > .focuscontainer-x', 'display:flex;flex-wrap:wrap;gap:.45em'),
    rule(GENRES + ' .detailsGroupItem > .focuscontainer-x > span', 'font-size:0'),
    rule(GENRES + ' .detailsGroupItem > .focuscontainer-x > span > a', SMALL + ';display:inline-flex;align-items:center;'
        + 'box-sizing:border-box;min-height:2.5em;padding:.35em .95em;margin:0;border-radius:999px;'
        + 'border:1px solid rgba(128,128,128,.45);background:rgba(128,128,128,.16);color:inherit;text-decoration:none'),

    // Sections
    rule(' .sectionTitle-cards,' + S + ' .detailVerticalSection .sectionTitle', 'font-size:1.1rem;font-weight:600;margin:0 0 var(--jp-space-2,.5em)')
];

var CSS = '@media (orientation:portrait) and (max-width:40em){\n' + RULES.join('\n') + '\n}';

export function installDetailHero() {
    try {
        if (!document.getElementById(STYLE_ID)) {
            var style = document.createElement('style');
            style.id = STYLE_ID;
            style.textContent = CSS;
            (document.head || document.documentElement).appendChild(style);
        }

        log('detail hero installed');
    } catch (err) {
        log('detail hero install failed', err);
    }
}
