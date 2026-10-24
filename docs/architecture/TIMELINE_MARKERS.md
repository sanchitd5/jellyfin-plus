# Timeline markers: the provider contract

Any external service can put markers on the Jellyfin Plus player's seek bar. The service is a
**provider**: the Jellyfin server asks it about an item while that item plays, normalises the answer
and hands it to the player. Players never talk to providers. Nothing about a particular provider is
in code: every provider is configuration.

- Server: `src/MarkerProviders.cs` (parsing, path filter, mapping), `src/MarkersController.cs` (routes,
  fetching, cache, moment endpoints), `src/MomentSheets.cs` (moment-preview generation and cache).
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
GET /JellyfinPlus/Markers/{itemId}/Moments          any authenticated user
  -> 200 { "status": "ready", "version": "<key>", "width": 320, "height": 180,
           "tileWidth": 10, "tileHeight": 10, "times": [ 90.0, 95.0, ... ] }
  -> 202 { "status": "pending", "version": "<key>", ... }   generating; ask again
  -> 200 { "status": "none", ... }   off, no markers, stock trickplay exists, or a recent failure
GET /JellyfinPlus/Markers/{itemId}/Moments/{n}.jpg  sheet n of the ready set, 404 otherwise
```

Tile `i` is on sheet `floor(i / 100)`, row-major; `times[i]` is its time (times are irregular, so
the manifest lists them rather than giving an interval). The server takes the times from **its own
merged marker list**, never from the request: each marker plus ±5 and ±10 s, at most 200 frames
(highest-value markers first). Frames are decoded with the ffmpeg Jellyfin is configured with
(input-side seek, one process per frame, at most two at a time, below-normal priority, killed after
30 s), on first ask - which the web client makes at playback start, as soon as the item's markers are
drawn, so sheets are ready before the first hover; one generation per item, failures remembered for 10 minutes. Output lives only
under Jellyfin's cache path, `jellyfin-plus-moments/{itemId}/{key}/` (key: item, file size and
mtime, times), least recently used sets pruned above 512 MB. Nothing is written beside the media,
and stock trickplay is never generated. 404 for an item the user cannot see, as above.

A sheet that fails to load hides only its own frames; with no source at all the popup shows the
label and value only.

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
