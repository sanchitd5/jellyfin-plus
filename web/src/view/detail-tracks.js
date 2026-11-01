import { log } from '../lib/log.js';
import { el } from '../lib/dom.js';
import { watchDetail } from '../controller/detail-page.js';
import { trackSummary } from '../model/detail-tracks.js';

/*
 * "WATCH OPTIONS" DISCLOSURE OVER THE STOCK TRACK SELECTS (docs/tasks/mobile-detail-page.md, Task B).
 *
 * Stock (jellyfin-web 12.3 itemDetails): form.trackSelections holds up to four native selects
 * (version, video, audio, subtitles). renderTrackSelections fills them, un-hides the form and each
 * container worth showing, and the selects' own change handlers switch the media source and
 * re-fill their siblings. On a phone that is three full-width selects with truncated codec strings
 * that nobody changes before pressing Play.
 *
 * This adds ONE <details class="jpTracks"> immediately before the form: a summary row (tune icon,
 * "Watch options", a one-line summary of the current picks, chevron). The selects are never cloned,
 * moved or written to; they stay the source of truth. While the row is closed CSS hides the form;
 * opening the row (page.jpTracksOpen) shows the stock form unchanged. The summary is read from the
 * selects (selected option text, the container's .hide) after every change and whenever the form's
 * class changes; the text itself comes from model/detail-tracks.js.
 *
 * Fail soft: jpHasTracks (the class that hides the form) is added to the page only after the
 * disclosure is in the DOM; no form, no row; a non-video item removes the row. When stock hides the
 * form (nothing to choose) a MutationObserver on its class hides the row too. CSS is scoped to the
 * phone portrait layout, outside which the row is display:none and the stock form shows.
 */

var STYLE_ID = 'jpDetailTracksStyle';
var HAS = 'jpHasTracks';
var OPEN = 'jpTracksOpen';
var TITLE = 'Watch options';

var ON = 'html.layout-mobile:not(.jp-theme-off):not(.layout-tv)';
var MQ = '@media (orientation: portrait) and (max-width: 40em)';

var CSS = [
    '.jpTracks{display:none}',
    MQ + '{',
    ON + ' .jpTracks:not([hidden]){display:block;margin:var(--jp-space-3,.75em) 0}',
    ON + ' .jpTracksSummary{display:flex;align-items:center;gap:var(--jp-space-3,.75em);box-sizing:border-box;min-height:56px;' +
        'padding:var(--jp-space-2,.5em) var(--jp-space-3,.75em);border-radius:var(--jp-radius-m,8px);background:var(--jp-surface-2,#2a2a2a);' +
        'color:var(--jp-text,#fff);cursor:pointer;list-style:none;-webkit-tap-highlight-color:transparent}',
    ON + ' .jpTracksSummary::-webkit-details-marker{display:none}',
    ON + ' .jpTracksSummary::marker{content:""}',
    ON + ' .jpTracksBody{flex:1 1 auto;min-width:0;display:flex;flex-direction:column}',
    ON + ' .jpTracksTitle{font-size:var(--jp-font-body,1rem);font-weight:600;line-height:1.3}',
    ON + ' .jpTracksText{font-size:var(--jp-font-small,.85rem);color:var(--jp-text-muted,rgba(255,255,255,.65));' +
        'overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
    ON + ' .jpTracksChevron{flex:0 0 auto;transition:transform var(--jp-dur-fast,150ms) var(--jp-ease,ease)}',
    ON + ' .jpTracks[open] .jpTracksChevron{transform:rotate(180deg)}',
    ON + ' #itemDetailPage.' + HAS + ':not(.' + OPEN + ') .trackSelections:not(.hide){display:none}',
    ON + ' #itemDetailPage.' + OPEN + ' .trackSelections:not(.hide){display:block}',
    ON + ' .trackSelections .selectContainer{margin:var(--jp-space-2,.5em) 0}',
    ON + ' .trackSelections .selectLabel{flex-basis:5.5em}',
    ON + ' .trackSelections .detailTrackSelect{min-height:var(--jp-tap,44px)}',
    '}'
].join('\n');

