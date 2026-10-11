using TournamentTracker.Stats;
using Xunit;

public class VoteTallyTests
{
    private static VoteCast V(byte from, byte to) => new VoteCast(from, to);

    [Fact]
    public void Votes_for_the_referee_count_as_skips()
    {
        // Referee is 9: three votes for them become skips, beating two for player 2.
        var votes = new[] { V(0, 9), V(1, 9), V(3, 9), V(4, 2), V(5, 2), V(9, VoteCast.DeadVote) };
        Assert.True(VoteTally.AnyFor(votes, 9));
        Assert.Equal(((byte?)null, false), VoteTally.Result(votes, 9));
    }

    [Fact]
    public void The_most_votes_still_goes_and_a_tie_sends_nobody()
    {
        Assert.Equal(((byte?)2, false), VoteTally.Result(new[] { V(0, 9), V(1, 2), V(3, 2), V(4, VoteCast.MissedVote) }, 9));
        Assert.Equal(((byte?)null, true), VoteTally.Result(new[] { V(0, 1), V(1, 2) }, 9));
        Assert.Equal(((byte?)null, false), VoteTally.Result(new[] { V(0, VoteCast.HasNotVoted) }, 9));
    }
}
