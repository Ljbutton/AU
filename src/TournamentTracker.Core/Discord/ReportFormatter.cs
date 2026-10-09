using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TournamentTracker.Stats;

namespace TournamentTracker.Discord
{
    /// <summary>Builds the Discord messages. Pure functions, so the layout is unit-tested.</summary>
    public static class ReportFormatter
    {
        public const string BotName = "Among Us Tournament";
        private const int CrewColor = 0x3B82F6;
        private const int ImpostorColor = 0xDC2626;
        private const int NeutralColor = 0x6B7280;
        private const int LeaderboardColor = 0xF59E0B;

        public static WebhookMessage GameReport(GameRecord game)
        {
            string name = game.Round > 0 ? $"Game {game.Name} · Round {game.Round}" : $"Game {game.Name}";
            string title = game.Winner switch
            {
                Outcome.Impostors => $"{name} — Impostors win",
                Outcome.Crewmates => $"{name} — Crewmates win",
                _ => $"{name} — no result (not counted)",
            };
            if (game.Voided) title = $"VOID · {name} — not counted";

            string settings = game.SettingsIssues.Count > 0 ? "⚠ **Wrong settings:** " + string.Join(", ", game.SettingsIssues) + "\n" : "";
            var summary = new Embed
            {
                Title = Clip(title, Embed.TitleLimit),
                Color = game.Voided ? NeutralColor : game.Winner == Outcome.Impostors ? ImpostorColor : game.Winner == Outcome.Crewmates ? CrewColor : NeutralColor,
                Description = Clip(settings + (game.Voided ? $"**Void{(game.VoidReason.Length > 0 ? ": " + game.VoidReason : "")}.** Kept for the record; no points or stats count.\n" : "") + PlayerTable(game), Embed.DescriptionLimit),
                Fields = new List<EmbedField>
                {
                    Field("Result", Outcome.Describe(game.EndReason), true),
                    Field("Map", game.Map, true),
                    Field("Length", Clock(game.DurationSeconds), true),
                    Field("Impostors", string.Join("\n", game.Players.Where(p => p.IsImpostor).Select(p => $"{p.Label} — {p.Kills} kill{(p.Kills == 1 ? "" : "s")}")), true),
                    Field("Meetings", MeetingSummary(game), true),
                    Field("MVP", Mvp(game), true),
                },
                Footer = new EmbedFooter { Text = $"{game.Tournament}{(game.Server.Length > 0 ? " · " + game.Server : "")} · {(game.Id.Length > 0 ? game.Id : "game " + game.GameNumber)} · Lobby {game.LobbyCode}" },
                Timestamp = (game.EndedUtc ?? game.StartedUtc).ToString("o", CultureInfo.InvariantCulture),
            };

            var points = new Embed
            {
                Title = "Points",
                Color = NeutralColor,
                Description = Clip(PointsBlock(game), 1500),
            };

            // Discord caps a message at 6000 characters across its embeds, so the timeline
            // gets whatever the other two leave.
            var timeline = new Embed
            {
                Title = "Timeline",
                Color = NeutralColor,
                Description = TimelineBlock(game.Timeline, 2600),
            };

            return new WebhookMessage { Username = BotName, Embeds = new List<Embed> { summary, points, timeline } };
        }

        /// <summary>
        /// The public channel's copy of a game: who won and how, each player's role, result and total, and the
        /// timeline. Nothing else: no points breakdown, MVP, vote counts, settings warnings or lobby code.
        /// </summary>
        public static WebhookMessage PublicReport(GameRecord game)
        {
            var full = GameReport(game);
            var results = new Embed
            {
                Title = full.Embeds![0].Title,
                Color = full.Embeds[0].Color,
                Description = Clip((game.Voided ? $"**Void{(game.VoidReason.Length > 0 ? ": " + game.VoidReason : "")}.** Not counted.\n" : "") + PublicTable(game), Embed.DescriptionLimit),
                Fields = new List<EmbedField>
                {
                    Field("Result", Outcome.Describe(game.EndReason), true),
                    Field("Map", game.Map, true),
                    Field("Length", Clock(game.DurationSeconds), true),
                },
                Timestamp = full.Embeds[0].Timestamp,
            };
            var timeline = new Embed { Title = "Timeline", Color = NeutralColor, Description = TimelineBlock(game.Timeline, 3500) };
            return new WebhookMessage { Username = BotName, Embeds = new List<Embed> { results, timeline } };
        }

