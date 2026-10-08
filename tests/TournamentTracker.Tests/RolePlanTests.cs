using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Each player's role decided once, before any is sent (a second one gets the host kicked).</summary>
public class RolePlanTests
{
    private const ushort Crew = 0, Imp = 1, Sci = 2, Eng = 3, Shape = 5;
    private static Dictionary<byte, (ushort, bool)> Chosen(params (byte Id, ushort Role)[] p) =>
        p.ToDictionary(x => x.Id, x => (x.Role, x.Role is Imp or Shape));

    [Fact]
    public void Nothing_to_change_changes_nothing()
    {
        var chosen = Chosen((0, Crew), (1, Imp), (2, Sci), (3, Crew));
        var plan = RolePlan.Plan(chosen, referee: 0, picks: null, new Random(1));
        Assert.Equal(chosen.ToDictionary(k => k.Key, k => k.Value.Item1), plan);
    }

    [Fact]
    public void A_referee_drawn_as_impostor_hands_it_to_someone_else()
    {
        for (int seed = 0; seed < 30; seed++)
        {
            var plan = RolePlan.Plan(Chosen((0, Shape), (1, Imp), (2, Eng), (3, Crew), (4, Crew)), referee: 0, picks: null, new Random(seed));
            Assert.Equal(Crew, plan[0]);
            Assert.Equal(2, plan.Values.Count(r => r is Imp or Shape));               // still two impostors
            Assert.Contains(Shape, plan.Values);                                       // the same impostor roles
            Assert.Contains(Eng, plan.Values);                                         // nobody's crew role is lost
            Assert.Equal(5, plan.Count);                                               // everyone gets exactly one role
        }
    }

    [Fact]
    public void A_referee_with_a_crew_role_is_a_plain_crewmate_and_the_role_moves()
    {
        var plan = RolePlan.Plan(Chosen((0, Eng), (1, Imp), (2, Crew), (3, Sci)), referee: 0, picks: null, new Random(2));
        Assert.Equal(Crew, plan[0]);
        Assert.Equal(Eng, plan[2]);
        Assert.Equal(Sci, plan[3]);
        Assert.Equal(Imp, plan[1]);
    }

    [Fact]
    public void The_rotation_picks_the_impostors_by_swapping_roles()
    {
        var plan = RolePlan.Plan(Chosen((0, Crew), (1, Imp), (2, Shape), (3, Sci), (4, Crew)), referee: 0, picks: new byte[] { 3, 4 }, new Random(3));
        Assert.True(plan[3] is Imp or Shape);
        Assert.True(plan[4] is Imp or Shape);
        Assert.Equal(new[] { Crew, Sci }, new[] { plan[1], plan[2] }.OrderBy(x => x));   // the old impostors take their crew roles
        Assert.Equal(Crew, plan[0]);
        // Picks that don't fit (wrong count, unknown player) leave the game's choice.
        var same = RolePlan.Plan(Chosen((0, Crew), (1, Imp), (2, Crew)), null, new byte[] { 2, 9 }, new Random(3));
        Assert.Equal(Imp, same[1]);
    }

    [Fact]
    public void A_referee_alone_with_no_one_to_swap_with_just_becomes_crew()
    {
        var plan = RolePlan.Plan(Chosen((0, Imp)), referee: 0, picks: null, new Random(4));
        Assert.Equal(Crew, plan[0]);
    }
}
