using TournamentTracker.Stats;
using Xunit;

namespace TournamentTracker.Tests;

public class ReplayPlaybackTests
{
    private static ReplayPlayback Record()
    {
        var clock = new FakeClock();
        var tracker = new GameTracker(new ScoringRules());
        var game = tracker.Start(1, "Cup", "X", "Polus", Players.Lobby(), clock.Now);
        var rec = new ReplayRecorder(game) { MapId = 2 };
        rec.Outfits[0] = new ReplayOutfit { Hat = "hat_pk05_Cheese", Skin = "skin_Police" };
        for (int f = 0; f <= 100; f++)                                  // 10 seconds
        {
            double t = f * 0.1;
            clock.Now = clock.Now.AddSeconds(0.1);
            bool carlDead = t >= 3;                                       // killed at 3 s, walking right until then
            bool meeting = t >= 6;                                        // everyone at the table from 6 s
            rec.Record(t, Players.Lobby().Select(p => new ReplayPosition(p.PlayerId,
                meeting ? 20 + p.PlayerId : (p.PlayerId == 2 && carlDead ? 3 : (float)t) - (p.PlayerId == 1 ? 2 * (float)t : 0),
                meeting ? 20 : p.PlayerId, p.PlayerId == 2 && carlDead, p.PlayerId == 1 && t > 4 && t < 5, false)));
            if (f == 30) tracker.Kill(0, 2, clock.Now);
            if (f == 60) tracker.MeetingCalled(3, 2, clock.Now);
        }
        var g = tracker.End("ImpostorByKill", Outcome.Impostors, Players.Lobby(), clock.Now)!;
        return ReplayPlayback.Load(rec.Build(g));
    }

    [Fact]
    public void Reads_back_players_outfits_and_the_map()
    {
        var r = Record();
        Assert.Equal(2, r.MapId);
        Assert.Equal(6, r.Players.Count);
        Assert.Equal("hat_pk05_Cheese", r.Players[0].Outfit.Hat);
        Assert.True(r.Players[0].Impostor);
        Assert.Equal(10, r.Duration, 1);
    }

    [Fact]
    public void Positions_are_smoothed_between_frames_but_jump_on_teleports()
    {
        var r = Record();
        Assert.Equal(1.25f, r.At(1.25)[0].X, 2);                  // halfway between frames
        Assert.False(r.At(1.25)[0].FacingLeft);
        Assert.True(r.At(1.25)[1].FacingLeft);                      // Bob walks left
        Assert.True(r.At(4.5)[1].InVent);
        var justBefore = r.At(5.95)[0];
        Assert.InRange(justBefore.X, 5.8f, 6.0f);                   // no sliding towards the meeting table
        Assert.Equal(20f, r.At(6.05)[0].X, 1);
    }

    [Fact]
    public void A_kill_leaves_a_body_until_the_meeting()
    {
        var r = Record();
        var body = Assert.Single(r.Bodies);
        Assert.Equal(2, body.Player);
        Assert.InRange(body.From, 2.95, 3.05);
        Assert.InRange(body.Until, 5.95, 6.15);                  // the meeting event
        Assert.Empty(r.BodiesAt(2));
        Assert.Single(r.BodiesAt(4));
        Assert.Empty(r.BodiesAt(7));
        Assert.True(r.At(4)[2].Dead);
        Assert.True(r.InMeeting(7));
        Assert.Contains(r.EventsUpTo(7, 3), e => e.Kind == "kill");
    }

    [Fact]
    public void The_library_finds_replays_newest_first_and_names_them()
    {
        using var dir = new TempDir();
        var older = Path.Combine(dir.Path, "games", "tt-replay-LJ-2-20261003-190000.json.gz");
        var newer = Path.Combine(dir.Path, "tt-replay-LJ-3-20261003-192144.json.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(older)!);
        File.WriteAllText(older, "x"); File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(newer, "x");
        var found = ReplayLibrary.Find(new[] { dir.Path, Path.Combine(dir.Path, "missing") });
        Assert.Equal(new[] { "LJ-3", "LJ-2" }, found.Select(ReplayLibrary.Label));
    }
}
