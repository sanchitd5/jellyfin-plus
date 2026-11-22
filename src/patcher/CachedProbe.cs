using System;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Plus.Patcher
{
    /// <summary>
    /// The one way this assembly asks the machine something slow (a process spawn, a device
    /// probe) from a path that must not wait for it. The answer is gathered off the caller's
    /// thread, at start-up (<see cref="Start"/>) or by the first read that finds nothing, and the
    /// hot path only ever calls <see cref="Peek"/>, which takes a lock for a few field reads and
    /// never runs the probe.
    ///
    /// A conclusive answer is kept for the life of the process. A null or inconclusive one (the
    /// tool failed, the driver was still loading at boot) is kept only until <c>retryAfter</c>
    /// has passed; the next read then schedules another probe and still returns the last answer
    /// meanwhile, so a bad first probe can never pin the server to "nothing found" until restart.
    /// </summary>
    /// <typeparam name="T">The probed value; null means the probe has no answer yet.</typeparam>
    public sealed class CachedProbe<T>
        where T : class
    {
        private readonly Func<T> _probe;
        private readonly Func<T, bool> _conclusive;
        private readonly TimeSpan _retryAfter;
        private readonly Func<DateTime> _now;
        private readonly Action<Action> _schedule;
        private readonly object _lock = new object();

        private T _value;
        private bool _final;
        private bool _running;
        private DateTime _retryAt = DateTime.MinValue;
        private int _generation;

        /// <param name="probe">The slow call. It may throw; a throw is an answer of null.</param>
        /// <param name="conclusive">True when an answer is final and never needs asking again.</param>
        /// <param name="retryAfter">How long a null or inconclusive answer stands before the next probe is scheduled.</param>
        /// <param name="now">Replaces the clock, for tests.</param>
        /// <param name="schedule">Runs the probe off the caller's thread; defaults to the thread pool. Tests pass a synchronous one.</param>
        public CachedProbe(
            Func<T> probe,
            Func<T, bool> conclusive,
            TimeSpan retryAfter,
            Func<DateTime> now = null,
            Action<Action> schedule = null)
        {
            _probe = probe;
            _conclusive = conclusive;
            _retryAfter = retryAfter;
            _now = now ?? (() => DateTime.UtcNow);
            _schedule = schedule ?? (work => Task.Run(work));
        }

        /// <summary>Starts the first probe without waiting for it. Safe to call more than once.</summary>
        public void Start()
        {
            Kick();
        }

        /// <summary>
        /// The last answer, or null before the first one lands. Never probes on the calling
        /// thread; when there is no conclusive answer and the retry time has passed it schedules a
        /// probe and returns what it has.
        /// </summary>
        public T Peek()
        {
            T value;
            bool due;
            lock (_lock)
            {
                value = _value;
                due = !_final && !_running && _now() >= _retryAt;
            }

            if (due)
            {
                Kick();
            }

            return value;
        }

        /// <summary>Forgets the answer, so the next read probes again. A probe already running is discarded when it lands.</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _value = null;
                _final = false;
                _retryAt = DateTime.MinValue;
                _generation++;
            }
        }

        private void Kick()
        {
            int generation;
            lock (_lock)
            {
                if (_running || _final)
                {
                    return;
                }

                _running = true;
                generation = _generation;
            }

            try
            {
                _schedule(() => Run(generation));
            }
            catch (Exception)
            {
                lock (_lock)
                {
                    _running = false;
                    _retryAt = _now() + _retryAfter;
                }
            }
        }

        private void Run(int generation)
        {
            T answer = null;
            try
            {
                answer = _probe();
            }
            catch (Exception)
            {
                // a throwing probe is no answer; the retry time decides when to ask again
            }

            bool conclusive = false;
            try
            {
                conclusive = answer != null && _conclusive(answer);
            }
            catch (Exception)
            {
                // an answer nobody can judge is inconclusive
            }

            lock (_lock)
            {
                if (generation != _generation)
                {
                    // Reset ran while this probe was in flight: its answer is from before the
                    // reset and must not stand. _running belongs to whichever probe starts next.
                    _running = false;
                    return;
                }

                _running = false;
                if (answer != null)
                {
                    _value = answer;
                }

                _final = conclusive;
                _retryAt = _now() + _retryAfter;
            }
        }
    }
}
