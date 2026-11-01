# Item details page on a phone: hero, overview, watch options

Design spec for the next pass on the video details screen (`#itemDetailPage`) on phones. Written
to be implemented by parallel agents in separate worktrees; nothing in here is built yet.
Continues `docs/roadmap/mobile-redesign.md` P7 (primary Play + sticky bar, shipped in
`web/src/view/detail-actions.js`) and sits beside the other details-page modules listed at the end.

Target: **jellyfin-web 12.3** as served on CT114 (`dpkg`: `jellyfin-web 12.3+ubu2404`). Every
selector below was counted in the served bundle (`/usr/share/jellyfin/web/*.js|*.css`) and read in
`src/apps/legacy/controllers/itemDetails/index.{html,js}` and `src/styles/librarybrowser.scss` at
tag `v12.3`.

## 1. What a phone shows today (portrait, `html.layout-mobile`, width <= 40em)

Top to bottom, for a Movie / Video:

| # | Element (stock selector) | Stock rule that shapes it | What goes wrong on a 390 px screen |
|---|---|---|---|
| 1 | `#itemBackdrop` (`.itemBackdrop`) | `height: 30vh` portrait; `margin-top: -48px !important` and a top gradient `::before` (AppOverrides.scss) so the app bar floats over it | Fine as a hero, but it ends in a hard edge; nothing below is composed with it. |
| 2 | `.detailImageContainer .card` (poster, `overflowPortrait` shape) | `position: absolute; left: 0; bottom: 0; max-width: 30vw; filter: drop-shadow(...)` on <= 32em | Poster sits in the header's bottom-left and pushes **every** header line right. |
| 3 | `.detailRibbon` > `.infoWrapper` > `.nameContainer` (`.parentName`, `h1.itemName`), `.itemMiscInfo.itemMiscInfo-primary` | `.layout-mobile .parentName/.itemName/.itemMiscInfo { align-items:center; justify-content:center; text-align:center }` plus `@mixin header-poster-padding` = `padding-left: 37.5%` | Title is **centred inside the right 62 %** of the screen: a two-word title wraps to three lines, the meta row (`.mediaInfoItem` x 4-6: year, runtime, rating, resolution, ends-at) wraps to two centred lines. `.endsAt` is already hidden <= 31.25em. |
| 4 | `.mainDetailButtons` (`.detailButton` x up to 14: `btnPlay`, `btnReplay`, `btnPlayTrailer`, `btnShuffle`, `btnInstantMix`, `btnDownload`, `btnPlaystate`, `btnUserRating`, `btnSplitVersions`, `btnMoreCommands`, plus the plugin's prev / next) | full width on <= 32em; `detail-actions.js` already makes `btnPlay` a wide accent `jpPrimaryPlay` and the rest 44 px icons | Icons carry no label (`.detailButton-text` is hidden on mobile; only a `title`). Favourite, Mark played, Download, More are four grey glyphs. |
| 5 | `.detailPagePrimaryContent` (`padding: 0 5%`): `.tagline`, `.overview` (`.detail-clamp-text`, 3 lines, `.overview-expand` button), `.itemGenres`, `.itemTags` (hidden by `detail-related.js`, replaced by `.gprChips` after it), `.itemExternalLinks`, `.itemDetailsGroup` > `.detailsGroupItem` (Directors, Writers, Studios; `.label` `flex-basis: 4.5em` + content), `.trackSelections` (three `.selectContainer` rows: Video, Audio, Subtitle, native `<select>`) | `.detailsGroupItem` and `.trackSelections` `max-width: 44em`, label left / value right | Tagline and overview have the same weight as the labels. Genres are a comma list. Three full-width native selects (~9 em tall) show truncated codec strings (`H264 1080p (Default)`) nobody changes before pressing Play. |
| 6 | `.detailPageSecondaryContainer`: `#castCollapsible` (`.personCard` scroller), `#similarCollapsible` (filled by `detail-related.js` for Videos), `.gprTimeline` rows (`detail-timeline.js`), `#scenesCollapsible` (chapters), `#specialsCollapsible`, `#additionalPartsCollapsible` | `.sectionTitle-cards` + `emby-scroller` with `3.3 %` edge padding | Works. Section headings are 1.5 em-ish and loud; spacing between sections is `verticalSection-extrabottompadding` (large). |
| 7 | page bottom | `.page { padding-bottom: calc(env(safe-area-inset-bottom) + 5em) }` | Fine with the plugin's bottom nav. |

Landscape phone (`max-height: 31.25em`) keeps the stock layout; the backdrop is already dropped
there by stock. Tablets (`jp-tablet`, >= 40em) and desktop are **not** in scope.

## 2. Target layout (phone portrait)

```
┌────────────────────────────────────────┐
│ ◀  (app bar floats, stock)             │
│                                        │
│          BACKDROP  38vh                │
│   (blurhash -> image, stock + plugin)  │
│░░░░░░░ gradient to --jp-bg ░░░░░░░░░░░│  <- hero scrim, bottom 45 % of the backdrop
│ ┌──────┐  Series / parent (muted)      │
│ │poster│  Title, 2 lines max, left     │  <- poster 27vw, overlaps backdrop by 40 %
│ │27vw  │  2024 · 1h 52m · PG-13 · 4K   │  <- one muted meta line, left, ellipsis
│ └──────┘  ★ 7.8  (ratings inline)      │
│ [ ▶  Resume 0:41:12            ]       │  <- jpPrimaryPlay (exists)
│  ♡      ✓      ⤓      ⋯     ‹   ›      │  <- 44px icons WITH 11px labels under
│  Fav   Played  Down   More  Prev Next  │
│ "Tagline in italics"                   │
│ Overview text clamped to 4 lines ...   │  <- tap anywhere on it expands
│ more                                   │
│ (Action) (Drama) (Thriller)            │  <- genre chips (stock anchors restyled)
│ (⚲ Person) (⚲ Person) (tag) (tag) +3   │  <- existing gprChips
│ ┌ Watch options ───────────────── ▾ ┐  │  <- one disclosure row, summary of 3 selects
│ │ 1080p H264 · English AAC 5.1 · Off│  │
│ └───────────────────────────────────┘  │
│ Directors   Name, Name                 │  <- label small+muted ABOVE value
│ Writers     Name                       │
│ Cast & Crew                       ›    │  <- section title 1.1rem, tighter
│ (cards ...)                            │
│ More Like This                    ›    │
│ (cards ...)                            │
│ Name · timeline rows (exist)           │
│ Chapters                               │
└────────────────────────────────────────┘
```

Principles, in the order they win:

1. **Restyle, never re-create.** Every stock element keeps its node, class, handlers and `title`.
   The page is re-rendered per item by the stock controller; anything we add is found again by the
   shared `watchDetail()` and is idempotent.
2. **CSS first.** Hero, poster, title, meta, icon labels, genre chips, details rows, section
   typography are all CSS under one scope. JS only where a value has to be read (watch-options
   summary) or an event handled (overview tap).
3. **Scope** every rule to `html.layout-mobile:not(.jp-theme-off):not(.layout-tv)` **and**
   `@media (orientation: portrait) and (max-width: 40em)`. Off switch -> stock. TV -> stock.
   Landscape -> stock. Tablet -> stock (this pass).
4. **Tokens** are `--jp-*` with stock-equivalent fallbacks, exactly as `detail-actions.js` does
   (`var(--jp-accent,#00a4dc)`). Available: `--jp-bg`, `--jp-surface`, `--jp-surface-2`,
   `--jp-text`, `--jp-text-muted`, `--jp-accent`, `--jp-on-accent`, `--jp-border`, `--jp-scrim`,
   `--jp-radius-s/m/l/pill`, `--jp-space-1..6`, `--jp-tap` (44px), `--jp-font-small/body/title`,
   `--jp-dur-fast`, `--jp-ease`, `--jp-elev-1`. Defined in `web/src/model/theme-tokens.js`; do
   not add tokens there.
5. **Fail soft to stock.** A module that throws leaves the stock page intact: style tags are
   additive, JS checks for every node before touching it, and the only hidden stock element
   (`.trackSelections` behind the disclosure) is hidden by a class that is added **after** the
   replacement rendered (same pattern as `.gprHasChips .itemTags`).
6. No new requests. Everything needed is in the DTO `watchDetail()` already fetches or in the DOM.

## 3. Tasks

Three tasks, three new files, disjoint ownership. Shared files take **one additive line each**
(listed per task); the main thread merges.

### Task A - `web/src/view/detail-hero.js` (CSS only) - `feature('detail hero', installDetailHero)`

Owns one `<style id="jpDetailHeroStyle">`. No DOM writes, no observers. Rules, all under the scope
in 2.3 (call it `SCOPE`):

**Hero and poster**

- `#itemBackdrop`: `height: 38vh; min-height: 220px; max-height: 360px`. Keep stock
  `margin-top: -48px` and its top gradient. Add a bottom scrim: a second gradient via
  `::after` (stock uses `::before`): `position:absolute; inset:auto 0 0 0; height:45%;
  background: linear-gradient(180deg, transparent, var(--jp-bg, #101010))`. (`.itemBackdrop`
  is `position: relative` in stock, so the pseudo-element positions against it.)
- `.detailImageContainer .card`: `position: absolute; left: 5%; bottom: auto; top: calc(100% - 40%)`
  is **not** possible because the card is a sibling of the backdrop inside
  `.detailPagePrimaryContainer`, not a child of it. Use the stock anchoring (absolute,
  `bottom: 0`, `left: 0`) and instead pull it down with `transform: translateY(40%)`;
  `max-width: 27vw; margin-left: 5%; filter: none; border-radius: var(--jp-radius-m, 8px);
  overflow: hidden; box-shadow: var(--jp-elev-1, 0 2px 6px rgba(0,0,0,.4))`. Keep `z-index: 3`.
  The poster stays the `image-zoom.js` tap target and the `detail-blurhash.js` canvas host; its
  `.cardScalable::after` zoom badge still renders (check it after the transform).
- `.detailRibbon`: `min-height: calc(27vw * 1.5 * 0.4 + 1em)` so the translated poster has room
  before the buttons row; `padding-left: calc(27vw + 5% + var(--jp-space-3, .75em))`.

**Title block** (inside `.detailRibbon .infoWrapper`)

- `.parentName`, `h1.itemName`, `.itemMiscInfo`: `text-align: left; justify-content: flex-start;
  align-items: flex-start; padding-left: 0 !important` (overrides the 37.5 % mixin and the
  centring; both are `.layout-mobile`-scoped, so `SCOPE` + element wins by specificity - verify in
  DevTools, add `!important` only where a stock `!important` forces it).
- `h1.itemName`: `font-size: clamp(1.25rem, 5.5vw, 1.6rem); line-height: 1.2; font-weight: 700;
  display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; overflow: hidden`.
  `.itemName.originalTitle` (original title line): `font-size: var(--jp-font-small, .85rem);
  color: var(--jp-text-muted)`; keep it, one line, ellipsis.
- `.parentName`: `font-size: var(--jp-font-small); color: var(--jp-text-muted); margin: 0`.
- `.itemMiscInfo-primary`: `flex-wrap: nowrap; overflow: hidden; white-space: nowrap;
  mask-image: linear-gradient(90deg, #000 85%, transparent)`; `.mediaInfoItem { margin: 0 .5em 0 0;
  font-size: var(--jp-font-small); color: var(--jp-text-muted) }`; show `.endsAt` again (stock
  hides it <= 31.25em; with one nowrap line it fits or fades out). Ratings
  (`.starRatingContainer`, `.criticRating`) stay in the same line, stock markup.

**Button row** (`.mainDetailButtons`; `detail-actions.js` owns `jpPrimaryPlay` and the sticky bar -
do not restate its rules)

- Every `.detailButton:not(.jpPrimaryPlay)`: `flex: 1 1 0; max-width: 72px; flex-direction: column;
  min-height: 56px`; label from the stock `title` attribute:
  `.detailButton:not(.jpPrimaryPlay)::after { content: attr(title); display: block;
  font-size: 11px; line-height: 1.2; margin-top: 2px; color: var(--jp-text-muted);
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 100% }`.
  Stock sets `title` on every one of these (`${Play}`, `${Download}`, `${ButtonMore}` ...,
  localised); the plugin's prev / next buttons (`.jpNavBtn`) carry `title` too (`detail-nav.js`
  `makeButton(template, icon, title, href)`). `.btnUserRating` flips its `title` between
  favourite states - the label follows for free.
