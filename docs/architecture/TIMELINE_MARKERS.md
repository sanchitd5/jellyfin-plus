# Timeline markers: the provider contract

Any external service can put markers on the Jellyfin Plus player's seek bar. The service is a
**provider**: the Jellyfin server asks it about an item while that item plays, normalises the answer
and hands it to the player. Players never talk to providers. Nothing about a particular provider is
in code: every provider is configuration.

- Server: `src/MarkerProviders.cs` (parsing, path filter, mapping), `src/MarkerSource.cs` (fetching,
  cache, merge, moment times), `src/MarkersController.cs` (routes), `src/MomentSheets.cs`
  (moment-preview generation, strips, storage), `src/MomentPrecompute.cs` (nightly task, new-item
  queue, check index).
- Client: `web/src/model/timeline-markers.js` (pure), `web/src/controller/timeline-markers.js` (OSD).

## Configuring providers

A provider list is a JSON array. It comes from, in order of precedence:

1. The environment variable **`JELLYFIN_PLUS_MARKER_PROVIDERS`** of the Jellyfin process, when set and
   not blank. Read **at request time** (re-parsed whenever its text changes), but a process's
   environment only changes on restart, so in practice it takes effect at the next Jellyfin start.
   It replaces the configured list entirely.
2. The plugin setting **`MarkerProviders`** (Dashboard > Plugins > Jellyfin Plus > Integrations, a
   JSON text box). Takes effect on save, no restart. The page refuses to store text that is not a JSON
   array: the previous value stays and the error is shown under the box.

Default: `[]`, the feature is off and players make no marker requests at all.

An invalid entry (bad id, duplicate id, bad URL) is skipped with a warning in the Jellyfin log; the
rest still work. Comments (`//`) and trailing commas are accepted.

### Provider entry

