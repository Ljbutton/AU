using System.Collections.Generic;
using System.Linq;
using System.Text;
using TournamentTracker.Stats;

namespace TournamentTracker.Discord
{
    public static class StandingsFormatter
    {
        private const int RoundColor = 0x8B5CF6;
        private const int ServerColor = 0x14B8A6;

        /// <summary>One lobby's round standings with the cut line under the players who move on.</summary>
        public static WebhookMessage Lobby(string tournament, string lobby, int round, IReadOnlyList<StandingRow> rows, int advance) =>
            Message(new Embed
            {
                Title = ReportFormatter.Clip($"{lobby} — Round {round} standings", Embed.TitleLimit),
                Color = RoundColor,
                Description = Table(rows, advance),
                Footer = new EmbedFooter { Text = $"{tournament} · top {advance} move on · Total = every round so far" },
            });

        /// <summary>Every lobby's players in one table for a round.</summary>
        public static WebhookMessage Round(string tournament, int round, IReadOnlyList<StandingRow> rows, int lobbies) =>
            Message(new Embed
            {
                Title = ReportFormatter.Clip($"{tournament} — Round {round}, all lobbies", Embed.TitleLimit),
                Color = RoundColor,
                Description = Table(rows.Take(25).ToList(), 0),
                Footer = new EmbedFooter { Text = $"{lobbies} lobb{(lobbies == 1 ? "y" : "ies")} · Total = every round so far" },
            });


        public static string Table(IReadOnlyList<StandingRow> rows, int advance)
        {
            var sb = new StringBuilder("```\n");
            sb.AppendLine(" #  Player           Round  Total   W-L");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                sb.Append((i + 1).ToString().PadLeft(2)).Append("  ")
                  .Append(Pad(r.Stats.Name, 16)).Append(' ')
                  .Append(ReportFormatter.Pts(r.Stats.Points).PadLeft(5)).Append(' ')
                  .Append(ReportFormatter.Pts(r.Total).PadLeft(6)).Append(' ')
                  .Append($"{r.Stats.Wins}-{r.Stats.Losses}".PadLeft(5))
                  .AppendLine();
                if (advance > 0 && i == advance - 1 && rows.Count > advance)
                    sb.AppendLine($"──────── top {advance} move on ────────");
            }
            if (rows.Count == 0) sb.AppendLine("No games in this round yet.");
            sb.Append("```");
            return ReportFormatter.Clip(sb.ToString(), Embed.DescriptionLimit);
        }

        /// <summary>Servers ranked by their players' total points, with each server's furthest player.</summary>
        public static WebhookMessage Servers(string tournament, IReadOnlyList<ServerStanding> servers)
        {
            var sb = new StringBuilder();
            if (servers.Count == 0) sb.Append("No players with a preliminary server yet.");
            for (int i = 0; i < servers.Count && i < 25; i++)
            {
                var s = servers[i];
                string furthest = s.StillIn ? "still in the running" : s.FurthestRound > 0 ? $"reached round {s.FurthestRound}" : "";
                sb.Append($"`{(i + 1).ToString().PadLeft(2)}.` **{Escape(s.Server)}** — {ReportFormatter.Pts(s.Points)} pts · {s.Players} player{(s.Players == 1 ? "" : "s")}");
                if (s.Furthest.Length > 0) sb.Append($"\n      furthest: {Escape(s.Furthest)}{(furthest.Length > 0 ? " (" + furthest + ")" : "")}");
                sb.AppendLine();
            }
            return Message(new Embed
            {
                Title = ReportFormatter.Clip($"{tournament} — Server standings", Embed.TitleLimit),
                Color = ServerColor,
                Description = ReportFormatter.Clip(sb.ToString().TrimEnd(), Embed.DescriptionLimit),
                Footer = new EmbedFooter { Text = "Unofficial · a player's server is where they played the most preliminary games" },
            });
        }

        /// <summary>A short end-of-game summary for the host's chat (preliminaries).</summary>
        public static List<string> ChatSummary(GameRecord game, int top = 5)
        {
            var lines = new List<string>
            {
                game.Winner == null ? $"Game {game.Name}: no result, not counted." : $"Game {game.Name}: {game.Winner} win ({Outcome.Describe(game.EndReason)}).",
            };
            if (game.Counted)
            {
                var best = game.Players.OrderByDescending(p => p.Points).Take(top)
                    .Select(p => $"{p.Name} {ReportFormatter.Signed(p.Points)}");
                lines.Add("Points: " + string.Join(", ", best));
            }
            lines.Add("The full report is in the Discord channel.");
            return lines;
        }

        private static WebhookMessage Message(Embed embed) =>
            new WebhookMessage { Username = ReportFormatter.BotName, Embeds = new List<Embed> { embed } };

        private static string Pad(string s, int width)
        {
            s = s.Replace("`", "'");
            return s.Length > width ? s.Substring(0, width - 1) + "…" : s.PadRight(width);
        }

        private static string Escape(string s) => s.Replace("*", "\\*").Replace("_", "\\_").Replace("~", "\\~").Replace("`", "'");
    }
}