- `.detailButton.hide` stays hidden (stock `.hide` is `display: none !important`).
- Row: `justify-content: flex-start; gap: var(--jp-space-1); padding: 0 5%`.

**Primary content** (`.detailPagePrimaryContent`)

- `.tagline`: `font-style: italic; color: var(--jp-text-muted); margin: var(--jp-space-3) 0 var(--jp-space-2)`.
- `.overview`: `font-size: var(--jp-font-body); line-height: 1.5; color: var(--jp-text)`.
  Clamp count and expand behaviour belong to Task C; Task A only sets type.
- Genres as chips, CSS only: `.itemGenres { display: flex; flex-wrap: wrap; gap: .45em }`,
  `.itemGenres .label { display: none }`, `.itemGenres a { ...same box as .gprChip: inline-flex;
  min-height: 2.5em; padding: .35em .95em; border-radius: 999px; border: 1px solid
  rgba(128,128,128,.45); background: rgba(128,128,128,.16); color: inherit; text-decoration:
  none }`. Verify the comma separators: stock renders `<a>, <a>` with text nodes between - hide
  them with `.itemGenres { font-size: 0 } .itemGenres a { font-size: var(--jp-font-small) }`
  (the text-node commas get 0 px; the anchors restore their size). Order: `.itemGenres` keeps its
  stock position (before `.gprChips`), so genres read first, then people, then tags.
