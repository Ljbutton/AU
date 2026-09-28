using TournamentTracker.Stats;
using Xunit;

namespace TournamentTracker.Tests;

public class TiebreakTests
{
    private static GamePlayer P(string name, double points, bool imp = false, bool won = false, int right = 0, int wrong = 0, int tasks = 0) => new()
    {
        Key = name.ToLower(), Name = name, Points = points, IsImpostor = imp, Won = won,
        CorrectVotes = right, IncorrectVotes = wrong, TasksTotal = imp ? 0 : 10, TasksCompleted = tasks,
    };

    private static GameRecord G(int round, params GamePlayer[] players) => new()
    {
        Host = "LJ", Round = round, Winner = Outcome.Crewmates, EndReason = "HumansByVote", Players = players.ToList(), Id = Guid.NewGuid().ToString(),
    };

    [Fact]
    public void A_tie_at_the_cut_goes_to_impostor_wins_and_the_winner_quietly_gets_a_quarter_point()
    {
        var games = new[] { G(1, P("Ann", 5), P("Bo", 3, imp: true, won: true), P("Cy", 3, right: 3), P("Di", 1)) };
        var rows = Standings.Lobby(games, "LJ", 1, advance: 2, gamesPerRound: 1);

        Assert.Equal(new[] { "Ann", "Bo", "Cy", "Di" }, rows.Select(r => r.Stats.Name));
        Assert.Equal(3.25, rows[1].Stats.Points);
        Assert.Equal(3.25, rows[1].Total);
        Assert.True(rows[1].Advancing);
        Assert.False(rows[2].Advancing);

        var tie = Assert.Single(Standings.Tiebreaks(games, 2, 1));
        Assert.Equal("impostor wins", tie.DecidedBy);
        Assert.Equal(new[] { "Bo" }, tie.Bumped);
        Assert.Contains("not shown publicly", tie.Describe());
    }

    [Fact]
    public void Level_on_impostor_wins_goes_to_vote_percent_then_task_percent()
    {
        var byVotes = new[] { G(1, P("Ann", 5), P("Bo", 3, right: 1, wrong: 1), P("Cy", 3, right: 2, wrong: 0)) };
        Assert.Equal("vote %", Standings.Tiebreaks(byVotes, 2, 1).Single().DecidedBy);
        Assert.Equal("Cy", Standings.Lobby(byVotes, "LJ", 1, 2, 1)[1].Stats.Name);

        var byTasks = new[] { G(1, P("Ann", 5), P("Bo", 3, right: 1, tasks: 9), P("Cy", 3, right: 1, tasks: 4)) };
        Assert.Equal("task %", Standings.Tiebreaks(byTasks, 2, 1).Single().DecidedBy);
        Assert.Equal("Bo", Standings.Lobby(byTasks, "LJ", 1, 2, 1)[1].Stats.Name);
    }

    [Fact]
    public void Nothing_is_bumped_until_the_round_is_played_or_when_they_are_level_on_everything()
    {
        var games = new[] { G(1, P("Ann", 5), P("Bo", 3, imp: true, won: true), P("Cy", 3)) };
        Assert.Empty(Standings.Tiebreaks(games, 2, gamesPerRound: 3));      // 1 of 3 games played
        Assert.Equal(3, Standings.Lobby(games, "LJ", 1, 2, 3)[1].Stats.Points);

        var level = new[] { G(1, P("Ann", 5), P("Bo", 3, right: 1, tasks: 5), P("Cy", 3, right: 1, tasks: 5)) };
        var tie = Standings.Tiebreaks(level, 2, 1).Single();
        Assert.False(tie.Settled);
        Assert.Contains("A referee needs to decide", tie.Describe());
        Assert.All(Standings.Lobby(level, "LJ", 1, 2, 1), r => Assert.Equal(r.Stats.Points, Math.Floor(r.Stats.Points)));
    }

    [Fact]
    public void The_bump_stays_in_the_running_total_in_later_rounds()
    {
        var games = new[]
        {
            G(1, P("Ann", 5), P("Bo", 3, imp: true, won: true), P("Cy", 3)),
            G(2, P("Ann", 1), P("Bo", 1), P("Cy", 1)),
        };
        var round2 = Standings.Lobby(games, "LJ", 2, 2, 1);
        Assert.Equal(4.25, round2.Single(r => r.Stats.Name == "Bo").Total);
        Assert.Equal(1, round2.Single(r => r.Stats.Name == "Bo").Stats.Points);
    }
}
