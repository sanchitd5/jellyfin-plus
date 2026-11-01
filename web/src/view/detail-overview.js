import { log } from '../lib/log.js';
import { watchDetail } from '../controller/detail-page.js';

/*
 * OVERVIEW THAT EXPANDS ON TAP, FOUR LINES ON A PHONE (item details page).
 *
 * Stock (jellyfin-web 12.3, itemDetails renderOverview): .overview gets detail-clamp-text
 * (-webkit-line-clamp: 3) and, only when scrollHeight > offsetHeight after render, the
 * .overview-expand button loses `hide`. Its click runs stock's toggleLineClamp, which flips the
 * clamp class and swaps the button text (ShowMore / ShowLess).
 *
 * This changes three things, phone portrait only: the clamp is 4 lines; the paragraph itself is a
 * tap target; and when the text fits in 4 lines the now pointless "Show more" button is hidden.
 * The stock button stays the mechanism: a tap on the paragraph only calls .click() on it, the class
 * and the button text are never toggled here, so stock state cannot drift. A tap on a link inside
 * the text (markdown) is left to the link.
 *
 * Fail soft: a missing .overview or .overview-expand means no listener action and no change; the
 * style tag is additive; the fit check only ever ADDS `hide` to the stock button, and only when the
 * text is not clamped. Scoped to html:not(.jp-theme-off):not(.layout-tv).layout-mobile, portrait,
 * <= 40em, inside #itemDetailPage (stock sets .overview on other pages too).
 */

var STYLE_ID = 'jpDetailOverviewStyle';
var FITS = 'jpOverviewFits';
var SAME = 'jpSameTitle';

var SCOPE = 'html.layout-mobile:not(.jp-theme-off):not(.layout-tv) #itemDetailPage';
var MEDIA = '@media (orientation: portrait) and (max-width: 40em)';

var CSS = [
    MEDIA + ' {',
    '  ' + SCOPE + ' .overview.detail-clamp-text { -webkit-line-clamp: 4; }',
    '  ' + SCOPE + ' .overview:has(+ .overview-expand:not(.hide)) { cursor: pointer; -webkit-tap-highlight-color: transparent; }',
    '  ' + SCOPE + '.' + FITS + ' .overview { cursor: auto; }',
    '  ' + SCOPE + ' .overview-expand { font-size: var(--jp-font-small, .85rem); font-weight: 600;'
        + ' color: var(--jp-accent, #00a4dc); min-height: var(--jp-tap, 44px); padding: 0; margin: 0;'
        + ' background: none; text-align: left; justify-content: flex-start; }',
    '  ' + SCOPE + '.' + SAME + ' .itemName.originalTitle { display: none; }',
    '}'
].join('\n');

function inScope() {
    var root = document.documentElement;
    return root.classList.contains('layout-mobile') && !root.classList.contains('jp-theme-off')
        && !root.classList.contains('layout-tv')
        && window.matchMedia('(orientation: portrait) and (max-width: 40em)').matches;
}

function expandButton(overview) {
    var next = overview.nextElementSibling;
    if (next && next.classList.contains('overview-expand')) {
        return next;
    }

    return overview.parentElement ? overview.parentElement.querySelector('.overview-expand') : null;
}

function onClick(e) {
    try {
        var overview = e.target && e.target.closest ? e.target.closest('#itemDetailPage .overview') : null;
        if (!overview || e.target.closest('a') || !inScope()) {
            return;
        }

        var expand = expandButton(overview);
        if (expand && !expand.classList.contains('hide')) {
            expand.click();
        }
    } catch (err) {
        log('detail overview: tap failed', err);
    }
}

function checkFits(page) {
    requestAnimationFrame(function () {
        try {
            var overview = page.querySelector('.overview');
            var expand = overview && expandButton(overview);
            // Only judge a clamped paragraph: an expanded one always "fits".
            if (!overview || !expand || !overview.classList.contains('detail-clamp-text') || !inScope()) {
                return;
            }

            if (overview.scrollHeight <= overview.clientHeight + 1) {
                expand.classList.add('hide');
                page.classList.add(FITS);
            }
        } catch (err) {
            log('detail overview: fit check failed', err);
        }
    });
}

export function installDetailOverview() {
    try {
        if (!document.getElementById(STYLE_ID)) {
            var style = document.createElement('style');
            style.id = STYLE_ID;
            style.textContent = CSS;
            (document.head || document.documentElement).appendChild(style);
        }

        document.addEventListener('click', onClick);
        watchDetail({
            start: function (page) {
                page.classList.remove(FITS);
                page.classList.remove(SAME);
            },
            item: function (page, item) {
                checkFits(page);
                // Original title identical to the title: class only, the stock node is untouched.
                if (item.OriginalTitle && item.Name && String(item.OriginalTitle).toLowerCase() === String(item.Name).toLowerCase()) {
                    page.classList.add(SAME);
                }
            }
        });

        log('detail overview installed');
    } catch (err) {
        log('detail overview: install failed', err);
    }
}
