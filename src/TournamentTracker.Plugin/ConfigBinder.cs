using System;
using System.Linq;
using BepInEx.Configuration;

namespace TournamentTracker.Plugin
{
    /// <summary>Reads BepInEx/config/com.ljbutton.tournamenttracker.cfg into <see cref="TrackerSettings"/>.</summary>
    internal static class ConfigBinder
    {
        public static TrackerSettings Bind(ConfigFile config)
        {
            var s = new TrackerSettings();
            var d = new TrackerSettings();

            s.TournamentName = config.Bind("General", "TournamentName", d.TournamentName,
                "Stats are kept per tournament. Change the name to start a fresh leaderboard (the old one is kept).").Value;
            s.CommandPrefix = config.Bind("General", "CommandPrefix", d.CommandPrefix,
                "Prefix for chat commands, e.g. !link").Value;
            s.AllowSelfLink = config.Bind("General", "AllowSelfLink", d.AllowSelfLink,
                "Let players link their own Discord account with !link in the lobby chat. The host can always link anyone.").Value;

            s.StatsWebhookUrl = config.Bind("Discord", "StatsWebhookUrl", d.StatsWebhookUrl,
                "Webhook URL for game reports and the leaderboard (Server Settings > Integrations > Webhooks).").Value.Trim();
            s.LiveFeedWebhookUrl = config.Bind("Discord", "LiveFeedWebhookUrl", d.LiveFeedWebhookUrl,
                "Optional webhook for a live play-by-play of kills, meetings and ejections. It reveals the impostors: use a staff-only channel.").Value.Trim();
            s.PostLeaderboardAfterEachGame = config.Bind("Discord", "PostLeaderboardAfterEachGame", d.PostLeaderboardAfterEachGame,
                "Post the updated leaderboard after every game.").Value;
            s.LeaderboardSize = config.Bind("Discord", "LeaderboardSize", d.LeaderboardSize,
                "How many players the leaderboard shows.").Value;

            var m = s.AutoMute;
            m.Enabled = config.Bind("AutoMute", "Enabled", false,
                "Server-mute players in Discord voice: alive players during tasks, dead players during meetings.").Value;
            m.BotTokens = config.Bind("AutoMute", "BotTokens", "",
                "Discord bot token(s), comma separated. The bot needs Mute Members and Deafen Members. " +
                "Extra bots make muting a full lobby faster.").Value
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
            m.GuildId = config.Bind("AutoMute", "GuildId", "",
                "Your Discord server ID (enable Developer Mode, right-click the server, Copy Server ID).").Value.Trim();
            m.DeafenAliveDuringTasks = config.Bind("AutoMute", "DeafenAliveDuringTasks", m.DeafenAliveDuringTasks,
                "Also deafen alive players during tasks so they can't hear the dead talking.").Value;
            m.DeadCanTalkDuringTasks = config.Bind("AutoMute", "DeadCanTalkDuringTasks", m.DeadCanTalkDuringTasks,
                "Unmute dead players during tasks so they can talk to each other.").Value;
            m.MuteDeadDuringMeetings = config.Bind("AutoMute", "MuteDeadDuringMeetings", m.MuteDeadDuringMeetings,
                "Keep dead players muted during meetings.").Value;

            var p = s.Scoring;
            p.Win = config.Bind("Scoring", "Win", p.Win, "Points for being on the winning team.").Value;
            p.ImpostorWinBonus = config.Bind("Scoring", "ImpostorWinBonus", p.ImpostorWinBonus, "Extra points for winning as impostor.").Value;
            p.Kill = config.Bind("Scoring", "Kill", p.Kill, "Points per kill.").Value;
            p.CorrectVote = config.Bind("Scoring", "CorrectVote", p.CorrectVote, "Points per vote a crewmate casts on an impostor.").Value;
            p.IncorrectVote = config.Bind("Scoring", "IncorrectVote", p.IncorrectVote, "Points per vote a crewmate casts on a crewmate (use a negative number to penalise).").Value;
            p.AllTasksCompleted = config.Bind("Scoring", "AllTasksCompleted", p.AllTasksCompleted, "Points for a crewmate finishing every task.").Value;
            p.Survived = config.Bind("Scoring", "Survived", p.Survived, "Points for being alive at the end.").Value;
            p.ReportedBody = config.Bind("Scoring", "ReportedBody", p.ReportedBody, "Points per body reported.").Value;

            return s;
        }
    }
}
