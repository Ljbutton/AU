using System;
using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// A sponsor break can't be interrupted: it runs its time with its sponsor on screen. A lobby picked
    /// meanwhile (Take, a cut, a Stream Deck key, a player camera) goes into the break's picture; anything
    /// that would replace the whole screen (a replay, a montage, BRB, intermission, starting soon, another
    /// break) is refused. Only its time running out, End break and Panic end it.
    /// </summary>
    public sealed partial class CasterDesk
    {
        /// <summary>Seconds left in the break, rounded up.</summary>
        private int BreakLeft(DateTime until) => Math.Max(0, (int)Math.Ceiling((until - _clock()).TotalSeconds));

        /// <summary>"A sponsor break is on: 18 s left. End break first." while a break is on stream, else null.</summary>
        public string? BreakRefusal()
        {
            if (Break is not { } b || OnAir.Layout != "break") return null;
            return $"A sponsor break is on: {BreakLeft(b.Until)} s left. End break first.";
        }

        /// <summary>What a lobby switch says when it went into a sponsor break that carries on, else null.</summary>
        public string? BreakSaid(OnAir air)
        {
            if (air.Layout != "break" || Break is not { } b) return null;
            return $"Sponsor break on ({BreakLeft(b.Until)} s left): {b.Lobby} is in the break's picture now.";
        }

        /// <summary>
        /// While a sponsor break is on: <paramref name="lobby"/> (null or empty: the break's own) goes into the
        /// break's picture, and the break carries on with its sponsor, its time and its sponsor appearance.
        /// Null when no break is on (the switch goes ahead as usual).
        /// </summary>
        private OnAir? IntoBreak(string? lobby, string by, string? transition)
        {
            OnAir next;
            lock (_lock)
            {
                if (_onAir.Layout != "break" || Break is not { } b) return null;
                string? to = string.IsNullOrEmpty(lobby) ? b.Lobby : lobby;
                next = new OnAir { Layout = "break", Slots = new List<string?> { to }, By = by, Since = _clock(), Transition = transition };
                _onAir = next;
                MarkShown(next);
                Break = (b.Sponsor, b.Until, to);
            }
            Left(next);
            Raise(next);
            Touch();
            return next;
        }

        /// <summary>The break over: its lobby (as it is now) full screen. Its time up, End break.</summary>
        private OnAir EndSponsorBreak(string by, string? transition = null) =>
            ShowLayout(Break?.Lobby ?? OnAir.Slots.FirstOrDefault() ?? "", "full", null, null, by, transition);

        /// <summary>
        /// Undo now would replace the whole screen of a sponsor break (what was on before it was intermission,
        /// say): the break's refusal, else null (Undo goes ahead; a lobby goes into the break's picture).
        /// </summary>
        public string? BackRefused()
        {
            if (BreakRefusal() is not { } no) return null;
            OnAir? back;
            lock (_switchLock) back = _previous;
            return back == null || back.Layout is "full" or "2up" or "4up" or "grid" or "cam" or "cams" or "break" ? null : no;
        }

        /// <summary>A lobby on the grid that has dropped (down for a while, or gone quiet for good): its tile goes.</summary>
        private bool GridDropped(string lobby)
        {
            var r = Board.Ranking().FirstOrDefault(x => string.Equals(x.Lobby, lobby, StringComparison.OrdinalIgnoreCase));
            if (r == null) return true;
            return DownLong(lobby) || !r.Online && !Reconnecting(lobby);
        }
    }
}
