using TournamentTracker.Stats;
using Xunit;

namespace TournamentTracker.Tests;

public class GameTrackerTests
{
    private readonly FakeClock _clock = new();
    private readonly GameTracker _tracker = new(new ScoringRules());

    private GameRecord Start() =>
        _tracker.Start(1, "Cup", "ABCDEF", "The Skeld", Players.Lobby(), _clock.Now);

    [Fact]
    public void Records_a_full_game_and_scores_it()
    {
        Start();
        _clock.Advance(30);
        _tracker.Kill(0, 2, _clock.Now);                // Alice kills Carl: first blood
        _clock.Advance(10);
        _tracker.MeetingCalled(3, 2, _clock.Now);       // Dana reports Carl
        _clock.Advance(60);
        _tracker.VotingComplete(new[]
        {
            new VoteCast(0, 3),                         // impostor votes crew: not graded
            new VoteCast(1, VoteCast.SkippedVote),
            new VoteCast(2, VoteCast.DeadVote),         // dead, ignored
            new VoteCast(3, 0),                         // correct
            new VoteCast(4, 0),                         // correct
            new VoteCast(5, VoteCast.MissedVote),
        }, exiledId: 0, tie: false, _clock.Now);
        _clock.Advance(20);
        for (int i = 0; i < 4; i++) _tracker.TaskCompleted(4, _clock.Now);
        _tracker.Sabotage(1, "Reactor", _clock.Now);
        _tracker.Sabotage(1, "Reactor", _clock.Now.AddSeconds(0.5)); // same sabotage via second overload
        _clock.Advance(15);
        _tracker.Kill(1, 3, _clock.Now);
        _tracker.Kill(1, 4, _clock.Now);

        var game = _tracker.End("ImpostorByKill", Outcome.Impostors, Players.Lobby(), _clock.Now)!;

        Assert.False(_tracker.InGame);
        Assert.Equal(Outcome.Impostors, game.Winner);
        var alice = game.ById(0)!;
        var bob = game.ById(1)!;
        var dana = game.ById(3)!;
        var eve = game.ById(4)!;
        var finn = game.ById(5)!;

        Assert.Equal(1, alice.Kills);
        Assert.Equal("Ejected", alice.DeathCause);
        Assert.Equal(2, bob.Kills);
        Assert.True(bob.Survived);
        Assert.Equal(1, bob.Sabotages);
        Assert.Equal(1, bob.Skips);
        Assert.Equal(0, alice.CorrectVotes + alice.IncorrectVotes);

        Assert.Equal(1, dana.BodiesReported);
        Assert.Equal(1, dana.CorrectVotes);
        Assert.Equal(1, eve.CorrectVotes);
        Assert.True(eve.AllTasksDone);
        Assert.Equal(1, finn.MissedVotes);
        Assert.Equal(2, alice.VotesReceived);

        Assert.Equal("Killed", game.ById(2)!.DeathCause);
        Assert.Equal(30, game.ById(2)!.DiedAtSeconds);

        // The tournament point sheet:
        Assert.Equal(1 + 1 - 2 + 4, alice.Points);      // kill, first blood, voted out first, kill win
        Assert.Equal(2 + 4, bob.Points);                // two kills, kill win
        // correct vote out, caught killer, lost (the vote ejected Alice, so it isn't also a read)
        Assert.Equal(2 + 1 - 2, dana.Points);
        // correct vote out, lost, 100% tasks (up to 3), finished every task
        Assert.Equal(2 - 2 + 3 + 1, eve.Points);
        Assert.Equal(-2, finn.Points);                  // missed the vote, lost
        // Carl died first: 90% of the crew's average (1 + 4 - 2) / 3 = 1, so 0.9 exactly
        Assert.Equal(0.9, game.ById(2)!.Points, 9);
        Assert.Equal(new[] { "Kill", "First blood", "Voted out first", "Kill win" }, alice.PointBreakdown.Select(l => l.Rule));
        Assert.Equal(1, alice.ImpostorEjectOrder);
        Assert.Equal(1, dana.CaughtKiller);
        Assert.True(alice.Won);
        Assert.False(eve.Won);

        var meeting = Assert.Single(game.Meetings);
        Assert.Equal(game.ById(3)!.Key, meeting.CallerKey);
        Assert.Equal(game.ById(2)!.Key, meeting.BodyKey);
        Assert.True(meeting.EjectedWasImpostor);
        Assert.Equal(5, meeting.Votes.Count);

        Assert.Contains(game.Timeline, e => e.Kind == "kill" && e.Text.Contains("first blood"));
        Assert.Contains(game.Timeline, e => e.Text.Contains("Red (Alice) was ejected (Impostor)"));
        Assert.Contains(game.Timeline, e => e.Text.Contains("finished all 4 tasks"));
        Assert.Single(game.Timeline, e => e.Kind == "sabotage");
        Assert.Equal(135, game.DurationSeconds);
    }

