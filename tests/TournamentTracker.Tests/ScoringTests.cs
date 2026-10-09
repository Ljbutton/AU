using System.Linq;
using TournamentTracker.Stats;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Each rule on the tournament point sheet. Red (0) and Blue (1) are the impostors.</summary>
public class ScoringTests
{
    private readonly FakeClock _clock = new();
    // The sheet's fixed rules; the percentage bonuses, the alive-at-the-loss rounds, being killed or
    // voted out as crew and dying first have their own tests below.
    private readonly GameTracker _t = new(new ScoringRules
    {
        VotingBonus = 0, CorrectVoteBonus = 0, TaskPercentBonus = 0, LowPercentPoints = 0, DiedFirstShareOfCrewAverage = 0,
        GotKilled = 0, EjectedAsCrew = 0, AliveLossPerRound = 0,
    });

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
        Assert.Equal(-2 - 2, Pts(g, 2));          // incorrect vote out, lost
        Assert.Equal(-2, Pts(g, 3));              // ejected crewmate: just the loss
        Assert.Equal(-2, Pts(g, 4));              // voted Red, who stayed in: no points either way
        Assert.Equal(-2 - 2, Pts(g, 5));
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
        Assert.Equal(-2, Pts(g, 2));              // dead, so just the ordinary loss
        Assert.Equal(-2, Pts(g, 3));
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
        Assert.Equal(5 + 1, Pts(g, 4));           // task win + finished every task (the % bonus is off here)
        Assert.Equal(5, Pts(g, 5));               // died first still wins with the team
        Assert.Equal(5, Pts(g, 2));
    }

    [Fact]
    public void Leaving_takes_the_loss_but_never_shares_a_win()
    {
        Start();
        _t.Kill(0, 2, _clock.Now);
        _t.Disconnected(4, _clock.Now);                             // crewmate leaves, crew loses
        _t.Disconnected(1, _clock.Now);                             // impostor leaves, impostors win
        var g = End("ImpostorByKill");
        Assert.Equal(-2, Pts(g, 4));
        Assert.Contains(g.ById(4)!.PointBreakdown, l => l.Rule == "Lost (left the game)");
        Assert.Equal(0, Pts(g, 1));
        Assert.Equal(1 + 1 + 4, Pts(g, 0));
    }

    [Fact]
    public void Leaving_alive_doesnt_dodge_the_sabotage_penalty()
    {
        Start();
        _t.Disconnected(4, _clock.Now);
        var g = End("ImpostorBySabotage");
        Assert.Equal(-5, Pts(g, 4));
    }

    [Fact]
    public void Rules_can_be_changed()
    {
        var rules = SheetRules.Fixed(); rules.Kill = 2; rules.ImpostorKillWin = 10; rules.DiedFirstShareOfCrewAverage = 0;
        var t = new GameTracker(rules);
        t.Start(1, "Cup", "X", "Polus", Players.Lobby(), _clock.Now);
        t.Kill(0, 2, _clock.Now);
        var g = t.End("ImpostorByKill", Outcome.Impostors, Players.Lobby(), _clock.Now)!;
        Assert.Equal(2 + 1 + 10, g.ById(0)!.Points);
        Assert.Equal(-2, g.ById(2)!.Points);
    }

    [Fact]
    public void Voting_and_tasks_are_percentages()
    {
        var t = new GameTracker(new ScoringRules { DiedFirstShareOfCrewAverage = 0 });
        var lobby = Players.Lobby();
        t.Start(1, "Cup", "X", "Polus", lobby, _clock.Now);
        // Nobody is ejected. Green votes Red, Blue (impostors), then Orange (crew).
        t.VotingComplete(new[] { new VoteCast(2, 0), new VoteCast(3, 1) }, null, false, _clock.Now);
        t.VotingComplete(new[] { new VoteCast(2, 1), new VoteCast(3, VoteCast.SkippedVote) }, null, false, _clock.Now);
        t.VotingComplete(new[] { new VoteCast(2, 4) }, null, false, _clock.Now);
        var final = Players.Lobby();
        final[2].TasksCompleted = 3;                    // 75%
        final[3].TasksCompleted = 1;                    // 25%: exactly at the line, the normal share
        var g = t.End("HumansByTask", Outcome.Crewmates, final, _clock.Now)!;

        var green = g.ById(2)!;                         // voted at all 3 meetings, 2 of 3 on impostors
        Assert.Contains(green.PointBreakdown, l => l.Rule == "Voted in 3/3 meetings" && l.Points == 1);
        Assert.Contains(green.PointBreakdown, l => l.Rule == "Votes right 2/3 (67%)" && Math.Abs(l.Points - 4.0 / 3) < 1e-9);
        Assert.Contains(green.PointBreakdown, l => l.Rule == "Tasks 75%" && l.Points == 2.25);
        Assert.Equal(1 + 4.0 / 3 + 2.25 + 5, green.Points, 9);

        var pink = g.ById(3)!;                          // voted once, skipped once: half the voting bonus; 1 of 1 right
        Assert.Contains(pink.PointBreakdown, l => l.Rule == "Voted in 1/2 meetings" && l.Points == 0.5);
        Assert.Contains(pink.PointBreakdown, l => l.Rule == "Votes right 1/1 (100%)" && l.Points == 2);
        Assert.Contains(pink.PointBreakdown, l => l.Rule == "Tasks 25%" && l.Points == 0.75);

        var orange = g.ById(4)!;                        // never at a vote: no voting lines; no tasks: down to -2
        Assert.DoesNotContain(orange.PointBreakdown, l => l.Rule.StartsWith("Voted in") || l.Rule.StartsWith("Votes right"));
        Assert.Contains(orange.PointBreakdown, l => l.Rule == "Tasks 0%" && l.Points == -2);
        Assert.DoesNotContain(g.ById(0)!.PointBreakdown, l => l.Rule.StartsWith("Tasks"));   // impostors don't get them
    }

    [Theory]
    [InlineData(2, 1.0, 2)]
    [InlineData(2, 0.5, 1)]
    [InlineData(2, 0.25, 0.5)]      // at 25%: still the normal share
    [InlineData(2, 0.125, -0.75)]   // halfway down to 0%: halfway from +0.5 to -2
    [InlineData(2, 0, -2)]
    [InlineData(3, 0.25, 0.75)]
    [InlineData(3, 0.125, -0.625)]
    [InlineData(3, 0, -2)]
    public void Below_25_percent_slides_down_to_minus_2(double max, double share, double expected) =>
        Assert.Equal(expected, Scoring.Share(max, share, new ScoringRules()), 9);

    [Fact]
    public void Crew_alive_at_a_loss_lose_a_quarter_per_round_up_to_4()
    {
        var rules = new ScoringRules { VotingBonus = 0, CorrectVoteBonus = 0, TaskPercentBonus = 0, LowPercentPoints = 0, DiedFirstShareOfCrewAverage = 0 };
        double Alive(int meetings, string reason)
        {
            var t = new GameTracker(rules);
            t.Start(1, "Cup", "X", "Polus", Players.Lobby(), _clock.Now);
            for (int m = 0; m < meetings; m++) t.VotingComplete(new[] { new VoteCast(5, VoteCast.SkippedVote) }, null, false, _clock.Now);
            var g = t.End(reason, Outcome.WinnerFromReason(reason), Players.Lobby(), _clock.Now)!;
            return g.ById(5)!.PointBreakdown.Where(l => l.Rule.StartsWith("Alive at the loss")).Sum(l => l.Points);
        }
        Assert.Equal(-0.25, Alive(0, "ImpostorByKill"));      // 1 round
        Assert.Equal(-1, Alive(3, "ImpostorByKill"));         // 4 rounds
        Assert.Equal(-4, Alive(15, "ImpostorByKill"));        // 16 rounds: capped at 4
        Assert.Equal(-4, Alive(30, "ImpostorBySabotage"));    // also on top of a sabotage loss
        Assert.Equal(0, Alive(3, "HumansByTask"));            // only a loss
    }

    [Fact]
    public void Crew_lose_a_quarter_for_being_killed_and_1_for_being_voted_out()
    {
        var t = new GameTracker(new ScoringRules { VotingBonus = 0, CorrectVoteBonus = 0, TaskPercentBonus = 0, LowPercentPoints = 0, DiedFirstShareOfCrewAverage = 0 });
        t.Start(1, "Cup", "X", "Polus", Players.Lobby(), _clock.Now);
        t.Kill(0, 2, _clock.Now);
        t.VotingComplete(new[] { new VoteCast(0, 3), new VoteCast(1, 3) }, 3, false, _clock.Now);   // Pink (crew) voted out
        var final = Players.Lobby();
        final[2].IsDead = true;
        var g = t.End("HumansByTask", Outcome.Crewmates, final, _clock.Now)!;
        Assert.Contains(g.ById(2)!.PointBreakdown, l => l.Rule == "Got killed" && l.Points == -0.25);
        Assert.Contains(g.ById(3)!.PointBreakdown, l => l.Rule == "Voted out" && l.Points == -1);
        Assert.Equal(5 - 0.25, g.ById(2)!.Points);
        Assert.Equal(5 - 1, g.ById(3)!.Points);
    }

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
        var t = new GameTracker(new ScoringRules { VotingBonus = 0, CorrectVoteBonus = 0, TaskPercentBonus = 0, LowPercentPoints = 0, GotKilled = 0, EjectedAsCrew = 0, AliveLossPerRound = 0 });
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
        Assert.Equal(0.9 * average, green.Points, 9);
        Assert.Contains(green.PointBreakdown, l => l.Rule.StartsWith("Died first: 90% of crew average"));
        Assert.Equal(green.Points, green.PointBreakdown.Sum(l => l.Points));
    }

    [Fact]
    public void Points_are_never_rounded_even_when_an_old_code_asks_for_halves()
    {
        var t = new GameTracker(new ScoringRules { BonusRounding = 0.5 });
        t.Start(1, "Cup", "X", "Polus", Players.Lobby(), _clock.Now);
        var final = Players.Lobby();
        final[2].TasksCompleted = 1;                    // 1 of 4 tasks: 3 x 25%
        var g = t.End("HumansByTask", Outcome.Crewmates, final, _clock.Now)!;
        Assert.Contains(g.ById(2)!.PointBreakdown, l => l.Rule == "Tasks 25%" && l.Points == 0.75);
    }

    [Theory]
    [InlineData("HumansByTask", "Tasks")]
    [InlineData("CrewmatesByVote", "Vote")]
    [InlineData("ImpostorByKill", "Kill")]
    [InlineData("ImpostorsBySabotage", "Sabotage")]
    [InlineData("ImpostorDisconnect", "Disconnect")]
    public void End_reasons_are_classified(string reason, string kind) => Assert.Equal(kind, Outcome.Kind(reason));
}