| Key | Default | Meaning |
|---|---|---|
| `id` | required | `[A-Za-z0-9_-]{1,32}`, unique. Returned as each marker's `source`. |
| `enabled` | `true` | `false` keeps the entry but never asks it. |
| `urlTemplate` | required | Absolute `http`/`https` URL. Placeholders, each URL-encoded: `{path}` (the item's media file path on the Jellyfin server), `{itemId}` (32 hex, no dashes), `{name}` (item name). A placeholder in the scheme or host is refused, so no item can steer the request elsewhere. |
| `pathPrefix` | none | The item's path must start with this (case-sensitive). |
| `excludePrefixes` | none | Paths starting with any of these are never asked about. |
| `extensions` | none | e.g. `["mkv", ".mp4"]`, case-insensitive. Empty: any. |
| `itemsField` | `markers` | Dotted path to the marker array in the response, e.g. `data.items`. |
| `offsetField` | `offsetSeconds` | Marker field: seconds from the start of the item. |
| `labelField` | `label` | Marker field: text shown on hover. |
| `valueField` | `value` | Marker field: optional number; the player sizes markers by it. |
| `valueUnit` | none | Text shown after every value from this provider, e.g. `tokens` (at most 24 characters). Returned as each valued marker's `unit`. |
| `kindField` | `kind` | Marker field: optional style key. |
| `kind` | none | A constant kind for every marker from this provider (overrides `kindField`). |
| `coverageField` | none | Dotted path to a boolean. When configured: missing or non-boolean is a failure, `false` means "no data for this item" (no markers). |
| `timeoutSeconds` | `5` | 1 to 30. |
| `cacheMinutes` | `10` | How long an answer is reused, 0 to 1440. Failures are reused for 1 minute. |

Any item with a path containing a `..` segment is never sent to a provider. An item without a path
only matches a provider that has no path filter and does not use `{path}`.

## What a provider answers

`GET <urlTemplate>` with placeholders filled. Any 2xx with a JSON body of at most 2 MiB. A provider
that speaks the **generic contract** needs no mapping keys:

```json
{
  "markers": [
    { "offsetSeconds": 37.0, "label": "Something happened", "value": 20, "kind": "highlight" }
  ]
}
```

Anything else (non-2xx, timeout, oversized body, not JSON, `itemsField` not an array) counts as a
failure: that provider contributes nothing and the others are unaffected. Per marker: an offset that
is not a finite number, is negative, or is past the item's runtime is dropped; a non-string label
becomes empty, a longer one is cut at 200 characters; a non-numeric value is dropped; a kind not
matching `[a-z0-9_-]{1,32}` (after lower-casing) is dropped. At most 2000 markers per provider.

## What the player gets

```
GET /JellyfinPlus/Markers/Enabled      any authenticated user
  -> { "enabled": true, "moments": true }   enabled: at least one valid, enabled provider exists;
                                            moments: moment sheets are on (MarkerMoments)

GET /JellyfinPlus/Markers/{itemId}     any authenticated user
  -> { "markers": [ { "offsetSeconds": 37.0, "label": "...", "value": 20, "kind": "tip", "source": "<provider id>" } ] }
```

`value`, `unit` and `kind` are omitted when absent. Markers from every matching provider are merged and
sorted by offset. 404 when the item does not exist **or the user cannot see it** (the same answer, so
existence does not leak; the item is resolved with the requesting user's own access). Provider
failures never surface: the answer is 200 with whatever the other providers gave, possibly empty.

The player asks `Enabled` once per page load (again five minutes after a failure) and makes no
per-item requests while it is false. It draws one dot per marker over the OSD position slider:
styled by `kind` (`tip`, `chapter`, `highlight` have their own colours; any other kind is neutral),
sized by `value` relative to the largest in the list, `label` (plus value) as the tooltip via the
`title` attribute only, never as HTML. A click seeks to `max(0, offsetSeconds - 3)`. Nothing is drawn
on the TV layout.

## Moment previews

Hovering or focusing a dot (touch: the first tap, or tap-and-hold; a second tap seeks) opens a popup
above the seek bar: `label · value unit` as plain text, and a strip of five frames, the marker's own
(highlighted) plus two either side 5 s apart, clamped to `[0, duration - 1]` with near-duplicates
(under 1 s) collapsed. Clicking a frame seeks to that frame's time.

Frames come from, in order:

1. The item's **stock Jellyfin trickplay**, when it has any (`Fields=Trickplay`; the smallest width
   >= 200, else the largest). Tile for a time: `floor(ms / Interval)`, on sheet
   `floor(n / (TileWidth * TileHeight))`, exactly as jellyfin-web does.
2. Otherwise **moment sheets**, generated by the plugin (setting **`MarkerMoments`**, default on,
   Integrations tab):

```
GET /JellyfinPlus/Markers/{itemId}/Moments          any authenticated user, Cache-Control: no-store
  -> 200 { "status": "ready", "version": "<key>", "width": 160, "height": 90,
           "tileWidth": 10, "tileHeight": 10, "times": [ 90.0, 95.0, ... ] }
  -> 202 { "status": "pending", "version": "<key>", ... }   generating; ask again
  -> 200 { "status": "none", ... }   off, no markers, stock trickplay exists, or a recent failure
GET /JellyfinPlus/Markers/{itemId}/Moments/{n}.jpg?v=<key>
  -> sheet n of the ready set; 404 otherwise, or when v is not the current set's key.
     With v: Cache-Control: private, max-age=31536000, immutable (the key is the content).
GET /JellyfinPlus/Markers/{itemId}/Moments/Marker/{i}.jpg?t=<offsetSeconds>
  -> one marker's strip (see "Runtime strip" below)
```

Tile `i` is on sheet `floor(i / 100)`, row-major; `times[i]` is its time (times are irregular, so
the manifest lists them rather than giving an interval). The server takes the times from **its own
merged marker list**, never from the request: each marker plus ±5 and ±10 s, at most 200 frames
(highest-value markers first). Frames are decoded with the ffmpeg Jellyfin is configured with
(input-side seek, one process per frame, killed after 30 s), 160x90 (aspect kept, padded), one
JPEG encode at `-q:v 10`. One generation per set key at a time whoever asks (viewer, prewarm,
nightly task or new-item queue), failures remembered for 10 minutes. Nothing is written beside the
media, and stock trickplay is never generated. 404 for an item the user cannot see, as above.

A sheet that fails to load hides only its own frames; with no source at all the popup shows the
label and value only.

### Where frames come from, fastest first

1. **Precomputed** (setting **`MarkerMomentsPrecompute`**, default on; `src/MomentPrecompute.cs`).
   The full set normally exists before anyone presses play:
   - **Scheduled task** *Precompute marker previews* (Dashboard > Scheduled Tasks, category
     *Jellyfin Plus*, key `JellyfinPlusMomentPrecompute`, default trigger daily 03:00, can be run by
     hand). Walks every non-virtual Video/Movie/Episode/MusicVideo (library query, filtered in code -
     no SQL against `jellyfin.db`): skips items no enabled provider's filter matches, without a
     duration, or with stock trickplay; skips items the check index says are unchanged (below);
     otherwise fetches the merged markers **with no user** (the same merge as `GET /Markers/{id}`
     minus the access step, which only HTTP requests take; this path has no route) and makes the set
     through the same `MomentSheets` code as a viewer. Reports progress, stops between items when
     cancelled, then deletes orphans and logs `store N MB in S sets + K strips`.
   - **New items**: `ItemAdded`/`ItemUpdated` put a matching Video on one bounded (2000), de-duplicated
     queue drained by one worker, each item no earlier than 2 minutes after it arrived (the file is
     complete, the provider has had time). Overflow is dropped; the nightly run covers it. A full scan
     costs one in-memory check per item.
   - **Load**: background frames use **one** ffmpeg at a time at **idle** priority, one frame at a
     time; before each frame they wait while any viewer's generation or strip is running or any session
     is transcoding (`ISessionManager.Sessions[].TranscodingInfo`, cached 5 s). A viewer asking for a
     set the background is making promotes it to the live lane (two ffmpeg, below-normal) for its
     remaining frames. Background provider calls are spaced to at most **4 a second** and never fill
     the viewers' in-memory marker cache.
   - **Recheck policy** (`precompute-index.json` in the store, "checked at T for file key K: ready /
     none / trickplay"; K = path + size + mtime as the library records them): an item is asked again
     when its file key changed, when its last check is **14 days** old, or - for items added in the
     last **2 days**, whose markers may still be arriving upstream - on every nightly run (and at
     most every 6 h from library events). A provider failure records nothing, so the item is retried
     next run. Items found with no markers or with stock trickplay lose any set they had.
2. **On first ask** (`GET .../Moments`): a viewer's miss generates at once in the live lane; the web
   client also asks at playback start (prewarm), as before.
3. **Runtime strip** - for an item whose full set is not ready yet, so a hover or click on any marker
   still gets frames in well under a second:

```
GET /JellyfinPlus/Markers/{itemId}/Moments/Marker/{i}.jpg?t=<offsetSeconds>
  -> 200 image/jpeg, the marker's five frames side by side (160x90 each),
     X-Moment-Strip: {"width":160,"height":90,"times":[90,95,100,105,110]}
  -> 409 + the ready manifest when the full set exists (use the sheets instead)
  -> 404 off, stock trickplay, no such marker, item missing or not visible
  -> 503 could not be made within 10 s (or failed in the last 10 minutes)
```

   `i` indexes the server's own merged list (`GET /Markers/{id}` order); `t`, when given, must agree
   with it within 0.5 s, else the marker nearest `t` (within 0.5 s) is used, else 404 - frame times
   never come from the request. One ffmpeg process with one input-side seek per frame (`-threads 2
   -ss T -i file` five times, scaled/padded, `hstack`, `-q:v 10`), in the live lane: its own two-slot
   gate, below-normal priority, the background waits while it runs; de-duplicated per key, killed
   after 10 s. Measured on CT114 (stock jellyfin-ffmpeg, h264 1080p recordings, host load ~6-7 of 8):
   **390-770 ms** per strip (median ~530 ms), 5-13 KB. Strips are cached under the item
   (`strips/`), `Cache-Control: private, max-age=86400` (not immutable: the URL names a marker, not a
   file version), and deleted when the item's full set is made or the file changes.

The web client picks per popup: stock trickplay; else the ready set's sheets; else that marker's
strip (label and value show immediately, frames when the strip lands), falling back to polling the
manifest if the strip fails. Strips are kept per item in the client's item cache (`entry.strips`).
At playback start it also fetches the strips of the **5 highest-value markers** when there is no
full set, while the manifest poll drives the full set.

### Storage: kept forever, small

Sets and strips are **kept indefinitely** in Jellyfin's **data** path,
`jellyfin-plus-moments/{itemId}/{key}/` and `.../strips/` - not the cache path, which Jellyfin's own
*Clean Cache Directory* task empties by age (the first version's cache-path store is removed by the
nightly run). There is no size cap and no LRU. Only orphans are deleted: an item's older sets when a
new set replaces them (other markers, replaced file, older format - the key includes a format
version, so the earlier 320 px sets are re-made at 160 px on next request or run), strips of a
replaced file or superseded by the full set, and every set of an item that no longer exists, now has
stock trickplay or has no markers any more.

Size, measured on CT114: **~1.26 KB a frame** in a 10x10 sheet at 160x90 `-q:v 10` (1.5 KB as a lone
JPEG; `-q:v 4` was 2.4 KB; WebP q50 was ~19% smaller in a sheet, not worth a second format). A
read-only sample of 120 provider-matching recordings without trickplay found markers on 16 (13%),
median 2 markers, ~31 frames per item with markers. Of ~48,000 matching videos that is roughly
**6,400 items, ~200,000 frames, ~250 MB** (at 200 frames for every item, the worst case, ~12 GB).
A first full run is bounded by provider calls and frames: ~48,000 checks at 4/s is ~3.3 h of
upstream calls, plus ~200,000 frames at ~0.3-0.4 s each on one idle core (~17-22 h, longer while
people watch); later runs only recheck changed, recent or 14-day-old items.

A provider can name the value's unit with the entry key **`valueUnit`** (at most 24 characters, e.g.
`"tokens"`): markers with a value then carry `"unit"`, shown as `label · 100 tokens`.

## Example

A recorder that keeps per-recording events and answers
`{"statusCode":200,"data":{"coverage":true,"events":[{"offsetSeconds":195,"name":"...","value":20}]}}`
(synthetic values):

```json
[
  {
    "id": "recorder",
    "urlTemplate": "http://192.0.2.10:8000/api/recordings/events?path={path}",
    "pathPrefix": "/media/recordings/",
    "excludePrefixes": ["/media/recordings/downloads/"],
    "extensions": ["mkv", "mp4"],
    "itemsField": "data.events",
    "labelField": "name",
    "coverageField": "data.coverage",
    "kind": "tip"
  }
]
```

The same JSON can go in `JELLYFIN_PLUS_MARKER_PROVIDERS`, e.g. in a systemd drop-in for
`jellyfin.service`: `Environment=JELLYFIN_PLUS_MARKER_PROVIDERS=[{"id":"recorder",...}]`.