    [Fact]
    public void Emergency_meeting_counts_for_the_caller_and_duplicate_start_is_ignored()
    {
        Start();
        _tracker.MeetingCalled(2, null, _clock.Now);
        _tracker.MeetingCalled(2, null, _clock.Now);    // second hook firing for the same meeting
        _tracker.VotingComplete(Array.Empty<VoteCast>(), null, tie: true, _clock.Now);

        var game = _tracker.Current!;
        Assert.Single(game.Meetings);
        Assert.Equal(1, game.ById(2)!.MeetingsCalled);
        Assert.Equal(0, game.ById(2)!.BodiesReported);
        Assert.True(game.Meetings[0].Tie);
        Assert.Contains(game.Timeline, e => e.Text.StartsWith("No one was ejected (tie)"));
    }

    [Fact]
    public void Votes_without_a_recorded_meeting_still_create_one()
    {
        Start();
        _tracker.VotingComplete(new[] { new VoteCast(2, 3) }, 3, false, _clock.Now);
        var m = Assert.Single(_tracker.Current!.Meetings);
        Assert.Null(m.CallerKey);
        Assert.False(m.EjectedWasImpostor);
        Assert.Equal(1, _tracker.Current.ById(2)!.IncorrectVotes);
    }

    [Fact]
    public void End_reconciles_deaths_and_tasks_the_hooks_missed()
    {
        Start();
        var final = Players.Lobby();
        final[2].IsDead = true;
        final[3].TasksCompleted = 3;
        final[5].Disconnected = true;

        var game = _tracker.End("HumansByVote", Outcome.Crewmates, final, _clock.Now)!;

        Assert.Equal("Killed", game.ById(2)!.DeathCause);
        Assert.Equal(3, game.ById(3)!.TasksCompleted);
        Assert.Equal("Disconnected", game.ById(5)!.DeathCause);
        Assert.False(game.ById(5)!.Won);
        Assert.True(game.ById(3)!.Won);
    }

    [Fact]
    public void Events_without_a_game_are_ignored()
    {
        _tracker.Kill(0, 1, _clock.Now);
        _tracker.MeetingCalled(0, null, _clock.Now);
        _tracker.TaskCompleted(0, _clock.Now);
        Assert.Null(_tracker.End("HumansByTask", Outcome.Crewmates, Players.Lobby(), _clock.Now));
    }

    [Fact]
    public void Abandoned_game_scores_nothing()
    {
        Start();
        _tracker.Kill(0, 2, _clock.Now);
        var game = _tracker.End("Abandoned", null, Players.Lobby(), _clock.Now)!;
        Assert.False(game.Counted);
        Assert.All(game.Players, p => Assert.Equal(0, p.Points));
        Assert.All(game.Players, p => Assert.False(p.Won));
    }

    [Theory]
    [InlineData("HumansByVote", Outcome.Crewmates)]
    [InlineData("HumansByTask", Outcome.Crewmates)]
    [InlineData("CrewmatesByTask", Outcome.Crewmates)]
    [InlineData("ImpostorByKill", Outcome.Impostors)]
    [InlineData("ImpostorsBySabotage", Outcome.Impostors)]
    [InlineData("ImpostorByVote", Outcome.Impostors)]
    [InlineData("ImpostorDisconnect", Outcome.Crewmates)]
    [InlineData("HumansDisconnect", Outcome.Impostors)]
    [InlineData("CrewmateDisconnect", Outcome.Impostors)]
    [InlineData("SomethingNew", null)]
    public void Winner_is_read_from_the_reason_name(string reason, string? winner) =>
        Assert.Equal(winner, Outcome.WinnerFromReason(reason));
}
