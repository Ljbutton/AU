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

    /// <summary>
    /// A referee throwing a game out (a restart) or bringing it back, typed in the private
    /// results channel: <c>!void LJ-3 lobby restarted</c> or <c>!unvoid LJ-3</c>. The game is
    /// found the same way as for <see cref="RefereeAdjustment"/>. Once the lobby's mod has
    /// seen it, it reposts the game with the new state, so deleting this message later
    /// changes nothing; only another !void or !unvoid does.
    /// </summary>
    public sealed class RefereeVoid
    {
        private static readonly Regex Pattern = new Regex(@"^!(?<cmd>void|unvoid)\s+(?<game>\S+)(?:\s+(?<reason>.+))?$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        public string MessageId { get; set; } = "";
        public DateTime TimeUtc { get; set; }
        public string Game { get; set; } = "";
        public bool Void { get; set; }
        public string Reason { get; set; } = "";

        public static RefereeVoid? TryParse(string content, string messageId, DateTime timeUtc)
        {
            var m = Pattern.Match(content.Trim());
            if (!m.Success) return null;
            return new RefereeVoid
            {
                MessageId = messageId,
                TimeUtc = timeUtc,
                Game = m.Groups["game"].Value,
                Void = string.Equals(m.Groups["cmd"].Value, "void", StringComparison.OrdinalIgnoreCase),
                Reason = m.Groups["reason"].Success ? m.Groups["reason"].Value.Trim() : "",
            };
        }
    }

    public sealed class VoidResult
    {
        public RefereeVoid Command { get; set; } = new RefereeVoid();
        public GameRecord? Game { get; set; }
        /// <summary>Already built into the newest copy of the game.</summary>
        public bool Settled { get; set; }
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

    /// <summary>
    /// A tie across the cut line in a finished lobby round. Settled by impostor wins, then vote
    /// %, then task %, over the round's games; the players it moves above the line get
    /// <see cref="Standings.TiebreakBump"/> added to their total, which isn't shown in any
    /// game's breakdown. Unsettled when they're level on all three: a referee decides.
    /// </summary>
    public sealed class CutTiebreak
    {
        public int Round { get; set; }
        public string Host { get; set; } = "";
        public double Points { get; set; }
        public List<string> Tied { get; set; } = new List<string>();
        public List<string> Bumped { get; set; } = new List<string>();
        public List<string> BumpedKeys { get; set; } = new List<string>();
        /// <summary>What separated them ("impostor wins", "vote %", "task %"); empty when unsettled.</summary>
        public string DecidedBy { get; set; } = "";
        public bool Settled => DecidedBy.Length > 0;

        public string Describe() => Settled
            ? $"Round {Round} · {Host}: {string.Join(", ", Tied)} tied on {Fmt(Points)} at the cut. {string.Join(", ", Bumped)} move{(Bumped.Count == 1 ? "s" : "")} on by {DecidedBy}: +{Fmt(Standings.TiebreakBump)} added to the round total, not shown publicly."
            : $"Round {Round} · {Host}: {string.Join(", ", Tied)} tied on {Fmt(Points)} at the cut and level on impostor wins, vote % and task %. A referee needs to decide (e.g. !adjust {Host}-<game> <player> +0.25 tiebreak).";

        private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
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

        /// <summary>
        /// Applies !void and !unvoid to the games (fresh copies), oldest first. A command the
        /// game's copy already lists was built into it and is skipped.
        /// </summary>
        public static List<VoidResult> ApplyVoids(IReadOnlyList<GameRecord> games, IEnumerable<RefereeVoid> commands)
        {
            var results = new List<VoidResult>();
            foreach (var c in commands.OrderBy(c => c.TimeUtc))
            {
                var game = FindGame(games, c.Game, c.TimeUtc);
                var result = new VoidResult { Command = c, Game = game };
                if (game != null && game.VoidCommands.Contains(c.MessageId))
                    result.Settled = true;
                else if (game != null)
                {
                    game.Voided = c.Void;
                    game.VoidReason = c.Void ? c.Reason : "";
                }
                results.Add(result);
            }
            return results;
        }

        private static GameRecord? FindGame(IReadOnlyList<GameRecord> games, RefereeAdjustment a) => FindGame(games, a.Game, a.TimeUtc);

        private static GameRecord? FindGame(IReadOnlyList<GameRecord> games, string text, DateTime timeUtc)
        {
            var named = games.FirstOrDefault(g => string.Equals(g.Name, text, StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(g.Id, text, StringComparison.OrdinalIgnoreCase));
            if (named != null) return named;
            // Just the lobby label: the game that lobby had started when the referee typed it.
            return games.Where(g => string.Equals(g.Host, text, StringComparison.OrdinalIgnoreCase) && g.StartedUtc <= timeUtc)
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

        public const double TiebreakBump = 0.25;

        /// <summary>Counted games a lobby has played in a round.</summary>
        public static int GamesPlayed(IEnumerable<GameRecord> games, string host, int round) =>
            games.Count(g => g.Counted && g.Round == round && string.Equals(g.Host, host, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// One lobby's standings for a round: its players ranked by this round's points, the
        /// first <paramref name="advance"/> marked as moving on, each with their running total
        /// across every round. Once the lobby has played <paramref name="gamesPerRound"/> games,
        /// a tie across the cut line is broken (see <see cref="CutTiebreak"/>).
        /// </summary>
        public static List<StandingRow> Lobby(IReadOnlyList<GameRecord> games, string host, int round, int advance, int gamesPerRound = 0)
        {
            var bumps = Bumps(games, advance, gamesPerRound);
            var roundGames = games.Where(g => g.Round == round && string.Equals(g.Host, host, StringComparison.OrdinalIgnoreCase)).ToList();
            return Ranked(roundGames, games, advance, bumps, b => b.Round == round && string.Equals(b.Host, host, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Every lobby together for a round (no cut line: players advance per lobby).</summary>
        public static List<StandingRow> Round(IReadOnlyList<GameRecord> games, int round, int advance = 0, int gamesPerRound = 0) =>
            Ranked(games.Where(g => g.Round == round).ToList(), games, 0, Bumps(games, advance, gamesPerRound), b => b.Round == round);

        private static List<StandingRow> Ranked(List<GameRecord> scope, IReadOnlyList<GameRecord> all, int advance,
            List<CutTiebreak> bumps, Func<CutTiebreak, bool> inScope)
        {
            var totals = Build(all, "").Players;
            var rows = Build(scope, "").Leaderboard()
                .Select(t => new StandingRow { Stats = t, Total = totals.TryGetValue(t.Key, out var total) ? total.Points : t.Points })
                .ToList();
            foreach (var b in bumps)
                foreach (var key in b.BumpedKeys)
                {
                    var row = rows.FirstOrDefault(r => r.Stats.Key == key);
                    if (row == null) continue;
                    row.Total += TiebreakBump;
                    if (inScope(b)) row.Stats.Points += TiebreakBump;
                }
            rows = rows.OrderByDescending(r => r.Stats.Points).ThenByDescending(r => r.Stats.Wins).ThenByDescending(r => r.Stats.Kills)
                .ThenBy(r => r.Stats.Name, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < rows.Count; i++) rows[i].Advancing = i < advance;
            return rows;
        }

        /// <summary>Every cut-line tie in lobby rounds that have finished (all their games played).</summary>
        public static List<CutTiebreak> Tiebreaks(IReadOnlyList<GameRecord> games, int advance, int gamesPerRound) =>
            Bumps(games, advance, gamesPerRound);

        private static List<CutTiebreak> Bumps(IReadOnlyList<GameRecord> games, int advance, int gamesPerRound)
        {
            var result = new List<CutTiebreak>();
            if (advance <= 0 || gamesPerRound <= 0) return result;
            foreach (var lobby in games.Where(g => g.Counted && g.Round > 0).GroupBy(g => (g.Round, Host: g.Host.ToLowerInvariant())))
            {
                var played = lobby.ToList();
                if (played.Count < gamesPerRound) continue;
                var tie = CutTie(Build(played, "").Leaderboard(), advance);
                if (tie == null) continue;
                tie.Round = lobby.Key.Round;
                tie.Host = played[0].Host;
                result.Add(tie);
            }
            return result;
        }

        private static readonly (string Name, Func<PlayerTotals, double> Value)[] TiebreakOrder =
        {
            ("impostor wins", t => t.ImpostorWins),
            ("vote %", t => t.VoteAccuracy),
            ("task %", t => t.TaskCompletion),
        };

        private static CutTiebreak? CutTie(IReadOnlyList<PlayerTotals> ranked, int advance)
        {
            if (ranked.Count <= advance) return null;
            double line = ranked[advance - 1].Points;
            if (Math.Abs(ranked[advance].Points - line) > 1e-9) return null;
            var tied = ranked.Where(t => Math.Abs(t.Points - line) < 1e-9).ToList();
            int above = advance - ranked.Count(t => t.Points > line + 1e-9);

            var order = tied.OrderByDescending(t => TiebreakOrder[0].Value(t))
                            .ThenByDescending(t => TiebreakOrder[1].Value(t))
                            .ThenByDescending(t => TiebreakOrder[2].Value(t))
                            .ToList();
            var tie = new CutTiebreak { Points = line, Tied = tied.Select(t => t.Name).ToList() };
            PlayerTotals last = order[above - 1], first = order[above];
            foreach (var (name, value) in TiebreakOrder)
            {
                if (Math.Abs(value(last) - value(first)) < 1e-9) continue;
                tie.DecidedBy = name;
                break;
            }
            if (!tie.Settled) return tie;
            tie.Bumped = order.Take(above).Select(t => t.Name).ToList();
            tie.BumpedKeys = order.Take(above).Select(t => t.Key).ToList();
            return tie;
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
                .Where(g => g.Counted)
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
