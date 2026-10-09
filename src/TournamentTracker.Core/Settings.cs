using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker
{
    public enum TrackerMode
    {
        /// <summary>Everything from the config file, nothing preset.</summary>
        Standard,
        /// <summary>Practice/qualifier games in other servers: reports go to the organiser's server, nothing else.</summary>
        Preliminary,
        /// <summary>The real tournament: live status, combined standings by round, referee tools.</summary>
        Tournament,
    }

    public sealed class TrackerSettings
    {
        public TrackerMode Mode { get; set; } = TrackerMode.Standard;

        /// <summary>Keeps each tournament's (and each preliminary's) stats apart. Empty: made from TournamentName.</summary>
        public string TournamentId { get; set; } = "";

        public string EffectiveTournamentId => Slugify(TournamentId.Length > 0 ? TournamentId : TournamentName);

        /// <summary>Preliminaries only: the server these games are played in, for server standings.</summary>
        public string ServerName { get; set; } = "";

        /// <summary>Tournament: how many players from each lobby move on to the next round (the cut line).</summary>
        public int AdvanceCount { get; set; } = 5;

        /// <summary>Games each lobby plays per round; the cut-line tiebreak applies once they're played.</summary>
        public int GamesPerRound { get; set; } = 3;

        /// <summary>The tournament's game settings, from the setup code; null: nothing locked or checked.</summary>
        public LobbySettings? LobbySettings { get; set; }

        /// <summary>Make it rare for last game's impostors to be impostor again straight away (see Rotation). Off unless the setup code turns it on.</summary>
        public bool ImpostorRotation { get; set; }

        /// <summary>With rotation on: the chance each of last game's impostors is impostor again (0.02 = 2%).</summary>
        public double RepeatImpostorChance { get; set; } = 0.02;

        /// <summary>This host's mod answers the organiser's commands in the results channel (!lobbies, !start…).</summary>
        public bool Lead { get; set; }

        /// <summary>Port for the stream overlay (!overlay on), on this computer only.</summary>
        public int OverlayPort { get; set; } = 8765;

        /// <summary>Record a replay of every game (positions ~10 times a second, about 0.5 MB a game).</summary>
        public bool RecordReplays { get; set; } = true;

        /// <summary>
        /// Each game waits in The Button (Referee) for the host to press Submit before its results
        /// go to Discord. Off: they go as soon as the game ends.
        /// </summary>
        public bool VerifyResults { get; set; } = true;

        /// <summary>Tournaments with a results channel: keep a live data message there for the organiser's view and casters.</summary>
        public bool PublishLive { get; set; } = true;

        /// <summary>Send announcements to the whole lobby's chat. Off: only the host sees them (and the app).</summary>
        public bool PublicChat { get; set; }

        /// <summary>Say in the lobby chat when a player is linked to Discord (auto-link or /link).</summary>
        public bool AnnounceLinks { get; set; } = true;

        /// <summary>Port for the app's private connection (this computer only); -1 turns it off.</summary>
        public int ControlPort { get; set; } = 8766;

        /// <summary>Tournament: the organiser's preliminary channels, read for server standings.</summary>
        public List<string> PrelimChannelIds { get; set; } = new List<string>();

        /// <summary>Lower-case letters, digits and dashes.</summary>
        public static string Slugify(string name)
        {
            var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
            string slug = new string(chars).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return slug.Length == 0 ? "tournament" : slug;
        }

        /// <summary>Stats are kept per tournament name, so a new name starts a fresh leaderboard.</summary>
        public string TournamentName { get; set; } = "Among Us Tournament";

        /// <summary>
        /// Names this host's lobby in Discord and in game numbers ("Game LJ-3"). Empty: the
        /// host's in-game name. Lets several hosts share one TournamentName.
        /// </summary>
        public string LobbyLabel { get; set; } = "";

        /// <summary>Discord webhook that receives the end-of-game report and leaderboard.</summary>
        public string StatsWebhookUrl { get; set; } = "";

        /// <summary>Keep one live message in Discord showing the lobby, its players and their links, and the phase.</summary>
        public bool LiveStatus { get; set; } = true;

        /// <summary>Preliminaries: the host's own server channel for reports and its standings (set in The Button). The StatsWebhookUrl channel is the organiser's private one.</summary>
        public string PublicWebhookUrl { get; set; } = "";

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

        /// <summary>How The Button's commands start internally ("!r2", "!void"); nothing is typed in the game chat.</summary>
        public string CommandPrefix { get; set; } = "!";

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

        /// <summary>The bot posts the live status with a "pick your colour" menu that links whoever picks.</summary>
        public bool LinkMenu { get; set; } = true;

        /// <summary>
        /// The bot sits (muted) in the host's voice channel to see who's talking, and the host's game
        /// lights up linked players while they talk (in the lobby and in meetings).
        /// </summary>
        public bool ShowTalking { get; set; } = true;

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
        public double VotedCrewmateOut { get; set; } = 2;

        // Impostor, by how the game ended
        public double ImpostorSabotageWin { get; set; } = 5;
        public double ImpostorVoteWin { get; set; } = 4;
        public double ImpostorKillWin { get; set; } = 4;
        public double ImpostorTaskLoss { get; set; } = -3;
        public double ImpostorVoteLoss { get; set; } = -2;

        // Impostor penalties
        public double VotedOutFirst { get; set; } = -2;
        public double VotedOutLast { get; set; } = -1;

        // Crewmate, during the game
        public double CompletedTasks { get; set; } = 0;
        public double CorrectVoteOut { get; set; } = 2;
        public double CaughtKiller { get; set; } = 1;
        public double GotKilled { get; set; } = 0;

        /// <summary>
        /// The first crewmate killed ends the game on this share of their crew teammates' average
        /// points (0.9 = 90%), whatever they scored themselves: dying first is often luck, but
        /// shouldn't put anyone in the top half. 0 turns it off.
        /// </summary>
        public double DiedFirstShareOfCrewAverage { get; set; } = 0.9;

        // Crewmate, by how the game ended
        public double CrewTaskWin { get; set; } = 5;
        public double CrewVoteWin { get; set; } = 3;
        public double CrewSabotageLossAlive { get; set; } = -5;
        public double CrewOtherLoss { get; set; } = -2;

        // Crewmate bonuses
        /// <summary>
        /// Reads: votes for someone who was not ejected that meeting (a vote that ejected someone
        /// already scores as a correct or incorrect vote out). Each read on an impostor is worth
        /// <see cref="ReadVotePoints"/>, up to this cap, then scaled by the share of reads that
        /// were on impostors. So 4 right out of 4 beats 1 right out of 1, and guessing at every
        /// meeting doesn't pay.
        /// </summary>
        public double ReadVoteBonus { get; set; } = 4;
        public double ReadVotePoints { get; set; } = 1;

        /// <summary>Scaled by the share of the crewmate's task effort they finished: the full amount at 100%.</summary>
        public double TaskPercentBonus { get; set; } = 3;
        /// <summary>How many short or common tasks one long task is worth in that share.</summary>
        public double LongTaskWeight { get; set; } = 2;
        /// <summary>Percentage bonuses round to the nearest multiple of this (0.5 = halves). 0 keeps exact values.</summary>
        public double BonusRounding { get; set; } = 0.5;

        // Crewmate penalties
        public double IncorrectVoteOut { get; set; } = -2;

        /// <summary>A game decided by the other team disconnecting. Not on the sheet, so off by default.</summary>
        public double DisconnectWin { get; set; } = 0;
    }
}
