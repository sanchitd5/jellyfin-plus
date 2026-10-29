using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Plus
{
    /// <summary>
    /// How many cores background work (image warming, smart tag precompute) may use right now: the cores the
    /// container has, minus the cores ffmpeg is using for playback. One sampler reads /proc every couple of
    /// seconds; callers only read a cached integer. Not Linux or /proc unreadable: the container's cores minus two
    /// per active transcode (<see cref="TranscodeCount"/>). Never below 1.
    /// </summary>
    internal static class CpuBudget
    {
        internal static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(2);

        /// <summary>Fallback when /proc cannot be read: the number of active transcodes (set by a service holding the session manager).</summary>
        internal static Func<int> TranscodeCount;

        /// <summary>Raised after each sample so waiting gates can re-read their limit.</summary>
        internal static event Action Updated;

        private static int cores = Math.Max(1, ContainerCores(Environment.ProcessorCount, ReadCgroupCpuMax()));
        private static int playback;
        private static int started;
        private static int logTick;
        private static ILogger log;
        private static readonly Dictionary<int, long> Prev = new Dictionary<int, long>();
        private static long prevStamp;
        private static Timer timer;

        /// <summary>Cores background work may use now (cheap; starts the sampler on first use).</summary>
        internal static int Cores
        {
            get
            {
                EnsureStarted();
                return Budget(Volatile.Read(ref cores), Volatile.Read(ref playback));
            }
        }

        /// <summary>Cores ffmpeg uses for playback right now.</summary>
        internal static int PlaybackCores
        {
            get
            {
                EnsureStarted();
                return Volatile.Read(ref playback);
            }
        }

        /// <summary>Concurrency for background lanes that must leave half the budget free: max(1, budget / 2).</summary>
        internal static int BackgroundDegree => Math.Max(1, Cores / 2);

        // ---------- pure maths (unit-tested) ----------

        /// <summary>The smaller of the visible processors and the cgroup quota ("max 100000" or "200000 100000").</summary>
        internal static int ContainerCores(int processorCount, string cpuMax)
        {
            var c = Math.Max(1, processorCount);
            if (string.IsNullOrWhiteSpace(cpuMax))
            {
                return c;
            }

            var parts = cpuMax.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] != "max"
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var quota)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var period) && quota > 0 && period > 0)
            {
                c = (int)Math.Min(c, Math.Max(1, (quota + period - 1) / period));
            }

            return c;
        }

        /// <summary>Cores used by a set of processes: CPU clock ticks over the interval, rounded up (a 5% jitter is not a core).</summary>
        internal static int PlaybackCoresFor(long deltaTicks, double elapsedSeconds, int ticksPerSecond)
        {
            if (deltaTicks <= 0 || elapsedSeconds <= 0 || ticksPerSecond <= 0)
            {
                return 0;
            }

            return (int)Math.Ceiling((deltaTicks / (double)ticksPerSecond / elapsedSeconds) - 0.05);
        }

        /// <summary>The budget: container cores minus playback cores, at least 1.</summary>
        internal static int Budget(int containerCores, int playbackCores) => Math.Max(1, containerCores - Math.Max(0, playbackCores));

        /// <summary>Fallback budget without /proc: two cores per active transcode.</summary>
        internal static int FallbackPlaybackCores(int activeTranscodes) => 2 * Math.Max(0, activeTranscodes);

        /// <summary>utime + stime (clock ticks) from the text of /proc/pid/stat; -1 when it does not parse. comm may contain spaces and parentheses.</summary>
        internal static long ParseStatTicks(string stat)
        {
            if (stat == null)
            {
                return -1;
            }

            var close = stat.LastIndexOf(')');
            if (close < 0)
            {
                return -1;
            }

            // after ") ": state is field 3, utime field 14, stime field 15
            var t = stat.Substring(close + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 13
                || !long.TryParse(t[11], NumberStyles.None, CultureInfo.InvariantCulture, out var u)
                || !long.TryParse(t[12], NumberStyles.None, CultureInfo.InvariantCulture, out var s))
            {
                return -1;
            }

            return u + s;
        }

        // ---------- sampler ----------

        /// <summary>Starts the sampler (idempotent) and logs the budget once at info, then once a minute at debug.</summary>
        internal static void Start(ILogger logger)
        {
            Interlocked.CompareExchange(ref log, logger, null);
            EnsureStarted();
        }

        private static void EnsureStarted()
        {
            if (Interlocked.Exchange(ref started, 1) == 1)
            {
                return;
            }

            try
            {
                var seeded = Sample();
                Volatile.Write(ref playback, seeded);
                log?.LogInformation("JellyfinPlus: CPU budget: {Cores} cores, playback uses {Playback}.", Volatile.Read(ref cores), seeded);
                timer = new Timer(_ => Tick(), null, SampleEvery, SampleEvery);
            }
            catch (Exception)
            {
                // a budget that cannot be measured stays at the container's cores
            }
        }

        private static void Tick()
        {
            try
            {
                var p = Sample();
                var changed = Interlocked.Exchange(ref playback, p) != p;
                if (changed)
                {
                    Updated?.Invoke();
                }

                // 30 ticks of 2 s: once a minute
                if (Interlocked.Increment(ref logTick) % 30 == 0)
                {
                    log?.LogDebug("JellyfinPlus: CPU budget: {Budget} of {Cores} cores, playback uses {Playback}.", Budget(Volatile.Read(ref cores), p), Volatile.Read(ref cores), p);
                }
            }
            catch (Exception)
            {
                // keep the last reading
            }
        }

        private static int Sample()
        {
            lock (Prev)
            {
                return SampleLocked();
            }
        }

        private static int SampleLocked()
        {
            if (!Directory.Exists("/proc/self"))
            {
                return FallbackPlaybackCores(TranscodeCount?.Invoke() ?? 0);
            }

            try
            {
                // Every ffmpeg counts, including any the plugin's own lanes started: the safe direction.
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var current = new Dictionary<int, long>();
                long delta = 0;
                var elapsed = prevStamp == 0 ? 0 : (now - prevStamp) / (double)System.Diagnostics.Stopwatch.Frequency;
                foreach (var dir in Directory.EnumerateDirectories("/proc"))
                {
                    var name = Path.GetFileName(dir);
                    if (name.Length == 0 || name[0] < '1' || name[0] > '9' || !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                    {
                        continue;
                    }

                    string comm;
                    try
                    {
                        comm = File.ReadAllText(dir + "/comm");
                    }
                    catch (Exception)
                    {
                        continue; // exited, or not ours to read
                    }

                    if (comm.IndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    long ticks;
                    try
                    {
                        ticks = ParseStatTicks(File.ReadAllText(dir + "/stat"));
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (ticks < 0)
                    {
                        continue;
                    }

                    current[pid] = ticks;
                    // a process seen for the first time counts as one busy core until it has a delta
                    delta += Prev.TryGetValue(pid, out var before) ? Math.Max(0, ticks - before) : (long)(Clk * Math.Max(elapsed, 1));
                }

                Prev.Clear();
                foreach (var kv in current)
                {
                    Prev[kv.Key] = kv.Value;
                }

                prevStamp = now;
                return PlaybackCoresFor(delta, elapsed <= 0 ? 1 : elapsed, Clk);
            }
            catch (Exception)
            {
                return FallbackPlaybackCores(TranscodeCount?.Invoke() ?? 0);
            }
        }

        // USER_HZ is 100 on every Linux the server runs on; sysconf(_SC_CLK_TCK) is not worth a P/Invoke.
        private const int Clk = 100;

        private static string ReadCgroupCpuMax()
        {
            try
            {
                return File.Exists("/sys/fs/cgroup/cpu.max") ? File.ReadAllText("/sys/fs/cgroup/cpu.max") : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// An async gate whose limit is read again on every acquire, release and budget change, so it follows
    /// <see cref="CpuBudget"/> instead of a number fixed at startup. The limit is clamped to at least 1.
    /// </summary>
    internal sealed class DynamicGate
    {
        private readonly Func<int> _limit;
        private readonly object _lock = new object();
        private readonly LinkedList<TaskCompletionSource> _waiters = new LinkedList<TaskCompletionSource>();
        private int _active;

        public DynamicGate(Func<int> limit)
        {
            _limit = limit;
            CpuBudget.Updated += Pump;
        }

        /// <summary>Holders right now.</summary>
        internal int Active
        {
            get
            {
                lock (_lock)
                {
                    return _active;
                }
            }
        }

        public async Task WaitAsync(CancellationToken ct = default)
        {
            Task wait;
            lock (_lock)
            {
                if (_waiters.Count == 0 && _active < Math.Max(1, _limit()))
                {
                    _active++;
                    return;
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var node = _waiters.AddLast(tcs);
                wait = tcs.Task;
                if (ct.CanBeCanceled)
                {
                    ct.Register(() =>
                    {
                        lock (_lock)
                        {
                            if (node.List != null)
                            {
                                _waiters.Remove(node);
                                tcs.TrySetCanceled(ct);
                            }
                        }
                    });
                }
            }

            await wait.ConfigureAwait(false); // a granted waiter already holds its slot
        }

        public void Release()
        {
            lock (_lock)
            {
                _active--;
                PumpLocked();
            }
        }

        private void Pump()
        {
            lock (_lock)
            {
                PumpLocked();
            }
        }

        private void PumpLocked()
        {
            var limit = Math.Max(1, _limit());
            while (_waiters.Count > 0 && _active < limit)
            {
                var first = _waiters.First.Value;
                _waiters.RemoveFirst();
                _active++;
                first.TrySetResult();
            }
        }
    }
}
