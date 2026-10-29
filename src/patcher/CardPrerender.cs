using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Jellyfin.Database.Implementations;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Plus.Patcher
{
    /// <summary>
    /// Pre-renders the grid-card images a library view asks for, so the first visit is a resize-cache hit.
    ///
    /// WHY. Stock requests a card image only once its card is on screen, and a cold server resize costs
    /// 0.1-0.25 s (a warm one ~1 ms: the result is a file in the resize cache). A first screenful of ~30
    /// cards therefore lands about a second after the cards do. Nothing here makes a resize faster; it
    /// makes the cold one happen before anyone is looking.
    ///
    /// RECORD. A Harmony prefix on ImageProcessor.ProcessImage(ImageProcessingOptions) - the one place every
    /// resized-image request becomes options, after the controller has parsed the URL and the Accept header -
    /// notes the SHAPE of grid-sized requests (image type, index, the six size fields, quality, the output
    /// format list, blur, background and foreground layers) and counts hits. No item, no path, no user.
    /// Requests with a played percentage or unplayed count are per-item values and are not recorded.
    /// Counts are in memory (a lock-free increment on the request path) and flushed to the plugin table
    /// JellyfinPlus_ImageSizes every minute from a timer thread.
    ///
    /// RENDER. The task takes the top shapes per image type and, for every item that has that image, calls
    /// the SAME IImageProcessor.ProcessImage stock calls, with options built exactly as ImageController
    /// builds them. The cache file name is computed inside ProcessImage from those options, so the
    /// pre-rendered entry is, by construction, the entry a real request looks up; this file contains no copy
    /// of the cache-key function that could drift. An entry already present is a File.Exists hit inside
    /// ProcessImage. Two low-priority threads, backing off while viewers are requesting images.
    ///
    /// Fail soft: the request-path prefix catches everything and returns to stock; the task only calls a
    /// public stock API; the table is plugin-owned.
    /// </summary>
    internal static class CardPrerender
    {
        internal const string Table = "JellyfinPlus_ImageSizes";

        /// <summary>Largest long side (px) of a requested box that still counts as a grid card.</summary>
        internal const int MaxEdge = 1000;

        /// <summary>Distinct shapes kept in memory and in the table; later ones are ignored (bounds growth).</summary>
        internal const int MaxShapes = 512;

        /// <summary>A shape needs this many recorded requests before it is pre-rendered.</summary>
        internal const int MinHits = 3;

        /// <summary>Shapes pre-rendered per image type.</summary>
        internal const int TopPerType = 4;

        internal const int Workers = 2;

        private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(60);

        /// <summary>One recorded request shape.</summary>
        internal sealed class Shape
        {
            public string Key;
            public ImageType Type;
            public int Index;
            public int? Width, Height, MaxWidth, MaxHeight, FillWidth, FillHeight;
            public int Quality;
            public string Formats;
            public int? Blur;
            public string BackgroundColor, ForegroundLayer;
            public long Hits; // since the last flush (memory) or total (loaded from the table)
        }

        private static readonly ConcurrentDictionary<string, Shape> _pending = new ConcurrentDictionary<string, Shape>();
        private static readonly AsyncLocal<bool> _self = new AsyncLocal<bool>();
        private static readonly SemaphoreSlim _runLock = new SemaphoreSlim(1, 1);
        private static readonly object _gate = new object();

        private static ILogger _log;
        private static IImageProcessor _proc;
        private static ILibraryManager _libs;
        private static IDbContextFactory<JellyfinDbContext> _factory;
        private static Timer _flush;
        private static CancellationTokenSource _stop = new CancellationTokenSource();
        private static volatile bool _schemaReady;
        private static long _lastRequestTick;
        private static int _recordWarned;
        private static string _lastSummary = "never run";

        /// <summary>True when the ImageProcessor prefix is installed.</summary>
        public static bool Available { get; private set; }

        /// <summary>True when the services were resolved and the table exists.</summary>
        internal static bool Ready => _schemaReady && _proc != null && _libs != null && _factory != null;

        // ---------------------------------------------------------------- patch

        public static void Apply(Harmony harmony, ILogger logger)
        {
            _log = logger ?? _log;

            // By name: Jellyfin.Drawing is not referenced by this assembly.
            var type = AccessTools.TypeByName("Jellyfin.Drawing.ImageProcessor");
            var method = type == null ? null : AccessTools.Method(type, "ProcessImage", new[] { typeof(ImageProcessingOptions) });
            if (method == null)
            {
                logger?.LogWarning("JellyfinPlus: could not resolve ImageProcessor.ProcessImage; card image sizes are not recorded and pre-rendering has nothing to do.");
                return;
            }

            harmony.Patch(method, prefix: new HarmonyMethod(typeof(CardPrerender).GetMethod(nameof(ProcessImagePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            Available = true;
            logger?.LogInformation("JellyfinPlus: patched ImageProcessor.ProcessImage (grid-card request shapes are recorded for pre-rendering)");
        }

        private static void ProcessImagePrefix(ImageProcessingOptions options)
        {
            try
            {
                if (_self.Value)
                {
                    return; // our own pre-render call
                }

                Volatile.Write(ref _lastRequestTick, Environment.TickCount64);
                Record(options);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _recordWarned, 1) == 0)
                {
                    _log?.LogWarning(ex, "JellyfinPlus: recording a card image shape failed (logged once); image requests are unaffected.");
                }
            }
        }

        /// <summary>Whether a request looks like a grid card, and if so its shape. Pure.</summary>
        internal static bool TryShape(ImageProcessingOptions o, out Shape shape)
        {
            shape = null;
            if (o == null || o.Image == null || o.Item == null)
            {
                return false;
            }

            var t = o.Image.Type;
            if (t != ImageType.Primary && t != ImageType.Thumb && t != ImageType.Backdrop)
            {
                return false;
            }

            // Per-item values: nothing a shape could stand for.
            if (o.PercentPlayed > 0 || o.UnplayedCount.HasValue || o.ImageIndex < 0 || o.ImageIndex > 20)
            {
                return false;
            }

            var longest = 0;
            foreach (var v in new[] { o.Width, o.Height, o.MaxWidth, o.MaxHeight, o.FillWidth, o.FillHeight })
            {
                if (v.HasValue)
                {
                    if (v.Value <= 0 || v.Value > MaxEdge)
                    {
                        return false;
                    }

                    longest = Math.Max(longest, v.Value);
                }
            }

            if (longest == 0 || o.SupportedOutputFormats == null || o.SupportedOutputFormats.Count == 0)
            {
                return false;
            }

            shape = new Shape
            {
                Type = t,
                Index = o.ImageIndex,
                Width = o.Width,
                Height = o.Height,
                MaxWidth = o.MaxWidth,
                MaxHeight = o.MaxHeight,
                FillWidth = o.FillWidth,
                FillHeight = o.FillHeight,
                Quality = o.Quality,
                Formats = string.Join(",", o.SupportedOutputFormats.Select(f => f.ToString())),
                Blur = o.Blur,
                BackgroundColor = string.IsNullOrEmpty(o.BackgroundColor) ? null : o.BackgroundColor,
                ForegroundLayer = string.IsNullOrEmpty(o.ForegroundLayer) ? null : o.ForegroundLayer,
            };
            shape.Key = KeyOf(shape);
            return true;
        }

        private static string KeyOf(Shape s)
        {
            string N(int? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "-";
            return new StringBuilder()
                .Append((int)s.Type).Append('|').Append(s.Index)
                .Append("|w").Append(N(s.Width)).Append("|h").Append(N(s.Height))
                .Append("|mw").Append(N(s.MaxWidth)).Append("|mh").Append(N(s.MaxHeight))
                .Append("|fw").Append(N(s.FillWidth)).Append("|fh").Append(N(s.FillHeight))
                .Append("|q").Append(s.Quality).Append("|f").Append(s.Formats)
                .Append("|b").Append(N(s.Blur)).Append("|bg").Append(s.BackgroundColor).Append("|fl").Append(s.ForegroundLayer)
                .ToString();
        }

        private static void Record(ImageProcessingOptions options)
        {
            if (!TryShape(options, out var shape))
            {
                return;
            }

            if (_pending.TryGetValue(shape.Key, out var known))
            {
                Interlocked.Increment(ref known.Hits);
            }
            else if (_pending.Count < MaxShapes)
            {
                shape.Hits = 1;
                _pending.AddOrUpdate(shape.Key, shape, (_, s) =>
                {
                    Interlocked.Increment(ref s.Hits);
                    return s;
                });
            }
        }

        /// <summary>The options a stock request of this shape would carry, for this item's image. Mirrors ImageController.</summary>
        internal static ImageProcessingOptions BuildOptions(Shape s, BaseItem item, ItemImageInfo info)
        {
            var formats = new List<ImageFormat>();
            foreach (var name in s.Formats.Split(','))
            {
                if (Enum.TryParse<ImageFormat>(name, out var f))
                {
                    formats.Add(f);
                }
            }

            return new ImageProcessingOptions
            {
                Item = item,
                ItemId = item.Id,
                Image = info,
                ImageIndex = s.Index,
                Width = s.Width,
                Height = s.Height,
                MaxWidth = s.MaxWidth,
                MaxHeight = s.MaxHeight,
                FillWidth = s.FillWidth,
                FillHeight = s.FillHeight,
                Quality = s.Quality,
                SupportedOutputFormats = formats.ToArray(),
                Blur = s.Blur,
                BackgroundColor = s.BackgroundColor,
                ForegroundLayer = s.ForegroundLayer,
                PercentPlayed = 0,
                UnplayedCount = null,
            };
        }

        /// <summary>Harness: the shapes recorded and not yet flushed.</summary>
        internal static List<Shape> PendingSnapshot() => _pending.Values.ToList();

        // ---------------------------------------------------------------- lifecycle

        public static void Start(IServiceProvider services, ILogger logger)
        {
            try
            {
                lock (_gate)
                {
                    _log = logger ?? _log;
                    if (_factory == null)
                    {
                        _proc = services.GetRequiredService<IImageProcessor>();
                        _libs = services.GetRequiredService<ILibraryManager>();
                        _factory = services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
                    }

                    EnsureSchema();
                    if (_flush == null)
                    {
                        _stop = new CancellationTokenSource();
                        _flush = new Timer(_ => Flush(), null, FlushEvery, FlushEvery);
                    }
                }

                _log?.LogInformation("JellyfinPlus: card image pre-render ready (table {Table})", Table);
            }
            catch (Exception ex)
            {
                _schemaReady = false;
                _log?.LogWarning(ex, "JellyfinPlus: card image pre-render could not start; image requests are served by stock.");
            }
        }

        public static void Stop()
        {
            lock (_gate)
            {
                try
                {
                    _stop.Cancel();
                }
                catch (Exception)
                {
                }

                _flush?.Dispose();
                _flush = null;
            }

            Flush();
        }

        // ---------------------------------------------------------------- table

        private static void EnsureSchema()
        {
            using var db = _factory.CreateDbContext();
            db.Database.OpenConnection();
            Exec(db.Database.GetDbConnection(), "CREATE TABLE IF NOT EXISTS " + Table + " ("
                + "ShapeKey TEXT NOT NULL PRIMARY KEY, ImageType INTEGER NOT NULL, ImageIndex INTEGER NOT NULL, "
                + "Width INTEGER NULL, Height INTEGER NULL, MaxWidth INTEGER NULL, MaxHeight INTEGER NULL, "
                + "FillWidth INTEGER NULL, FillHeight INTEGER NULL, Quality INTEGER NOT NULL, Formats TEXT NOT NULL, "
                + "Blur INTEGER NULL, BackgroundColor TEXT NULL, ForegroundLayer TEXT NULL, "
                + "Hits INTEGER NOT NULL, LastSeenUtc TEXT NOT NULL) WITHOUT ROWID", null);
            _schemaReady = true;
        }

        /// <summary>Adds the in-memory counts to the table. Never throws; a failed row keeps its count for next time.</summary>
        internal static void Flush()
        {
            if (!_schemaReady || _factory == null)
            {
                return;
            }

            try
            {
                var rows = new List<(Shape Shape, long Delta)>();
                foreach (var s in _pending.Values)
                {
                    var d = Interlocked.Exchange(ref s.Hits, 0);
                    if (d > 0)
                    {
                        rows.Add((s, d));
                    }
                }

                if (rows.Count == 0)
                {
                    return;
                }

                using var db = _factory.CreateDbContext();
                db.Database.OpenConnection();
                var conn = db.Database.GetDbConnection();
                var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                using var tx = conn.BeginTransaction();
                try
                {
                    foreach (var (s, d) in rows)
                    {
                        Exec(
                            conn,
                            "INSERT INTO " + Table + " (ShapeKey, ImageType, ImageIndex, Width, Height, MaxWidth, MaxHeight, FillWidth, FillHeight, "
                            + "Quality, Formats, Blur, BackgroundColor, ForegroundLayer, Hits, LastSeenUtc) VALUES "
                            + "(@k, @t, @i, @w, @h, @mw, @mh, @fw, @fh, @q, @f, @b, @bg, @fl, @n, @now) "
                            + "ON CONFLICT(ShapeKey) DO UPDATE SET Hits = Hits + excluded.Hits, LastSeenUtc = excluded.LastSeenUtc",
                            tx,
                            ("@k", s.Key), ("@t", (int)s.Type), ("@i", s.Index), ("@w", Db(s.Width)), ("@h", Db(s.Height)),
                            ("@mw", Db(s.MaxWidth)), ("@mh", Db(s.MaxHeight)), ("@fw", Db(s.FillWidth)), ("@fh", Db(s.FillHeight)),
                            ("@q", s.Quality), ("@f", s.Formats), ("@b", Db(s.Blur)), ("@bg", (object)s.BackgroundColor ?? DBNull.Value),
                            ("@fl", (object)s.ForegroundLayer ?? DBNull.Value), ("@n", d), ("@now", now));
                    }

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    foreach (var (s, d) in rows)
                    {
                        Interlocked.Add(ref s.Hits, d);
                    }

                    throw;
                }
            }
            catch (Exception ex)
            {
                _log?.LogDebug(ex, "JellyfinPlus: card image shape flush failed; counts are kept for the next try");
            }
        }

        private static object Db(int? v) => v.HasValue ? v.Value : DBNull.Value;

        private static int? IntOrNull(DbDataReader r, int i) => r.IsDBNull(i) ? null : (int?)r.GetInt32(i);

        private static string StrOrNull(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

        /// <summary>The shapes worth rendering: at least MinHits recorded requests, the TopPerType most requested per image type.</summary>
        internal static List<Shape> TopShapes()
        {
            var all = new List<Shape>();
            using var db = _factory.CreateDbContext();
            db.Database.OpenConnection();
            using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT ShapeKey, ImageType, ImageIndex, Width, Height, MaxWidth, MaxHeight, FillWidth, FillHeight, "
                + "Quality, Formats, Blur, BackgroundColor, ForegroundLayer, Hits FROM " + Table + " WHERE Hits >= @min ORDER BY Hits DESC";
            var p = cmd.CreateParameter();
            p.ParameterName = "@min";
            p.Value = MinHits;
            cmd.Parameters.Add(p);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                all.Add(new Shape
                {
                    Key = r.GetString(0),
                    Type = (ImageType)r.GetInt32(1),
                    Index = r.GetInt32(2),
                    Width = IntOrNull(r, 3),
                    Height = IntOrNull(r, 4),
                    MaxWidth = IntOrNull(r, 5),
                    MaxHeight = IntOrNull(r, 6),
                    FillWidth = IntOrNull(r, 7),
                    FillHeight = IntOrNull(r, 8),
                    Quality = r.GetInt32(9),
                    Formats = r.GetString(10),
                    Blur = IntOrNull(r, 11),
                    BackgroundColor = StrOrNull(r, 12),
                    ForegroundLayer = StrOrNull(r, 13),
                    Hits = r.GetInt64(14),
                });
            }

            return all.GroupBy(s => s.Type).SelectMany(g => g.Take(TopPerType)).ToList();
        }

        // ---------------------------------------------------------------- task

        private sealed class Counts
        {
            public long Rendered, Cached, Passthrough, Failed, Items;
        }

        /// <summary>Starts a run without waiting. False when off, not ready or one is already running.</summary>
        public static bool StartBackground()
        {
            if (!Ready || _runLock.CurrentCount == 0)
            {
                return false;
            }

            var token = _stop.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunAsync(null, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "JellyfinPlus: background card image pre-render failed.");
                }
            });
            return true;
        }

        /// <summary>Renders the missing card images for the recorded shapes; completes with a JSON summary.</summary>
        public static async Task<string> RunAsync(IProgress<double> progress, CancellationToken ct)
        {
            if (!Ready)
            {
                return Summary(false, "not ready", null, null, 0, 0);
            }

            if (!await _runLock.WaitAsync(0).ConfigureAwait(false))
            {
                return Summary(false, "already running", null, null, 0, 0);
            }

            try
            {
                var started = DateTime.UtcNow;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Flush();
                var shapes = await Task.Run(TopShapes, ct).ConfigureAwait(false);
                if (shapes.Count == 0)
                {
                    progress?.Report(100);
                    return Remember(Summary(true, "no recorded shapes yet", shapes, null, 0, clock.ElapsedMilliseconds));
                }

                var ids = _libs.GetItemIds(new InternalItemsQuery { Recursive = true, IsVirtualItem = false });
                var counts = new Counts();
                var next = -1;
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var alive = Workers;
                for (var w = 0; w < Workers; w++)
                {
                    var t = new Thread(() =>
                    {
                        _self.Value = true;
                        try
                        {
                            while (!ct.IsCancellationRequested)
                            {
                                var i = Interlocked.Increment(ref next);
                                if (i >= ids.Count)
                                {
                                    break;
                                }

                                Yield(ct);
                                Process(ids[i], shapes, started, counts);
                                if (i % 50 == 0)
                                {
                                    progress?.Report(99.0 * i / Math.Max(1, ids.Count));
                                }
                            }
                        }
                        finally
                        {
                            if (Interlocked.Decrement(ref alive) == 0)
                            {
                                done.TrySetResult(true);
                            }
                        }
                    })
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                        Name = "JellyfinPlus card prerender",
                    };
                    t.Start();
                }

                await done.Task.ConfigureAwait(false);
                progress?.Report(100);
                var summary = Remember(Summary(true, ct.IsCancellationRequested ? "cancelled" : "done", shapes, counts, ids.Count, clock.ElapsedMilliseconds));
                _log?.LogInformation("JellyfinPlus: card image pre-render {Summary}", summary);
                ct.ThrowIfCancellationRequested();
                return summary;
            }
            finally
            {
                _runLock.Release();
            }
        }

        /// <summary>Waits a little while viewers are requesting images, so a pre-render never competes with them.</summary>
        private static void Yield(CancellationToken ct)
        {
            for (var k = 0; k < 8 && !ct.IsCancellationRequested && Environment.TickCount64 - Volatile.Read(ref _lastRequestTick) < 300; k++)
            {
                Thread.Sleep(150);
            }
        }

        private static void Process(Guid id, List<Shape> shapes, DateTime started, Counts counts)
        {
            try
            {
                var item = _libs.GetItemById(id);
                if (item == null)
                {
                    return;
                }

                Interlocked.Increment(ref counts.Items);
                foreach (var s in shapes)
                {
                    var info = item.GetImageInfo(s.Type, s.Index);
                    if (info == null || !info.IsLocalFile || string.IsNullOrEmpty(info.Path))
                    {
                        continue;
                    }

                    try
                    {
                        var r = _proc.ProcessImage(BuildOptions(s, item, info)).GetAwaiter().GetResult();
                        if (string.Equals(r.Path, info.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            Interlocked.Increment(ref counts.Passthrough); // stock serves the original (default options, or an encode failure)
                        }
                        else if (r.DateModified >= started.AddSeconds(-1))
                        {
                            Interlocked.Increment(ref counts.Rendered);
                        }
                        else
                        {
                            Interlocked.Increment(ref counts.Cached);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (Interlocked.Increment(ref counts.Failed) <= 3)
                        {
                            _log?.LogDebug(ex, "JellyfinPlus: card image pre-render failed for one image");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref counts.Failed);
                _log?.LogDebug(ex, "JellyfinPlus: card image pre-render could not load one item");
            }
        }

        private static string Summary(bool ran, string reason, List<Shape> shapes, Counts c, int itemsListed, long ms)
            => JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Ran"] = ran,
                ["Reason"] = reason,
                ["Shapes"] = shapes?.Count ?? 0,
                ["ItemsListed"] = itemsListed,
                ["ItemsRead"] = c?.Items ?? 0,
                ["Rendered"] = c?.Rendered ?? 0,
                ["Cached"] = c?.Cached ?? 0,
                ["Passthrough"] = c?.Passthrough ?? 0,
                ["Failed"] = c?.Failed ?? 0,
                ["Ms"] = ms,
            });

        private static string Remember(string summary)
        {
            _lastSummary = summary;
            return summary;
        }

        /// <summary>Last run's summary, for diagnostics.</summary>
        internal static string LastSummary => _lastSummary;

        // ---------------------------------------------------------------- ADO

        private static void Exec(DbConnection conn, string sql, DbTransaction tx, params (string, object)[] ps)
        {
            using var c = conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = sql;
            foreach (var (n, v) in ps)
            {
                var p = c.CreateParameter();
                p.ParameterName = n;
                p.Value = v;
                c.Parameters.Add(p);
            }

            c.ExecuteNonQuery();
        }
    }
}
