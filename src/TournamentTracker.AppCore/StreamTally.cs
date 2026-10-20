using System;

namespace TournamentTracker.App
{
    /// <summary>
    /// The caster's tally for this host's lobby, for Home: red "ON STREAM" while Red Alert has the lobby on
    /// Program (and how: full screen, in the quad…), "NEXT" while it's in Preview, nothing otherwise. Red Alert
    /// sends it over the send page's VDO.Ninja link (only this lobby's own state, nothing about other lobbies
    /// or the players), when it changes and every few seconds; the send page hands it to The Button. Heard
    /// nothing for <see cref="StaleSeconds"/> (the link dropped, the send page closed, an older Red Alert that
    /// doesn't send it): Home shows nothing.
    /// </summary>
    public sealed class StreamTally
    {
        /// <summary>No word from Red Alert for this long: the tally isn't shown.</summary>
        public const double StaleSeconds = 10;

        public const string Hint = "You're on stream: stay in the game window, no menus.";

        private readonly object _lock = new object();
        private string _state = "none";
        private string? _view;
        private DateTime _at = DateTime.MinValue;

        /// <summary>
        /// What Red Alert said: "pgm" (on stream), "pvw" (in Preview) or "none", and for "pgm" the view it's
        /// in ("full", "2up", "4up", "grid", "cam", "cams", "break"). False (and nothing kept) for anything else.
        /// </summary>
        public bool Set(string? state, string? view, DateTime now)
        {
            if (state is not ("pgm" or "pvw" or "none")) return false;
            if (view != null && (view.Length > 12 || !IsWord(view))) view = null;
            lock (_lock) { _state = state; _view = state == "pgm" ? view : null; _at = now; }
            return true;
        }

        /// <summary>Forgotten at once (sending switched off).</summary>
        public void Clear()
        {
            lock (_lock) { _state = "none"; _view = null; _at = DateTime.MinValue; }
        }

        /// <summary>For Home: the tally and what it says, or null when there's nothing to show (or it's stale).</summary>
        public object? View(DateTime now)
        {
            string state; string? view; DateTime at;
            lock (_lock) { state = _state; view = _view; at = _at; }
            if (at == DateTime.MinValue || (now - at).TotalSeconds > StaleSeconds || state == "none") return null;
            return state == "pgm"
                ? new { State = "pgm", Badge = "ON STREAM", Text = Words(view), Hint = (string?)Hint }
                : new { State = "pvw", Badge = "NEXT", Text = "Next: the caster has your lobby in Preview.", Hint = (string?)null };
        }

        /// <summary>"On stream (full screen)", "On stream (in the quad)"…; plain "On stream" for a view this version doesn't know.</summary>
        public static string Words(string? view) => view switch
        {
            "full" => "On stream (full screen)",
            "2up" => "On stream (in the 2-up)",
            "4up" => "On stream (in the quad)",
            "grid" => "On stream (in the grid)",
            "cam" or "cams" => "On stream (player camera)",
            "break" => "On stream (sponsor break)",
            _ => "On stream",
        };

        private static bool IsWord(string s)
        {
            foreach (char c in s) if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9')) return false;
            return s.Length > 0;
        }
    }
}
