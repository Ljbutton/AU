using TournamentTracker.Stats;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Each rule on the tournament point sheet. Red (0) and Blue (1) are the impostors.</summary>
public class ScoringTests
{
    private readonly FakeClock _clock = new();
    private readonly GameTracker _t = new(new ScoringRules());

    private void Start() => _t.Start(1, "Cup", "X", "Polus", Players.Lobby(), _clock.Now);

    private void Eject(byte exiled, params (byte Voter, byte Target)[] votes)
    {
        _clock.Advance(30);
        _t.VotingComplete(votes.Select(v => new VoteCast(v.Voter, v.Target)).ToArray(), exiled, false, _clock.Now);
    }

    private GameRecord End(string reason, Action<List<PlayerSnapshot>>? final = null)
    {
        var players = Players.Lobby();
        final?.Invoke(players);
        return _t.End(reason, Outcome.WinnerFromReason(reason), players, _clock.Now)!;
    }

    private static double Pts(GameRecord g, byte id) => g.ById(id)!.Points;

    [Fact]
    public void Crew_vote_win_both_impostors_ejected()
    {
        Start();
        _t.MeetingCalled(2, null, _clock.Now);                       // Green calls the button
        Eject(0, (2, 0), (3, 0), (4, 0), (1, 5));                   // Red out: Green, Pink, Orange correct
        _t.MeetingCalled(5, null, _clock.Now);
        Eject(1, (2, 1), (5, 1), (4, 3));                          // Blue out: Orange voted Pink (not ejected, no penalty)
        var g = End("HumansByVote");

        Assert.Equal(-2 - 2, Pts(g, 0));          // voted out first, lost to vote
        Assert.Equal(-1 - 2, Pts(g, 1));          // voted out last, lost to vote
        Assert.Equal(2 + 2 + 1 + 3, Pts(g, 2));   // two correct vote-outs, caught killer, vote win
        Assert.Equal(2 + 3, Pts(g, 3));
        Assert.Equal(2 + 3, Pts(g, 4));
        Assert.Equal(2 + 1 + 3, Pts(g, 5));       // correct vote-out, caught killer, vote win
    }

    [Fact]
    public void Wrong_ejection_costs_crewmates_and_pays_impostors()
    {
        Start();
        Eject(3, (0, 3), (1, 3), (2, 3), (4, 0), (5, 3));           // Pink (crew) ejected
        var g = End("ImpostorByVote");

        Assert.Equal(1 + 3, Pts(g, 0));           // voted a crewmate out, vote win
        Assert.Equal(1 + 3, Pts(g, 1));
        Assert.Equal(-2 - 1, Pts(g, 2));          // incorrect vote out, lost
        Assert.Equal(-1, Pts(g, 3));              // ejected crewmate: just the loss
        Assert.Equal(-1, Pts(g, 4));              // voted Red, who stayed in: no points either way
        Assert.Equal(-2 - 1, Pts(g, 5));
    }

    [Fact]
    public void Sabotage_loss_hits_living_crewmates_hardest()
    {
        Start();
        _t.Kill(0, 2, _clock.Now);                                  // Green dies first
        _t.Kill(1, 3, _clock.Now.AddSeconds(5));                    // Pink dies later
        var g = End("ImpostorBySabotage", p => { p[2].IsDead = true; p[3].IsDead = true; });

        Assert.Equal(1 + 1 + 5, Pts(g, 0));       // kill, first blood, sabotage win
        Assert.Equal(1 + 5, Pts(g, 1));
        Assert.Equal(1 - 1, Pts(g, 2));           // died first, dead so "other loss"
        Assert.Equal(0.5 - 1, Pts(g, 3));         // got killed, other loss
        Assert.Equal(-5, Pts(g, 4));              // alive at a sabotage loss
    }

    [Fact]
    public void Task_win_pays_crew_and_costs_impostors()
    {
        Start();
        for (int i = 0; i < 4; i++) _t.TaskCompleted(4, _clock.Now);
        _t.Kill(0, 5, _clock.Now);
        var g = End("HumansByTask", p => p[5].IsDead = true);

        Assert.Equal(1 + 1 - 3, Pts(g, 0));       // kill, first blood, lost to tasks
        Assert.Equal(-3, Pts(g, 1));
        Assert.Equal(3 + 5, Pts(g, 4));           // completed tasks, task win
        Assert.Equal(1 + 5, Pts(g, 5));           // died first still wins with the team
        Assert.Equal(5, Pts(g, 2));
    }

    [Fact]
    public void Disconnected_players_get_no_win_or_loss_points()
    {
        Start();
        _t.Kill(0, 2, _clock.Now);
        _t.Disconnected(4, _clock.Now);
        var g = End("ImpostorByKill");
        Assert.Equal(0, Pts(g, 4));
        Assert.Equal(1 + 1 + 3, Pts(g, 0));
    }

    [Fact]
    public void Rules_can_be_changed()
    {
        var t = new GameTracker(new ScoringRules { Kill = 2, ImpostorKillWin = 10, GotKilled = 0, DiedFirst = 0 });
        t.Start(1, "Cup", "X", "Polus", Players.Lobby(), _clock.Now);
        t.Kill(0, 2, _clock.Now);
        var g = t.End("ImpostorByKill", Outcome.Impostors, Players.Lobby(), _clock.Now)!;
        Assert.Equal(2 + 1 + 10, g.ById(0)!.Points);
        Assert.Equal(-1, g.ById(2)!.Points);
    }

    [Theory]
    [InlineData("HumansByTask", "Tasks")]
    [InlineData("CrewmatesByVote", "Vote")]
    [InlineData("ImpostorByKill", "Kill")]
    [InlineData("ImpostorsBySabotage", "Sabotage")]
    [InlineData("ImpostorDisconnect", "Disconnect")]
    public void End_reasons_are_classified(string reason, string kind) => Assert.Equal(kind, Outcome.Kind(reason));
}
