using System.Collections.Generic;
using TournamentTracker.Voice;

namespace TournamentTracker.Broadcast
{
    /// <summary>One player as the host's game sees them this frame.</summary>
    public sealed class FeedPlayer
    {
        public byte Id { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public bool Dead { get; set; }
        public bool InVent { get; set; }
        public bool Disconnected { get; set; }
        /// <summary>The room they're standing in (the game's room name), or null in a corridor.</summary>
        public string? Room { get; set; }
    }

    /// <summary>A sabotage that's on right now. <see cref="TimeLeft"/> is set for reactor, O2 and the like.</summary>
    public sealed class FeedSabotage
    {
        public string System { get; set; } = "";
        public float? TimeLeft { get; set; }
        /// <summary>How far the fix is (0–1): hands on the reactor panels, O2 codes entered. Null when the game doesn't say.</summary>
        public float? Fixing { get; set; }
    }

    /// <summary>What the host's camera shows, in world units: its centre and half its width and height.</summary>
    public sealed class FeedCamera
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float HalfWidth { get; set; }
        public float HalfHeight { get; set; }
    }

    /// <summary>Where something happened: world position, room, and the camera at that moment.</summary>
    public sealed class FeedPlace
    {
        public float X { get; set; }
        public float Y { get; set; }
        public string? Room { get; set; }
        public FeedCamera? Camera { get; set; }
    }

    /// <summary>Everything the plugin reads from the game each tick (5 times a second).</summary>
    public sealed class FeedFrame
    {
        public VoicePhase Phase { get; set; }
        public List<FeedPlayer> Players { get; set; } = new List<FeedPlayer>();
        public List<FeedSabotage> Sabotages { get; set; } = new List<FeedSabotage>();
        public FeedCamera? Camera { get; set; }
        /// <summary>The lobby's kill cooldown setting, in seconds.</summary>
        public float KillCooldown { get; set; } = 25;
        /// <summary>The lobby's kill distance setting, in world units (short 1, medium 1.8, long 2.5).</summary>
        public float KillDistance { get; set; } = 1.8f;
    }

    /// <summary>
    /// How the host's game decides what's happening. The caster's Button can send new values;
    /// these are the starting ones.
    /// </summary>
    public sealed class FeedTuning
    {
        /// <summary>An impostor this many kill distances from a crewmate counts as "near" them.</summary>
        public float DangerReach { get; set; } = 1.6f;
        /// <summary>A crewmate is "alone" with an impostor when nobody else alive is this close (world units).</summary>
        public float AloneRadius { get; set; } = 5.5f;
        /// <summary>A danger has to be gone this long before it's called over (stops flicker).</summary>
        public float DangerEndSeconds { get; set; } = 1.5f;
        /// <summary>The game gives impostors this cooldown at the start (or their setting, if shorter).</summary>
        public float FirstKillCooldown { get; set; } = 10;
        /// <summary>A snapshot of the lobby every this many seconds.</summary>
        public float SnapshotSeconds { get; set; } = 1;
        /// <summary>Everyone's screen position is sampled this often for replays (sent once a second).</summary>
        public float TrackSeconds { get; set; } = 0.2f;
    }
}
