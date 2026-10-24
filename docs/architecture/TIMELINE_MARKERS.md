# Timeline markers: the provider contract

Any external service can put markers on the Jellyfin Plus player's seek bar. The service is a
**provider**: the Jellyfin server asks it about an item while that item plays, normalises the answer
and hands it to the player. Players never talk to providers. Nothing about a particular provider is
in code: every provider is configuration.

- Server: `src/MarkerProviders.cs` (parsing, path filter, mapping), `src/MarkersController.cs` (routes,
  fetching, cache).
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
  -> { "enabled": true }               true when at least one valid, enabled provider exists

GET /JellyfinPlus/Markers/{itemId}     any authenticated user
  -> { "markers": [ { "offsetSeconds": 37.0, "label": "...", "value": 20, "kind": "tip", "source": "<provider id>" } ] }
```

`value` and `kind` are omitted when absent. Markers from every matching provider are merged and
sorted by offset. 404 when the item does not exist **or the user cannot see it** (the same answer, so
existence does not leak; the item is resolved with the requesting user's own access). Provider
failures never surface: the answer is 200 with whatever the other providers gave, possibly empty.

The player asks `Enabled` once per page load (again five minutes after a failure) and makes no
per-item requests while it is false. It draws one dot per marker over the OSD position slider:
styled by `kind` (`tip`, `chapter`, `highlight` have their own colours; any other kind is neutral),
sized by `value` relative to the largest in the list, `label` (plus value) as the tooltip via the
`title` attribute only, never as HTML. A click seeks to `max(0, offsetSeconds - 3)`. Nothing is drawn
on the TV layout.

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
