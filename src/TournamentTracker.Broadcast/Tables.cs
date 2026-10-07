using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TournamentTracker.Stats;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>A standings row for the caster: rank, real name, points.</summary>
    public sealed class TableRow
    {
        public int Rank { get; set; }
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public int Color { get; set; }
        /// <summary>Points in this table's scope (the round, or the tournament); what it's ranked by.</summary>
        public double Points { get; set; }
        /// <summary>The tournament total so far.</summary>
        public double Total { get; set; }
        public int Games { get; set; }
        public int Wins { get; set; }
        public int ImpostorWins { get; set; }
        public int ImpostorGames { get; set; }
        public int CrewWins { get; set; }
        public int CrewGames { get; set; }
        public int Kills { get; set; }
        public bool Advancing { get; set; }
    }

    /// <summary>
    /// Standings for the broadcast, from the tournament's own scoring: the scored game records
    /// (shared results, read by the organiser view) through <see cref="Stats.Standings"/> — the
    /// same tables the Points page and Discord show. Nothing is scored here. Also works out
    /// "points on the line" (each ending's points, scored by the referee's mod with the same point
    /// sheet, added to the lobby's table) and the standings change after each game.
    /// </summary>
    public sealed class Tables
    {
        private readonly Func<IReadOnlyList<GameRecord>> _games;
        private readonly Roster _roster;
        private readonly Func<int> _advance, _perRound;
        private readonly object _lock = new object();
        private readonly Dictionary<string, (int Count, Dictionary<string, int> Ranks)> _seen = new Dictionary<string, (int, Dictionary<string, int>)>();

        /// <summary>The latest change after a game (shown for a while on stream).</summary>
        public object? Change { get; private set; }
        public DateTime ChangeAt { get; private set; }
        private string? _changeLobby;

        public Tables(Func<IReadOnlyList<GameRecord>> games, Roster roster, Func<int> advance, Func<int> perRound)
        {
            _games = games;
            _roster = roster;
            _advance = advance;
            _perRound = perRound;
        }

        public IReadOnlyList<GameRecord> Games => _games();

        /// <summary>The round being played (the latest with games).</summary>
        public int CurrentRound => Games.Where(g => g.Counted).Select(g => g.Round).DefaultIfEmpty(0).Max();

        private List<TableRow> Rows(IEnumerable<StandingRow> rows) => rows.Select((r, i) => new TableRow
        {
            Rank = i + 1,
            Key = r.Stats.Key,
            Name = _roster.Match(r.Stats.Key, DiscordOf(r.Stats.Key), r.Stats.Name).Entry?.Name ?? r.Stats.Name,
            Color = r.Stats.LastColorId,
            Points = Math.Round(r.Stats.Points, 2),
            Total = Math.Round(r.Total, 2),
            Games = r.Stats.Games, Wins = r.Stats.Wins,
            ImpostorWins = r.Stats.ImpostorWins, ImpostorGames = r.Stats.ImpostorGames,
            CrewWins = r.Stats.CrewWins, CrewGames = r.Stats.CrewGames,
            Kills = r.Stats.Kills,
            Advancing = r.Advancing,
        }).ToList();

        private string? DiscordOf(string key) => Games.SelectMany(g => g.Players).FirstOrDefault(p => p.Key == key && !string.IsNullOrEmpty(p.DiscordId))?.DiscordId;

        /// <summary>The whole round, every lobby.</summary>
        public List<TableRow> Round(int round) => Rows(Standings.Round(Games.ToList(), round, _advance(), _perRound()));

        /// <summary>One lobby's table for the round (who moves on is decided per lobby).</summary>
        public List<TableRow> Lobby(string lobby, int round) => Rows(Standings.Lobby(Games.ToList(), lobby, round, _advance(), _perRound()));

        /// <summary>
        /// A lobby's table for its own round. Lobbies can be in different rounds (a big tournament starts
        /// round 2 in one lobby while another finishes round 1), so each keeps its own; before the new
        /// round's first game, the last round's final table stays up.
        /// </summary>
        public (int Round, List<TableRow> Rows) LobbyTable(string lobby, int round)
        {
            for (int r = round; r >= 1; r--)
            {
                var rows = Lobby(lobby, r);
                if (rows.Count > 0) return (r, rows);
                if (r == round && Games.Any(g => g.Counted && g.Round == r && string.Equals(g.Host, lobby, StringComparison.OrdinalIgnoreCase))) return (r, rows);
            }
            return (round, new List<TableRow>());
        }

        /// <summary>
        /// Leader boards for the stats ticker: the top ten in each, over the tournament's counted games
        /// (vote % needs 3 votes, impostor win % 2 impostor games).
        /// </summary>
        public List<(string Title, string Unit, List<(string Name, int Color, string Value)> Rows)> Leaders(int top = 10)
        {
            var all = Standings.Build(Games.Where(g => g.Counted), "").Leaderboard().ToList();
            string Name(PlayerTotals p) => _roster.Match(p.Key, DiscordOf(p.Key), p.Name).Entry?.Name ?? p.Name;
            string Pct(double v) => Math.Round(v * 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
            List<(string, int, string)> Board(IEnumerable<PlayerTotals> rows, Func<PlayerTotals, string> value) =>
                rows.Take(top).Select(p => (Name(p), p.LastColorId, value(p))).ToList();
            var boards = new List<(string, string, List<(string, int, string)>)>
            {
                ("Tournament points", "pts", Board(all.OrderByDescending(p => p.Points), p => Math.Round(p.Points, 1).ToString(System.Globalization.CultureInfo.InvariantCulture))),
                ("Sharpest voters", "right", Board(all.Where(p => p.CorrectVotes + p.IncorrectVotes >= 3).OrderByDescending(p => p.VoteAccuracy).ThenByDescending(p => p.CorrectVotes), p => Pct(p.VoteAccuracy))),
                ("Most kills", "kills", Board(all.Where(p => p.Kills > 0).OrderByDescending(p => p.Kills).ThenBy(p => p.Games), p => p.Kills.ToString())),
                ("Impostor win %", "", Board(all.Where(p => p.ImpostorGames >= 2).OrderByDescending(p => (double)p.ImpostorWins / p.ImpostorGames).ThenByDescending(p => p.ImpostorWins), p => Pct((double)p.ImpostorWins / p.ImpostorGames))),
                ("Task machines", "tasks", Board(all.Where(p => p.TasksCompleted > 0).OrderByDescending(p => p.TasksCompleted), p => p.TasksCompleted.ToString())),
                ("Survivors", "games", Board(all.Where(p => p.Survived > 0).OrderByDescending(p => p.Survived).ThenBy(p => p.Games), p => p.Survived + "/" + p.Games)),
            };
            return boards.Where(b => b.Item3.Count >= 3).ToList();
        }

        /// <summary>Every counted game of the tournament.</summary>
        public List<TableRow> Overall() => Rows(Standings.Build(Games.Where(g => g.Counted), "").Leaderboard().Select(t => new StandingRow { Stats = t, Total = t.Points }));

        /// <summary>
        /// "If impostors win: Jake moves to 1st": each ending's points (from the referee's mod) added
        /// to the lobby's table, and who that moves, compared with now.
        /// </summary>
        public List<object> OnTheLine(string lobby, int round, JsonElement? ifEnded, int taskPct, IDictionary<string, (string Name, int Color)> live)
        {
            var lines = new List<object>();
            if (ifEnded is not { ValueKind: JsonValueKind.Object } outcomes) return lines;
            var now = Lobby(lobby, round);
            int cut = _advance();
            // A crew win by tasks is the likely one once the bar is well along.
            var endings = new[] { ("impostors", "If impostors win"), (taskPct >= 70 ? "crewTasks" : "crewVote", "If crewmates win") };
            foreach (var (name, title) in endings)
            {
                if (!outcomes.TryGetProperty(name, out var pts) || pts.ValueKind != JsonValueKind.Object) continue;
                var totals = now.ToDictionary(r => r.Key, r => r.Points);
                foreach (var p in pts.EnumerateObject())
                    totals[p.Name] = (totals.TryGetValue(p.Name, out var t) ? t : 0) + p.Value.GetDouble();
                var after = totals.OrderByDescending(kv => kv.Value).Select((kv, i) => (kv.Key, Rank: i + 1, kv.Value)).ToList();
                var moves = new List<string>();
                foreach (var (key, rank, value) in after)
                {
                    if (!live.TryGetValue(key, out var who)) continue;
                    int was = now.FirstOrDefault(r => r.Key == key)?.Rank ?? now.Count + 1;
                    string tag = NameTag.Make(who.Color, who.Name);
                    if (rank == 1 && was != 1) moves.Add($"{tag} moves to 1st");
                    else if (cut > 0 && rank <= cut && was > cut) moves.Add($"{tag} moves into the top {cut}");
                    else if (cut > 0 && rank > cut && was <= cut) moves.Add($"{tag} drops below the cut");
                }
                lines.Add(new { ending = name, title, moves = moves.Take(3).ToList() });
            }
            return lines;
        }

        /// <summary>Drops what was noted about these lobbies (the simulator's, when it stops).</summary>
        public void Forget(Func<string, bool> which)
        {
            lock (_lock)
            {
                foreach (var k in _seen.Keys.Where(k => which(k.Split('|')[0])).ToList()) _seen.Remove(k);
                if (_changeLobby != null && which(_changeLobby)) { Change = null; _changeLobby = null; }
            }
        }

        /// <summary>Checks each lobby's table for a new game; when there is one, notes who went up and down.</summary>
        public void Update(IEnumerable<string> lobbies, DateTime now)
        {
            int round = CurrentRound;
            foreach (var lobby in lobbies)
            {
                int count = Games.Count(g => g.Counted && g.Round == round && string.Equals(g.Host, lobby, StringComparison.OrdinalIgnoreCase));
                var table = Lobby(lobby, round);
                var ranks = table.ToDictionary(r => r.Key, r => r.Rank);
                lock (_lock)
                {
                    string key = lobby + "|" + round;
                    if (_seen.TryGetValue(key, out var was) && was.Count < count && count > 0)
                    {
                        Change = new
                        {
                            lobby, round,
                            rows = table.Select(r => new { r.Rank, r.Name, r.Color, r.Points, r.Advancing, Was = was.Ranks.TryGetValue(r.Key, out var w) ? w : (int?)null }).ToList(),
                        };
                        ChangeAt = now;
                        _changeLobby = lobby;
                    }
                    _seen[key] = (count, ranks);
                }
            }
        }
    }
}
