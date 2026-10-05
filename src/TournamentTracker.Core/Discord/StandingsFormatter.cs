using System;
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
        public static WebhookMessage Lobby(string tournament, string lobby, int round, IReadOnlyList<StandingRow> rows, int advance,
            int played = 0, int gamesPerRound = 0) =>
            Message(new Embed
            {
                Title = ReportFormatter.Clip($"{lobby} — Round {round} standings{Progress(played, gamesPerRound)}", Embed.TitleLimit),
                Color = RoundColor,
                Description = Table(rows, advance),
                Footer = new EmbedFooter { Text = $"{tournament} · top {advance} move on · Total = every round so far" },
            });

        /// <summary>" · game 2 of 3", " · final" when the round is played out, or "" when games per round isn't set.</summary>
        public static string Progress(int played, int gamesPerRound) =>
            gamesPerRound <= 0 || played <= 0 ? ""
            : played == gamesPerRound ? $" · final (game {played} of {gamesPerRound})"
            : $" · after game {played} of {gamesPerRound}";

        /// <summary>Every lobby's players in one table for a round, with how far each lobby has got.</summary>
        public static WebhookMessage Round(string tournament, int round, IReadOnlyList<StandingRow> rows,
            IReadOnlyList<(string Lobby, int Played)> progress, int gamesPerRound)
        {
            string lobbies = string.Join(" · ", progress.Select(p =>
                $"{p.Lobby} {p.Played}{(gamesPerRound > 0 ? "/" + gamesPerRound : "")}{(gamesPerRound > 0 && p.Played >= gamesPerRound ? " ✓" : "")}"));
            return Message(new Embed
            {
                Title = ReportFormatter.Clip($"{tournament} — Round {round}, all lobbies", Embed.TitleLimit),
                Color = RoundColor,
                Description = ReportFormatter.Clip((lobbies.Length > 0 ? "Games played: " + lobbies + "\n" : "") + Table(rows.Take(25).ToList(), 0), Embed.DescriptionLimit),
                Footer = new EmbedFooter { Text = $"{progress.Count} lobb{(progress.Count == 1 ? "y" : "ies")} · Total = every round so far" },
            });
        }


        /// <summary>
        /// A lobby's round is played out: every player's score for the round, in order, with where
        /// the points came from (each source added up over the round's games). For the staff
        /// channel: it doesn't say who moves on.
        /// </summary>
        public static WebhookMessage RoundSummary(string tournament, string lobby, int round, IReadOnlyList<StandingRow> rows,
            IReadOnlyList<GameRecord> roundGames)
        {
            var sources = PointSources(roundGames);
            bool withTotal = rows.Any(r => Math.Abs(r.Total - r.Stats.Points) > 0.001);
            var sb = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                sb.Append($"**{i + 1}. {r.Stats.Name}** · {ReportFormatter.Pts(r.Stats.Points)} pts");
                if (withTotal) sb.Append($" (total {ReportFormatter.Pts(r.Total)})");
                sb.Append($" · {r.Stats.Wins}-{r.Stats.Losses}").AppendLine();
                var lines = sources.TryGetValue(r.Stats.Key, out var found) ? new List<(string Source, double Points)>(found) : new List<(string Source, double Points)>();
                // Anything the games don't explain (a referee's !adjust, a tiebreak) shows as its own item.
                double rest = r.Stats.Points - lines.Sum(l => l.Points);
                if (Math.Abs(rest) > 0.001) lines.Add(("Referee adjustment", rest));
                sb.AppendLine(lines.Count == 0 ? "↳ no points" : "↳ " + string.Join(" · ", lines.Select(l => $"{l.Source} {Signed(l.Points)}")));
            }
            var highlights = new List<string>();
            var all = rows.Select(r => r.Stats).ToList();
            if (all.Count > 0)
            {
                var killer = all.OrderByDescending(t => t.Kills).First();
                if (killer.Kills > 0) highlights.Add($"Most kills: {killer.Name} ({killer.Kills})");
                var voter = all.OrderByDescending(t => t.CorrectVotes).ThenByDescending(t => t.VoteAccuracy).First();
                if (voter.CorrectVotes > 0) highlights.Add($"Best votes: {voter.Name} ({voter.CorrectVotes} right)");
            }
            if (highlights.Count > 0) sb.AppendLine().Append(string.Join(" · ", highlights));
            int games = roundGames.Count(g => g.Counted);
            return Message(new Embed
            {
                Title = ReportFormatter.Clip($"{lobby} — round {round} scores", Embed.TitleLimit),
                Color = RoundColor,
                Description = ReportFormatter.Clip(sb.ToString().TrimEnd(), Embed.DescriptionLimit),
                Footer = new EmbedFooter { Text = $"{tournament} · {games} game{(games == 1 ? "" : "s")} · staff only" },
            });
        }

        /// <summary>Each player's points by source over the given games: "Kill x2" and "Kill" both count as Kill.</summary>
        public static Dictionary<string, List<(string Source, double Points)>> PointSources(IEnumerable<GameRecord> games)
        {
            var bySource = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games.Where(g => g.Counted))
                foreach (var p in g.Players)
                {
                    if (!bySource.TryGetValue(p.Key, out var mine)) bySource[p.Key] = mine = new Dictionary<string, double>();
                    foreach (var line in p.PointBreakdown)
                    {
                        string source = SourceOf(line.Rule);
                        mine[source] = (mine.TryGetValue(source, out var v) ? v : 0) + line.Points;
                    }
                }
            return bySource.ToDictionary(kv => kv.Key, kv => kv.Value.Where(x => Math.Abs(x.Value) > 0.001)
                .OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value)).ToList(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A point rule without its per-game detail: "Kill x2" → "Kill", "Tasks 80%" → "Tasks", "Reads 3/4 on impostors" → "Reads".</summary>
        public static string SourceOf(string rule)
        {
            string s = System.Text.RegularExpressions.Regex.Replace(rule ?? "", @" x\d+$", "");
            s = s.Replace(" (left the game)", "");
            if (s.StartsWith("Tasks ")) return "Tasks";
            if (s.StartsWith("Reads ")) return "Reads";
            if (s.StartsWith("Died first")) return "Died first";
            return s;
        }

        private static string Signed(double v) => (v > 0 ? "+" : "") + ReportFormatter.Pts(v);

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
                game.Voided ? $"Game {game.Name} is void and doesn't count."
                : game.Winner == null ? $"Game {game.Name}: no result, not counted." : $"Game {game.Name}: {game.Winner} win ({Outcome.Describe(game.EndReason)}).",
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
