using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Plus
{
    // Reads Intel GPU load the way the kernel exports it, with no tool and no privilege.
    //
    // Every open DRM file shows its per-client engine usage in /proc/<pid>/fdinfo/<fd>
    // (Documentation/gpu/drm-usage-stats.rst). The i915 and xe drivers print different fields:
    //
    //   i915   drm-engine-<class>: <n> ns         busy time of the class, accumulated
    //          drm-engine-capacity-<class>: <n>   engines in the class (printed when more than one)
    //          classes: render, copy, video, video-enhance
    //   xe     drm-cycles-<class>: <n>            busy GPU cycles, accumulated
    //          drm-total-cycles-<class>: <n>      the cycles the engine had available
    //          classes: rcs, bcs, vcs, vecs, ccs
    //
    // Both print drm-driver, drm-pdev and drm-client-id. This file is BCL only and holds no state,
    // so the test harness compiles it on its own against fixture files (scripts/gpu-vendor-harness).
    // GpuLoadController does the sampling and the caching.

    /// <summary>One DRM client (one open DRM file) as an fdinfo file describes it.</summary>
    internal sealed class DrmClient
    {
        /// <summary>drm-driver: i915 or xe.</summary>
        public string Driver { get; set; }

        /// <summary>drm-pdev: the PCI address of the device, which scopes the client id.</summary>
        public string Pdev { get; set; }

        /// <summary>drm-client-id.</summary>
        public string ClientId { get; set; }

        /// <summary>The same client on the same device, whichever fd or process shows it.</summary>
        public string Key => (Pdev ?? string.Empty) + "/" + ClientId;

        /// <summary>Accumulated busy time per canonical class: nanoseconds for i915, cycles for xe.</summary>
        public Dictionary<string, long> Busy { get; } = new Dictionary<string, long>();

        /// <summary>xe only: the cycles the class had available, per canonical class. Empty for i915.</summary>
        public Dictionary<string, long> TotalCycles { get; } = new Dictionary<string, long>();

        /// <summary>i915 only: engines in the class, where the driver printed it.</summary>
        public Dictionary<string, int> Capacity { get; } = new Dictionary<string, int>();

        /// <summary>True when Busy is in xe cycles rather than i915 nanoseconds.</summary>
        public bool InCycles => string.Equals(Driver, "xe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The load worked out from two samples.</summary>
    internal sealed class DrmLoadResult
    {
        /// <summary>Busy percent (0-100) of each canonical class, on the busiest device, for the classes that were measurable.</summary>
        public Dictionary<string, double> BusyByClass { get; } = new Dictionary<string, double>();

        /// <summary>The busiest class's percent, or null when no class could be measured.</summary>
        public double? BusyPercent { get; set; }

        /// <summary>Distinct clients whose video-engine time moved in the window.</summary>
        public int VideoClients { get; set; }

        /// <summary>How many clients were in both samples (the only ones that can be measured).</summary>
        public int ClientsMeasured { get; set; }
    }

    internal static class DrmFdinfo
    {
        /// <summary>The canonical class names; i915 and xe spellings both map onto these.</summary>
        public const string Render = "render";
        public const string Copy = "copy";
        public const string Video = "video";
        public const string VideoEnhance = "video-enhance";
        public const string ComputeEngine = "compute";

        /// <summary>
        /// The raw busy percent above which an i915 class with no capacity line is left out. A
        /// little over 100 is jitter on a one-engine class and is clamped; well over it is a
        /// multi-engine class whose figure is not a percentage of anything.
        /// </summary>
        private const double UnnormalisedCeiling = 110.0;

        /// <summary>Maps an i915 or xe engine class name to the canonical one, or null for one not tracked.</summary>
        public static string CanonicalClass(string name)
        {
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "render":
                case "rcs":
                    return Render;
                case "copy":
                case "bcs":
                    return Copy;
                case "video":
                case "vcs":
                    return Video;
                case "video-enhance":
                case "vecs":
                    return VideoEnhance;
                case "compute":
                case "ccs":
                    return ComputeEngine;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Parses one fdinfo file. Null when it is not a DRM client (no drm-driver or no
        /// drm-client-id), which is every ordinary file descriptor in the process.
        /// </summary>
        public static DrmClient Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var client = new DrmClient();
            foreach (string raw in text.Split('\n'))
            {
                int colon = raw.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string key = raw.Substring(0, colon).Trim().ToLowerInvariant();
                string value = raw.Substring(colon + 1).Trim();
                if (!key.StartsWith("drm-", StringComparison.Ordinal))
                {
                    continue;
                }

                if (key == "drm-driver")
                {
                    client.Driver = value;
                }
                else if (key == "drm-pdev")
                {
                    client.Pdev = value;
                }
                else if (key == "drm-client-id")
                {
                    client.ClientId = value;
                }
                else if (key.StartsWith("drm-engine-capacity-", StringComparison.Ordinal))
                {
                    string cls = CanonicalClass(key.Substring("drm-engine-capacity-".Length));
                    if (cls != null && int.TryParse(FirstToken(value), NumberStyles.None, CultureInfo.InvariantCulture, out int cap) && cap > 0)
                    {
                        client.Capacity[cls] = cap;
                    }
                }
                else if (key.StartsWith("drm-engine-", StringComparison.Ordinal))
                {
                    Add(client.Busy, CanonicalClass(key.Substring("drm-engine-".Length)), value);
                }
                else if (key.StartsWith("drm-total-cycles-", StringComparison.Ordinal))
                {
                    Add(client.TotalCycles, CanonicalClass(key.Substring("drm-total-cycles-".Length)), value);
                }
                else if (key.StartsWith("drm-cycles-", StringComparison.Ordinal))
                {
                    Add(client.Busy, CanonicalClass(key.Substring("drm-cycles-".Length)), value);
                }
            }

            return client.Driver == null || client.ClientId == null ? null : client;
        }

        private static string FirstToken(string value)
        {
            int sp = value.IndexOf(' ');
            return sp < 0 ? value : value.Substring(0, sp);
        }

        private static void Add(Dictionary<string, long> into, string cls, string value)
        {
            // "<n> ns" for i915, a bare number for xe: the number is the first token either way.
            if (cls != null && long.TryParse(FirstToken(value), NumberStyles.None, CultureInfo.InvariantCulture, out long n))
            {
                into[cls] = n;
            }
        }

        /// <summary>
        /// Every DRM client this process can see on the given render node, deduplicated. Walks
        /// <c>procRoot/&lt;pid&gt;/fd</c>, and reads <c>fdinfo</c> only for the descriptors that
        /// are links to the render node, so a process with thousands of descriptors costs one
        /// readlink each and not a file read. With a null <paramref name="renderNode"/> every
        /// /dev/dri/renderD* link counts. An unreadable process (not ours) is skipped, which is why
        /// this sees Jellyfin and its ffmpeg children and not another container's work.
        /// Returns null when procRoot itself cannot be read.
        /// </summary>
        public static Dictionary<string, DrmClient> Scan(string procRoot, string renderNode)
        {
            var found = new Dictionary<string, DrmClient>(StringComparer.Ordinal);
            string[] pids;
            try
            {
                pids = Directory.GetDirectories(procRoot).Where(d => IsNumeric(Path.GetFileName(d))).ToArray();
            }
            catch (Exception)
            {
                return null;
            }

            foreach (string pid in pids)
            {
                string[] fds;
                try
                {
                    fds = Directory.GetFileSystemEntries(Path.Combine(pid, "fd"));
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string fd in fds)
                {
                    try
                    {
                        string target = new FileInfo(fd).LinkTarget;
                        if (!IsNode(target, renderNode))
                        {
                            continue;
                        }

                        DrmClient client = Parse(File.ReadAllText(Path.Combine(pid, "fdinfo", Path.GetFileName(fd))));
                        if (client != null && !found.ContainsKey(client.Key))
                        {
                            found[client.Key] = client;
                        }
                    }
                    catch (Exception)
                    {
                        // the descriptor closed between listing and reading, or is not ours
                    }
                }
            }

            return found;
        }

        private static bool IsNode(string target, string renderNode)
        {
            if (string.IsNullOrEmpty(target))
            {
                return false;
            }

            return renderNode == null
                ? target.StartsWith("/dev/dri/renderD", StringComparison.Ordinal)
                : string.Equals(target, renderNode, StringComparison.Ordinal);
        }

        private static bool IsNumeric(string name) => name.Length > 0 && name.All(char.IsDigit);

        /// <summary>
        /// The load between two samples. Only a client present in both can be measured: a client
        /// that appeared or went away inside the window has no start or no end to subtract. i915
        /// divides a class's summed busy nanoseconds by the window times the engines in the class;
        /// xe divides its summed busy cycles by the cycles that elapsed. Devices are kept apart,
        /// and the figure reported is the busiest class on the busiest device, clamped to 0-100.
        /// An i915 class with no capacity line whose raw figure is over 110% is left out (see
        /// <see cref="UnnormalisedCeiling"/>), and so is an xe class with no elapsed cycles; when
        /// any class is left out the headline BusyPercent is null, because that class may be the
        /// busiest one. The classes that could be measured stay in BusyByClass.
        /// </summary>
        public static DrmLoadResult Compute(Dictionary<string, DrmClient> previous, Dictionary<string, DrmClient> current, double elapsedSeconds)
        {
            var result = new DrmLoadResult();
            if (previous == null || current == null || elapsedSeconds <= 0)
            {
                return result;
            }

            double elapsedNs = elapsedSeconds * 1e9;

            // (pdev, class) -> summed busy delta, the cycles that elapsed, and the engines in the class
            var busy = new Dictionary<string, double>();
            var cycles = new Dictionary<string, double>();
            var capacity = new Dictionary<string, int>();
            var inCycles = new HashSet<string>();
            var videoClients = new HashSet<string>();

            foreach (DrmClient now in current.Values)
            {
                if (!previous.TryGetValue(now.Key, out DrmClient then))
                {
                    continue;
                }

                result.ClientsMeasured++;
                foreach (KeyValuePair<string, long> kv in now.Busy)
                {
                    if (!then.Busy.TryGetValue(kv.Key, out long was) || kv.Value < was)
                    {
                        // a class the earlier sample lacked, or a counter that went backwards
                        continue;
                    }

                    string slot = (now.Pdev ?? string.Empty) + "|" + kv.Key;
                    long delta = kv.Value - was;
                    busy[slot] = (busy.TryGetValue(slot, out double b) ? b : 0) + delta;
                    if (now.InCycles)
                    {
                        inCycles.Add(slot);
                    }

                    if (kv.Key == Video && delta > 0)
                    {
                        videoClients.Add(now.Key);
                    }

                    if (now.Capacity.TryGetValue(kv.Key, out int cap))
                    {
                        capacity[slot] = Math.Max(cap, capacity.TryGetValue(slot, out int c) ? c : 1);
                    }

                    if (now.InCycles && now.TotalCycles.TryGetValue(kv.Key, out long total) && then.TotalCycles.TryGetValue(kv.Key, out long totalWas) && total > totalWas)
                    {
                        cycles[slot] = Math.Max(total - totalWas, cycles.TryGetValue(slot, out double c2) ? c2 : 0);
                    }
                }
            }

            // A class left out as unmeasurable may be the busiest one, so the headline is then unknown.
            bool anyUnmeasured = false;
            foreach (KeyValuePair<string, double> kv in busy)
            {
                // i915 is measured against wall time; xe against the cycles that elapsed. An xe
                // class with no elapsed cycles cannot be measured and is left out, not read as 0.
                double denominator;
                bool unnormalised = false;
                if (inCycles.Contains(kv.Key))
                {
                    if (!cycles.TryGetValue(kv.Key, out denominator) || denominator <= 0)
                    {
                        anyUnmeasured = true;
                        continue;
                    }
                }
                else
                {
                    unnormalised = !capacity.TryGetValue(kv.Key, out int cap);
                    denominator = elapsedNs * (unnormalised ? 1 : cap);
                }

                double raw = kv.Value / denominator * 100.0;
                if (unnormalised && raw > UnnormalisedCeiling)
                {
                    // i915 prints a capacity line only for a class with more than one engine, so a
                    // figure this far over 100% with no line is a multi-engine class that cannot be
                    // normalised. Clamping it would report a busy GPU that may be half idle.
                    anyUnmeasured = true;
                    continue;
                }

                double pct = Math.Max(0, Math.Min(100, raw));
                string cls = kv.Key.Substring(kv.Key.IndexOf('|') + 1);
                if (!result.BusyByClass.TryGetValue(cls, out double prior) || pct > prior)
                {
                    result.BusyByClass[cls] = Math.Round(pct, 1);
                }
            }

            result.VideoClients = videoClients.Count;
            result.BusyPercent = anyUnmeasured || result.BusyByClass.Count == 0 ? (double?)null : result.BusyByClass.Values.Max();
            return result;
        }
    }

    /// <summary>
    /// Idle residency from sysfs: how much of a window the GT spent asleep (RC6 on i915, the GT idle
    /// state on xe). World-readable, no perf and no tool. It is "the GT slept", not "the engines
    /// were busy", and unlike fdinfo it covers every process on the GPU, so it is reported beside
    /// the busy figure and never folded into it.
    /// </summary>
    internal static class GpuIdleResidency
    {
        /// <summary>
        /// The residency counters of a render node, as (file, milliseconds asleep so far). Looks at
        /// <c>device/drm/card*/gt/gt*/rc6_residency_ms</c> and <c>device/drm/card*/power/rc6_residency_ms</c>
        /// (i915) and <c>device/tile*/gt*/gtidle/idle_residency_ms</c> (xe) under
        /// <c>drmRoot/&lt;node&gt;</c>. Empty when none exists or is readable.
        /// </summary>
        public static Dictionary<string, long> Read(string drmRoot, string renderNodeName)
        {
            var read = new Dictionary<string, long>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(renderNodeName))
            {
                return read;
            }

            string device = Path.Combine(drmRoot, renderNodeName, "device");
            try
            {
                foreach (string card in Sub(Path.Combine(device, "drm"), "card*"))
                {
                    foreach (string gt in Sub(Path.Combine(card, "gt"), "gt*"))
                    {
                        Take(read, Path.Combine(gt, "rc6_residency_ms"));
                    }

                    // Older kernels keep one counter for the whole device.
                    if (read.Count == 0)
                    {
                        Take(read, Path.Combine(card, "power", "rc6_residency_ms"));
                    }
                }

                foreach (string tile in Sub(device, "tile*"))
                {
                    foreach (string gt in Sub(tile, "gt*"))
                    {
                        Take(read, Path.Combine(gt, "gtidle", "idle_residency_ms"));
                    }
                }
            }
            catch (Exception)
            {
                // an unreadable tree is "no figure", not an error
            }

            return read;
        }

        /// <summary>
        /// The idle percent of the window for the least-idle GT (the busiest one), from two reads,
        /// or null when there is nothing comparable. Rounded to one decimal, clamped to 0-100.
        /// </summary>
        public static double? IdlePercent(Dictionary<string, long> previous, Dictionary<string, long> current, double elapsedSeconds)
        {
            if (previous == null || current == null || elapsedSeconds <= 0)
            {
                return null;
            }

            double elapsedMs = elapsedSeconds * 1000.0;
            double? least = null;
            foreach (KeyValuePair<string, long> kv in current)
            {
                if (!previous.TryGetValue(kv.Key, out long was) || kv.Value < was)
                {
                    continue;
                }

                double pct = Math.Max(0, Math.Min(100, (kv.Value - was) / elapsedMs * 100.0));
                if (least == null || pct < least)
                {
                    least = pct;
                }
            }

            return least == null ? (double?)null : Math.Round(least.Value, 1);
        }

        private static IEnumerable<string> Sub(string dir, string pattern)
        {
            return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : Array.Empty<string>();
        }

        private static void Take(Dictionary<string, long> into, string file)
        {
            try
            {
                if (File.Exists(file) && long.TryParse(File.ReadAllText(file).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long ms))
                {
                    into[file] = ms;
                }
            }
            catch (Exception)
            {
                // unreadable counter: left out
            }
        }
    }
}
