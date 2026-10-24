import { log } from '../lib/log.js';
import { el } from '../lib/dom.js';
import { watchDetail } from './detail-page.js';
import { getCardBuilder } from './detail-related.js';
import { detailHref, normId } from '../model/detail-nav.js';
import { orderTimeline, windowAround, SIDE } from '../model/detail-timeline.js';

/*
 * TIMELINE ON A CLIP'S DETAILS PAGE: for each person in the clip, a row of that person's other
 * clips in date order, the current clip in the middle, the ones made before it to the left and
 * after it to the right.
 *
 *  - ORDER. All of the person's Video items are asked for once, ids and dates only
 *    (EnableImages=false, EnableUserData=false), and ordered in model/detail-timeline.js:
 *    PremiereDate, else DateCreated, undated last. Only the window around the current clip is then
 *    fetched again with what a card needs, and drawn with the stock cardBuilder (found by shape in
 *    detail-related.js) so a card plays, opens and menus exactly like any other.
 *  - ACCESS. Both requests go through the page's ApiClient with the viewer's user id, so the server
 *    applies that user's library access and parental rating as for any list. Nothing is widened.
 *  - BOUNDS. At most PEOPLE rows per clip and LIST_CAP ids per person. A person with more clips
 *    than that, where the current clip falls outside the first LIST_CAP, gets no row rather than a
 *    wrong one.
 *  - FAIL SOFT. No ApiClient, no cardBuilder, a failed request or a person with no other clip
 *    means no row, never an error in the page. TV layouts are untouched.
 *
 * The row markup is the same emby-scroller / emby-itemscontainer pair view/search-infinite.js
 * builds, so the stock click handling and scrolling apply. A click on a timeline card is captured
 * by detail-nav.js like any list, so the previous / next buttons then walk the timeline.
 */

var STYLE_ID = 'jellyfinPlusTimelineStyle';
var SECTION_CLASS = 'jpTimeline';
var CURRENT_CLASS = 'jpTimelineCurrent';
var PEOPLE = 3;
var LIST_CAP = 3000;

var SCROLLER_HTML = '<div is="emby-scroller" data-horizontal="true" data-centerfocus="card" class="padded-top-focusscale padded-bottom-focusscale">'
    + '<div is="emby-itemscontainer" class="focuscontainer-x itemsContainer scrollSlider"></div></div>';

function isTv() {
    return document.documentElement.classList.contains('layout-tv');
}

/* The page still shows this item (the viewer has not moved on while a request was out). */
function stillOn(page, item) {
    return normId(page.__jpDetailId) === normId(item.Id);
}

function removeSections(page) {
    Array.prototype.forEach.call(page.querySelectorAll('.' + SECTION_CLASS), function (n) {
        n.parentNode.removeChild(n);
    });
}

/* People once each, in the clip's own order (a person credited twice is one row). */
function peopleOf(item) {
    var seen = {};
    var out = [];
    (item.People || []).forEach(function (p) {
        if (p && p.Id && p.Name && !seen[p.Id] && out.length < PEOPLE) {
            seen[p.Id] = true;
            out.push(p);
        }
    });
    return out;
}

function newSection(page, person, win, serverId) {
    var section = el('div', 'verticalSection detailVerticalSection ' + SECTION_CLASS);
    section.setAttribute('data-person', person.Id);

    var title = el('h2', 'sectionTitle sectionTitle-cards padded-right');
    title.appendChild(document.createTextNode('Timeline · '));
    var who = el('a', 'jpTimelinePerson', person.Name);
    who.setAttribute('href', detailHref(person.Id, serverId));
    title.appendChild(who);
    title.appendChild(el('span', 'jpTimelineCount', (win.index + 1) + ' of ' + win.total));
    section.appendChild(title);

    var holder = el('div');
    holder.innerHTML = SCROLLER_HTML;
    section.appendChild(holder.firstChild);

    // After the chips, before "More Like This"; at the end of the page content otherwise.
    var anchor = page.querySelector('#similarCollapsible') || page.querySelector('#castCollapsible');
    var parent = anchor ? anchor.parentNode : page.querySelector('.detailPageContent');
    if (!parent) {
        return null;
    }

    // Rows for several people keep the clip's people order.
    var last = page.querySelectorAll('.' + SECTION_CLASS);
    if (last.length) {
        last[last.length - 1].parentNode.insertBefore(section, last[last.length - 1].nextSibling);
    } else {
        parent.insertBefore(section, anchor || null);
    }

    return section;
}

