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
            string title = game.Winner switch
            {
                Outcome.Impostors => $"Game {game.GameNumber} — Impostors win",
                Outcome.Crewmates => $"Game {game.GameNumber} — Crewmates win",
                _ => $"Game {game.GameNumber} — no result (not counted)",
            };

            var summary = new Embed
            {
                Title = Clip(title, Embed.TitleLimit),
                Color = game.Winner == Outcome.Impostors ? ImpostorColor : game.Winner == Outcome.Crewmates ? CrewColor : NeutralColor,
                Description = Clip(PlayerTable(game), Embed.DescriptionLimit),
                Fields = new List<EmbedField>
                {
                    Field("Result", Outcome.Describe(game.EndReason), true),
                    Field("Map", game.Map, true),
                    Field("Length", Clock(game.DurationSeconds), true),
                    Field("Impostors", string.Join("\n", game.Players.Where(p => p.IsImpostor).Select(p => $"{p.Label} — {p.Kills} kill{(p.Kills == 1 ? "" : "s")}")), true),
                    Field("Meetings", MeetingSummary(game), true),
                    Field("MVP", Mvp(game), true),
                },
                Footer = new EmbedFooter { Text = $"{game.Tournament} · Lobby {game.LobbyCode}" },
                Timestamp = (game.EndedUtc ?? game.StartedUtc).ToString("o", CultureInfo.InvariantCulture),
            };

            var timeline = new Embed
            {
                Title = "Timeline",
                Color = NeutralColor,
                Description = TimelineBlock(game.Timeline, Embed.DescriptionLimit),
            };

            return new WebhookMessage { Username = BotName, Embeds = new List<Embed> { summary, timeline } };
        }

        public static WebhookMessage Leaderboard(StatsStore store, int size)
        {
            var rows = store.Leaderboard().Take(Math.Max(1, size)).ToList();
            var sb = new StringBuilder("```\n");
            sb.AppendLine(" #  Player           Pts   W-L   K  Vote%  Task%");
            for (int i = 0; i < rows.Count; i++)
            {
                var t = rows[i];
                sb.Append((i + 1).ToString().PadLeft(2)).Append("  ")
                  .Append(Pad(t.Name, 15)).Append(' ')
                  .Append(t.Points.ToString().PadLeft(4)).Append(' ')
                  .Append($"{t.Wins}-{t.Losses}".PadLeft(5)).Append(' ')
                  .Append(t.Kills.ToString().PadLeft(3)).Append(' ')
                  .Append(Percent(t.VoteAccuracy, t.CorrectVotes + t.IncorrectVotes).PadLeft(6)).Append(' ')
                  .Append(Percent(t.TaskCompletion, t.TasksTotal).PadLeft(6))
                  .AppendLine();
            }
            if (rows.Count == 0) sb.AppendLine("No games recorded yet.");
            sb.Append("```");

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
                            Text = $"{store.GamesRecorded} game{(store.GamesRecorded == 1 ? "" : "s")} · Crew {store.CrewWins} – {store.ImpostorWins} Impostors",
                        },
                    },
                },
            };
        }

        public static WebhookMessage LiveEvent(GameRecord game, TimelineEvent e) => new WebhookMessage
        {
            Username = BotName,
            Content = Clip($"`G{game.GameNumber} {Clock(e.AtSeconds)}` {Icon(e.Kind)} {Escape(e.Text)}", 2000),
        };

        public static string PlayerTable(GameRecord game)
        {
            var sb = new StringBuilder("```\n");
            sb.AppendLine("Player           Role        K Tasks  ✓  ✗  Pts Result");
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
                  .Append(p.CorrectVotes.ToString().PadLeft(2)).Append(' ')
                  .Append(p.IncorrectVotes.ToString().PadLeft(2)).Append(' ')
                  .Append((p.Points > 0 ? "+" + p.Points : p.Points.ToString()).PadLeft(4)).Append(' ')
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
            return best == null ? "—" : $"{best.Label} (+{best.Points})";
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
