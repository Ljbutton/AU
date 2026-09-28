using System.Collections.Generic;

namespace TournamentTracker
{
    public sealed class TrackerSettings
    {
        /// <summary>Stats are kept per tournament name, so a new name starts a fresh leaderboard.</summary>
        public string TournamentName { get; set; } = "Among Us Tournament";

        /// <summary>
        /// Names this host's lobby in Discord and in game numbers ("Game LJ-3"). Empty: the
        /// host's in-game name. Lets several hosts share one TournamentName.
        /// </summary>
        public string LobbyLabel { get; set; } = "";

        /// <summary>Discord webhook that receives the end-of-game report and leaderboard.</summary>
        public string StatsWebhookUrl { get; set; } = "";

        /// <summary>Optional webhook for a live play-by-play (kills, meetings, ejections). Keep it in a staff-only channel.</summary>
        public string LiveFeedWebhookUrl { get; set; } = "";

        /// <summary>Keep one live message in Discord showing the lobby, its players and their links, and the phase.</summary>
        public bool LiveStatus { get; set; } = true;

        /// <summary>Webhook for the live status message. Empty: the StatsWebhookUrl channel.</summary>
        public string StatusWebhookUrl { get; set; } = "";

        /// <summary>
        /// A private channel where every host's mod posts its games, for one combined leaderboard
        /// across all lobbies. Uses the first bot token. Empty: each host keeps its own leaderboard.
        /// </summary>
        public string ResultsChannelId { get; set; } = "";

        public bool PostLeaderboardAfterEachGame { get; set; } = true;
        public int LeaderboardSize { get; set; } = 15;

        /// <summary>Players need this many counted games to appear on the leaderboard.</summary>
        public int LeaderboardMinGames { get; set; } = 1;

        /// <summary>Show linked players as Discord @mentions on the leaderboard (no one gets pinged).</summary>
        public bool LeaderboardMentions { get; set; }

        public string CommandPrefix { get; set; } = "!";

        /// <summary>Lets players link their own Discord account with "!link" from the lobby chat.</summary>
        public bool AllowSelfLink { get; set; } = true;

        public AutoMuteSettings AutoMute { get; set; } = new AutoMuteSettings();
        public ScoringRules Scoring { get; set; } = new ScoringRules();
    }

    public sealed class AutoMuteSettings
    {
        public bool Enabled { get; set; }

        /// <summary>
        /// One or more bot tokens. Discord rate-limits member edits per bot, so extra
        /// bots (all invited to the server) spread a 15-player mute across more lanes.
        /// </summary>
        public List<string> BotTokens { get; set; } = new List<string>();

        public string GuildId { get; set; } = "";

        /// <summary>Alive players are deafened as well as muted during tasks, so they can't hear the dead.</summary>
        public bool DeafenAliveDuringTasks { get; set; } = true;

        /// <summary>Dead players are unmuted during tasks so they can talk among themselves.</summary>
        public bool DeadCanTalkDuringTasks { get; set; } = true;

        /// <summary>Dead players stay muted during meetings.</summary>
        public bool MuteDeadDuringMeetings { get; set; } = true;

        // Seconds to wait before switching voice to a new phase.
        /// <summary>Lobby to tasks: lets everyone react to the role reveal before the mute.</summary>
        public double DelayGameStart { get; set; } = 3;
        /// <summary>Meeting to tasks: the end of the ejection screen.</summary>
        public double DelayMeetingEnd { get; set; } = 3;
        /// <summary>Tasks or meeting to the end screen or lobby.</summary>
        public double DelayGameEnd { get; set; } = 3;
        /// <summary>Tasks to meeting. 0 so nobody loses the start of the discussion.</summary>
        public double DelayMeetingStart { get; set; } = 0;

        /// <summary>Server-mute people in the game's voice channel who aren't in the lobby, while a game is running.</summary>
        public bool MuteSpectators { get; set; }

        /// <summary>The voice channel the game uses. Empty: the channel most linked players are in.</summary>
        public string VoiceChannelId { get; set; } = "";

        /// <summary>Discord user IDs never muted as spectators (casters, referees).</summary>
        public List<string> SpectatorExemptUserIds { get; set; } = new List<string>();

        /// <summary>Discord user IDs who can still talk while referee mode (!ref on) mutes everyone else.</summary>
        public List<string> RefereeUserIds { get; set; } = new List<string>();

        /// <summary>Link players automatically when their in-game name matches a Discord name in the voice channel.</summary>
        public bool AutoLinkByName { get; set; } = true;

        public bool IsConfigured => Enabled && BotTokens.Count > 0 && GuildId.Length > 0;
    }

    /// <summary>
    /// Tournament points, matching the "Point Sheet Template" tab of Tournament_Points.xlsx.
    /// Every value can be changed in the config file; 0 switches a rule off. Penalties are
    /// stored as negative numbers. Halves are allowed.
    /// </summary>
    public sealed class ScoringRules
    {
        // Impostor, during the game
        public double Kill { get; set; } = 1;
        public double FirstBlood { get; set; } = 1;
        public double VotedCrewmateOut { get; set; } = 1;

        // Impostor, by how the game ended
        public double ImpostorSabotageWin { get; set; } = 5;
        public double ImpostorVoteWin { get; set; } = 3;
        public double ImpostorKillWin { get; set; } = 3;
        public double ImpostorTaskLoss { get; set; } = -3;
        public double ImpostorVoteLoss { get; set; } = -2;

        // Impostor penalties
        public double VotedOutFirst { get; set; } = -2;
        public double VotedOutLast { get; set; } = -1;

        // Crewmate, during the game
        public double CompletedTasks { get; set; } = 3;
        public double CorrectVoteOut { get; set; } = 2;
        public double CaughtKiller { get; set; } = 1;
        public double DiedFirst { get; set; } = 1;
        public double GotKilled { get; set; } = 0.5;

        // Crewmate, by how the game ended
        public double CrewTaskWin { get; set; } = 5;
        public double CrewVoteWin { get; set; } = 3;
        public double CrewSabotageLossAlive { get; set; } = -5;
        public double CrewOtherLoss { get; set; } = -1;

        // Crewmate bonuses scaled by percentage: the full amount at 100%, half at 50%, and so on.
        /// <summary>Share of the crewmate's votes that were on impostors (whether or not they got ejected).</summary>
        public double VoteAccuracyBonus { get; set; } = 2;
        /// <summary>Share of the crewmate's tasks they finished.</summary>
        public double TaskPercentBonus { get; set; } = 2;
        /// <summary>Percentage bonuses round to the nearest multiple of this (0.5 = halves). 0 keeps exact values.</summary>
        public double BonusRounding { get; set; } = 0.5;

        // Crewmate penalties
        public double IncorrectVoteOut { get; set; } = -2;

        /// <summary>A game decided by the other team disconnecting. Not on the sheet, so off by default.</summary>
        public double DisconnectWin { get; set; } = 0;
    }
}