        public static string PublicTable(GameRecord game)
        {
            var sb = new StringBuilder("```\n");
            sb.AppendLine("Player           Role          Pts Result");
            foreach (var p in game.Players.OrderByDescending(p => p.IsImpostor).ThenByDescending(p => p.Points).ThenBy(p => p.ColorId))
            {
                string result = (game.Winner == null ? "" : p.Won ? "Won" : "Lost")
                    + (p.DeathCause == null ? "" : (game.Winner == null ? "" : ", ") + p.DeathCause.ToLowerInvariant());
                sb.Append(Pad(Colors.Name(p.ColorId) + " " + p.Name, 16)).Append(' ')
                  .Append(Pad(p.Role, 11)).Append(' ')
                  .Append((game.Counted ? Signed(p.Points) : "-").PadLeft(5)).Append(' ')
                  .Append(result).AppendLine();
            }
            sb.Append("```");
            return sb.ToString();
        }

        /// <summary>One line per player: their total, then each rule that scored.</summary>
        public static string PointsBlock(GameRecord game)
        {
            if (!game.Counted) return "Not counted: the game ended without a result.";
            var sb = new StringBuilder("```\n");
            foreach (var p in game.Players.OrderByDescending(p => p.Points).ThenBy(p => p.ColorId))
            {
                string rules = p.PointBreakdown.Count == 0
                    ? "nothing scored"
                    : string.Join(", ", p.PointBreakdown.Select(l => $"{l.Rule} {Signed(l.Points)}"));
                sb.Append(Pad(p.Name, 12)).Append(Signed(p.Points).PadLeft(6)).Append("  ").Append(rules).AppendLine();
            }
            sb.Append("```");
            return sb.ToString();
        }

        public static string Pts(double points) => points.ToString("0.##", CultureInfo.InvariantCulture);

        public static string Signed(double points) => points > 0 ? "+" + Pts(points) : Pts(points);

        /// <param name="minGames">Players with fewer counted games are left off.</param>
        /// <param name="mentionFor">When given, players linked to Discord are shown as @mentions (this doesn't ping them).</param>
        public static WebhookMessage Leaderboard(StatsStore store, int size, int minGames = 1, Func<string, string?>? mentionFor = null)
        {
            var eligible = store.Leaderboard().Where(t => t.Games >= Math.Max(1, minGames)).ToList();
            var rows = eligible.Take(Math.Max(1, size)).ToList();
            var sb = new StringBuilder();
            if (mentionFor == null)
            {
                sb.Append("```\n");
                sb.AppendLine(" #  Player            Pts   W-L   K  Vote%  Task%");
                for (int i = 0; i < rows.Count; i++)
                {
                    var t = rows[i];
                    sb.Append((i + 1).ToString().PadLeft(2)).Append("  ")
                      .Append(Pad(t.Name, 15)).Append(' ')
                      .Append(Pts(t.Points).PadLeft(5)).Append(' ')
                      .Append($"{t.Wins}-{t.Losses}".PadLeft(5)).Append(' ')
                      .Append(t.Kills.ToString().PadLeft(3)).Append(' ')
                      .Append(Percent(t.VoteAccuracy, t.CorrectVotes + t.IncorrectVotes).PadLeft(6)).Append(' ')
                      .Append(Percent(t.TaskCompletion, t.TasksTotal).PadLeft(6))
                      .AppendLine();
                }
                if (rows.Count == 0) sb.AppendLine(NoPlayersYet(minGames));
                sb.Append("```");
            }
            else
            {
                // Mentions only render outside code blocks, so this layout is a list.
                for (int i = 0; i < rows.Count; i++)
                {
                    var t = rows[i];
                    string? id = mentionFor(t.Key);
                    string who = id != null ? $"<@{id}>" : Escape(t.Name);
                    sb.Append($"`{(i + 1).ToString().PadLeft(2)}.` {who} — **{Pts(t.Points)}** pts · {t.Wins}-{t.Losses} · {t.Kills} K");
                    if (t.CorrectVotes + t.IncorrectVotes > 0) sb.Append($" · votes {Percent(t.VoteAccuracy, 1)}");
                    if (t.TasksTotal > 0) sb.Append($" · tasks {Percent(t.TaskCompletion, 1)}");
                    sb.AppendLine();
                }
                if (rows.Count == 0) sb.AppendLine(NoPlayersYet(minGames));
            }
            int hidden = store.Players.Count - eligible.Count;

            return new WebhookMessage
            {
                Username = BotName,
                Embeds = new List<Embed>
                {
                    new Embed
                    {
                        Title = Clip($"{store.Tournament} — Leaderboard", Embed.TitleLimit),
                        Color = LeaderboardColor,
                        Description = Clip(sb.ToString(), Embed.DescriptionLimit),
                        Footer = new EmbedFooter
                        {
                            Text = $"{store.GamesRecorded} game{(store.GamesRecorded == 1 ? "" : "s")} · Crew {store.CrewWins} – {store.ImpostorWins} Impostors"
                                + (hidden > 0 ? $" · {hidden} player{(hidden == 1 ? "" : "s")} under {minGames} games not shown" : ""),
                        },
                    },
                },
            };
        }

