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
        private static readonly Regex MentionOrId = new Regex(@"^(?:<@!?(\d{15,21})>|(\d{15,21}))$");

        /// <summary>
        /// Handles a chat line if it is a command. Returns false for ordinary chat.
        /// Replies to players are public so they see them; host-only commands answer privately.
        /// </summary>
        public bool HandleChat(PlayerSnapshot sender, bool fromHost, string text)
        {
            string prefix = _settings.CommandPrefix;
            text = text.Trim();
            if (prefix.Length == 0 || !text.StartsWith(prefix, StringComparison.Ordinal) || text.Length == prefix.Length) return false;

            var parts = text.Substring(prefix.Length).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            string command = parts[0].ToLowerInvariant();
            var args = parts.Skip(1).ToArray();

            switch (command)
            {
                case "help":
                    Help(fromHost);
                    return true;
                case "link":
                    LinkCommand(sender, fromHost, args);
                    return true;
                case "unlink":
                    UnlinkCommand(sender, fromHost, args);
                    return true;
                case "stats":
                    StatsCommand(sender, args);
                    return true;
                case "links" when fromHost:
                    LinksCommand();
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
                case "sheetsync" when fromHost:
                    SheetSyncCommand();
                    return true;
                case "resetstats" when fromHost:
                    ResetStatsCommand(args);
                    return true;
                default:
                    return false;
            }
        }

        private void Help(bool fromHost)
        {
            string p = _settings.CommandPrefix;
            Reply($"Commands: {p}link <discord name or id> · {p}unlink · {p}stats [player]", !fromHost);
            if (fromHost)
            {
                Reply($"Host: {p}link <player> <discord> · {p}unlink <player> · {p}links · {p}automute on|off · " +
                      $"{p}unmuteall · {p}leaderboard · {p}sheetsync · {p}resetstats confirm", false);
            }
        }

        private void LinkCommand(PlayerSnapshot sender, bool fromHost, string[] args)
        {
            string p = _settings.CommandPrefix;
            if (args.Length == 0)
            {
                Reply(fromHost ? $"Usage: {p}link [player] <discord name or id>" : $"Usage: {p}link <discord name or id>", !fromHost);
                return;
            }

            PlayerSnapshot target = sender;
            string discordArg;
            if (fromHost && args.Length >= 2 && FindPlayer(args[0]) is PlayerSnapshot named)
            {
                target = named;
                discordArg = string.Join(" ", args.Skip(1));
            }
            else
            {
                discordArg = string.Join(" ", args);
            }

            if (!fromHost && !_settings.AllowSelfLink)
            {
                Reply("Ask the host to link your Discord account.", true);
                return;
            }

            bool isPublic = !fromHost || target.Key != sender.Key;
            var mute = _settings.AutoMute;
            var match = MentionOrId.Match(discordArg);
            string? id = match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;

            if (mute.BotTokens.Count == 0 || mute.GuildId.Length == 0)
            {
                if (id == null)
                {
                    Reply("Linking by name needs the bot set up; use the numeric Discord user ID.", isPublic);
                    return;
                }
                Links.Link(target.Key, target.Name, id, id);
                Reply($"Linked {target} to Discord ID {id}.", isPublic);
                return;
            }

            string token = mute.BotTokens[0];
            string guild = mute.GuildId;
            Task.Run(async () =>
            {
                try
                {
                    GuildMember? member;
                    string? problem = null;
                    if (id != null)
                    {
                        member = await _rest.GetMemberAsync(token, guild, id).ConfigureAwait(false);
                        if (member == null) problem = "That Discord user isn't in the server.";
                    }
                    else
                    {
                        string query = discordArg.TrimStart('@');
                        var found = await _rest.SearchMembersAsync(token, guild, query).ConfigureAwait(false);
                        member = PickMember(found, query);
                        if (member == null)
                        {
                            problem = found.Count == 0
                                ? $"No one in the Discord server is called \"{query}\"."
                                : $"\"{query}\" matches {found.Count} people; use the exact username or the user ID.";
                        }
                    }

                    _mainThread.Enqueue(() =>
                    {
                        if (member == null)
                        {
                            Reply(problem!, isPublic);
                            return;
                        }
                        Links.Link(target.Key, target.Name, member.Id, member.DisplayName);
                        Reply($"Linked {target} to @{member.DisplayName}.", isPublic);
                    });
                }
                catch (Exception e)
                {
                    _log.Error("Link lookup failed: " + e.Message);
                    _mainThread.Enqueue(() => Reply("Couldn't reach Discord, try again.", isPublic));
                }
            });
        }

        internal static GuildMember? PickMember(IReadOnlyList<GuildMember> found, string query)
        {
            if (found.Count == 1) return found[0];
            bool Same(string? s) => string.Equals(s, query, StringComparison.OrdinalIgnoreCase);
            var exact = found.Where(m => Same(m.Username) || Same(m.Nick) || Same(m.GlobalName)).ToList();
            return exact.Count == 1 ? exact[0] : null;
        }

        private void UnlinkCommand(PlayerSnapshot sender, bool fromHost, string[] args)
        {
            var target = sender;
            if (fromHost && args.Length > 0)
            {
                var named = FindPlayer(string.Join(" ", args));
                if (named == null)
                {
                    Reply($"No player matches \"{string.Join(" ", args)}\".", false);
                    return;
                }
                target = named;
            }
            bool isPublic = !fromHost || target.Key != sender.Key;
            Reply(Links.Unlink(target.Key) ? $"Unlinked {target}." : $"{target} wasn't linked.", isPublic);
        }

        private void StatsCommand(PlayerSnapshot sender, string[] args)
        {
            var target = args.Length > 0 ? FindPlayer(string.Join(" ", args)) : sender;
            if (target == null)
            {
                Reply($"No player matches \"{string.Join(" ", args)}\".", true);
                return;
            }
            var t = Store.Find(target.Key);
            if (t == null || t.Games == 0)
            {
                Reply($"{target.Name}: no games yet.", true);
                return;
            }
            int rank = Store.Leaderboard().ToList().FindIndex(x => x.Key == t.Key) + 1;
            Reply($"{t.Name}: #{rank}, {ReportFormatter.Pts(t.Points)} pts, {t.Wins}W-{t.Losses}L, {t.Kills} kills", true);
            Reply($"Imp {t.ImpostorWins}/{t.ImpostorGames} · Crew {t.CrewWins}/{t.CrewGames} · votes {t.CorrectVotes}✓ {t.IncorrectVotes}✗", true);
        }

        private void LinksCommand()
        {
            if (Players.Count == 0)
            {
                Reply($"{Links.All.Count} Discord links saved.", false);
                return;
            }
            var linked = Players.Where(p => Links.Find(p.Key) != null).Select(p => $"{Colors.Name(p.ColorId)}→@{Links.Find(p.Key)!.DiscordName}");
            var unlinked = Players.Where(p => Links.Find(p.Key) == null).Select(p => p.ToString()).ToList();
            Reply("Linked: " + (linked.Any() ? string.Join(", ", linked) : "nobody"), false);
            if (unlinked.Count > 0) Reply("Not linked: " + string.Join(", ", unlinked), false);
        }

        private void AutoMuteCommand(string[] args)
        {
            if (AutoMute == null)
            {
                Reply("Automute isn't set up: add BotTokens and GuildId to the config and set Enabled = true.", false);
                return;
            }
            string arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (arg == "on" || arg == "off") AutoMute.Enabled = arg == "on";
            Reply($"Automute is {(AutoMute.Enabled ? "ON" : "OFF")}.", false);
        }

        private void LeaderboardCommand()
        {
            if (string.IsNullOrWhiteSpace(_settings.StatsWebhookUrl))
                Reply("No StatsWebhookUrl set; showing top 5 here only.", false);
            else
                Post(_settings.StatsWebhookUrl, ReportFormatter.Leaderboard(Store, _settings.LeaderboardSize));

            var top = Store.Leaderboard().Take(5).Select((t, i) => $"{i + 1}. {t.Name} {ReportFormatter.Pts(t.Points)}");
            Reply(Store.GamesRecorded == 0 ? "No games recorded yet." : string.Join(" · ", top), false);
        }

        /// <summary>Sends every saved game of this tournament to the sheet again. Rows are replaced, not duplicated.</summary>
        private void SheetSyncCommand()
        {
            if (Sheets == null)
            {
                Reply("Google Sheets isn't set up: add GoogleSheetsUrl and GoogleSheetsSecret to the config.", false);
                return;
            }
            var games = new List<GameRecord>();
            if (Directory.Exists(_gamesDir))
            {
                foreach (var file in Directory.GetFiles(_gamesDir, "game-*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    try
                    {
                        var game = System.Text.Json.JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(file));
                        if (game != null) games.Add(game);
                    }
                    catch (Exception e)
                    {
                        _log.Warn($"Skipping {Path.GetFileName(file)}: {e.Message}");
                    }
                }
            }
            foreach (var game in games) Sheets.Enqueue(game);
            Reply(games.Count == 0 ? "No saved games to send yet." : $"Sending {games.Count} game{(games.Count == 1 ? "" : "s")} to Google Sheets.", false);
        }

        private void ResetStatsCommand(string[] args)
        {
            if (args.FirstOrDefault()?.ToLowerInvariant() != "confirm")
            {
                Reply($"This archives {Store.GamesRecorded} games and starts the leaderboard over. Type {_settings.CommandPrefix}resetstats confirm", false);
                return;
            }
            TrySave(() =>
            {
                if (File.Exists(_statsPath))
                    File.Move(_statsPath, _statsPath.Replace(".json", $"-archived-{_clock():yyyyMMdd-HHmmss}.json"));
            }, "stats archive");
            Store = new StatsStore { Tournament = _settings.TournamentName };
            TrySave(() => Store.Save(_statsPath), "stats");
            Reply("Stats reset. The old file was archived next to the new one.", false);
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
