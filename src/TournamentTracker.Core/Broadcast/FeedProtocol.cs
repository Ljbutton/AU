using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TournamentTracker.Broadcast
{
    /// <summary>
    /// The broadcast feed, as one versioned contract between a host (the mod in their game, and their
    /// Button's "send to the caster" page) and the broadcast app (TT Broadcast). Every message carries
    /// <see cref="V"/> = <see cref="Version"/>. Readers ignore fields they don't know, so a newer host
    /// never breaks an older reader; a host older than <see cref="Version"/> still works but is shown as
    /// "host needs update". Field by field: docs/broadcast-protocol.md.
    /// </summary>
    public static class FeedProtocol
    {
        /// <summary>This version of the contract. Raise it when a field changes meaning or a reader must know something new.</summary>
        public const int Version = 1;
        /// <summary>Hosts below this still show, marked "host needs update" (0: from before messages were versioned).</summary>
        public const int Current = Version;

        // ---- Every message --------------------------------------------------------------------------
        public const string V = "v";              // protocol version (missing: 0, from before versions)
        public const string Type = "type";        // see Types
        public const string Kind = "kind";        // events only: see Kinds
        public const string Lobby = "lobby";      // the host's lobby label (else the lobby code)
        public const string Round = "round";
        public const string Game = "game";        // "LJ-3", or null outside a game
        public const string T = "t";              // when it happened, ms since 1970 on the host's clock
        public const string Clock = "clock";      // seconds into the game, or null
        public const string Src = "src";          // this run of the mod (a new one after Among Us restarts)
        public const string Seq = "seq";          // the run's message number, 1, 2, 3… (with src: each message once, in order)
        public const string Resent = "re";        // true when the host's page sends it again after a drop

        /// <summary>Message types.</summary>
        public static class Types
        {
            public const string Event = "event";    // something happened (Kinds)
            public const string Snap = "snap";      // the lobby's state, once a second
            public const string Track = "track";    // everyone's screen position, for replays, once a second
            public const string Voice = "voice";    // from the host's page: lobby voice status (Part 11)
            public const string Health = "health";  // from the host's page: is the screen share sending pictures (Part 22)
            public const string Skip = "skip";      // from the host's page: a stand-in for an old snap/track it dropped while the link was down
        }

        /// <summary>Event kinds.</summary>
        public static class Kinds
        {
            public const string GameStart = "gameStart", Kill = "kill", Meeting = "meeting", Eject = "eject", Tasks = "tasks",
                GameEnd = "gameEnd", Vent = "vent", Sabotage = "sabotage", KillReady = "killReady", Danger = "danger";
            public static readonly IReadOnlyList<string> All = new[] { GameStart, Kill, Meeting, Eject, Tasks, GameEnd, Vent, Sabotage, KillReady, Danger };
        }

        // ---- Over VDO.Ninja (the host's push link; only the caster has its password) -------------------
        /// <summary>Host → caster: <c>{sendData:{tt:[messages]}, type:'pcs'}</c>.</summary>
        public const string DataKey = "tt";
        /// <summary>Caster → host: a spectator view command, <c>{sendData:{ttc:"spec lit on"}, type:'rpcs'}</c>.</summary>
        public const string CommandKey = "ttc";
        /// <summary>Caster → host: roster names for the lobby's players (player key → name).</summary>
        public const string NamesKey = "ttn";
        /// <summary>Caster → host: what arrived, per run of the mod (src → last seq), so the page stops sending those again.</summary>
        public const string AckKey = "ttack";
        /// <summary>The only commands a host's page passes on to the mod.</summary>
        public const string CommandPattern = @"^spec [a-z]+( [a-z0-9.]+)?$";

        /// <summary>A message's version (0 when it has none: a host from before versions).</summary>
        public static int VersionOf(JsonElement message) =>
            message.ValueKind == JsonValueKind.Object && message.TryGetProperty(V, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;

        /// <summary>Messages the mod sends (the ones that say how up to date the host's game is).</summary>
        public static bool FromMod(string? type) => type is Types.Event or Types.Snap or Types.Track;

        /// <summary>What to tell the caster about a host on <paramref name="version"/>, or null when it's current.</summary>
        public static string? NeedsUpdate(int version) =>
            version >= Current ? null : version == 0 ? "host needs update (their mod is from before the broadcast app)" : $"host needs update (feed v{version}, this app expects v{Current})";
    }
}
