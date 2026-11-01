# Item details page on a phone: UX review of the shipped pass

Read-only review of the LIVE page on CT114 (jellyfin-web 12.3) after Tasks A (hero), B (watch
options) and C (overview) from [mobile-detail-page.md](mobile-detail-page.md) were merged and
deployed (main at `b639114`). Reviewed with Chrome DevTools emulation: 390x844 @3x iPhone UA,
360x800 @3x, 844x390 landscape, and `?jpTheme=off`. Four items were walked top to bottom: one with
~124 tags + 1 person + 1 genre, two with ~105 tags, and one with a Resume state and almost no
fields. No item in the library (6,000 scanned by overview length, every Episode / Series / Movie
query) has an overview longer than 200 characters, more than one audio stream, or any
subtitle stream, and there are no Episodes or Series at all. **Task C's clamp/expand and Task B's
"change audio, summary follows" could therefore not be exercised live**; what is said about them
below is from the rendered CSS/DOM and the code, and is marked so. No item metadata, server
setting or playback was touched; the only browser-side state changed was the
`jellyfinPlus.mobileTheme` localStorage key, which the `?jpTheme=off` test set and which was
removed again (= default look) afterwards.

Measured numbers below are CSS px at 390x844 unless stated.

## What works (verified live)

- Scope and off-ramps: `?jpTheme=off` gives the stock page (`.infoWrapper` padding 37.5 %,
  centred title, native selects, 3-line clamp, no `::after` labels, no `::after` scrim, no
  `.jpTracks`). Landscape 844x390 classifies as `jp-tablet` (>= 40em) and renders stock too,
  with the P7 wide Play (by design, `detail-actions.js` covers `jp-tablet`). No horizontal
  overflow anywhere (`scrollWidth == innerWidth` on every page, every width).
- Hero: backdrop 321 px (38vh, within the 220-360 clamp), bottom scrim present (`::after` 144 px).
  Title left-aligned, 2-line clamp holds (37-char title = 2 lines at 21 px / 19.8 px on 360).
  Meta line is one line, no overflow (`scrollWidth == clientWidth`), `Ends at` back.
- Button row: Play is a 364x44 accent bar; the icon buttons are 72x56, all >= 44 px. Labels come
  from `title` (verified `::after` content for Download / Mark played / Add to favourites / More).
  Resume item: primary reads "Resume", the stock replay button follows as an icon.
- Watch options: row 56 px tall (tap target fine), summary text filled, open/close toggles
  `jpTracksOpen`, the stock form appears below it (`display:block`, 92 px for two rows) with
  44 px selects; closed again on next item. Row absent when the item is not a Video (not
  observed - no such item exists - code path only).
- Overview: `-webkit-line-clamp: 4` applied; short overviews show no "Show more" button.
- Bottom of page: last section bottom = 2160, document = 2245, bottom nav 56 px fixed, page
  `padding-bottom: 72px` -> nothing is hidden under the nav on any of the four items at either
  width. Cast, Timeline, More Like This rows render and scroll; nothing clipped or empty.

## Findings

### P0-1 The sticky action bar never shows (hidden behind the app bar)

- **What**: once the button row scrolls out, `.jpStickyBar` gets `jpShow` and is laid out
  (390x52) at `top: 0px`, `z-index: 998`. The 12.3 app bar is the MUI `header.MuiPaper-root`
  (48 px, `z-index: 1100`, fixed at top 0). `headerBottom()` in
  `web/src/view/detail-actions.js` queries `.skinHeader`, which 12.3 no longer renders, returns 0,
  and the bar is drawn fully under the app bar. Confirmed by screenshot at scrollY 560 and 700:
  no bar visible; by `getBoundingClientRect()` top 0, height 52.
- **Why it hurts**: the whole thumb-reach story of P7 - Play reachable without scrolling back
  to y=434 - is silently absent on a phone. This is also the sort of "renders, is stored, never
  reaches the user" failure CLAUDE.md warns about.
- **Fix** (`web/src/view/detail-actions.js`, JS, one function): `headerBottom()` should look for
  the first of `.skinHeader, .MuiAppBar-root, header.MuiPaper-root` with a non-zero height, and
  the bar's rule should get `z-index` above the app bar is **not** the right answer (it must sit
  *under* the header, so `top` must equal the header bottom). Fail-soft: if none is found, keep
  today's `0`. Also re-run `headerBottom()` on `resize`/orientation, since the IO `rootMargin` is
  computed once. Done = at 390x844, scroll past the row: a 52 px bar is visible directly under
  the 48 px app bar, Play in it starts playback (the proxy already clicks the stock twin).

