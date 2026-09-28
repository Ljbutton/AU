using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TournamentTracker.Stats
{
    /// <summary>
    /// A referee's point change, typed in the private results channel:
    /// <c>!adjust LJ-3 red -2 meta call</c>. The game is its name (LJ-3), or just the host's
    /// label (LJ) for the game that lobby was playing when the message was sent.
    /// </summary>
    public sealed class RefereeAdjustment
    {
        private static readonly Regex Pattern = new Regex(
            @"^!adjust\s+(?<game>\S+)\s+(?<player>.+?)\s+(?<pts>[+-]?\d+(?:[.,]\d+)?)(?:\s+(?<reason>.+))?$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        public string MessageId { get; set; } = "";
        public DateTime TimeUtc { get; set; }
        public string Game { get; set; } = "";
        public string Player { get; set; } = "";
        public double Points { get; set; }
        public string Reason { get; set; } = "";

        public static RefereeAdjustment? TryParse(string content, string messageId, DateTime timeUtc)
        {
            var m = Pattern.Match(content.Trim());
            if (!m.Success) return null;
            return new RefereeAdjustment
            {
                MessageId = messageId,
                TimeUtc = timeUtc,
                Game = m.Groups["game"].Value,
                Player = m.Groups["player"].Value.Trim(),
                Points = double.Parse(m.Groups["pts"].Value.Replace(',', '.'), CultureInfo.InvariantCulture),
                Reason = m.Groups["reason"].Success ? m.Groups["reason"].Value.Trim() : "adjustment",
            };
        }

        /// <summary>Discord message IDs carry their creation time.</summary>
        public static DateTime TimeOfSnowflake(string id) =>
            ulong.TryParse(id, out var n)
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(n >> 22) + 1420070400000L).UtcDateTime
                : DateTime.MinValue;
    }

    public sealed class AdjustmentResult
    {
        public RefereeAdjustment Adjustment { get; set; } = new RefereeAdjustment();
        public GameRecord? Game { get; set; }
        public GamePlayer? Player { get; set; }
        public bool Applied => Player != null;
    }

    public sealed class StandingRow
    {
        public PlayerTotals Stats { get; set; } = new PlayerTotals();
        public double Total { get; set; }
        public bool Advancing { get; set; }
    }

    public sealed class ServerStanding
    {
        public string Server { get; set; } = "";
        public double Points { get; set; }
        public int Players { get; set; }
        public string Furthest { get; set; } = "";
        public int FurthestRound { get; set; }
        public bool StillIn { get; set; }
    }

    public static class Standings
    {
        /// <summary>Applies referee adjustments to the games (which must be fresh copies) and says what each one hit.</summary>
        public static List<AdjustmentResult> Apply(IReadOnlyList<GameRecord> games, IEnumerable<RefereeAdjustment> adjustments)
        {
            var results = new List<AdjustmentResult>();
            foreach (var a in adjustments.OrderBy(a => a.TimeUtc))
            {
                var result = new AdjustmentResult { Adjustment = a, Game = FindGame(games, a) };
                result.Player = result.Game == null ? null : FindPlayer(result.Game, a.Player);
                if (result.Player != null)
                {
                    result.Player.PointBreakdown.Add(new PointLine("Referee: " + a.Reason, a.Points));
                    result.Player.Points += a.Points;
                }
                results.Add(result);
            }
            return results;
        }

        private static GameRecord? FindGame(IReadOnlyList<GameRecord> games, RefereeAdjustment a)
        {
            var named = games.FirstOrDefault(g => string.Equals(g.Name, a.Game, StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(g.Id, a.Game, StringComparison.OrdinalIgnoreCase));
            if (named != null) return named;
            // Just the lobby label: the game that lobby had started when the referee typed it.
            return games.Where(g => string.Equals(g.Host, a.Game, StringComparison.OrdinalIgnoreCase) && g.StartedUtc <= a.TimeUtc)
                        .OrderByDescending(g => g.StartedUtc)
                        .FirstOrDefault();
        }

        private static GamePlayer? FindPlayer(GameRecord game, string text)
        {
            var color = Colors.Parse(text);
            if (color.HasValue && game.Players.FirstOrDefault(p => p.ColorId == color.Value) is GamePlayer byColor) return byColor;
            var exact = game.Players.Where(p => string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            var prefix = game.Players.Where(p => p.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
            return prefix.Count == 1 ? prefix[0] : null;
        }

        public static StatsStore Build(IEnumerable<GameRecord> games, string tournament)
        {
            var store = new StatsStore { Tournament = tournament };
            foreach (var g in games.OrderBy(g => g.StartedUtc))
            {
                store.Apply(g);
                store.NoteFinished(g);
            }
            return store;
        }

        /// <summary>
        /// One lobby's standings for a round: its players ranked by this round's points, the
        /// first <paramref name="advance"/> marked as moving on, each with their running total
        /// across every round.
        /// </summary>
        public static List<StandingRow> Lobby(IReadOnlyList<GameRecord> games, string host, int round, int advance)
        {
            var roundGames = games.Where(g => g.Round == round && string.Equals(g.Host, host, StringComparison.OrdinalIgnoreCase)).ToList();
            return Ranked(roundGames, games, advance);
        }

        /// <summary>Every lobby together for a round (no cut line: players advance per lobby).</summary>
        public static List<StandingRow> Round(IReadOnlyList<GameRecord> games, int round) =>
            Ranked(games.Where(g => g.Round == round).ToList(), games, 0);

        private static List<StandingRow> Ranked(List<GameRecord> scope, IReadOnlyList<GameRecord> all, int advance)
        {
            var totals = Build(all, "").Players;
            return Build(scope, "").Leaderboard()
                .Select((t, i) => new StandingRow
                {
                    Stats = t,
                    Total = totals.TryGetValue(t.Key, out var total) ? total.Points : t.Points,
                    Advancing = i < advance,
                })
                .ToList();
        }

        /// <summary>
        /// Each player's home server is the preliminary server they played the most games in
        /// (ties: the one they played in last). Servers are ranked by their players' total
        /// tournament points, with each server's furthest player: the highest round reached,
        /// ties broken alphabetically.
        /// </summary>
        public static List<ServerStanding> Servers(IEnumerable<GameRecord> prelimGames, IReadOnlyList<GameRecord> tournamentGames, int currentRound)
        {
            var home = prelimGames
                .Where(g => g.Server.Length > 0 && g.Counted)
                .SelectMany(g => g.Players.Select(p => (p.Key, g.Server, g.StartedUtc)))
                .GroupBy(x => x.Key)
                .ToDictionary(
                    byPlayer => byPlayer.Key,
                    byPlayer => byPlayer.GroupBy(x => x.Server)
                        .OrderByDescending(s => s.Count())
                        .ThenByDescending(s => s.Max(x => x.StartedUtc))
                        .First().Key);

            var totals = Build(tournamentGames, "").Players;
            var furthest = tournamentGames
                .SelectMany(g => g.Players.Select(p => (p.Key, g.Round)))
                .GroupBy(x => x.Key)
                .ToDictionary(x => x.Key, x => x.Max(y => y.Round));

            return totals.Values
                .Where(t => home.ContainsKey(t.Key))
                .GroupBy(t => home[t.Key])
                .Select(server =>
                {
                    int best = server.Max(t => furthest.TryGetValue(t.Key, out var r) ? r : 0);
                    var top = server.Where(t => (furthest.TryGetValue(t.Key, out var r) ? r : 0) == best)
                                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                                    .First();
                    return new ServerStanding
                    {
                        Server = server.Key,
                        Points = server.Sum(t => t.Points),
                        Players = server.Count(),
                        Furthest = top.Name,
                        FurthestRound = best,
                        StillIn = currentRound > 0 && best >= currentRound,
                    };
                })
                .OrderByDescending(s => s.Points)
                .ThenBy(s => s.Server, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
