using System;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// What each host's The Button is told about its own lobby, for the tally on its Home: "pgm" while the
    /// lobby is on stream (with the view: "full", "2up", "4up", "grid", "cam", "cams" or "break"), "pvw"
    /// while it's staged in Preview, else "none". Only that lobby's own state: nothing about other lobbies,
    /// the players or the impostors. The desk page sends it over the lobby's VDO.Ninja link (the way roster
    /// names and camera picks go) when it changes and every few seconds; an older Button ignores it.
    /// </summary>
    public static class HostTally
    {
        /// <summary>The message's version (a newer one may add views The Button shows as plain "On stream").</summary>
        public const int Version = 1;

        public sealed class Message
        {
            public int V { get; init; } = Version;
            public string State { get; init; } = "none";
            public string? View { get; init; }
        }

        /// <summary>This lobby's tally message: the same rule as the desk's own tally lights (a replay isn't live).</summary>
        public static Message For(string lobby, OnAir air, PreviewState preview)
        {
            string state = CasterDesk.Tally(lobby, air, preview);
            return new Message { State = state, View = state == "pgm" ? air.Layout : null };
        }
    }
}