function injectStyle() {
    if (document.getElementById(STYLE_ID)) {
        return;
    }

    var s = document.createElement('style');
    s.id = STYLE_ID;
    s.textContent = CSS;
    (document.head || document.documentElement).appendChild(s);
}

/* One entry per stock container, in DOM order. The label is the attribute the select was given, not the upgraded DOM. */
function readEntries(form) {
    return Array.prototype.map.call(form.querySelectorAll('.selectContainer'), function (c) {
        var sel = c.querySelector('select');
        var opt = sel && sel.selectedIndex >= 0 ? sel.options[sel.selectedIndex] : null;
        return {
            label: sel ? sel.getAttribute('label') || '' : '',
            text: opt ? opt.textContent : '',
            shown: !c.classList.contains('hide')
        };
    });
}

/* The row is looked up each time: start() may have replaced it while the form (and its listeners) lived on. */
function refresh(form) {
    try {
        var prev = form.previousElementSibling;
        var details = prev && prev.classList.contains('jpTracks') ? prev : null;
        if (!details) {
            return;
        }

        details.hidden = form.classList.contains('hide');
        var text = details.querySelector('.jpTracksText');
        var value = trackSummary(readEntries(form));
        if (text && text.textContent !== value) {
            text.textContent = value;
        }
    } catch (err) {
        log('detail tracks: refresh failed', err);
    }
}

function build() {
    var details = el('details', 'jpTracks');
    var summary = el('summary', 'jpTracksSummary');
    summary.setAttribute('aria-expanded', 'false');

    var icon = el('span', 'material-icons tune', 'tune');
    icon.setAttribute('aria-hidden', 'true');
    var body = el('span', 'jpTracksBody');
    body.appendChild(el('span', 'jpTracksTitle', TITLE));
    body.appendChild(el('span', 'jpTracksText', ''));
    var chevron = el('span', 'material-icons expand_more jpTracksChevron', 'expand_more');
    chevron.setAttribute('aria-hidden', 'true');

    summary.appendChild(icon);
    summary.appendChild(body);
    summary.appendChild(chevron);
    details.appendChild(summary);
    return details;
}

function remove(page) {
    page.classList.remove(HAS, OPEN);
    Array.prototype.forEach.call(page.querySelectorAll('.jpTracks'), function (d) {
        d.remove();
    });
}

function mount(page, item) {
    var form = page.querySelector('.trackSelections');
    if (!item || item.MediaType !== 'Video' || !form || !form.parentNode) {
        remove(page);
        return;
    }

    var prev = form.previousElementSibling;
    var details = prev && prev.classList.contains('jpTracks') ? prev : null;
    if (!details) {
        remove(page);
        details = build();
        form.parentNode.insertBefore(details, form);
    }

    if (!details.__jpWired) {
        details.__jpWired = true;
        details.addEventListener('toggle', function () {
            page.classList.toggle(OPEN, details.open);
            var summary = details.querySelector('summary');
            if (summary) {
                summary.setAttribute('aria-expanded', details.open ? 'true' : 'false');
            }
        });
    }

    if (!form.__jpTracksWired) {
        form.__jpTracksWired = true;
        // Capture, then a tick later: stock's change handler re-fills the sibling selects first.
        form.addEventListener('change', function (e) {
            if (e.target && e.target.classList && e.target.classList.contains('detailTrackSelect')) {
                setTimeout(function () {
                    refresh(form);
                }, 0);
            }
        }, true);

        if (typeof MutationObserver === 'function') {
            new MutationObserver(function () {
                refresh(form);
            }).observe(form, { attributes: true, attributeFilter: ['class'], subtree: true });
        }
    }

    refresh(form);
    // Only now does CSS hide the stock form: the replacement exists.
    page.classList.add(HAS);
}

export function installDetailTracks() {
    try {
        injectStyle();
        watchDetail({
            start: function (page) {
                remove(page);
            },
            item: function (page, item) {
                mount(page, item);
            }
        });
    } catch (err) {
        log('detail tracks: install failed', err);
    }
}
