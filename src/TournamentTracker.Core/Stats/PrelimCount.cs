using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TournamentTracker.Discord;

namespace TournamentTracker.Stats
{
    /// <summary>
    /// The organiser's balance check for preliminaries: of all the points scored, how many went to
    /// impostors and how many to crewmates, with wins and games, per lobby and for every lobby
    /// together. Each host's mod keeps its own lobby's count up to date in the organiser's private
    /// reports channel; the scheduled job keeps the all-lobbies count there.
    /// </summary>
    public static class PrelimCount
    {
        public const string TitleSuffix = " — Count";
        public const string AllTitleSuffix = " — Count, all preliminary games";

        public sealed class Tally
        {
            public string Lobby { get; set; } = "";
            public int Games { get; set; }
            public int Voided { get; set; }
            public int ImpostorWins { get; set; }
            public int CrewWins { get; set; }
            public double ImpostorPoints { get; set; }
            public double CrewPoints { get; set; }
            public int ImpostorSeats { get; set; }
            public int CrewSeats { get; set; }

            public double TotalPoints => ImpostorPoints + CrewPoints;
            /// <summary>Share of the points that went to impostors, 0–100 (50 with no points yet).</summary>
            public double ImpostorShare => Share(ImpostorPoints, CrewPoints);
            public double CrewShare => 100 - ImpostorShare;
        }

        /// <summary>
        /// Share by absolute size, so negative points (losses) still count as points that went
        /// to that side's players: what's balanced is how much each side's results move the table.
        /// </summary>
        private static double Share(double imp, double crew)
        {
            double a = Math.Abs(imp), b = Math.Abs(crew);
            return a + b <= 0 ? 50 : Math.Round(a * 100 / (a + b), 1);
        }

        public static Tally Count(IEnumerable<GameRecord> games, string lobby = "")
        {
            var t = new Tally { Lobby = lobby };
            foreach (var g in games)
            {
                if (g.Voided) { t.Voided++; continue; }
                if (!g.Counted) continue;
                t.Games++;
                if (g.Winner == Outcome.Impostors) t.ImpostorWins++;
                else if (g.Winner == Outcome.Crewmates) t.CrewWins++;
                foreach (var p in g.Players)
                {
                    if (p.IsImpostor) { t.ImpostorPoints += p.Points; t.ImpostorSeats++; }
                    else { t.CrewPoints += p.Points; t.CrewSeats++; }
                }
            }
            t.ImpostorPoints = Math.Round(t.ImpostorPoints, 2);
            t.CrewPoints = Math.Round(t.CrewPoints, 2);
            return t;
        }

        /// <summary>"Server · host" for a game's lobby.</summary>
        public static string LobbyOf(GameRecord g) =>
            g.Server.Length > 0 && g.Host.Length > 0 ? g.Server + " · " + g.Host : g.Server.Length > 0 ? g.Server : g.Host.Length > 0 ? g.Host : "Lobby";

        /// <summary>One lobby's count: the message each host's mod keeps up to date.</summary>
        public static WebhookMessage LobbyMessage(string tournament, Tally t) => new WebhookMessage
        {
            Username = "Tournament Tracker",
            Embeds = new List<Embed>
            {
                new Embed
                {
                    Title = ReportFormatter.Clip(tournament + TitleSuffix + ": " + t.Lobby, Embed.TitleLimit),
                    Color = 0x5865F2,
                    Description = Lines(t),
                    Footer = new EmbedFooter { Text = "Updates after every game · only in this private channel" },
                },
            },
        };

        /// <summary>Every lobby together, then each lobby on its own line.</summary>
        public static WebhookMessage AllMessage(string tournament, IReadOnlyList<GameRecord> games)
        {
            var all = Count(games, "All lobbies");
            var lobbies = games.GroupBy(LobbyOf).Select(g => Count(g, g.Key)).Where(x => x.Games + x.Voided > 0)
                .OrderByDescending(x => x.Games).ThenBy(x => x.Lobby, StringComparer.OrdinalIgnoreCase).ToList();
            var rows = lobbies.Select(x =>
                $"**{x.Lobby}** — {x.Games} game{(x.Games == 1 ? "" : "s")} · imp {Pct(x.ImpostorShare)} / crew {Pct(x.CrewShare)} of points · imp wins {x.ImpostorWins}/{x.Games}");
            string text = Lines(all) + (lobbies.Count > 0 ? "\n\n" + string.Join("\n", rows) : "");
            return new WebhookMessage
            {
                Username = "Tournament Tracker",
                Embeds = new List<Embed>
                {
                    new Embed
                    {
                        Title = ReportFormatter.Clip(tournament + AllTitleSuffix, Embed.TitleLimit),
                        Color = 0xED4245,
                        Description = ReportFormatter.Clip(text, Embed.DescriptionLimit),
                        Footer = new EmbedFooter { Text = $"{lobbies.Count} lobb{(lobbies.Count == 1 ? "y" : "ies")} · updates every few minutes" },
                    },
                },
            };
        }

        private static string Lines(Tally t)
        {
            if (t.Games == 0) return "No counted games yet." + (t.Voided > 0 ? $" ({t.Voided} voided)" : "");
            string avgImp = t.ImpostorSeats > 0 ? N(t.ImpostorPoints / t.ImpostorSeats) : "–";
            string avgCrew = t.CrewSeats > 0 ? N(t.CrewPoints / t.CrewSeats) : "–";
            return $"**Points: impostors {Pct(t.ImpostorShare)} · crew {Pct(t.CrewShare)}**\n"
                 + $"Impostors {N(t.ImpostorPoints)} pts · crew {N(t.CrewPoints)} pts\n"
                 + $"Per player per game: impostor {avgImp} · crewmate {avgCrew}\n"
                 + $"Wins: impostors {t.ImpostorWins} ({Pct(t.Games > 0 ? t.ImpostorWins * 100.0 / t.Games : 0)}) · crew {t.CrewWins} ({Pct(t.Games > 0 ? t.CrewWins * 100.0 / t.Games : 0)})\n"
                 + $"Games: {t.Games}" + (t.Voided > 0 ? $" ({t.Voided} voided, not counted)" : "");
        }

        private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        private static string Pct(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "%";
    }
}
