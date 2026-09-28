using TournamentTracker.Stats;
using Xunit;

namespace TournamentTracker.Tests;

public class LobbyPlanTests
{
    /// <summary>Round 1: lobbies "H0".."Hn", each with 10 players scoring 10, 9, … 1 (plus the lobby number / 10 to keep seeds apart).</summary>
    internal static List<GameRecord> RoundOne(int lobbies)
    {
        var games = new List<GameRecord>();
        for (int l = 0; l < lobbies; l++)
            games.Add(new GameRecord
            {
                Host = "H" + l, Round = 1, Winner = Outcome.Crewmates, EndReason = "HumansByVote", Id = "g" + l,
                Players = Enumerable.Range(0, 10).Select(i => new GamePlayer
                {
                    Key = $"h{l}p{i}", Name = $"H{l}P{i}", Points = 10 - i + l / 10.0, DiscordId = i == 0 ? "10" + l : null,
                }).ToList(),
            });
        return games;
    }

    [Fact]
    public void Movers_are_snake_seeded_into_lobbies_of_ten()
    {
        var plan = LobbyPlan.Build(RoundOne(4), 2, advance: 5, gamesPerRound: 1);   // 20 move on: 2 lobbies
        Assert.Equal(new[] { "Lobby A", "Lobby B" }, plan.Lobbies.Select(l => l.Name));
        Assert.All(plan.Lobbies, l => Assert.Equal(10, l.Players.Count));
        // Seeds by points: H3P0 (10.3), H2P0, H1P0, H0P0, H3P1…; snake: A gets 1st, 4th, 5th, 8th…
        Assert.Equal(new[] { "H3P0", "H0P0", "H3P1", "H0P1" }, plan.Lobbies[0].Players.Take(4).Select(p => p.Name));
        Assert.Equal(new[] { "H2P0", "H1P0", "H2P1", "H1P1" }, plan.Lobbies[1].Players.Take(4).Select(p => p.Name));
        Assert.Equal("H3P5", plan.Alternates[0].Name);             // best who didn't move on
        Assert.Equal("103", plan.Lobbies[0].Players[0].DiscordId);
    }

    [Fact]
    public void Ten_or_fewer_make_the_final()
    {
        var plan = LobbyPlan.Build(RoundOne(2), 2, 5, 1);
        Assert.Equal("Final", Assert.Single(plan.Lobbies).Name);
    }

    [Fact]
    public void The_organiser_can_move_swap_drop_add_and_set_hosts()
    {
        var plan = LobbyPlan.Build(RoundOne(4), 2, 5, 1);
        Assert.Equal("Moved H3P0 to Lobby B.", plan.Apply("move", new[] { "H3P0", "B" }));
        Assert.Equal(11, plan.Lobbies[1].Players.Count);
        Assert.StartsWith("Swapped H3P0 (Lobby A) and H0P0 (Lobby B)", plan.Apply("swap", new[] { "H3P0", "H0P0" }));

        string dropped = plan.Apply("drop", new[] { "H2P0" });
        Assert.StartsWith("Dropped H2P0 from Lobby B. Next up: H3P5 (5.3, H3)", dropped);
        Assert.Equal("Added H3P5 to Lobby B.", plan.Apply("add", new[] { "H3P5", "B" }));
        Assert.DoesNotContain(plan.Alternates, p => p.Name == "H3P5");
        Assert.Equal("Added Newbie to Lobby A (not in any earlier round).", plan.Apply("add", new[] { "Newbie", "A" }));

        Assert.Equal("Lobby A is hosted by LJ.", plan.Apply("host", new[] { "A", "LJ" }));
        Assert.Same(plan.Lobbies[0], plan.FindLobby("LJ"));
        Assert.Equal("No lobby called Z.", plan.Apply("move", new[] { "H3P1", "Z" }));
        Assert.StartsWith("Couldn't find", plan.Apply("drop", new[] { "Nobody" }));
    }
}
