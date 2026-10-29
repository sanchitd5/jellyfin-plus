using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Plus.Patcher
{
    /// <summary>
    /// The incremental half of the filter key cache. A full rebuild re-aggregates every item of a library
    /// (12.8 s mean on a 72k-item library, ~800 times a day because recordings come and go). Here the full
    /// rebuild also records, per item, which keys it contributes to; a later change then re-derives only the
    /// changed items' rows and recomputes count/min for only the keys they touch.
    ///
    /// TABLES (plugin-owned, raw SQL, created IF NOT EXISTS):
    ///   JellyfinPlus_FilterItemKeys(LibraryId BLOB, Kind, TypeNo, CleanValue, ItemId BLOB, Value)
    ///     one row per (item, key). Kind 0 is a "present" row: the item is in the cached set (the TranslateQuery
    ///     filter admitted it), whether or not it has any key. TypeNo indexes the type list kept in the Incr row,
    ///     so the item type string is not repeated per row. The primary key is ordered by key, so "count and
    ///     MIN(Value) of this key" is a prefix range read.
    ///   JellyfinPlus_FilterIncr(LibraryId, Watermark, GenreFp, FullAtUtc, MetaBuiltAt, Types)
    ///     the state the item rows describe. No Incr row means "no usable item rows": the full path runs.
    ///
    /// CORRECTNESS. The key tables stay what a full build writes. The delta is: items added or removed (a set
    /// difference between the filter's current item ids and the stored "present" rows, so it does not depend on
    /// any date), plus items whose DateLastSaved is at or after the watermark (the newest DateLastSaved seen
    /// when the previous run read the library). Their old rows are removed, their new rows derived by the same
    /// join the full build streams, and the touched keys recomputed from the item rows. Anything unexpected
    /// (no Incr row, the Genre entities changed, the delta is large, the Incr row does not belong to the
    /// current key rows, an exception) falls through to the full rebuild, which also runs at least daily as
    /// a self-heal. A full build verifies its own item rows against the aggregates it computed and records
    /// none when they disagree (a change racing the build).
    /// </summary>
    internal static partial class FilterCacheEngine
    {
        internal const string ItemTable = "JellyfinPlus_FilterItemKeys";
        internal const string IncrTable = "JellyfinPlus_FilterIncr";

        /// <summary>Largest delta (changed plus removed items) applied incrementally; more runs the full build.</summary>
        internal static int MaxDelta = 2000;

        /// <summary>A library gets a full rebuild at least this often, whatever the deltas say.</summary>
        internal static TimeSpan FullSelfHeal = TimeSpan.FromHours(24);

        /// <summary>Off = always the full path (harness, and a switch if the incremental path is ever suspected).</summary>
        internal static bool IncrementalEnabled = true;

        /// <summary>"full" or "incremental": how the most recent library build was done (status, harness).</summary>
        internal static volatile string LastMode = "none";

        private sealed class IncrInfo
        {
            public string Watermark;
            public string GenreFp;
            public string Types;
        }

        private sealed class TypeMap
        {
            private readonly List<string> _list;
            private readonly Dictionary<string, int> _idx;

            public TypeMap(IEnumerable<string> init)
            {
                _list = init.ToList();
                _idx = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < _list.Count; i++)
                {
                    _idx[_list[i]] = i;
                }
            }

            public int No(string t)
            {
                t ??= string.Empty;
                if (!_idx.TryGetValue(t, out var n))
                {
                    n = _list.Count;
                    _list.Add(t);
                    _idx[t] = n;
                }

                return n;
            }

            public string Name(int no) => _list[no];

            public string Json() => JsonSerializer.Serialize(_list);
        }

        private sealed class ValRow
        {
            public string Type { get; set; }

            public string Clean { get; set; }

            public string Value { get; set; }

            public Guid ItemId { get; set; }
        }

        private static void EnsureIncrSchema(DbConnection conn)
        {
            Exec(conn, null, "CREATE TABLE IF NOT EXISTS " + ItemTable + " ("
                + "LibraryId BLOB NOT NULL, Kind INTEGER NOT NULL, TypeNo INTEGER NOT NULL, CleanValue TEXT NOT NULL, "
                + "ItemId BLOB NOT NULL, Value TEXT NOT NULL, PRIMARY KEY (LibraryId, Kind, TypeNo, CleanValue, ItemId)) WITHOUT ROWID");
            Exec(conn, null, "CREATE TABLE IF NOT EXISTS " + IncrTable + " ("
                + "LibraryId TEXT NOT NULL PRIMARY KEY, Watermark TEXT NOT NULL, GenreFp TEXT NOT NULL, FullAtUtc TEXT NOT NULL, "
                + "MetaBuiltAt TEXT NOT NULL, Types TEXT NOT NULL) WITHOUT ROWID");
        }

        /// <summary>The newest DateLastSaved text of the library's rows (read before the fingerprint, see BuildLibrary).</summary>
        private static string ReadWatermark(DbConnection conn, Guid lib)
        {
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT MAX(DateLastSaved) FROM BaseItems WHERE TopParentId = @l";
            AddParam(c, "@l", lib.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant());
            var o = c.ExecuteScalar();
            return o == null || o is DBNull ? string.Empty : Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- shared item-row derivation

        /// <summary>
        /// Emits every item row (present, tags, genres, year, rating) of <paramref name="items"/>. The one
        /// definition both the full build (streams all items) and the delta (a few ids) use.
        /// </summary>
        private static void EmitItemRows(JellyfinDbContext d, IQueryable<BaseItemEntity> items, Action<int, string, string, Guid, string> emit)
        {
            foreach (var e in items.Select(e => new { e.Id, e.Type, e.ProductionYear, e.OfficialRating }))
            {
                emit(0, e.Type, string.Empty, e.Id, string.Empty);
                if (e.ProductionYear != null && e.ProductionYear > 0)
                {
                    var y = e.ProductionYear.Value.ToString(CultureInfo.InvariantCulture);
                    emit((int)Kind.Year, e.Type, y, e.Id, y);
                }

                if (!string.IsNullOrEmpty(e.OfficialRating))
                {
                    emit((int)Kind.Rating, e.Type, e.OfficialRating, e.Id, e.OfficialRating);
                }
            }

            foreach (var (type, kind) in new[] { (ItemValueType.Genre, Kind.Genre), (ItemValueType.Tags, Kind.Tag) })
            {
                var values = d.ItemValues.Where(v => v.Type == type);
                var q = d.ItemValuesMap
                    .AsNoTracking()
                    .Join(items, m => m.ItemId, e => e.Id, (m, e) => new { m.ItemValueId, e.Type, ItemId = e.Id })
                    .Join(values, x => x.ItemValueId, v => v.ItemValueId, (x, v) => new ValRow { Type = x.Type, Clean = v.CleanValue, Value = v.Value, ItemId = x.ItemId });
                foreach (var r in q)
                {
                    emit((int)kind, r.Type, r.Clean ?? string.Empty, r.ItemId, r.Value ?? string.Empty);
                }
            }
        }

        /// <summary>Inserts item rows; a repeat of (item, key) keeps the smaller Value (SQLite BINARY min, like the aggregate's MIN).</summary>
        private sealed class ItemWriter : IDisposable
        {
            private readonly DbConnection _conn;
            private readonly byte[] _lib;
            private readonly int _batch;
            private readonly bool _owns;
            private DbTransaction _tx;
            private DbCommand _cmd;
            private DbParameter _k, _t, _c, _i, _v;
            private int _pending;

            public ItemWriter(DbConnection conn, DbTransaction external, byte[] lib, int batch)
            {
                _conn = conn;
                _tx = external;
                _owns = external == null;
                _lib = lib;
                _batch = batch;
            }

            public void Add(int kind, int typeNo, string clean, Guid id, string value)
            {
                if (_cmd == null)
                {
                    _cmd = _conn.CreateCommand();
                    _cmd.CommandText = "INSERT INTO " + ItemTable + " (LibraryId, Kind, TypeNo, CleanValue, ItemId, Value) VALUES (@l, @k, @t, @c, @i, @v) "
                        + "ON CONFLICT (LibraryId, Kind, TypeNo, CleanValue, ItemId) DO UPDATE SET Value = min(Value, excluded.Value)";
                    AddParam(_cmd, "@l", _lib);
                    _k = Mk("@k");
                    _t = Mk("@t");
                    _c = Mk("@c");
                    _i = Mk("@i");
                    _v = Mk("@v");
                }

                if (_owns && _tx == null)
                {
                    _tx = _conn.BeginTransaction();
                }

                _cmd.Transaction = _tx;
                _k.Value = kind;
                _t.Value = typeNo;
                _c.Value = clean ?? string.Empty;
                _i.Value = id.ToByteArray();
                _v.Value = value ?? string.Empty;
                _cmd.ExecuteNonQuery();
                if (_owns && ++_pending >= _batch)
                {
                    Commit();
                }
            }

            public void Commit()
            {
                if (_owns && _tx != null)
                {
                    _tx.Commit();
                    _tx.Dispose();
                    _tx = null;
                    _pending = 0;
                }
            }

            private DbParameter Mk(string n)
            {
                var p = _cmd.CreateParameter();
                p.ParameterName = n;
                _cmd.Parameters.Add(p);
                return p;
            }

            public void Dispose()
            {
                _cmd?.Dispose();
                if (_owns)
                {
                    _tx?.Dispose();
                }
            }
        }

        // ---------------------------------------------------------------- full build: record the item rows

        /// <summary>
        /// The full build when incremental is on: streams the item rows of the whole library into the item
        /// table (committed in batches, so the write lock is never held for the whole stream), then derives the
        /// key rows from that table with one grouped scan (distinct items per key, MIN(Value)) in the order
        /// the aggregate path produces them. True when it did; false (item rows removed) means the caller runs
        /// the plain aggregates instead. <paramref name="incr"/> says what the Incr row should record.
        ///
        /// WHY NOT THE AGGREGATES. EF's grouped COUNT(DISTINCT) over the ItemValuesMap join, run by the SQLite
        /// 3.46.1 that ships in e_sqlite3 2.1.10, returned 1057 where the true distinct count was 1058 for one tag
        /// of the real library (the same SQL on SQLite 3.54 and with the map index disabled gives 1058). The item
        /// rows come from plain joins with no COUNT(DISTINCT), so the key rows derived from them are exact, and
        /// the incremental update, which also works from item rows, can match them exactly.
        /// </summary>
        private static bool FullFromItems(JellyfinDbContext db, DbConnection conn, Guid lib, string watermark, string genreFp, out List<KeyRow> rows, out IncrInfo incr)
        {
            rows = null;
            incr = null;
            var blob = lib.ToByteArray();
            try
            {
                Exec(conn, null, "DELETE FROM " + IncrTable + " WHERE LibraryId = @l", ("@l", Id(lib)));
                Exec(conn, null, "DELETE FROM " + ItemTable + " WHERE LibraryId = @l", ("@l", blob));
                if (string.IsNullOrEmpty(watermark))
                {
                    return false;
                }

                var types = new TypeMap(Array.Empty<string>());
                using (var rd = _factory.CreateDbContext())
                using (var w = new ItemWriter(conn, null, blob, 20000))
                {
                    var items = _translate(rd.BaseItems.AsNoTracking(), rd, new InternalItemsQuery { TopParentIds = new[] { lib } });
                    EmitItemRows(rd, items, (k, t, c, id, v) => w.Add(k, types.No(t), c, id, v));
                    w.Commit();
                }

                var genres = new List<KeyRow>();
                var list = new List<KeyRow>();
                using (var c = conn.CreateCommand())
                {
                    c.CommandText = "SELECT Kind, TypeNo, CleanValue, COUNT(*), MIN(Value) FROM " + ItemTable
                        + " WHERE LibraryId = @l AND Kind > 0 GROUP BY Kind, TypeNo, CleanValue ORDER BY Kind";
                    AddParam(c, "@l", blob);
                    using var r = c.ExecuteReader();
                    while (r.Read())
                    {
                        var kind = (Kind)r.GetInt32(0);
                        var row = new KeyRow(kind, types.Name(r.GetInt32(1)), r.GetString(2), r.GetString(4), (int)r.GetInt64(3), null);
                        list.Add(row);
                        if (kind == Kind.Genre)
                        {
                            genres.Add(row);
                        }
                    }
                }

                AddGenreEntityRows(list, genres, GenreEntities(db));
                rows = list;
                incr = new IncrInfo { Watermark = watermark, GenreFp = genreFp, Types = types.Json() };
                return true;
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "JellyfinPlus: filter key cache: could not record item rows; running the plain aggregates, incremental stays off until the next full build");
                try
                {
                    Exec(conn, null, "DELETE FROM " + ItemTable + " WHERE LibraryId = @l", ("@l", blob));
                }
                catch (Exception)
                {
                }

                rows = null;
                incr = null;
                return false;
            }
        }

        // ---------------------------------------------------------------- incremental

        /// <summary>
        /// Brings the cached keys of a stale library up to date from the items that changed. True when it did;
        /// false (nothing written) when the full rebuild has to run. Never throws.
        /// </summary>
        private static bool TryIncremental(JellyfinDbContext db, DbConnection conn, Guid lib, string name, string libFp, string genreFp, string watermark)
        {
            try
            {
                return IncrementalCore(db, conn, lib, name, libFp, genreFp, watermark);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "JellyfinPlus: filter key cache: incremental update failed, running the full rebuild");
                return false;
            }
        }

        private static bool IncrementalCore(JellyfinDbContext db, DbConnection conn, Guid lib, string name, string libFp, string genreFp, string watermark)
        {
            var sw = Stopwatch.StartNew();
            var lid = Id(lib);
            var blob = lib.ToByteArray();
            if (string.IsNullOrEmpty(watermark))
            {
                return false;
            }

            string oldMark, oldGenre, fullAt, metaBuilt, typesJson;
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT Watermark, GenreFp, FullAtUtc, MetaBuiltAt, Types FROM " + IncrTable + " WHERE LibraryId = @l";
                AddParam(c, "@l", lid);
                using var r = c.ExecuteReader();
                if (!r.Read())
                {
                    return false;
                }

                oldMark = r.GetString(0);
                oldGenre = r.GetString(1);
                fullAt = r.GetString(2);
                metaBuilt = r.GetString(3);
                typesJson = r.GetString(4);
            }

            if (oldGenre != genreFp
                || string.IsNullOrEmpty(oldMark)
                || !DateTime.TryParse(fullAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fullTime)
                || DateTime.UtcNow - fullTime > FullSelfHeal)
            {
                return false;
            }

            // The Incr row must describe the key rows currently stored (an older build of this plugin, or a
            // hand edit, may have rewritten them without it).
            var builtAts = new List<string>();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT DISTINCT BuiltAtUtc FROM " + MetaTable + " WHERE LibraryId = @l";
                AddParam(c, "@l", lid);
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    builtAts.Add(r.GetString(0));
                }
            }

            if (builtAts.Count != 1 || builtAts[0] != metaBuilt)
            {
                return false;
            }

            var types = new TypeMap(JsonSerializer.Deserialize<List<string>>(typesJson));

            var present = new Dictionary<Guid, int>();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT ItemId, TypeNo FROM " + ItemTable + " WHERE LibraryId = @l AND Kind = 0";
                AddParam(c, "@l", blob);
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    present[new Guid((byte[])r[0])] = r.GetInt32(1);
                }
            }

            if (present.Count == 0)
            {
                return false;
            }

            var items = _translate(db.BaseItems.AsNoTracking(), db, new InternalItemsQuery { TopParentIds = new[] { lib } });
            var current = items.Select(e => new { e.Id, e.Type }).ToList();
            var curIds = new HashSet<Guid>(current.Count);
            var affected = new HashSet<Guid>();
            foreach (var e in current)
            {
                curIds.Add(e.Id);
                if (!present.TryGetValue(e.Id, out var no))
                {
                    affected.Add(e.Id);
                }
                else if (!string.Equals(types.Name(no), e.Type ?? string.Empty, StringComparison.Ordinal))
                {
                    affected.Add(e.Id);
                }
            }

            var removed = present.Keys.Where(g => !curIds.Contains(g)).ToList();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT Id FROM BaseItems WHERE TopParentId = @l AND DateLastSaved >= @w";
                AddParam(c, "@l", lib.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant());
                AddParam(c, "@w", oldMark);
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var g = Guid.Parse(r.GetString(0));
                    if (curIds.Contains(g))
                    {
                        affected.Add(g);
                    }
                }
            }

            if (affected.Count + removed.Count > MaxDelta)
            {
                return false;
            }

            // New rows of the changed items: the same derivation the full build streams, restricted to their ids.
            var fresh = new List<(int Kind, int TypeNo, string Clean, Guid Id, string Value)>();
            var ids = affected.ToList();
            for (var i = 0; i < ids.Count; i += 400)
            {
                var chunk = ids.GetRange(i, Math.Min(400, ids.Count - i));
                EmitItemRows(db, items.Where(e => chunk.Contains(e.Id)), (k, t, c, id, v) => fresh.Add((k, types.No(t), c, id, v)));
            }

            var entities = GenreEntities(db);
            var gone = new HashSet<Guid>(removed);
            gone.UnionWith(affected);
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var sourceCount = int.Parse(libFp.Split('|')[0], CultureInfo.InvariantCulture);
            var touchedCount = 0;

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var tx = conn.BeginTransaction();
                    Exec(conn, tx, "CREATE TEMP TABLE IF NOT EXISTS jp_aff (Id BLOB PRIMARY KEY) WITHOUT ROWID");
                    Exec(conn, tx, "DELETE FROM jp_aff");
                    foreach (var g in gone)
                    {
                        Exec(conn, tx, "INSERT OR IGNORE INTO jp_aff (Id) VALUES (@i)", ("@i", g.ToByteArray()));
                    }

                    var touched = new HashSet<(int Kind, int TypeNo, string Clean)>();
                    if (gone.Count > 0)
                    {
                        using (var c = conn.CreateCommand())
                        {
                            c.Transaction = tx;
                            c.CommandText = "SELECT DISTINCT Kind, TypeNo, CleanValue FROM " + ItemTable
                                + " WHERE LibraryId = @l AND Kind > 0 AND ItemId IN (SELECT Id FROM jp_aff)";
                            AddParam(c, "@l", blob);
                            using var r = c.ExecuteReader();
                            while (r.Read())
                            {
                                touched.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2)));
                            }
                        }

                        Exec(conn, tx, "DELETE FROM " + ItemTable + " WHERE LibraryId = @l AND ItemId IN (SELECT Id FROM jp_aff)", ("@l", blob));
                    }

                    using (var w = new ItemWriter(conn, tx, blob, int.MaxValue))
                    {
                        foreach (var f in fresh)
                        {
                            w.Add(f.Kind, f.TypeNo, f.Clean, f.Id, f.Value);
                            if (f.Kind > 0)
                            {
                                touched.Add((f.Kind, f.TypeNo, f.Clean));
                            }
                        }
                    }

                    foreach (var key in touched)
                    {
                        ApplyKey(conn, tx, lid, blob, key.Kind, types.Name(key.TypeNo), key.TypeNo, key.Clean, entities);
                    }

                    touchedCount = touched.Count;
                    foreach (var k in _allKinds)
                    {
                        long n;
                        using (var c = conn.CreateCommand())
                        {
                            c.Transaction = tx;
                            c.CommandText = "SELECT COUNT(*) FROM " + KeysTable + " WHERE LibraryId = @l AND Kind = @k";
                            AddParam(c, "@l", lid);
                            AddParam(c, "@k", (int)k);
                            n = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
                        }

                        Exec(
                            conn,
                            tx,
                            "UPDATE " + MetaTable + " SET BuiltAtUtc = @b, SourceItemCount = @s, KeyCount = @n, Fingerprint = @f WHERE LibraryId = @l AND Kind = @k",
                            ("@b", now),
                            ("@s", sourceCount),
                            ("@n", (int)n),
                            ("@f", FingerprintFor(k, libFp, genreFp)),
                            ("@l", lid),
                            ("@k", (int)k));
                    }

                    Exec(
                        conn,
                        tx,
                        "UPDATE " + IncrTable + " SET Watermark = @w, MetaBuiltAt = @b, Types = @t WHERE LibraryId = @l",
                        ("@w", watermark),
                        ("@b", now),
                        ("@t", types.Json()),
                        ("@l", lid));
                    tx.Commit();
                    break;
                }
                catch (DbException ex) when (attempt < 6 && IsBusy(ex))
                {
                    Thread.Sleep(250 * (attempt + 1));
                }
            }

            InvalidateFingerprints();
            LastMode = "incremental";
            Count("rebuild-incremental");
            _log?.LogInformation(
                "JellyfinPlus: filter key cache: library '{Library}' updated incrementally in {Ms} ms ({Changed} changed, {Removed} removed items, {Keys} keys touched)",
                name,
                (int)sw.ElapsedMilliseconds,
                affected.Count,
                removed.Count,
                touchedCount);
            return true;
        }

        /// <summary>Recomputes one key's count and display value from the item rows and writes (or deletes) its key rows.</summary>
        private static void ApplyKey(
            DbConnection conn,
            DbTransaction tx,
            string lid,
            byte[] blob,
            int kind,
            string itemType,
            int typeNo,
            string clean,
            Dictionary<string, (Guid Id, string Name)> entities)
        {
            long n;
            string min;
            using (var c = conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "SELECT COUNT(*), MIN(Value) FROM " + ItemTable
                    + " WHERE LibraryId = @l AND Kind = @k AND TypeNo = @t AND CleanValue = @c";
                AddParam(c, "@l", blob);
                AddParam(c, "@k", kind);
                AddParam(c, "@t", typeNo);
                AddParam(c, "@c", clean);
                using var r = c.ExecuteReader();
                r.Read();
                n = r.GetInt64(0);
                min = r.IsDBNull(1) ? null : r.GetString(1);
            }

            SetKey(conn, tx, lid, kind, itemType, clean, n, min, null);
            if (kind == (int)Kind.Genre)
            {
                if (n > 0 && entities.TryGetValue(clean, out var ent))
                {
                    SetKey(conn, tx, lid, (int)Kind.GenreEntity, itemType, clean, n, ent.Name ?? min, Id(ent.Id));
                }
                else
                {
                    SetKey(conn, tx, lid, (int)Kind.GenreEntity, itemType, clean, 0, null, null);
                }
            }
        }

        private static void SetKey(DbConnection conn, DbTransaction tx, string lid, int kind, string itemType, string clean, long n, string value, string refId)
        {
            if (n <= 0)
            {
                Exec(
                    conn,
                    tx,
                    "DELETE FROM " + KeysTable + " WHERE LibraryId = @l AND Kind = @k AND ItemType = @t AND CleanValue = @c",
                    ("@l", lid),
                    ("@k", kind),
                    ("@t", itemType ?? string.Empty),
                    ("@c", clean));
                return;
            }

            Exec(
                conn,
                tx,
                "INSERT OR REPLACE INTO " + KeysTable + " (LibraryId, Kind, ItemType, CleanValue, Value, ItemCount, RefId) VALUES (@l, @k, @t, @c, @v, @n, @r)",
                ("@l", lid),
                ("@k", kind),
                ("@t", itemType ?? string.Empty),
                ("@c", clean),
                ("@v", value ?? string.Empty),
                ("@n", (int)n),
                ("@r", (object)refId ?? DBNull.Value));
        }
    }
}