### P1-1 Watch options row draws every icon twice

- **What**: `.jpTracksSummary .tune` is 48 px wide and the chevron 48 px, against 21 px for a
  stock icon. 12.3 renders Material icons from the **class** (`.material-icons.tune::before`
  has glyph content, length 3) *and* the ligature text `tune` the module also sets, so each glyph
  appears twice: two sliders on the left, two chevrons on the right (both rotate on open).
- **Fix** (`web/src/view/detail-tracks.js`, `build()`): follow stock - class only, no text:
  `el('span', 'material-icons tune')` / `el('span', 'material-icons expand_more jpTracksChevron')`
  with `aria-hidden` as now. Other plugin icons (`gprChipIcon`, bar buttons) already use one of
  the two mechanisms and render once; grep for `'material-icons <name>', '<name>'` finds only
  this file. Done = each icon 21-24 px wide.

### P1-2 Icon labels truncate on the labels that matter

- **What**: labels are 11 px, `max-width: 72px`, one line, ellipsis. Natural widths: Download
  52, Mark played 64, Add to favourites 89, More 27 (measured). "Mark play…" and "Add to fav…"
  render on every item at both widths. The favourite button's label swaps to "Remove from
  favourites" when set - also truncated. (After the fix the first word of each is still the
  one that carries the meaning, which is why two lines beat a wider box.)
- **Fix** (`web/src/view/detail-hero.js`, the `::after` rule): allow two lines -
  `white-space: normal; display: -webkit-box; -webkit-box-orient: vertical;
  -webkit-line-clamp: 2; text-align: center; overflow-wrap: anywhere; line-height: 1.15` and
  `min-height: 64px` on the button; keep `max-width: 72px`. Done = all four stock labels fully
  readable at 360 px; row height <= 66 px.

### P1-3 Tags wall: 535 px of chips above the genre, the cast and everything else

- **What**: 124 tags render as one `.gprChipRow` (`detail-related.js`, fold at 40): 40 chips +
  "+83 more" = **15 rows, 535 px** (643 px at 360 wide), at y 761-1296. Tapping "+N more" makes it
  1401 px and the page 3215 px. Each chip is 30 px tall (`min-height: 2.5em` at the chip's
  12.07 px font) with a 5.9 px gap - below the 44 px tap guideline and tightly packed, so a
  thumb lands on a neighbour easily. Order on the page is overview -> person chip -> 15 rows of
  tags -> **genre** (P1-4) -> Cast & Crew (y 1373) -> Timeline -> More Like This. On the two
  ~105-tag items it is the same shape (~13 rows).
- **Why it hurts**: the page's actual navigation (people, genre, related videos, timeline) sits
  under 1.5 screens of low-value tokens; the fold at 40 was tuned for a desktop line, not a 351 px
  column. A phone user scrolls past it every time.