        private static string NoPlayersYet(int minGames) =>
            minGames > 1 ? $"No one has played {minGames} games yet." : "No games recorded yet.";

        public static string PlayerTable(GameRecord game)
        {
            var sb = new StringBuilder("```\n");
            sb.AppendLine("Player           Role        K Tasks  ✓  ✗   Pts Result");
            var ordered = game.Players.OrderByDescending(p => p.IsImpostor).ThenByDescending(p => p.Points).ThenBy(p => p.ColorId);
            foreach (var p in ordered)
            {
                string tasks = p.IsImpostor || p.TasksTotal == 0 ? "-" : $"{p.TasksCompleted}/{p.TasksTotal}";
                string result = (game.Winner == null ? "" : p.Won ? "Won" : "Lost")
                    + (p.DeathCause == null ? "" : (game.Winner == null ? "" : ", ") + p.DeathCause.ToLowerInvariant());
                sb.Append(Pad(Colors.Name(p.ColorId) + " " + p.Name, 16)).Append(' ')
                  .Append(Pad(p.Role, 11)).Append(' ')
                  .Append(p.Kills.ToString()).Append(' ')
                  .Append(tasks.PadLeft(5)).Append(' ')
                  .Append(p.EjectVotesOnImpostor.ToString().PadLeft(2)).Append(' ')
                  .Append(p.EjectVotesOnCrewmate.ToString().PadLeft(2)).Append(' ')
                  .Append(Signed(p.Points).PadLeft(5)).Append(' ')
                  .Append(result)
                  .AppendLine();
            }
            sb.Append("```");
            return sb.ToString();
        }

        public static string TimelineBlock(IReadOnlyList<TimelineEvent> events, int limit)
        {
            var lines = events.Select(e => $"{Clock(e.AtSeconds)}  {e.Text}").ToList();
            const string open = "```\n", close = "```";
            int budget = limit - open.Length - close.Length - 40;

            // Keep the start and the end of a long game; drop from the middle.
            int total = lines.Sum(l => l.Length + 1);
            int dropped = 0;
            while (total > budget && lines.Count > 2)
            {
                int mid = lines.Count / 2;
                total -= lines[mid].Length + 1;
                lines.RemoveAt(mid);
                dropped++;
            }
            if (dropped > 0) lines.Insert(lines.Count / 2, $"… {dropped} more event{(dropped == 1 ? "" : "s")} …");

            string body = string.Join("\n", lines.Select(l => Clip(l, budget)));
            return open + (body.Length == 0 ? "Nothing happened." : body) + "\n" + close;
        }

        private static string MeetingSummary(GameRecord game)
        {
            if (game.Meetings.Count == 0) return "None";
            int reports = game.Meetings.Count(m => m.BodyKey != null);
            int correct = game.Meetings.Count(m => m.EjectedWasImpostor == true);
            int wrong = game.Meetings.Count(m => m.EjectedWasImpostor == false);
            return $"{game.Meetings.Count} ({reports} report{(reports == 1 ? "" : "s")})\n{correct} impostor{(correct == 1 ? "" : "s")} / {wrong} crew ejected";
        }

        private static string Mvp(GameRecord game)
        {
            var best = game.Players.Where(p => p.Won).OrderByDescending(p => p.Points).ThenByDescending(p => p.Kills).FirstOrDefault()
                ?? game.Players.OrderByDescending(p => p.Points).FirstOrDefault();
            return best == null ? "—" : $"{best.Label} ({Signed(best.Points)})";
        }

        private static string Icon(string kind) => kind switch
        {
            "kill" => "🔪",
            "meeting" => "📢",
            "eject" => "🚀",
            "tasks" => "✅",
            "sabotage" => "⚠️",
            "disconnect" => "🔌",
            "start" => "▶️",
            "end" => "🏁",
            _ => "•",
        };

        private static EmbedField Field(string name, string value, bool inline) =>
            new EmbedField { Name = name, Value = Clip(string.IsNullOrWhiteSpace(value) ? "—" : value, Embed.FieldValueLimit), Inline = inline };

        public static string Clock(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        }

        private static string Percent(double value, int sample) => sample == 0 ? "-" : Math.Round(value * 100) + "%";

        private static string Pad(string s, int width)
        {
            s = s.Replace("`", "'");
            return s.Length > width ? s.Substring(0, width - 1) + "…" : s.PadRight(width);
        }

        public static string Clip(string s, int limit) => s.Length <= limit ? s : s.Substring(0, limit - 1) + "…";

        private static string Escape(string s) => s.Replace("`", "'").Replace("*", "\\*").Replace("_", "\\_").Replace("~", "\\~");
    }
}
