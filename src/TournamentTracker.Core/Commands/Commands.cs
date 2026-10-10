using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TournamentTracker.Discord;
using TournamentTracker.Stats;

namespace TournamentTracker
{
    public sealed partial class TournamentSession
    {
        private bool HandleCommand(PlayerSnapshot sender, bool fromHost, string text)
        {
            string prefix = _settings.CommandPrefix;
            text = text.Trim();
            if (prefix.Length == 0 || !text.StartsWith(prefix, StringComparison.Ordinal) || text.Length == prefix.Length) return false;

            var parts = text.Substring(prefix.Length).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            string command = parts[0].ToLowerInvariant();
            var args = parts.Skip(1).ToArray();

            if (fromHost && TryRoundCommand(command, args)) return true;

            switch (command)
            {
                case "refslot" when fromHost:
                    RefSlotCommand(sender, args);
                    return true;
                case "refdead" when fromHost:
                    RefDeadCommand(args);
                    return true;
                case "setup" when fromHost:
                    SetupCommand(args);
                    return true;
                case "automute" when fromHost:
                    AutoMuteCommand(args);
                    return true;
                case "unmuteall" when fromHost:
                    UnmuteEveryone();
                    Reply(AutoMute == null ? "Automute isn't set up." : "Everyone unmuted; automute is OFF. Use !automute on to resume.", false);
                    return true;
                case "leaderboard" when fromHost:
                case "lb" when fromHost:
                    LeaderboardCommand();
                    return true;
                case "refresh" when fromHost:
                    RepostStatus();
                    Reply(_settings.LiveStatus ? "Posted a fresh status message in Discord." : "The live status message is off (LiveStatus in the config).", false);
                    return true;
                case "ref" when fromHost:
                    RefereeCommand(sender, args);
                    return true;
                case "spectators" when fromHost:
                    SpectatorsCommand(args);
                    return true;
                case "servers" when fromHost:
                    if (Shared == null || _settings.PrelimChannelIds.Count == 0)
                        Reply("Server standings need a tournament setup code with preliminary channels.", false);
                    else
                    {
                        Chain(PostServerStandingsAsync);
                        Reply("Posting the server standings.", false);
                    }
                    return true;
                case "overlay" when fromHost:
                    OverlayCommand(args);
                    return true;
                case "feed" when fromHost:
                    FeedCommand(args);
                    return true;
                case "voicenotice" when fromHost:
                    VoiceNoticeCommand();
                    return true;
                case "lead" when fromHost:
                    LeadCommand();
                    return true;
                case "lock" when fromHost:
                    LockCommand(args);
                    return true;
                case "void" when fromHost:
                case "unvoid" when fromHost:
                    VoidCommand(command == "void", args);
                    return true;
                case "spec" when fromHost:
                    SpectatorCommand(args);
                    return true;
                case "link" when fromHost:
                    LinkCommand(args);
                    return true;
                case "unlink" when fromHost:
                    UnlinkCommand(args);
                    return true;
                case "verify" when fromHost:
                case "submit" when fromHost:
                    VerifyCommand(args);
                    return true;
                case "voidgame" when fromHost:
                case "unvoidgame" when fromHost:
                    VoidGameCommand(command == "voidgame", args);
                    return true;
                case "adjust" when fromHost:
                    AdjustCommand(args);
                    return true;
                case "resetpoints" when fromHost:
                    ResetPointsCommand();
                    return true;
                case "resetleaderboard" when fromHost:
                    ResetLeaderboardCommand();
                    return true;
                default:
                    return false;
            }
        }

        private void AutoMuteCommand(string[] args)
        {
            if (AutoMute == null)
            {
                Reply("Automute isn't set up: add BotTokens and GuildId to the config and set Enabled = true.", false);
                return;
            }
            string arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (arg == "on" || arg == "off")
            {
                AutoMute.Enabled = arg == "on";
                _autoMuteOff = !AutoMute.Enabled;
                SaveState();
            }
            Reply(AutoMute.Enabled ? "Automute is ON." : "Automute is OFF: the bot won't touch anyone's voice (you can use AutoMuteUs instead). It stays off until you turn it on.", false);
        }