- **Fix** - in `web/src/controller/detail-related.js`, which owns `.gprChips`; yes it belongs there
  (the chips are its DOM, Task A's style is CSS-only, and the rule is "restyle, never re-create").
  Two parts, both presentation only:
  1. Phone fold: when `html.layout-mobile` (or a `(max-width: 40em)` match) fold tags at **8**
     (`CHIP_FOLD_PHONE`), keep 40 elsewhere; the "+N more" button stays, and its click still
     reveals everything (one tap, as now). Two to three rows (~100 px) instead of 15.
  2. Tap size on phones, in the module's own style: `html.layout-mobile .gprChip { min-height:
     36px; font-size: 13px }` and `html.layout-mobile .gprChipRow { gap: 8px 6px }`. 36 px with
     8 px of row gap gives a 44 px pitch per row, which is the effective target.
  Not a disclosure, not a horizontal scroller: both hide the tags that are the only way to reach
  a tag search from this page, and a horizontal row of 124 fights the vertical scroll.
  Invariants: no new request, same `href`s (the search page enforces access as before), the
  stock `.itemTags` line is still hidden only after the chips rendered (`gprHasChips` order
  unchanged); failure anywhere leaves stock. TV is already excluded by the module.
  Done = on the 124-tag item at 390x844 the tags block is <= 120 px closed; the Cast section's
  top is above y 1000; "+116 more" expands in place; `?jpTheme=off` unchanged (the chips are a
  QoL feature, not part of the theme, and render in both states today - leave that).

### P1-4 The genre is indistinguishable from a tag and comes last

- **What**: 12.3 renders genres in `.itemDetailsGroup` (after `.detailSectionContent`), so the
  genre chip (Task A, `order:-1` inside the group, label hidden) lands **after** the 535 px tag
  wall, as a lone chip with the same pill, same colour (`rgb(170,178,191)`) and only
  `font-weight: 600` to tell it apart. A user reads it as one more tag (or a person).
- **Fix** (`web/src/view/detail-hero.js`, CSS). Genres (`.itemDetailsGroup`) and tags
  (`.gprChips` inside `.detailSectionContent`) live in different stock parents, so CSS cannot
  interleave them as the spec's picture shows. Two honest options: (a) keep the genre after the
  chips but make it unmistakable - accent treatment `border-color: var(--jp-accent, #00a4dc);
  color: var(--jp-accent)` and restore the small muted "GENRE" label above it (drop the
  `display:none` on that `.label`); or (b) `.detailSection { display: flex; flex-direction:
  column }` with `order` so the whole `.itemDetailsGroup` (genre, and Directors / Writers rows
  when present) sits right under the buttons, before the overview. Recommend (a): overview and
  people/tags stay first and the genre reads as what it is. Done = the genre row has a visible
  muted "GENRE" label and accent border; nothing else moves.

### P1-5 Hero composition depends on the primary image's shape

- **What**: the spec assumed a 2:3 poster; this library's primaries are square or landscape
  contact sheets. Measured poster boxes: 105x105 (square item: overlaps the backdrop by 6 px),
  97x55 at 360 wide (landscape item: sits **42 px below** the backdrop's bottom edge, a 55 px
  thumbnail floating beside a two-line title), 105x158 (the one portrait item: 57 px overlap,
  as designed). The `.infoWrapper` min-height is sized for 2:3 (`27vw*1.5*0.6`), so with a flat
  poster the title block is taller than the poster and the composition reads as a gap.
- **Fix** (`web/src/view/detail-hero.js`, CSS only, no transform): give the hero poster a fixed
  box regardless of card shape - `.infoWrapper .detailImageContainer .card .cardScalable
  .cardPadder { padding-bottom: 133% !important }` (3:4; the image container is
  `background-size: cover`, so a square or wide sheet is centre-cropped, which is fine for a
  thumbnail that opens the full image on tap) and size `WRAP_MIN` from the same ratio
  (`27vw * 1.33 * 0.6`). Check the `.cardScalable::after` zoom badge and the blurhash canvas
  still sit inside the new box. If a cropped sheet is unacceptable, the alternative is to drop
  the overlap idea and anchor the poster at the top of `.infoWrapper` (`top: 0; bottom: auto`),
  which is honest for any shape. Done = on the three shapes the poster's top edge is above the
  backdrop's bottom edge by >= 30 px and the title block never overhangs below the poster.

### P2-1 Watch options is noise when there is nothing to choose

- **What**: on every item reviewed each shown select has exactly one option (one video, one
  audio, no subtitles). The row (56 px + 24 px margins) opens to two inert 44 px selects with no
  arrow affordance (`.selectArrowContainer` is `display:none`, `appearance:none`, no background
  image), label and value vertically misaligned (label baseline ~12 px above the value because
  `.selectContainer` is `align-items: normal` with a 44 px select). It reads as a broken form.
- **Fix** (`web/src/view/detail-tracks.js`): in `refresh()`, compute `choice = entries.some(e
  => e.shown && e.options > 1)` (add `options: sel.options.length` to `readEntries`); when
  false, keep the row (the summary is the only place the stream is named on a phone) but render
  it inert: add `jpTracksStatic` on the `<details>` -> CSS hides the chevron, `pointer-events:
  none` on the summary, `aria-expanded` removed. When true, also fix the open form: `.trackSelections
  .selectContainer { align-items: center }` and `.detailTrackSelect { padding-inline-end: 1.6em;
  background: url(<chevron svg>) no-repeat right center }` or re-show `.selectArrowContainer`.
  Done = single-option item: row shows, no chevron, no toggle; a two-option item (none in this
  library - verify with a test file or by code) opens and the summary follows the change.

### P2-2 Original-title line duplicates the title

- **What**: the three tag-heavy items show `.itemName.originalTitle` whose text equals the title
  case-insensitively (verified); it costs an 18 px line (35 px, two lines, in stock) and reads as a
  stutter under a 2-line title.
- **Fix**: `detail-hero.js` is CSS-only by charter; the cheapest honest place is a three-line
  `watchDetail` hook in **`web/src/view/detail-overview.js`** (already has the hook and the page):
  `if (item.OriginalTitle && item.Name && item.OriginalTitle.toLowerCase() === item.Name.toLowerCase())
  page.classList.add('jpSameTitle')` and in its CSS `#itemDetailPage.jpSameTitle .itemName.originalTitle
  { display: none }`. Fail-soft: class only, stock node untouched. Done = no second line when
  equal; present when different.

### P2-3 Resume state has no visible progress and two "Play"s

- **What**: the Resume item shows "Resume" (no position - stock 12.3 puts none in `title`) and the
  next icon is the stock replay button labelled **"Play"** (its `title`), so the row reads
  "Resume / Play / Download…" with nothing saying "from the beginning". No progress bar appears on
  the poster (`.itemProgressBar` absent on the detail card in 12.3) and none under the title.
- **Fix** (`web/src/view/detail-actions.js`, where `markPrimary` already reads the stock state):
  when the primary is Resume and `item.UserData.PlaybackPositionTicks > 0`, set the replay
  button's `title` to "Play from start" (restoring it on teardown) so the `::after` label follows;
  optionally append a 2 px `--jp-accent` progress line under the primary button from
  `PlaybackPositionTicks / RunTimeTicks` (pure CSS width on a child span, no request). Done =
  Resume item shows "Resume" + "Play from start"; the progress line matches the stock card's
  percentage on the home row.

### P2-4 Spacing and length

- **What**: themed page is 2245 px vs 2128 stock at 390 (2300 vs 2190 at 360): the hero is
  +68 px, labels +12 px, the Watch options row +80 px. Gaps: buttons -> Watch options 35 px,
  Watch options -> overview 15 px, overview -> chips 23 px, chips -> genre 19 px. The empty stock
  `.itemGenres` `<p>` (0 px) and `.overview-controls` (0 px) cost nothing. Nothing is oversized
  except P1-3; the above-the-fold at 844 ends at the Watch options row (y 633), so the overview
  starts just below the fold.
- **Fix**: nothing on its own; P1-3 and P2-1 recover ~450 px and bring the overview above the
  fold. If more is wanted: `.jpTracks { margin: var(--jp-space-2) 0 }` (-8 px).

### P2-5 Verification gaps (not defects)

- Task C: no overview > 200 chars exists, so 4-line clamp + tap-to-expand + "fits" suppression
  are unverified live. Code review: the delegated click calls the stock button; the fit check only
  ever adds `hide`. A manual check needs an item with a long overview edited in (not done:
  metadata is off-limits for this review).
- Task B: no multi-audio or subtitle item exists, so "pick another audio -> summary updates ->
  Play uses it" is unverified live; `model/detail-tracks.test.mjs` covers the text only.
- Episode header / parent name: no Episodes or Series in the library.
- `env(safe-area-inset-bottom)`: emulation reports 0; the 72 px page padding + 56 px nav leave
  16 px of slack, which a real notch phone's inset (34 px) would exceed only if the nav does not
  itself add the inset - check on a device.

## Work packages (disjoint files, for Sonnet implementers)

| WP | Owns | Fixes | Done when |
|---|---|---|---|
| **WP-1 sticky + tracks** | `web/src/view/detail-actions.js`, `web/src/view/detail-tracks.js` | P0-1, P1-1, P2-1, P2-3 | At 390x844 the sticky bar is visible under the 48 px app bar after scrolling past the row and its Play starts playback; Watch options icons render once; single-option items show an inert row without a chevron; the open form's label and value are vertically centred; the Resume item's second button is labelled "Play from start". `?jpTheme=off` and landscape unchanged. |
| **WP-2 hero CSS** | `web/src/view/detail-hero.js`, `web/src/view/detail-overview.js` (P2-2 hook only) | P1-2, P1-4, P1-5, P2-2 | All four stock icon labels fully readable at 360 px on two lines; the genre row carries a visible "GENRE" label and accent border; the poster box is 3:4 on square, wide and portrait primaries and overlaps the backdrop by >= 30 px on each; identical original title is hidden; zoom badge and blurhash still in place. |
| **WP-3 tags** | `web/src/controller/detail-related.js` | P1-3 | On the 124-tag item the closed tags block is <= 120 px at 390 wide with a "+N more" that expands in place; chips are >= 36 px tall with a 44 px row pitch; desktop fold stays 40; stock `.itemTags` still hidden only after the chips exist; no new request; hrefs unchanged. |

Shared files: none need touching. Build with `JP_NO_MINIFY=1 scripts/build-web-panel.sh`;
the main thread deploys once and re-walks the four items (ids in the brief) at 390x844 and
360x800, scrolled to the bottom, before calling any of it done.
