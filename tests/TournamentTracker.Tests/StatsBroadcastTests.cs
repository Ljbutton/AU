using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Stats;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>The stats database, storyline notes, standings from the tournament's scoring, points on the line.</summary>
public class StatsBroadcastTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Dispose() => _dir.Dispose();

    private static object P(int id, string name, int color, bool imp = false) => new { id, name, color, colorName = "X", imp };
    private static object R(int id, string name, int color, bool imp) => new { id, name, color, key = name.ToLower() + "#1", discord = (string?)null, imp, dead = false };

    private void Game(CasterDesk desk, string lobby, int n, string winner, params (string Killer, string Victim)[] kills)
    {
        void E(string kind, object data, double clock)
        {
            var msg = JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
            msg["type"] = "event"; msg["kind"] = kind; msg["lobby"] = lobby; msg["round"] = 1; msg["game"] = $"{lobby}-{n}"; msg["clock"] = clock; msg["t"] = 1;
            desk.Apply(JsonSerializer.Serialize(msg));
        }
        var names = new[] { "Jake", "Maria", "Sam", "Priya" };
        object Who(string name) => P(Array.IndexOf(names, name), name, Array.IndexOf(names, name));
        E("gameStart", new { map = "Polus", players = names.Select((x, i) => P(i, x, i, x == "Jake")).ToArray(), roster = names.Select((x, i) => R(i, x, i, x == "Jake")).ToArray() }, 0);
        double t = 10;
        foreach (var (k, v) in kills) E("kill", new { killer = Who(k), victim = Who(v), room = "Electrical", winning = false }, t += 20);
        E("meeting", new { caller = Who("Priya"), body = Who("Maria"), emergency = false }, t + 5);
        E("eject", new { ejected = Who("Sam"), wasImpostor = false, skipped = false, tie = false, votes = new[] { new { voter = 3, target = (int?)2, skipped = false } } }, t + 30);
        E("sabotage", new { system = "Reactor", state = "start", critical = true, timeLeft = 30.0 }, t + 40);
        E("sabotage", new { system = "Reactor", state = "fixed", critical = true, timeLeft = 4.5 }, t + 65);
        E("gameEnd", new { winner, reason = winner == "Impostors" ? "ImpostorsByKill" : "HumansByVote", how = winner == "Impostors" ? "kills" : "vote", abandoned = false }, t + 90);
        _clock.Advance(300);
    }

    [Fact]
    public void Every_game_is_saved_with_kills_votes_ejections_sabotages_and_the_winner()
    {
        var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path);
        try
        {
            Game(desk, "LJ", 1, "Impostors", ("Jake", "Maria"));
            var g = desk.Archive.Games.Single();
            Assert.Equal("LJ", g.Referee);
            Assert.Equal("LJ-1", g.Game);
            Assert.Equal("Impostors", g.Winner);
            Assert.Equal("maria#1", g.Kills.Single().Victim);
            Assert.Equal("Electrical", g.Kills.Single().Room);
            Assert.Equal("sam#1", g.Meetings.Single().Ejected);
            Assert.Equal("sam#1", g.Meetings.Single().Votes["priya#1"]);
            Assert.Equal(4.5, g.Sabotages.Single().LeftWhenFixed);
            Assert.True(g.P("jake#1")!.Won);
            Assert.Equal(1, g.P("jake#1")!.Kills);
            Assert.Single(Directory.GetFiles(Path.Combine(_dir.Path, "broadcast-games")));
            // Kept: a new desk reads it back.
            var again = new GameArchive(Path.Combine(_dir.Path, "broadcast-games"));
            Assert.Equal("LJ-1", again.Games.Single().Game);
        }
        finally { desk.Dispose(); }
    }

    [Fact]
    public void Storyline_notes_come_from_the_days_games_and_can_be_pinned_or_dismissed()
    {
        var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path);
        try
        {
            for (int i = 1; i <= 3; i++) Game(desk, "LJ", i, "Impostors", ("Jake", "Maria"), ("Jake", "Sam"));
            var notes = desk.Storylines.Notes(new HashSet<string>());
            var texts = notes.Select(n => NameTag.Plain(n.Text)).ToList();
            Assert.Contains("Jake has killed Maria 3 times today", texts);
            Assert.Contains("Jake has won 3 in a row", texts);
            Assert.Contains("Sam has been voted out 3 times today", texts);
            Assert.Contains(texts, t => t.StartsWith("First blood of the day: Jake on Maria in Electrical"));
            Assert.Contains("Jake leads today with 6 kills", texts);
            Assert.Contains("Jake is 3–0 as impostor today", texts);
            Assert.Contains(texts, t => t.StartsWith("Fastest impostors win today"));

            var rivalry = notes.First(n => n.Kind == "rivalry");
            desk.Storylines.Mark(rivalry.Id, "pin");
            Assert.Equal(rivalry.Id, desk.Storylines.Notes(new HashSet<string>())[0].Id);    // pinned first
            desk.Storylines.Mark(rivalry.Id, "used");
            Assert.DoesNotContain(desk.Storylines.Notes(new HashSet<string>()), n => n.Id == rivalry.Id);
            // Notes about players on stream rank higher.
            var focus = desk.Storylines.Notes(new HashSet<string> { "sam#1" });
            Assert.Contains("sam#1", focus[0].Players);
        }
        finally { desk.Dispose(); }
    }

    [Fact]
    public void Standings_come_from_the_tournaments_scoring_and_show_whats_on_the_line()
    {
        var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        desk.Simulate(true);
        try
        {
            for (int i = 0; i < 1200 && desk.Games.Count < 6; i++) { _clock.Advance(1); desk.SimTick(); }
            Assert.True(desk.Games.Count >= 6, "the simulation finished some games");
            int round = desk.Tables.CurrentRound;
            var table = desk.Tables.Round(round);
            Assert.NotEmpty(table);
            // The same numbers the tournament's own standings give.
            var expected = Standings.Round(desk.Games.ToList(), round, 5, 3);
            Assert.Equal(expected[0].Stats.Key, table[0].Key);
            Assert.Equal(Math.Round(expected[0].Total, 2), table[0].Points);
            Assert.Contains(table, r => r.Name.Contains(' '));          // roster names (simulated roster)

            // Points on the line: give the last-placed player of a lobby a big win and they move up.
            string lobby = desk.Games[0].Host;
            var lobbyTable = desk.Tables.Lobby(lobby, desk.Games[0].Round);
            var last = lobbyTable.Last();
            var ifEnded = JsonSerializer.SerializeToElement(new Dictionary<string, Dictionary<string, double>>
            {
                ["impostors"] = new() { [last.Key] = 1000 },
                ["crewVote"] = new() { [last.Key] = -5 },
            });
            var lines = desk.Tables.OnTheLine(lobby, desk.Games[0].Round, ifEnded, 10, new Dictionary<string, (string, int)> { [last.Key] = (last.Name, last.Color) });
            var imp = JsonSerializer.SerializeToElement(lines[0], Camel);
            Assert.Equal("If impostors win", imp.GetProperty("title").GetString());
            Assert.Equal($"{last.Name} moves to 1st", NameTag.Plain(imp.GetProperty("moves")[0].GetString()!));
        }
        finally { desk.Dispose(); }
    }

    [Fact]
    public void The_standings_change_after_each_game_is_noticed()
    {
        var games = new List<GameRecord>();
        var roster = new Roster(null);
        var tables = new Tables(() => games, roster, () => 2, () => 3);
        GameRecord G(int n, string winner)
        {
            var g = new GameRecord { GameNumber = n, Host = "LJ", Round = 1, StartedUtc = _clock.Now, Winner = winner, EndReason = winner == "Impostors" ? "ImpostorsByKill" : "HumansByVote" };
            g.Players.Add(new GamePlayer { Key = "a", Name = "A", IsImpostor = true, Kills = 2 });
            g.Players.Add(new GamePlayer { Key = "b", Name = "B" });
            g.Players.Add(new GamePlayer { Key = "c", Name = "C" });
            Scoring.ScoreGame(g, new ScoringRules());
            return g;
        }
        games.Add(G(1, "Crewmates"));
        tables.Update(new[] { "LJ" }, _clock.Now);
        Assert.Null(tables.Change);
        games.Add(G(2, "Impostors"));
        tables.Update(new[] { "LJ" }, _clock.Now);
        var change = JsonSerializer.SerializeToElement(tables.Change, Camel);
        Assert.Equal("LJ", change.GetProperty("lobby").GetString());
        Assert.Contains(change.GetProperty("rows").EnumerateArray(), r => r.GetProperty("was").ValueKind == JsonValueKind.Number);
    }
}
