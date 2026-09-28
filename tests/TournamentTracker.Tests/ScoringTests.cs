using System.Linq;
using TournamentTracker.Stats;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Each rule on the tournament point sheet. Red (0) and Blue (1) are the impostors.</summary>
public class ScoringTests
{
    private readonly FakeClock _clock = new();
    // The sheet's own rules; the percentage bonuses have their own tests below.
    private readonly GameTracker _t = new(new ScoringRules { ReadVoteBonus = 0, TaskPercentBonus = 0, DiedFirstShareOfCrewAverage = 0 });

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

        Assert.Equal(2 + 4, Pts(g, 0));           // voted a crewmate out, vote win
        Assert.Equal(2 + 4, Pts(g, 1));
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
        Assert.Equal(-1, Pts(g, 2));              // dead, so just the ordinary loss
        Assert.Equal(-1, Pts(g, 3));
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
        Assert.Equal(5, Pts(g, 4));               // task win (finishing tasks is scored by the % bonus, off here)
        Assert.Equal(5, Pts(g, 5));               // died first still wins with the team
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
        Assert.Equal(1 + 1 + 4, Pts(g, 0));
    }

    [Fact]
    public void Rules_can_be_changed()
    {
        var t = new GameTracker(new ScoringRules { Kill = 2, ImpostorKillWin = 10, GotKilled = 0, DiedFirstShareOfCrewAverage = 0 });
        t.Start(1, "Cup", "X", "Polus", Players.Lobby(), _clock.Now);
        t.Kill(0, 2, _clock.Now);
        var g = t.End("ImpostorByKill", Outcome.Impostors, Players.Lobby(), _clock.Now)!;
        Assert.Equal(2 + 1 + 10, g.ById(0)!.Points);
        Assert.Equal(-1, g.ById(2)!.Points);
    }

    [Fact]
    public void Percentage_bonuses_scale_with_vote_accuracy_and_tasks()
    {
        var t = new GameTracker(new ScoringRules());   // defaults: reads 1 each up to 4, tasks 3, halves
        var lobby = Players.Lobby();                    // crewmates have 4 tasks
        t.Start(1, "Cup", "X", "Polus", lobby, _clock.Now);
        // Nobody is ejected, so every vote is a read. Green: 2 of 3 on impostors -> 2 x 67% = 1.33 -> 1.5.
        t.VotingComplete(new[] { new VoteCast(2, 0), new VoteCast(3, 1) }, null, false, _clock.Now);
        t.VotingComplete(new[] { new VoteCast(2, 1), new VoteCast(3, VoteCast.SkippedVote) }, null, false, _clock.Now);
        t.VotingComplete(new[] { new VoteCast(2, 4) }, null, false, _clock.Now);
        var final = Players.Lobby();
        final[2].TasksCompleted = 3;                    // 75% of 3 = 2.25 -> 2.5
        final[3].TasksCompleted = 1;                    // 25% of 3 = 0.75 -> 1
        var g = t.End("HumansByTask", Outcome.Crewmates, final, _clock.Now)!;

        var green = g.ById(2)!;
        Assert.Contains(green.PointBreakdown, l => l.Rule == "Reads 2/3 on impostors" && l.Points == 1.5);
        Assert.Contains(green.PointBreakdown, l => l.Rule == "Tasks 75%" && l.Points == 2.5);
        Assert.Equal(1.5 + 2.5 + 5, green.Points);

        var pink = g.ById(3)!;                          // one read on an impostor, one skip: 1 x 100%
        Assert.Contains(pink.PointBreakdown, l => l.Rule == "Reads 1/1 on impostors" && l.Points == 1);
        Assert.Contains(pink.PointBreakdown, l => l.Rule == "Tasks 25%" && l.Points == 1);

        var orange = g.ById(4)!;                        // never voted: no vote bonus at all
        Assert.DoesNotContain(orange.PointBreakdown, l => l.Rule.StartsWith("Reads"));
        Assert.DoesNotContain(g.ById(0)!.PointBreakdown, l => l.Rule.StartsWith("Tasks"));   // impostors don't get them
    }

    [Theory]
    [InlineData(1, 0, 1)]     // one right read
    [InlineData(4, 0, 4)]     // right at four meetings: the full bonus
    [InlineData(6, 0, 4)]     // capped
    [InlineData(4, 4, 2)]     // voting at everything: half right, half the bonus
    [InlineData(1, 3, 0.5)]   // 1 x 25% = 0.25 -> 0.5
    [InlineData(0, 3, 0)]
    public void Reads_reward_how_often_and_how_accurately(int correct, int incorrect, double expected) =>
        Assert.Equal(expected, Scoring.ReadBonus(correct, incorrect, new ScoringRules()));

    [Fact]
    public void A_vote_that_ejects_someone_is_not_a_read()
    {
        var t = new GameTracker(new ScoringRules());
        t.Start(1, "Cup", "X", "Polus", Players.Lobby(), _clock.Now);
        // Meeting 1: Green and Pink vote Red out (vote out, not a read); Orange votes Blue (a read).
        t.VotingComplete(new[] { new VoteCast(2, 0), new VoteCast(3, 0), new VoteCast(4, 1) }, 0, false, _clock.Now);
        // Meeting 2: nobody ejected; Green votes Blue (a read), Pink votes Orange (a wrong read).
        t.VotingComplete(new[] { new VoteCast(2, 1), new VoteCast(3, 4) }, null, false, _clock.Now);
        var g = t.End("HumansByVote", Outcome.Crewmates, Players.Lobby(), _clock.Now)!;

        Assert.Equal((1, 0), (g.ById(2)!.ReadVotesCorrect, g.ById(2)!.ReadVotesIncorrect));
        Assert.Equal((0, 1), (g.ById(3)!.ReadVotesCorrect, g.ById(3)!.ReadVotesIncorrect));
        Assert.Equal((1, 0), (g.ById(4)!.ReadVotesCorrect, g.ById(4)!.ReadVotesIncorrect));
        Assert.Equal(1, g.ById(2)!.EjectVotesOnImpostor);
        Assert.DoesNotContain(g.ById(3)!.PointBreakdown, l => l.Rule.StartsWith("Reads"));
    }

    [Fact]
    public void Long_tasks_weigh_double_in_the_task_bonus()
    {
        // 2 common + 3 long + 5 short. Done: all 5 short and 1 common, no long ones.
        var p = new GamePlayer { TasksTotal = 10, TasksCompleted = 6, LongTasksTotal = 3, LongTasksCompleted = 0 };
        Assert.Equal(6.0 / 13, Scoring.TaskEffort(p, 2), 3);   // 46%, where a plain count says 60%
        Assert.Equal(0.6, Scoring.TaskEffort(p, 1), 3);
        p.LongTasksCompleted = 3; p.TasksCompleted = 9;       // everything but one short task
        Assert.Equal(12.0 / 13, Scoring.TaskEffort(p, 2), 3);
    }

    [Fact]
    public void First_crewmate_killed_gets_90_percent_of_the_crew_average()
    {
        var t = new GameTracker(new ScoringRules { ReadVoteBonus = 0, TaskPercentBonus = 0 });
        t.Start(1, "Cup", "X", "Polus", Players.Lobby(), _clock.Now);
        t.Kill(0, 2, _clock.Now);                                   // Green dies first
        t.VotingComplete(new[] { new VoteCast(3, 0), new VoteCast(4, 0) }, 0, false, _clock.Now);
        t.VotingComplete(new[] { new VoteCast(3, 1), new VoteCast(5, 1) }, 1, false, _clock.Now);
        var final = Players.Lobby();
        final[2].IsDead = true;
        var g = t.End("HumansByVote", Outcome.Crewmates, final, _clock.Now)!;

        var crew = g.Players.Where(p => !p.IsImpostor && p.PlayerId != 2).ToList();
        double average = crew.Average(p => p.Points);
        var green = g.ById(2)!;
        Assert.Equal(Scoring.Round(0.9 * average, 0.5), green.Points);
        Assert.Contains(green.PointBreakdown, l => l.Rule.StartsWith("Died first: 90% of crew average"));
        Assert.Equal(green.Points, green.PointBreakdown.Sum(l => l.Points));
    }

    [Theory]
    [InlineData(2, 0.67, 0.5, 1.5)]
    [InlineData(2, 0.6, 0.5, 1.0)]
    [InlineData(2, 0.625, 0.5, 1.5)]
    [InlineData(3, 1.0, 0.5, 3.0)]
    [InlineData(2, 0.67, 0, 1.34)]
    public void Scaled_bonus_rounds_to_the_step(double max, double share, double step, double expected) =>
        Assert.Equal(expected, Scoring.Scaled(max, share, step), 3);

    [Theory]
    [InlineData("HumansByTask", "Tasks")]
    [InlineData("CrewmatesByVote", "Vote")]
    [InlineData("ImpostorByKill", "Kill")]
    [InlineData("ImpostorsBySabotage", "Sabotage")]
    [InlineData("ImpostorDisconnect", "Disconnect")]
    public void End_reasons_are_classified(string reason, string kind) => Assert.Equal(kind, Outcome.Kind(reason));
}
