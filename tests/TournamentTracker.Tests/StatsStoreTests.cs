using TournamentTracker.Stats;
using Xunit;

namespace TournamentTracker.Tests;

public class StatsStoreTests
{
    private static GameRecord Game(string winner, Action<GameTracker, DateTime>? play = null)
    {
        var clock = new FakeClock();
        var tracker = new GameTracker(new ScoringRules());
        tracker.Start(1, "Cup", "ABCDEF", "Polus", Players.Lobby(), clock.Now);
        play?.Invoke(tracker, clock.Now.AddSeconds(10));
        return tracker.End(winner == Outcome.Impostors ? "ImpostorByKill" : "HumansByTask", winner, Players.Lobby(), clock.Now.AddSeconds(60))!;
    }

    [Fact]
    public void Totals_accumulate_across_games()
    {
        var store = new StatsStore { Tournament = "Cup" };
        store.Apply(Game(Outcome.Impostors, (t, now) => { t.Kill(0, 2, now); t.Kill(0, 3, now.AddSeconds(5)); }));
        store.Apply(Game(Outcome.Crewmates));

        var alice = store.Players.Values.Single(p => p.Name == "Alice");
        Assert.Equal(2, alice.Games);
        Assert.Equal(1, alice.Wins);
        Assert.Equal(1, alice.Losses);
        Assert.Equal(2, alice.Kills);
        Assert.Equal(2, alice.ImpostorGames);
        Assert.Equal(1, alice.ImpostorWins);

        var carl = store.Players.Values.Single(p => p.Name == "Carl");
        Assert.Equal(1, carl.Deaths);
        Assert.Equal(1, carl.FirstDeaths);
        Assert.Equal(2, store.GamesRecorded);
        Assert.Equal(1, store.ImpostorWins);
        Assert.Equal(1, store.CrewWins);

        // Alice: 2 kills + first blood + kill win (7), then lost to tasks (-3).
        Assert.Equal(4, alice.Points);
        // Carl: died first in a loss, so 90% of the crew's -2 average (-1.8, rounded to -2), then a task win (+5).
        Assert.Equal(3, carl.Points);
        Assert.Equal("Alice", store.Leaderboard()[0].Name);
    }

    [Fact]
    public void Abandoned_games_are_not_counted()
    {
        var store = new StatsStore();
        var tracker = new GameTracker(new ScoringRules());
        tracker.Start(1, "Cup", "X", "Polus", Players.Lobby(), DateTime.UtcNow);
        store.Apply(tracker.End("Abandoned", null, Players.Lobby(), DateTime.UtcNow)!);
        Assert.Equal(0, store.GamesRecorded);
        Assert.Empty(store.Players);
    }

    [Fact]
    public void Saves_and_loads()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "sub", "stats.json");
        var store = new StatsStore { Tournament = "Cup" };
        store.Apply(Game(Outcome.Crewmates));
        store.Save(path);
        store.Save(path); // overwrite path goes through File.Replace

        var loaded = StatsStore.Load(path, "Cup");
        Assert.Equal(1, loaded.GamesRecorded);
        Assert.Equal(6, loaded.Players.Count);
        Assert.Equal(store.Leaderboard()[0].Points, loaded.Leaderboard()[0].Points);
    }

    [Fact]
    public void Corrupt_file_is_kept_aside_and_a_fresh_store_returned()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "stats.json");
        File.WriteAllText(path, "{ not json");
        var store = StatsStore.Load(path, "Cup");
        Assert.Equal(0, store.GamesRecorded);
        Assert.Single(Directory.GetFiles(dir.Path, "stats.json.corrupt-*"));
    }
}
