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

            s.GoogleSheetsUrl = config.Bind("GoogleSheets", "WebAppUrl", "",
                "Web app URL from deploying sheets/TournamentSheet.gs in your Google Sheet (ends in /exec). Leave empty to skip Google Sheets.").Value.Trim();
            s.GoogleSheetsSecret = config.Bind("GoogleSheets", "Secret", "",
                "The same secret you set in the script's SECRET line. Stops anyone else who finds the URL from writing to your sheet.").Value.Trim();

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

            // Defaults follow the "Point Sheet Template" tab of Tournament_Points.xlsx.
            // Penalties are negative numbers; halves are fine.
            var p = s.Scoring;
            double Rule(string key, double value, string what) =>
                config.Bind("Scoring", key, value, what).Value;

            p.Kill = Rule("Kill", p.Kill, "Impostor: per kill.");
            p.FirstBlood = Rule("FirstBlood", p.FirstBlood, "Impostor: extra for the first kill of the game.");
            p.VotedCrewmateOut = Rule("VotedCrewmateOut", p.VotedCrewmateOut, "Impostor: per vote for a crewmate who then got ejected.");
            p.ImpostorSabotageWin = Rule("ImpostorSabotageWin", p.ImpostorSabotageWin, "Impostor: team won by sabotage.");
            p.ImpostorVoteWin = Rule("ImpostorVoteWin", p.ImpostorVoteWin, "Impostor: team won by voting crewmates out.");
            p.ImpostorKillWin = Rule("ImpostorKillWin", p.ImpostorKillWin, "Impostor: team won by kills.");
            p.ImpostorTaskLoss = Rule("ImpostorTaskLoss", p.ImpostorTaskLoss, "Impostor: team lost to crewmates finishing tasks.");
            p.ImpostorVoteLoss = Rule("ImpostorVoteLoss", p.ImpostorVoteLoss, "Impostor: team lost by being voted out.");
            p.VotedOutFirst = Rule("VotedOutFirst", p.VotedOutFirst, "Impostor: the first impostor ejected.");
            p.VotedOutLast = Rule("VotedOutLast", p.VotedOutLast, "Impostor: an impostor ejected after the first.");

            p.CompletedTasks = Rule("CompletedTasks", p.CompletedTasks, "Crewmate: finished every task.");
            p.CorrectVoteOut = Rule("CorrectVoteOut", p.CorrectVoteOut, "Crewmate: per vote for an impostor who then got ejected.");
            p.CaughtKiller = Rule("CaughtKiller", p.CaughtKiller, "Crewmate: called the meeting (report or button) where an impostor got ejected.");
            p.DiedFirst = Rule("DiedFirst", p.DiedFirst, "Crewmate: first player killed (instead of GotKilled).");
            p.GotKilled = Rule("GotKilled", p.GotKilled, "Crewmate: killed, other than first.");
            p.IncorrectVoteOut = Rule("IncorrectVoteOut", p.IncorrectVoteOut, "Crewmate: per vote for a crewmate who then got ejected.");
            p.VoteAccuracyBonus = Rule("VoteAccuracyBonus", p.VoteAccuracyBonus, "Crewmate: bonus scaled by the share of their votes that were on impostors (full amount at 100%). Counts every vote, ejected or not; skips don't count.");
            p.TaskPercentBonus = Rule("TaskPercentBonus", p.TaskPercentBonus, "Crewmate: bonus scaled by the share of their tasks they finished (full amount at 100%).");
            p.BonusRounding = Rule("BonusRounding", p.BonusRounding, "Round the two percentage bonuses to a multiple of this (0.5 = halves, 0 = exact).");
            p.CrewTaskWin = Rule("CrewTaskWin", p.CrewTaskWin, "Crewmate: team won on tasks.");
            p.CrewVoteWin = Rule("CrewVoteWin", p.CrewVoteWin, "Crewmate: team won by voting the impostors out.");
            p.CrewSabotageLossAlive = Rule("CrewSabotageLossAlive", p.CrewSabotageLossAlive, "Crewmate: alive when the team lost to a sabotage.");
            p.CrewOtherLoss = Rule("CrewOtherLoss", p.CrewOtherLoss, "Crewmate: team lost any other way (kills, votes, or dead during a sabotage loss).");

            p.DisconnectWin = Rule("DisconnectWin", p.DisconnectWin, "Either team: won because the other team disconnected. Not on the sheet.");

            return s;
        }
    }
}