/* Scroll the row sideways so the current card sits in the middle; never scrolls the page. */
function centre(section) {
    var scroller = section.querySelector('[is="emby-scroller"]');
    var card = section.querySelector('.' + CURRENT_CLASS);
    if (!scroller || !card) {
        return;
    }

    var s = scroller.getBoundingClientRect();
    var c = card.getBoundingClientRect();
    var now = typeof scroller.getScrollPosition === 'function' ? scroller.getScrollPosition() : scroller.scrollLeft;
    var to = Math.max(0, now + (c.left - s.left) - (s.width - c.width) / 2);
    if (typeof scroller.scrollToPosition === 'function') {
        scroller.scrollToPosition(to, true);
    } else {
        scroller.scrollLeft = to;
    }
}

function draw(page, item, person, win, cards) {
    var cb = getCardBuilder();
    if (!cb) {
        log('detail timeline: cardBuilder not found yet; skipped for this view');
        return;
    }

    var byId = {};
    cards.forEach(function (c) {
        byId[normId(c.Id)] = c;
    });
    // /Items?Ids= answers in its own order: put the cards back in timeline order, the current one included.
    var ordered = win.before.concat([win.current], win.after).map(function (it) {
        return byId[normId(it.Id)];
    }).filter(Boolean);
    if (ordered.length < 2) {
        return;
    }

    var section = newSection(page, person, win, item.ServerId);
    var container = section && section.querySelector('[is="emby-itemscontainer"]');
    if (!container) {
        return;
    }

    cb.buildCards(ordered, {
        itemsContainer: container,
        shape: 'overflowBackdrop',
        centerText: true,
        showTitle: true,
        showPremiereDate: true,
        lazy: true,
        showDetailsMenu: true,
        overlayPlayButton: true,
        overlayText: false
    });

    var cur = container.querySelector('[data-id="' + normId(item.Id) + '"]')
        || container.querySelector('[data-id="' + item.Id + '"]');
    if (cur) {
        cur.classList.add(CURRENT_CLASS);
        cur.setAttribute('aria-current', 'true');
    }

    requestAnimationFrame(function () {
        centre(section);
    });
    log('detail timeline: ' + person.Name + ' ' + (win.index + 1) + ' of ' + win.total);
}

function loadPerson(page, item, person, client, userId) {
    return Promise.resolve(client.getItems(userId, {
        PersonIds: person.Id,
        IncludeItemTypes: 'Video',
        Recursive: true,
        Fields: 'DateCreated,SortName',
        SortBy: 'PremiereDate,DateCreated,SortName',
        SortOrder: 'Ascending',
        EnableImages: false,
        EnableUserData: false,
        Limit: LIST_CAP
    })).then(function (res) {
        if (!stillOn(page, item)) {
            return null;
        }

        var win = windowAround(orderTimeline((res && res.Items) || []), item.Id, SIDE);
        if (!win) {
            return null;
        }

        var ids = win.before.concat([win.current], win.after).map(function (it) {
            return it.Id;
        });
        return Promise.resolve(client.getItems(userId, {
            Ids: ids.join(','),
            Fields: 'PrimaryImageAspectRatio,CanDelete,DateCreated',
            EnableImageTypes: 'Primary,Backdrop,Thumb'
        })).then(function (full) {
            if (stillOn(page, item)) {
                draw(page, item, person, win, (full && full.Items) || []);
            }
        });
    });
}

function render(page, item) {
    removeSections(page);
    var client = window.ApiClient;
    if (isTv() || !item || item.Type !== 'Video' || !client || typeof client.getItems !== 'function') {
        return;
    }

    var userId = client.getCurrentUserId();
    // One person after another, so the rows land in the clip's people order.
    peopleOf(item).reduce(function (chain, person) {
        return chain.then(function () {
            return loadPerson(page, item, person, client, userId);
        }).catch(function (err) {
            log('detail timeline: ' + person.Name + ' failed', err);
        });
    }, Promise.resolve());
}

function installStyle() {
    if (document.getElementById(STYLE_ID)) {
        return;
    }

    var style = document.createElement('style');
    style.id = STYLE_ID;
    style.textContent = [
        '.jpTimelinePerson { color: inherit; text-decoration: none; }',
        '.jpTimelinePerson:hover, .jpTimelinePerson:focus-visible { text-decoration: underline; }',
        '.jpTimelineCount { margin-left: 0.6em; font-size: 70%; font-weight: normal; opacity: 0.7; }',
        '.' + SECTION_CLASS + ' .' + CURRENT_CLASS + ' .cardBox { border-radius: 0.3em;'
            + ' box-shadow: 0 0 0 3px var(--jp-accent, #00a4dc); }',
        '.' + SECTION_CLASS + ' .' + CURRENT_CLASS + ' .cardText:first-child { font-weight: bold; }'
    ].join('\n');
    (document.head || document.documentElement).appendChild(style);
}

export function installDetailTimeline() {
    try {
        installStyle();
        watchDetail({
            start: removeSections,
            item: render
        });
        log('detail timeline: installed');
    } catch (err) {
        log('detail timeline: install failed', err);
    }
}
