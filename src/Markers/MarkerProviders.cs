using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Plus
{
    /// <summary>
    /// One configured source of timeline markers: an external service the server asks, per played
    /// item, for points on the seek bar. Admin-set only (plugin configuration or the
    /// JELLYFIN_PLUS_MARKER_PROVIDERS environment variable), never from a request.
    /// Contract: docs/architecture/TIMELINE_MARKERS.md.
    /// </summary>
    public sealed class MarkerProvider
    {
        /// <summary>Gets or sets the short name, [A-Za-z0-9_-]{1,32}; returned as each marker's <c>source</c>.</summary>
        public string Id { get; set; }

        /// <summary>Gets or sets a value indicating whether the provider is asked at all.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets the absolute http(s) URL with placeholders {path}, {itemId}, {name} (URL-encoded). Placeholders may not be in the host.</summary>
        public string UrlTemplate { get; set; }

        /// <summary>Gets or sets the prefix an item's path must start with (ordinal). Null: any path.</summary>
        public string PathPrefix { get; set; }

        /// <summary>Gets or sets the path prefixes that are never asked about, even under PathPrefix.</summary>
        public string[] ExcludePrefixes { get; set; }

        /// <summary>Gets or sets the file extensions that qualify (".mkv" or "mkv", any case). Null/empty: any.</summary>
        public string[] Extensions { get; set; }

        /// <summary>Gets or sets the dotted path to the marker array in the response. Default "markers".</summary>
        public string ItemsField { get; set; } = "markers";

        /// <summary>Gets or sets the field holding a marker's offset in seconds. Default "offsetSeconds".</summary>
        public string OffsetField { get; set; } = "offsetSeconds";

        /// <summary>Gets or sets the field holding a marker's label text. Default "label".</summary>
        public string LabelField { get; set; } = "label";

        /// <summary>Gets or sets the field holding a marker's numeric value. Default "value".</summary>
        public string ValueField { get; set; } = "value";

        /// <summary>Gets or sets the field holding a marker's kind. Default "kind". Ignored when <see cref="Kind"/> is set.</summary>
        public string KindField { get; set; } = "kind";

        /// <summary>Gets or sets a constant kind for every marker from this provider. Optional.</summary>
        public string Kind { get; set; }

        /// <summary>Gets or sets plain text shown after every value from this provider, e.g. "tokens". Optional, at most 24 characters.</summary>
        public string ValueUnit { get; set; }

        /// <summary>Gets or sets the dotted path to a boolean in the response; when present and not true, no markers. Optional.</summary>
        public string CoverageField { get; set; }

        /// <summary>Gets or sets the upstream timeout in seconds (1..30). Default 5.</summary>
        public double TimeoutSeconds { get; set; } = 5;

        /// <summary>Gets or sets how long a successful answer is reused, in minutes (0..1440). Default 10.</summary>
        public double CacheMinutes { get; set; } = 10;
    }

    /// <summary>One marker in the plugin's own, normalised contract.</summary>
    public sealed class TimelineMarker
    {
        /// <summary>Gets or sets seconds from the start of the item, >= 0.</summary>
        [JsonPropertyName("offsetSeconds")]
        public double OffsetSeconds { get; set; }

        /// <summary>Gets or sets plain text (clients must never treat it as HTML). May be empty.</summary>
        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        /// <summary>Gets or sets an optional number (size/weight of the marker).</summary>
        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Value { get; set; }

        /// <summary>Gets or sets the plain-text unit of <see cref="Value"/> (provider valueUnit). Omitted without a value.</summary>
        [JsonPropertyName("unit")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Unit { get; set; }

        /// <summary>Gets or sets an optional style key, [a-z0-9_-]{1,32}.</summary>
        [JsonPropertyName("kind")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Kind { get; set; }

        /// <summary>Gets or sets the id of the provider that supplied it.</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; }
    }

    /// <summary>Parsing, path matching and response mapping for marker providers. Pure: no I/O.</summary>
    internal static class MarkerProviders
    {
        internal const string EnvVar = "JELLYFIN_PLUS_MARKER_PROVIDERS";
        internal const int MaxMarkersPerProvider = 2000;
        internal const int MaxLabelLength = 200;

        private static readonly Regex IdPattern = new Regex("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant);
        private static readonly Regex KindPattern = new Regex("^[a-z0-9_-]{1,32}$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static readonly object Gate = new object();
        private static string _lastRaw;
        private static IReadOnlyList<MarkerProvider> _lastParsed = Array.Empty<MarkerProvider>();

        /// <summary>
        /// The effective providers, read at request time: the environment variable when set (non-blank),
        /// else the plugin configuration. Parsed once per distinct text. Invalid entries are dropped.
        /// </summary>
        internal static IReadOnlyList<MarkerProvider> Current(string configured, Action<string> warn)
        {
            var env = Environment.GetEnvironmentVariable(EnvVar);
            var raw = string.IsNullOrWhiteSpace(env) ? configured : env;
            lock (Gate)
            {
                if (!string.Equals(raw, _lastRaw, StringComparison.Ordinal))
                {
                    _lastParsed = Parse(raw, warn);
                    _lastRaw = raw;
                }

                return _lastParsed;
            }
        }

        /// <summary>The valid, enabled providers in a JSON array; anything unusable is skipped with a warning.</summary>
        internal static IReadOnlyList<MarkerProvider> Parse(string json, Action<string> warn)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<MarkerProvider>();
            }

            List<MarkerProvider> list;
            try
            {
                list = JsonSerializer.Deserialize<List<MarkerProvider>>(json, Options);
            }
            catch (JsonException ex)
            {
                warn?.Invoke("marker providers: not a JSON array of providers (" + ex.Message + ")");
                return Array.Empty<MarkerProvider>();
            }

            var ok = new List<MarkerProvider>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in list ?? new List<MarkerProvider>())
            {
                if (p == null || !p.Enabled)
                {
                    continue;
                }

                if (p.Id == null || !IdPattern.IsMatch(p.Id) || !ids.Add(p.Id))
                {
                    warn?.Invoke("marker providers: skipped an entry with a missing, invalid or duplicate id");
                    continue;
                }

                if (!ValidTemplate(p.UrlTemplate))
                {
                    warn?.Invoke("marker providers: skipped '" + p.Id + "': urlTemplate must be an absolute http(s) URL with no placeholder in the host");
                    continue;
                }

                p.TimeoutSeconds = double.IsFinite(p.TimeoutSeconds) ? Math.Clamp(p.TimeoutSeconds, 1, 30) : 5;
                p.CacheMinutes = double.IsFinite(p.CacheMinutes) ? Math.Clamp(p.CacheMinutes, 0, 1440) : 10;
                p.ItemsField = Blank(p.ItemsField) ? "markers" : p.ItemsField;
                p.OffsetField = Blank(p.OffsetField) ? "offsetSeconds" : p.OffsetField;
                p.LabelField = Blank(p.LabelField) ? "label" : p.LabelField;
                p.ValueField = Blank(p.ValueField) ? "value" : p.ValueField;
                p.KindField = Blank(p.KindField) ? "kind" : p.KindField;
                p.Kind = CleanKind(p.Kind);
                p.ValueUnit = Blank(p.ValueUnit) ? null : p.ValueUnit.Trim();
                if (p.ValueUnit != null && p.ValueUnit.Length > 24)
                {
                    p.ValueUnit = p.ValueUnit.Substring(0, 24);
                }
                ok.Add(p);
            }

            return ok;
        }

        /// <summary>Whether an item path passes a provider's filter. A ".." segment never does.</summary>
        internal static bool Matches(MarkerProvider p, string path)
        {
            var needsPath = p.PathPrefix != null || (p.Extensions != null && p.Extensions.Length > 0)
                || (p.UrlTemplate != null && p.UrlTemplate.Contains("{path}", StringComparison.Ordinal));
            if (string.IsNullOrEmpty(path))
            {
                return !needsPath;
            }

            if (path.Split('/', '\\').Any(s => s == ".."))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(p.PathPrefix) && !path.StartsWith(p.PathPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (p.ExcludePrefixes != null && p.ExcludePrefixes.Any(x => !string.IsNullOrEmpty(x) && path.StartsWith(x, StringComparison.Ordinal)))
            {
                return false;
            }

            if (p.Extensions != null && p.Extensions.Length > 0)
            {
                var ext = Path.GetExtension(path).TrimStart('.');
                return p.Extensions.Any(e => e != null && string.Equals(e.TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase));
            }

            return true;
        }

        /// <summary>The provider's URL for an item: every placeholder URL-encoded.</summary>
        internal static Uri BuildUrl(MarkerProvider p, string path, Guid itemId, string name)
        {
            var s = p.UrlTemplate
                .Replace("{path}", Uri.EscapeDataString(path ?? string.Empty), StringComparison.Ordinal)
                .Replace("{itemId}", itemId.ToString("N", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{name}", Uri.EscapeDataString(name ?? string.Empty), StringComparison.Ordinal);
            return Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;
        }

        /// <summary>
        /// Maps a provider response to normalised markers. Null when the response does not have the
        /// configured shape (a failure); an empty list when it does but holds nothing usable, or the
        /// coverage field says there is no data. Offsets past <paramref name="durationSeconds"/> (when known) are dropped.
        /// </summary>
        internal static List<TimelineMarker> Map(MarkerProvider p, JsonElement root, double durationSeconds)
        {
            if (!string.IsNullOrEmpty(p.CoverageField))
            {
                if (!TryGet(root, p.CoverageField, out var cov) || (cov.ValueKind != JsonValueKind.True && cov.ValueKind != JsonValueKind.False))
                {
                    return null;
                }

                if (cov.ValueKind != JsonValueKind.True)
                {
                    return new List<TimelineMarker>();
                }
            }

            if (!TryGet(root, p.ItemsField, out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var list = new List<TimelineMarker>();
            foreach (var m in items.EnumerateArray())
            {
                if (list.Count >= MaxMarkersPerProvider)
                {
                    break;
                }

                if (m.ValueKind != JsonValueKind.Object
                    || !TryGet(m, p.OffsetField, out var off) || off.ValueKind != JsonValueKind.Number
                    || !off.TryGetDouble(out var offset) || !double.IsFinite(offset) || offset < 0
                    || (durationSeconds > 0 && offset > durationSeconds))
                {
                    continue;
                }

                double? value = null;
                if (TryGet(m, p.ValueField, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d))
                {
                    value = d;
                }

                var label = TryGet(m, p.LabelField, out var l) && l.ValueKind == JsonValueKind.String ? (l.GetString() ?? string.Empty).Trim() : string.Empty;
                if (label.Length > MaxLabelLength)
                {
                    label = label.Substring(0, MaxLabelLength);
                }

                var kind = p.Kind;
                if (kind == null && TryGet(m, p.KindField, out var k) && k.ValueKind == JsonValueKind.String)
                {
                    kind = CleanKind(k.GetString());
                }

                list.Add(new TimelineMarker { OffsetSeconds = Math.Round(offset, 3), Label = label, Value = value, Unit = value == null ? null : p.ValueUnit, Kind = kind, Source = p.Id });
            }

            return list;
        }

        /// <summary>Follows a dotted path of object properties ("data.tips").</summary>
        internal static bool TryGet(JsonElement e, string dotted, out JsonElement found)
        {
            found = e;
            foreach (var part in dotted.Split('.'))
            {
                if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(part, out found))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Blank(string s) => string.IsNullOrWhiteSpace(s);

        private static string CleanKind(string k)
        {
            k = k?.Trim().ToLowerInvariant();
            return k != null && KindPattern.IsMatch(k) ? k : null;
        }

        /// <summary>Absolute http(s), and the placeholders cannot change the host (so no request can pick the target).</summary>
        private static bool ValidTemplate(string t)
        {
            if (Blank(t))
            {
                return false;
            }

            Uri a = Fill(t, "a"), b = Fill(t, "b");
            return a != null && b != null
                && (a.Scheme == Uri.UriSchemeHttp || a.Scheme == Uri.UriSchemeHttps)
                && string.Equals(a.GetLeftPart(UriPartial.Authority), b.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
        }

        private static Uri Fill(string t, string v)
        {
            var s = t.Replace("{path}", v, StringComparison.Ordinal).Replace("{itemId}", v, StringComparison.Ordinal).Replace("{name}", v, StringComparison.Ordinal);
            return Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;
        }
    }
}
