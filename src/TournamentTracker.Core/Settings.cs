using System.Collections.Generic;

namespace TournamentTracker
{
    public sealed class TrackerSettings
    {
        /// <summary>Stats are kept per tournament name, so a new name starts a fresh leaderboard.</summary>
        public string TournamentName { get; set; } = "Among Us Tournament";

        /// <summary>Discord webhook that receives the end-of-game report and leaderboard.</summary>
        public string StatsWebhookUrl { get; set; } = "";

        /// <summary>Optional webhook for a live play-by-play (kills, meetings, ejections). Keep it in a staff-only channel.</summary>
        public string LiveFeedWebhookUrl { get; set; } = "";

        public bool PostLeaderboardAfterEachGame { get; set; } = true;
        public int LeaderboardSize { get; set; } = 15;

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

        // Crewmate penalties
        public double IncorrectVoteOut { get; set; } = -2;

        /// <summary>A game decided by the other team disconnecting. Not on the sheet, so off by default.</summary>
        public double DisconnectWin { get; set; } = 0;
    }
}
