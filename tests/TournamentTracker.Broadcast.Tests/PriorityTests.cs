using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Broadcast;
using TournamentTracker.Discord;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>The caster's lobby ranking: tiers, fading, offline lobbies, the config file and the simulator.</summary>
public class PriorityTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private PriorityConfig _config = new();
    private readonly LobbyBoard _board;

    public PriorityTests() => _board = new LobbyBoard(() => _config, () => _clock.Now);
    public void Dispose() => _dir.Dispose();

    private static object P(string colour, bool imp = false) => new { id = 1, name = colour, color = 0, colorName = colour, imp };

    private void Send(string lobby, string type, string? kind, object data)
    {
        var msg = JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        msg["type"] = type;
        msg["lobby"] = lobby;
        msg["round"] = 1;
        msg["game"] = lobby + "-1";
        msg["t"] = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds();
        if (kind != null) msg["kind"] = kind;
        _board.Apply(JsonSerializer.Serialize(msg));
    }

    private void Snap(string lobby, string phase = "ingame", int crew = 7, int imps = 2, int tasks = 40, object? sabotage = null, bool danger = false) =>
        Send(lobby, "snap", null, new { phase, crewAlive = crew, impAlive = imps, alive = crew + imps, taskPct = tasks, sabotage, danger, video = true });

    private LobbyRank Rank(string lobby) => _board.Ranking().Single(r => r.Lobby == lobby);

    [Fact]
    public void Quiet_games_are_low_and_the_lobby_is_lower()
    {
        Snap("A");
        Snap("B", phase: "lobby");
        Assert.Equal("low", Rank("A").Tier);
        Assert.Equal(12, Rank("A").Score);
        Assert.Equal(3, Rank("B").Score);
        Assert.Equal("A", _board.Ranking()[0].Lobby);
        Assert.StartsWith("Playing · 7 crew v 2 imp · tasks 40%", Rank("A").Line);
    }

    [Fact]
    public void Each_starting_tier_lands_where_it_should()
    {
        Snap("danger", danger: true);
        Send("danger", "event", "danger", new { state = "start", impostor = P("Purple", true), crewmate = P("Lime"), room = "Electrical" });
        Snap("reactor", sabotage: new { system = "Reactor", critical = true, timeLeft = 12.0 });
        Snap("o2slow", sabotage: new { system = "O2", critical = true, timeLeft = 25.0 });
        Snap("tasks", tasks: 88);
        Snap("final", crew: 2, imps: 1);
        Snap("onekill", crew: 3, imps: 2);
        Snap("meeting", phase: "meeting");
        Snap("close", crew: 4, imps: 2);

        Assert.Equal("veryHigh", Rank("danger").Tier);
        Assert.Equal("Purple alone with Lime in Electrical, kill ready", Rank("danger").Plays[0].Text.Plain());
        Assert.Equal("veryHigh", Rank("reactor").Tier);
        Assert.Equal("Reactor 12s, nobody fixing", Rank("reactor").Plays[0].Text.Plain());
        Assert.Equal("medium", Rank("o2slow").Tier);
        Assert.Equal("high", Rank("tasks").Tier);
        Assert.Equal("high", Rank("final").Tier);       // final 3 and one kill from winning: tops out in high
        Assert.True(Rank("final").Score > Rank("tasks").Score);
        Assert.Equal("high", Rank("onekill").Tier);
        Assert.Equal("high", Rank("meeting").Tier);
        Assert.Equal("medium", Rank("close").Tier);
    }

    [Fact]
    public void Ejections_wins_and_game_ends_must_show()
    {
        Snap("A");
        Send("A", "event", "eject", new { ejected = P("Purple", true), wasImpostor = true, skipped = false, tie = false });
        Assert.Equal("must", Rank("A").Tier);
        Assert.Equal("Purple ejected: Impostor", Rank("A").Plays[0].Text.Plain());

        Snap("B");
        Send("B", "event", "kill", new { killer = P("Red", true), victim = P("Blue"), room = "MedBay", winning = true });
        Assert.Equal("must", Rank("B").Tier);
        Assert.Equal("Red killed Blue in MedBay: impostors win", Rank("B").Plays[0].Text.Plain());

        Send("C", "event", "gameEnd", new { winner = "Crewmates", how = "tasks", abandoned = false });
        Assert.Equal("must", Rank("C").Tier);
        Assert.Equal("Crewmates win on tasks", Rank("C").Plays[0].Text.Plain());
    }

    [Fact]
    public void A_kill_holds_then_fades_back_down()
    {
        Snap("A");
        Send("A", "event", "kill", new { killer = P("Purple", true), victim = P("Lime"), room = "Electrical", winning = false });
        var first = Rank("A");
        Assert.Equal("medium", first.Tier);
        Assert.Equal("Purple killed Lime in Electrical", first.Plays[0].Text.Plain());

        _clock.Advance(5);  Snap("A");
        Assert.Equal(40, Rank("A").Plays.Single(p => p.Rule == "kill").Value);      // held
        _clock.Advance(6);  Snap("A");
        Assert.Equal(20, Rank("A").Plays.Single(p => p.Rule == "kill").Value);      // one half-life
        _clock.Advance(60); Snap("A");
        Assert.DoesNotContain(Rank("A").Plays, p => p.Rule == "kill");              // gone
        Assert.Equal("low", Rank("A").Tier);
    }

    [Fact]
    public void A_state_counts_while_it_lasts_then_fades()
    {
        Snap("A", danger: true);
        _clock.Advance(20); Snap("A", danger: true);
        Assert.Equal(80, Rank("A").Plays[0].Value);
        Snap("A");
        _clock.Advance(6);
        Snap("A");
        Assert.Equal(40, Rank("A").Plays.Single(p => p.Rule == "danger").Value);
    }

    [Fact]
    public void Two_things_at_once_rank_above_one()
    {
        Snap("A", phase: "meeting");
        Snap("B", phase: "meeting", tasks: 90);
        Assert.True(Rank("B").Score > Rank("A").Score);
        Assert.Equal("B", _board.Ranking()[0].Lobby);
    }

    [Fact]
    public void Stacking_never_lifts_a_lobby_into_must_show()
    {
        Snap("A", phase: "ingame", crew: 2, imps: 1, tasks: 90, danger: true, sabotage: new { system = "Reactor", critical = true, timeLeft = 5.0 });
        Assert.Equal("veryHigh", Rank("A").Tier);
        Assert.Equal(89.9, Rank("A").Score);
    }

    [Fact]
    public void Repeats_update_one_play_instead_of_adding_more()
    {
        Snap("A");
        Send("A", "event", "vent", new { player = P("Purple", true), action = "enter", room = "MedBay" });
        _clock.Advance(1);
        Send("A", "event", "vent", new { player = P("Purple", true), action = "enter", room = "Admin" });
        var vent = Rank("A").Plays.Single(p => p.Rule == "vent");
        Assert.Equal(2, vent.Repeats);
        Assert.Equal("Purple vented in Admin", vent.Text.Plain());

        // A sabotage counting down is one play whose text changes.
        Snap("A", sabotage: new { system = "Reactor", critical = true, timeLeft = 14.0 });
        Snap("A", sabotage: new { system = "Reactor", critical = true, timeLeft = 13.0 });
        Assert.Equal("Reactor 13s, nobody fixing", Rank("A").Plays.Single(p => p.Rule == "criticalSabotage").Text.Plain());
    }

    [Fact]
    public void A_lobby_that_stops_sending_goes_offline_and_the_rest_carry_on()
    {
        Snap("A", danger: true);
        Snap("B");
        _clock.Advance(7);
        Snap("B");
        var a = Rank("A");
        Assert.False(a.Online);
        Assert.Equal("offline", a.Tier);
        Assert.Equal(0, a.Score);
        Assert.Equal("B", _board.Ranking()[0].Lobby);
        Snap("A");
        Assert.True(Rank("A").Online);
    }

    [Fact]
    public void Witnessed_plays_are_ready_for_the_vision_overlay()
    {
        Snap("A");
        Send("A", "event", "witnessed_kill", new { witness = P("Lime"), impostor = P("Purple", true), room = "MedBay" });
        Assert.Equal("veryHigh", Rank("A").Tier);
        Assert.Equal("Lime SAW Purple kill in MedBay", Rank("A").Plays[0].Text.Plain());
        Snap("B");
        Send("B", "event", "witnessed_vent", new { witness = P("Lime"), impostor = P("Purple", true), room = "MedBay" });
        Assert.Equal("high", Rank("B").Tier);
        Assert.Equal("Lime SAW Purple vent in MedBay", Rank("B").Plays[0].Text.Plain());
    }

    [Fact]
    public void The_config_file_is_written_read_and_survives_mistakes()
    {
        string path = Path.Combine(_dir.Path, PriorityConfig.FileName);
        var file = new PriorityConfigFile(path);
        Assert.True(File.Exists(path));
        Assert.Contains("\"witnessedKill\"", File.ReadAllText(path));
        Assert.Equal(100, file.Current.Rule("eject").Points);

        // Edited: the change is picked up; rules left out keep their defaults.
        File.WriteAllText(path, """{ "halfLifeSeconds": 3, "rules": { "kill": { "points": 55, "holdSeconds": 2 } } }""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var c = file.Refresh(force: true);
        Assert.Equal(3, c.HalfLifeSeconds);
        Assert.Equal(55, c.Rule("kill").Points);
        Assert.Equal(80, c.Rule("danger").Points);

        // Broken: the last good settings stay, and it says why.
        File.WriteAllText(path, "{ not json");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
        c = file.Refresh(force: true);
        Assert.Equal(55, c.Rule("kill").Points);
        Assert.NotNull(file.Problem);
    }

    [Fact]
    public void A_rule_can_be_switched_off()
    {
        _config.Rules["meeting"].On = false;
        Snap("A", phase: "meeting");
        Assert.Equal(12, Rank("A").Score);   // just "playing"
    }

    [Fact]
    public void The_simulator_runs_four_lobbies_through_whole_games()
    {
        var sim = new FeedSimulator(_clock.Now);
        var kinds = new HashSet<string>();
        var tiers = new HashSet<string>();
        for (int i = 0; i < 600; i++)                // 10 minutes
        {
            _clock.Advance(1);
            foreach (var json in sim.Advance(_clock.Now))
            {
                _board.Apply(json);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("kind", out var k)) kinds.Add(k.GetString()!);
            }
            var ranking = _board.Ranking();
            Assert.Equal(4, ranking.Count);
            Assert.All(ranking, r => Assert.True(r.Online));
            foreach (var r in ranking) tiers.Add(r.Tier);
        }
        foreach (var kind in new[] { "gameStart", "kill", "meeting", "eject", "sabotage", "vent", "danger", "gameEnd" })
            Assert.Contains(kind, kinds);
        foreach (var tier in new[] { "must", "veryHigh", "high", "medium", "low" })
            Assert.Contains(tier, tiers);

        // A lobby whose host drops goes offline; the others keep going.
        sim.SetOffline("MAL", true);
        _clock.Advance(10);
        foreach (var json in sim.Advance(_clock.Now)) _board.Apply(json);
        Assert.False(Rank("MAL").Online);
        Assert.Equal(3, _board.Ranking().Count(r => r.Online));
    }

    [Fact]
    public void Reads_what_the_real_mod_sends()
    {
        var settings = new TrackerSettings { TournamentName = "Cup", LiveStatus = false, ControlPort = -1, Mode = TrackerMode.Tournament };
        using var s = new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));
        var lobby = Players.Lobby();
        s.GameStarted("QWERTY", "Polus", lobby);
        s.FeedTick(new FeedFrame { Phase = VoicePhase.Tasks });
        s.Kill(0, 2, new FeedPlace { X = 1, Y = 1, Room = "Electrical" });
        s.MeetingCalled(3, 2);
        _clock.Advance(1);
        s.FeedTick(new FeedFrame { Phase = VoicePhase.Meeting });

        using var doc = JsonDocument.Parse(s.FeedSince(0));
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray()) _board.Apply(item);
        var rank = _board.Ranking().Single();
        Assert.Equal("high", rank.Tier);               // a meeting with 3 crew v 2: one kill from an impostor win
        Assert.Contains(rank.Plays, p => p.Text.Plain() == "Dana reported Carl's body");
        Assert.Contains(rank.Plays, p => p.Rule == "oneKillFromWin");
        Assert.Contains(rank.Plays, p => p.Text.Plain() == "Alice killed Carl in Electrical");
        Assert.Equal(3, rank.Crew);
    }
}