        private void RefereeCommand(PlayerSnapshot sender, string[] args)
        {
            if (AutoMute == null)
            {
                Reply("Automute isn't set up, so referee mode can't mute anyone.", false);
                return;
            }
            string arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (arg == "on")
            {
                if (!AutoMute.Enabled)
                {
                    Reply("Automute is off; turn it on with !automute on first.", false);
                    return;
                }
                var host = Links.Find(sender.Key)?.DiscordUserId;
                AutoMute.StartRefereeMode(host == null ? Array.Empty<string>() : new[] { host });
                Reply(host == null
                    ? "Referee mode ON: everyone in voice is muted except RefereeUserIds. You aren't linked, so link yourself to talk. !ref off to end."
                    : "Referee mode ON: everyone in voice is muted except you and the referees. !ref off to end.", false);
                if (Players.Count > 0) Reply("Referee is explaining the rules; you're muted for now.", true);
            }
            else if (arg == "off")
            {
                bool was = AutoMute.RefereeMode;
                AutoMute.StopRefereeMode();
                Reply(was ? "Referee mode OFF: everyone can talk again." : "Referee mode was already off.", false);
            }
            else
            {
                Reply($"Referee mode is {(AutoMute.RefereeMode ? "ON" : "OFF")}. Switch it with Referee in The Button.", false);
            }
        }

        private void SpectatorsCommand(string[] args)
        {
            if (AutoMute == null)
            {
                Reply("Automute isn't set up, so spectators can't be muted.", false);
                return;
            }
            string arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (arg == "on" || arg == "off") AutoMute.MuteSpectators = arg == "on";
            string presence = Presence == null ? " (not connected to Discord voice yet)" : Presence.Connected ? "" : " (connecting to Discord…)";
            Reply($"Spectator muting is {(AutoMute.MuteSpectators ? "ON: people in voice who aren't playing are muted during games" : "OFF")}{presence}.", false);
        }

        private void LeaderboardCommand()
        {
            if (Shared != null && _settings.Mode == TrackerMode.Tournament)
            {
                Chain(PostLobbyStandingsAsync);
                Chain(PostRoundStandingsAsync);
                Reply($"Posting round {Round} standings: this lobby, then all lobbies.", false);
                return;
            }
            if (Shared != null)
            {
                Chain(PostCombinedAsync);
                Reply("Posting the combined leaderboard for all lobbies.", false);
                return;
            }
            if (string.IsNullOrWhiteSpace(_settings.StatsWebhookUrl))
                Reply("No StatsWebhookUrl set; showing top 5 here only.", false);
            else
                Post(_settings.StatsWebhookUrl, LeaderboardMessage());

            var top = Store.Leaderboard().Take(5).Select((t, i) => $"{i + 1}. {t.Name} {ReportFormatter.Pts(t.Points)}");
            Reply(Store.GamesRecorded == 0 ? "No games recorded yet." : string.Join(" · ", top), false);
        }

        /// <summary>Starts the combined leaderboard over for every host. Past games stay in the channel.</summary>
        private void ResetLeaderboardCommand()
        {
            if (Shared == null)
            {
                Reply($"There's no combined leaderboard (ResultsChannelId isn't set).", false);
                return;
            }
            string who = LobbyLabel();
            Chain(async () =>
            {
                if (await Shared.PostResetAsync(who.Length > 0 ? who : "the host").ConfigureAwait(false))
                    await PostCombinedAsync().ConfigureAwait(false);
            });
            Reply("Combined leaderboard reset for every lobby. Delete the reset message in the results channel to undo.", false);
        }

        /// <summary>Finds a present player by colour, exact name or unique name prefix.</summary>
        public PlayerSnapshot? FindPlayer(string text)
        {
            text = text.Trim();
            if (text.Length == 0) return null;
            var color = Colors.Parse(text);
            if (color.HasValue)
            {
                var byColor = Players.FirstOrDefault(p => p.ColorId == color.Value);
                if (byColor != null) return byColor;
            }
            var exact = Players.Where(p => string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            var prefix = Players.Where(p => p.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
            return prefix.Count == 1 ? prefix[0] : null;
        }
    }
}
