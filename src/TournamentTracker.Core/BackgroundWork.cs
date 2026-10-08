using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TournamentTracker
{
    /// <summary>
    /// Work taken off the game's main thread (turning the caster feed, the overlay, the app's status
    /// and the live data into JSON), done one job at a time, in the order given, on one background
    /// thread. Inline (done at once, on the caller's thread) until <see cref="Start"/>: tests and
    /// tools see the results straight away, as before.
    /// </summary>
    public sealed class BackgroundWork : IDisposable
    {
        private readonly ILog _log;
        private BlockingCollection<Action>? _jobs;
        private Thread? _thread;
        private int _pending;
        private bool _loggedError;

        public BackgroundWork(ILog log) => _log = log;

        /// <summary>True once jobs go to the background thread.</summary>
        public bool Background => _jobs != null;

        /// <summary>Jobs given but not done yet.</summary>
        public int Pending => Volatile.Read(ref _pending);

        /// <summary>From now on, jobs run on a background thread.</summary>
        public void Start()
        {
            if (_jobs != null) return;
            _jobs = new BlockingCollection<Action>();
            var jobs = _jobs;
            _thread = new Thread(() =>
            {
                foreach (var job in jobs.GetConsumingEnumerable())
                {
                    Run(job);
                    Interlocked.Decrement(ref _pending);
                }
            }) { IsBackground = true, Name = "Tournament Tracker JSON" };
            _thread.Start();
        }

        /// <summary>Runs <paramref name="job"/> after every job given before it.</summary>
        public void Post(Action job)
        {
            var jobs = _jobs;
            if (jobs == null || jobs.IsAddingCompleted) { Run(job); return; }
            Interlocked.Increment(ref _pending);
            try { jobs.Add(job); }
            catch (InvalidOperationException) { Interlocked.Decrement(ref _pending); Run(job); }
        }

        /// <summary>Waits (up to <paramref name="timeout"/>) for every job given so far.</summary>
        public bool Flush(TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (Pending > 0)
            {
                if (DateTime.UtcNow > until) return false;
                Thread.Sleep(1);
            }
            return true;
        }

        private void Run(Action job)
        {
            try { job(); }
            catch (Exception e)
            {
                if (!_loggedError) _log.Error("Background work failed: " + e);
                _loggedError = true;
            }
        }

        public void Dispose()
        {
            var jobs = _jobs;
            if (jobs == null) return;
            jobs.CompleteAdding();
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// A job done every <c>period</c> seconds, split into steps that run on consecutive frames
    /// (one step a frame), so no single frame does all of it.
    /// </summary>
    public sealed class TickStagger
    {
        private readonly double _period;
        private readonly int _steps;
        private double _nextStart = double.MinValue;
        private int _step = -1;

        public TickStagger(double period, int steps)
        {
            if (steps < 1) throw new ArgumentOutOfRangeException(nameof(steps));
            _period = period;
            _steps = steps;
        }

        /// <summary>Called once a frame: the step to run this frame (0 starts a new round), or -1 for none.</summary>
        public int Next(double now)
        {
            if (_step >= 0 && _step < _steps - 1) return ++_step;
            if (now < _nextStart) { _step = -1; return -1; }
            // A round every period, counted from when the last one started (at least one frame per step).
            _nextStart = now + _period;
            _step = 0;
            return 0;
        }
    }
}
