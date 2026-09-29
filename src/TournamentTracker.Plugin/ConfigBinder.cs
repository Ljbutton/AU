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
            s.LobbyLabel = config.Bind("General", "LobbyLabel", "",
                "Names this host's lobby in Discord and in game numbers (\"Game LJ-3\"). Leave empty to use the host's in-game name. All hosts can then share one TournamentName.").Value.Trim();
            s.ChatCommands = config.Bind("General", "ChatCommands", d.ChatCommands,
                "Accept commands typed in the lobby chat (!r2, !void…). Off: use the Tournament Tracker app instead, so nothing is typed in chat.").Value;
            s.PublicChat = config.Bind("General", "PublicChat", d.PublicChat,
                "Send announcements to everyone's chat. Off: only you (and the app) see them.").Value;
            s.AnnounceLinks = config.Bind("General", "AnnounceLinks", d.AnnounceLinks,
                "Say in the lobby chat when a player is linked to Discord (by name automatically, or with /link).").Value;
            s.ControlPort = config.Bind("General", "ControlPort", d.ControlPort,
                "Port the Tournament Tracker app connects on (this computer only). -1 turns the connection off.").Value;
            s.PublishLive = config.Bind("General", "PublishLive", d.PublishLive,
                "Tournaments with a results channel: keep a live data message there for the organiser's view and caster overlay.").Value;
            s.RecordReplays = config.Bind("General", "RecordReplays", d.RecordReplays,
                "Record a replay of every game for the replay viewer (about 0.5 MB each, saved with the game).").Value;
            s.OverlayPort = config.Bind("General", "OverlayPort", d.OverlayPort,
                "Port for the stream overlay (!overlay on), reachable from this computer only.").Value;
            s.GamesPerRound = config.Bind("General", "GamesPerRound", d.GamesPerRound,
                "Tournament rounds: games each lobby plays per round. A setup code overrides it.").Value;
            s.CommandPrefix = config.Bind("General", "CommandPrefix", d.CommandPrefix,
                "Prefix for chat commands, e.g. !link").Value;
            s.AllowSelfLink = config.Bind("General", "AllowSelfLink", d.AllowSelfLink,
                "Let players link their own Discord account with !link in the lobby chat. The host can always link anyone.").Value;

            s.StatsWebhookUrl = config.Bind("Discord", "StatsWebhookUrl", d.StatsWebhookUrl,
                "Webhook URL for game reports and the leaderboard (Server Settings > Integrations > Webhooks).").Value.Trim();
            s.LiveFeedWebhookUrl = config.Bind("Discord", "LiveFeedWebhookUrl", d.LiveFeedWebhookUrl,
                "Optional webhook for a live play-by-play of kills, meetings and ejections. It reveals the impostors: use a staff-only channel.").Value.Trim();
            s.LiveStatus = config.Bind("Discord", "LiveStatus", s.LiveStatus,
                "Keep one live message in Discord showing the lobby code, map, phase, and each player's colour, name and Discord link. Deaths only show once the game has revealed them.").Value;
            s.StatusWebhookUrl = config.Bind("Discord", "StatusWebhookUrl", "",
                "Webhook for the live status message. Leave empty to use the StatsWebhookUrl channel.").Value.Trim();
            s.ResultsChannelId = config.Bind("Discord", "ResultsChannelId", "",
                "For several hosts: the ID of a private channel where every host's mod posts its games, so one combined leaderboard covers all lobbies. Type !resetleaderboard in that channel to start it over. Needs a bot token and the Message Content Intent.").Value.Trim();
            s.PostLeaderboardAfterEachGame = config.Bind("Discord", "PostLeaderboardAfterEachGame", d.PostLeaderboardAfterEachGame,
                "Post the updated leaderboard after every game.").Value;
            s.LeaderboardSize = config.Bind("Discord", "LeaderboardSize", d.LeaderboardSize,
                "How many players the leaderboard shows.").Value;
            s.LeaderboardMinGames = config.Bind("Discord", "LeaderboardMinGames", d.LeaderboardMinGames,
                "Players need at least this many counted games to appear on the leaderboard.").Value;
            s.LeaderboardMentions = config.Bind("Discord", "LeaderboardMentions", d.LeaderboardMentions,
                "Show linked players as Discord @mentions on the leaderboard. Nobody gets pinged.").Value;

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

            m.DelayGameStart = config.Bind("AutoMute", "DelayGameStart", m.DelayGameStart,
                "Seconds between the game starting and alive players being muted (time to react to the role reveal).").Value;
            m.DelayMeetingEnd = config.Bind("AutoMute", "DelayMeetingEnd", m.DelayMeetingEnd,
                "Seconds between a meeting ending and alive players being muted again.").Value;
            m.DelayGameEnd = config.Bind("AutoMute", "DelayGameEnd", m.DelayGameEnd,
                "Seconds between the game ending and everyone being unmuted.").Value;
            m.DelayMeetingStart = config.Bind("AutoMute", "DelayMeetingStart", m.DelayMeetingStart,
                "Seconds between a meeting being called and alive players being unmuted. 0 so nobody misses the start of the discussion.").Value;

            m.MuteSpectators = config.Bind("AutoMute", "MuteSpectators", m.MuteSpectators,
                "Server-mute people in the game's voice channel who aren't playing, while a game is running. Also !spectators on|off.").Value;
            m.VoiceChannelId = config.Bind("AutoMute", "VoiceChannelId", "",
                "The game's voice channel ID. Leave empty to use the channel most linked players are in.").Value.Trim();
            m.SpectatorExemptUserIds = config.Bind("AutoMute", "SpectatorExemptUserIds", "",
                "Discord user IDs never muted as spectators (casters, referees), comma separated.").Value
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
            m.RefereeUserIds = config.Bind("AutoMute", "RefereeUserIds", "",
                "Discord user IDs who can still talk during referee mode (!ref on), besides the host. Comma separated.").Value
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList();
            m.AutoLinkByName = config.Bind("AutoMute", "AutoLinkByName", m.AutoLinkByName,
                "In the lobby, link a player automatically when their in-game name matches exactly one person's Discord name in the voice channel.").Value;
            m.LinkMenu = config.Bind("AutoMute", "LinkMenu", m.LinkMenu,
                "The bot posts the live status itself, with a menu where players pick their colour to link. Off: a plain webhook message.").Value;

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
            p.GotKilled = Rule("GotKilled", p.GotKilled, "Crewmate: killed.");
            p.DiedFirstShareOfCrewAverage = Rule("DiedFirstShareOfCrewAverage", p.DiedFirstShareOfCrewAverage, "Crewmate: the first player killed ends on this share of their crew teammates' average points (0.9 = 90%). 0 turns it off.");
            p.IncorrectVoteOut = Rule("IncorrectVoteOut", p.IncorrectVoteOut, "Crewmate: per vote for a crewmate who then got ejected.");
            p.ReadVoteBonus = Rule("ReadVoteBonus", p.ReadVoteBonus, "Crewmate: most a player can earn for reads (votes for someone who wasn't ejected that meeting).");
            p.ReadVotePoints = Rule("ReadVotePoints", p.ReadVotePoints, "Crewmate: per read on an impostor, up to ReadVoteBonus, then scaled by the share of their reads that were on impostors.");
            p.TaskPercentBonus = Rule("TaskPercentBonus", p.TaskPercentBonus, "Crewmate: bonus scaled by the share of their task effort they finished (full amount at 100%).");
            p.LongTaskWeight = Rule("LongTaskWeight", p.LongTaskWeight, "Crewmate: how many short or common tasks one long task counts as in the task bonus.");
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