- `.detailsGroupItem`: `flex-direction: column; gap: 2px; margin-bottom: var(--jp-space-3)`;
  `.detailsGroupItem .label { flex-basis: auto; font-size: var(--jp-font-small); color:
  var(--jp-text-muted); text-transform: uppercase; letter-spacing: .04em }`;
  content `a` keep stock colour.
- `.itemExternalLinks`: same label treatment; links inline, comma-separated as stock.

**Sections** (`.detailPageSecondaryContainer`)

- `.sectionTitle-cards, .detailVerticalSection .sectionTitle`: `font-size: 1.1rem; font-weight: 600;
  margin: 0 0 var(--jp-space-2)`. `.sectionTitleTextButton > .material-icons` (the `>` chevron)
  unchanged.
- `.verticalSection-extrabottompadding`: `padding-bottom: var(--jp-space-4)` (stock is larger).
- Do not touch `.emby-scroller` padding (`3.3 %` edge bleed is right) nor card sizes
  (`overflowPortraitCard` / `overflowBackdropCard` width breakpoints decide image URLs - see
  P6's warning in `mobile-redesign.md`).

Verify (A): on 390x844 and 360x800 emulation against the live server (main thread), a Movie, an
Episode (parent name shown), a Video with no poster (`data-src=""`: the card still renders empty;
the ribbon padding must not leave a hole - add `.detailImageContainer:has(.cardImageContainer[data-src=""])` -> poster hidden and ribbon `padding-left: 5%`), an item with original title, an
item with 6 `mediaInfoItem`s. Title never exceeds 2 lines; meta is one line; every icon has a
readable label; genre chips are tappable and route to the genre list; the poster still opens the
zoom overlay; the blurhash canvas still paints under the translated poster (it positions over the
element's box - check it moves with the transform; if not, the canvas is inserted as a sibling
and needs the same `transform`, which is a one-line follow-up in `detail-blurhash.js`, main
thread). Then `?jpTheme=off` -> stock page byte-for-byte in the rules (no `jpDetailHeroStyle`
effect), landscape -> stock, TV layout -> stock.

### Task B - `web/src/view/detail-tracks.js` + `web/src/model/detail-tracks.js` - `feature('detail tracks', installDetailTracks)`

Watch options behind one disclosure row.

Stock markup (12.3 `itemDetails/index.html`):

```html
<form class="trackSelections hide">
  <div class="selectContainer selectSourceContainer hide trackSelectionFieldContainer flex-shrink-zero">
    <select is="emby-select" class="selectSource detailTrackSelect" label="${LabelVersion}"></select></div>
  <div class="selectContainer selectVideoContainer hide ...">   <select class="selectVideo detailTrackSelect" label="${LabelVideo}">
  <div class="selectContainer selectAudioContainer hide ...">   <select class="selectAudio detailTrackSelect" label="${LabelAudio}">
  <div class="selectContainer selectSubtitlesContainer hide ..."><select class="selectSubtitles detailTrackSelect" label="${LabelSubtitles}">
</form>
```

`renderTrackSelections(page, instance, item, user)` fills the `<select>`s, removes `hide` from
the form and from each container that has more than one option (video/audio) or at least one
(subtitles), and the `change` handlers on them switch the media source and re-render the
others. The selects are the single source of truth: never clone them, never set values on
them. Each `<select is="emby-select">` wraps itself in a `.selectLabel` + `.selectArrowContainer`
at upgrade time; read the label from the `label` attribute, not from the DOM.

Behaviour:

1. On `watchDetail` `item(page, item)` for `item.MediaType === 'Video'` (and only when
   `.trackSelections` exists), insert **once per page** a disclosure element immediately **before**
   the form: `<details class="jpTracks"><summary class="jpTracksSummary" ...>` with a leading
   `material-icons` `tune` glyph, a title "Watch options", a second line `.jpTracksText` and a
   trailing `expand_more` chevron. Move nothing: the `<details>` is empty apart from its summary;
   opening it toggles `page.classList` `jpTracksOpen`, and CSS shows the stock form
   (`.jpTracksOpen .trackSelections { display: block }`) / hides it otherwise
   (`.jpHasTracks .trackSelections:not(.hide) { display: none }`). The form keeps its own `hide`
   semantics: when stock hides it (no selectable tracks), `.jpTracks` hides too
   (`.trackSelections.hide` -> the previous sibling `.jpTracks` ... CSS cannot select backwards:
   so instead the module mirrors it: a `MutationObserver` on the form's `class` attribute sets
   `hidden` on the `<details>`). `jpHasTracks` is added to the page only after the `<details>` is
   in the DOM (fail-soft order, principle 2.5).
2. Summary text = `trackSummary(entries)` from `model/detail-tracks.js` (pure, unit-tested in
   `model/detail-tracks.test.mjs`, run the way the existing `*.test.mjs` run - see
   `web/package.json`). Input: `[{ label, text, shown }]` per container in DOM order, where `text`
   is the selected option's text and `shown` is `!container.classList.contains('hide')`. Output:
   shown entries' `text`, each trimmed, joined by ` · `; a text longer than 28 chars is cut at the
   last space before 28 and gets `…`; empty input -> `"Default"`. Examples (write these as the
   tests): `[{Video:"1080p H264"},{Audio:"English - AAC - 5.1 (Default)"},{Subtitles:"Off"}]`
   -> `1080p H264 · English - AAC - 5.1… · Off`; all hidden -> `Default`; Version shown first
   -> it leads.
3. Refresh the summary on `change` of any `.detailTrackSelect` (event delegation on the form,
   capture, after stock's handler ran: use a `setTimeout(0)` so the re-rendered sibling selects
   are read) and whenever the form's class changes (the same observer as in 1). The disclosure
   stays open across a change (stock re-fills options, it does not rebuild the form).
4. Styling (own `<style id="jpDetailTracksStyle">`, scope as 2.3): the summary is a
   `--jp-surface-2` card, `border-radius: var(--jp-radius-m)`, `min-height: 56px`,
   `padding: var(--jp-space-2) var(--jp-space-3)`, title `--jp-font-body` 600, text
   `--jp-font-small` muted, ellipsis; `list-style: none` and `::-webkit-details-marker { display:
   none }`; chevron rotates 180° when `[open]`. Inside the opened form, `.selectContainer`
   `margin: var(--jp-space-2) 0`, `.selectLabel` `flex-basis: 5.5em`, and `.detailTrackSelect`
   gets `min-height: var(--jp-tap)` so the native picker is a 44 px target.
5. Keyboard / a11y: `<summary>` is natively focusable and toggles on Enter / Space;
   `aria-controls` is not available across the sibling boundary, so set `aria-expanded` on the
   summary by hand in the `toggle` handler.

Verify (B): on a multi-audio item, the summary names the default audio; picking another audio in
the opened disclosure changes the summary within a frame and the subsequent Play uses that
stream (check `AudioStreamIndex` in the `PlaybackInfo` request); an item with one version and one
audio and no subtitles shows "Watch options / 1080p H264 · English…" (video container shown) or
stays hidden entirely if stock hides the whole form; items whose form has `hide` (a Series page,
a Person) show no row; `?jpTheme=off` -> stock selects; TV -> stock.

### Task C - `web/src/view/detail-overview.js` - `feature('detail overview', installDetailOverview)`

Overview that expands on tap, 4 lines on a phone.

Stock (12.3 `renderOverview`): `.overview` gets `detail-clamp-text` (`-webkit-line-clamp: 3`,
`overflow: hidden`) and, only when `scrollHeight > offsetHeight` after render, the
`.overview-expand` button loses `hide`; its click calls `toggleLineClamp` which toggles the
clamp class and swaps the button text (`ShowMore` / `ShowLess`). `overview` is also set on
`.overview-container` items on other pages: scope to `#itemDetailPage` only.

Behaviour:

1. CSS (`<style id="jpDetailOverviewStyle">`, scope 2.3, plus `#itemDetailPage`):
   `.overview.detail-clamp-text { -webkit-line-clamp: 4 }`; `.overview { cursor: pointer;
   -webkit-tap-highlight-color: transparent }` only while `.overview-expand:not(.hide)` follows
   it (`.overview:has(+ .overview-expand:not(.hide))`); `.overview-expand`: render as a plain
   accent text button `font-size: var(--jp-font-small); font-weight: 600; color: var(--jp-accent);
   min-height: var(--jp-tap); padding: 0; margin: 0`, left-aligned under the paragraph.
2. JS: one delegated `click` listener on `document` for `#itemDetailPage .overview`; when the
   sibling `.overview-expand` exists and is not `.hide`, call `expand.click()` - **never**
   toggle the class yourself, so the stock button text and state stay consistent. Ignore clicks on
   an `<a>` inside the overview (markdown links) by checking `e.target.closest('a')`.
3. The clamp-needed test in stock ran against 3 lines; with 4 lines a 4-line overview now fits
   but stock still shows the "Show more" button (its `scrollHeight` test ran before our CSS -
   or after, depending on style-tag timing). Handle it: on `watchDetail` `item()`, after a
   `requestAnimationFrame`, if `overview.scrollHeight <= overview.clientHeight + 1` add `hide` to
   `.overview-expand` and remove `cursor: pointer` (drop a `jpOverviewFits` class on the page).
   If it does need the clamp, leave stock alone.

Verify (C): long overview -> 4 lines + "Show more"; tap the paragraph -> full text, button says
"Show less"; tap again -> clamped; an overview of exactly 3-4 lines -> no button, not tappable; a
markdown link inside the text opens the link and does not expand; `?jpTheme=off` -> stock 3 lines.

### Shared-file lines (one each, additive, per task)

- `web/src/bootstrap.js`: an `import` next to the other `detail-*` imports and one
  `feature('detail hero' | 'detail tracks' | 'detail overview', install…)` line placed **after**
  `feature('detail actions', installDetailActions)` and before `feature('bottom nav', …)`.
- `context-map.md`: one row in the `view/` block (and one for `model/detail-tracks.js`, Task B).
- `ENHANCEMENTS.md`: one row in the client table (section 7 area, beside the other details-page
  rows: "Item page: primary Play and a sticky action bar"). Columns as the neighbours: what,
  where, fail-soft, access ("no new requests; no access change").

## 4. Rules for the implementing agents

- Read `AGENTS.md` and `CLAUDE.md` first; the brief in the scratchpad (`livetestbox.md`) says
  what is on CT114. **No deploy, no SSH, no restart**; the main thread merges, builds once,
  deploys once and verifies live.
- Build with `cd web && npm install` (once) then `JP_NO_MINIFY=1 scripts/build-web-panel.sh`;
  the build passing is the compile check, it proves nothing about the page. Unit tests only for
  `model/detail-tracks.js`.
- Commit with `GIT_AUTHOR_EMAIL=sanchitd5@live.com GIT_COMMITTER_EMAIL=sanchitd5@live.com` (and
  the matching `*_NAME`); the `git -c user.email` form is refused by the hook.
- Module shape as its neighbours: `var STYLE_ID`, a `CSS` array joined by `\n`, `installX()`
  exported, the whole body in `try { } catch (err) { log('…: install failed', err) }`, an
  explanatory block comment at the top in the house style (what stock does, what this changes,
  why, fail-soft).
- Do not import from `detail-actions.js` or `detail-related.js`; import only
  `lib/log.js`, `lib/dom.js` (`el`) and `controller/detail-page.js` (`watchDetail`).
- Never read the item through a new request; `watchDetail` gives the DTO.
- Report what was verified against what was assumed. The main thread will open each page state in
  2.3's scope and its three off-ramps (theme off, landscape, TV) before calling any of it done.

## 5. Out of scope, noted for later

- Tablet (40-64em) layout: side-by-side poster + text, two-column details.
- A "Technical" disclosure for the media-info table (`btnMoreCommands` -> Media Info dialog today).
- Pulling the hero's dominant colour into `--jp-bg` for the scrim (needs a canvas read of a
  cross-origin-safe image; the blurhash already decoded in `detail-blurhash.js` is the obvious
  source: average of the 32x32 canvas).
- Replacing native `<select>`s with a plugin sheet (`view/sheet.js`): would mean re-implementing
  the stock change handlers' media-source switch; deferred until the disclosure proves too small.

## 6. Existing modules on this page (do not duplicate)

| module | does |
|---|---|
| `controller/detail-page.js` | `watchDetail()` - the one item fetch per page; use it |
| `view/detail-actions.js` | `jpPrimaryPlay`, sticky bar (P7) |
| `controller/detail-nav.js` | prev / next buttons (`.jpNavBtn`) in the button row |
| `controller/detail-download.js` | unhides `.btnDownload` for downloadable videos |
| `controller/detail-related.js` | `.gprChips` (people + tags) after `.itemTags`; fills `#similarCollapsible` |
| `controller/detail-timeline.js` | per-person timeline rows |
| `view/detail-blurhash.js` | blurhash canvas under `#itemBackdrop` and the poster |
| `view/detail-fade.js` | 120 ms backdrop fade |
| `view/image-zoom.js` | poster tap -> full-size overlay; `.cardScalable::after` zoom badge |
