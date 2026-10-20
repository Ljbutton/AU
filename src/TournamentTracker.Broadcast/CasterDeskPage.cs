using System;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// The desk page carries every lobby's data (its hidden VDO.Ninja links send what arrives to Red Alert).
    /// While it's gone (its renderer crashed or hung, or it's being loaded again) every lobby's data stops at
    /// once: that isn't every lobby dropping (their pictures still reach OBS), so auto switch, the LOBBY DOWN
    /// cards and "interrupted" games wait until the page is back and its links have had
    /// <see cref="PageGraceSeconds"/> to connect again. A lobby still down after that is dealt with as usual.
    /// </summary>
    public sealed partial class CasterDesk
    {
        /// <summary>The page asks Red Alert for something at least every 3 s (more often when it's in front): this long without, it's gone.</summary>
        public const double PageAwaySeconds = 6;
        /// <summary>After the page is back (or loaded again), how long its data links get to connect before a lobby without data counts as down.</summary>
        public const double PageGraceSeconds = 20;

        private DateTime _pageSeen, _pageGraceUntil;
        private bool _pageFailed;

        /// <summary>
        /// The desk page asked for something (it polls, and sends what the lobbies send). When it had been
        /// gone, how long for (in seconds), and the grace starts; else null.
        /// </summary>
        public double? PageSeen()
        {
            var now = _clock();
            lock (_lock)
            {
                double? gone = _pageSeen == default ? (double?)null : (now - _pageSeen).TotalSeconds;
                bool back = _pageFailed || gone >= PageAwaySeconds;
                _pageSeen = now;
                _pageFailed = false;
                if (!back) return null;
                _pageGraceUntil = now.AddSeconds(PageGraceSeconds);
                return gone ?? 0;
            }
        }

        /// <summary>The desk page was loaded (again): its data links start from nothing.</summary>
        public void PageLoaded()
        {
            var now = _clock();
            lock (_lock) { _pageGraceUntil = now.AddSeconds(PageGraceSeconds); _pageSeen = now; }
        }

        /// <summary>The window says the page crashed or hung (before it has been brought back).</summary>
        public void PageFailed()
        {
            lock (_lock) _pageFailed = true;
        }

        /// <summary>The page is gone, or back for less than the grace: a lobby without data isn't treated as down yet.</summary>
        public bool PageWait
        {
            get
            {
                var now = _clock();
                lock (_lock) return _pageFailed || _pageSeen != default && (now - _pageSeen).TotalSeconds >= PageAwaySeconds || now < _pageGraceUntil;
            }
        }
    }

    public sealed partial class BroadcastServer
    {
        /// <summary>The window says its page crashed or hung: auto switch waits until it's back.</summary>
        public void PageFailed()
        {
            _desk?.PageFailed();
        }

        /// <summary>The desk page was loaded (again).</summary>
        private void PageOpened() => _desk?.PageLoaded();

        /// <summary>The desk page asked for something: when it had been gone, that's said in the log.</summary>
        private void PageAsked()
        {
            if (_desk?.PageSeen() is { } gone)
                Log.Info($"The desk page is back (nothing from it for {gone:0} s): auto switch waits {CasterDesk.PageGraceSeconds:0} s while the lobbies' data links connect again.");
        }
    }
}
