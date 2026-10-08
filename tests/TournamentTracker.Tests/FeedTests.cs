using System.Text.Json;
using TournamentTracker.Broadcast;
using TournamentTracker.Discord;
using TournamentTracker.Stats;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>The caster's broadcast feed: events and snapshots from the host's game.</summary>
public class FeedTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly TournamentSession _s;
    private readonly List<PlayerSnapshot> _lobby = Players.Lobby();   // Alice (0) and Bob (1) are the impostors
    private long _read;

    public FeedTests()
    {
        var settings = new TrackerSettings { TournamentName = "Fall Cup", LiveStatus = false, ControlPort = -1, Mode = TrackerMode.Tournament };
        _s = new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));
        _s.RunCommand("r2");
    }

    public void Dispose()
    {
        _s.Dispose();
        _dir.Dispose();
    }

    /// <summary>Everyone standing apart in a line, 10 units from each other, unless moved.</summary>
    private static FeedFrame Frame(VoicePhase phase = VoicePhase.Tasks, Action<Dictionary<byte, FeedPlayer>>? move = null, params FeedSabotage[] sabotages)
    {
        var players = Enumerable.Range(0, 6).ToDictionary(i => (byte)i, i => new FeedPlayer { Id = (byte)i, X = i * 10, Y = 0, Room = "Cafeteria" });
        move?.Invoke(players);
        return new FeedFrame
        {
            Phase = phase,
            Players = players.Values.ToList(),
            Sabotages = sabotages.ToList(),
            Camera = new FeedCamera { X = 0, Y = 0, HalfWidth = 5.33f, HalfHeight = 3 },
            KillCooldown = 25,
            KillDistance = 1.8f,
        };
    }

    /// <summary>Feed items since the last read.</summary>
    private List<JsonElement> Read()
    {
        var doc = JsonDocument.Parse(_s.FeedSince(_read)).RootElement;
        _read = doc.GetProperty("last").GetInt64();
        return doc.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private List<JsonElement> Events(string kind) =>
        Read().Where(e => e.GetProperty("type").GetString() == "event" && e.GetProperty("kind").GetString() == kind).ToList();

    private void Start()
    {
        _s.GameStarted("QWERTY", "The Skeld", _lobby);
        _s.FeedTick(Frame());
    }

    [Fact]
    public void Game_start_lists_the_teams_and_every_message_is_tagged()
    {
        Start();
        var items = Read();
        var start = items.Single(e => e.TryGetProperty("kind", out var k) && k.GetString() == "gameStart");
        Assert.Equal(2, start.GetProperty("round").GetInt32());
        Assert.Equal("The Skeld", start.GetProperty("map").GetString());
        Assert.Equal(2, start.GetProperty("impAlive").GetInt32());
        Assert.Equal(4, start.GetProperty("crewAlive").GetInt32());
        Assert.True(start.GetProperty("players")[0].GetProperty("imp").GetBoolean());
        Assert.Equal(new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds(), start.GetProperty("t").GetInt64());
        Assert.False(string.IsNullOrEmpty(start.GetProperty("game").GetString()));

        var snap = items.Single(e => e.GetProperty("type").GetString() == "snap");
        Assert.Equal("ingame", snap.GetProperty("phase").GetString());
        // Part 22: numbered in order, with this run of the mod's id, so the caster takes each once after a drop.
        var seqs = items.Select(e => e.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(Enumerable.Range((int)seqs[0], seqs.Count).Select(i => (long)i), seqs);
        Assert.Single(items.Select(e => e.GetProperty("src").GetString()).Distinct());
        // Every message carries the broadcast feed's version (docs/broadcast-protocol.md).
        Assert.All(items, e => Assert.Equal(FeedProtocol.Version, FeedProtocol.VersionOf(e)));
        Assert.Equal(6, snap.GetProperty("alive").GetInt32());
        Assert.Equal(0, snap.GetProperty("taskPct").GetInt32());
    }

    [Fact]
    public void A_kill_says_who_where_and_where_on_screen()
    {
        Start();
        Read();
        _clock.Advance(30);
        _s.Kill(0, 2, new FeedPlace { X = 2.665f, Y = -1.5f, Room = "Electrical" });
        var kill = Events("kill").Single();
        Assert.Equal("Alice", kill.GetProperty("killer").GetProperty("name").GetString());
        Assert.Equal("Carl", kill.GetProperty("victim").GetProperty("name").GetString());
        Assert.Equal("Electrical", kill.GetProperty("room").GetString());
        Assert.Equal(30, kill.GetProperty("clock").GetDouble());
        var screen = kill.GetProperty("screen");
        Assert.Equal(0.75, screen.GetProperty("x").GetDouble(), 3);    // three quarters across
        Assert.Equal(0.75, screen.GetProperty("y").GetDouble(), 3);    // three quarters down
        Assert.True(screen.GetProperty("onScreen").GetBoolean());
        Assert.Equal(3, kill.GetProperty("crewAlive").GetInt32());
        Assert.False(kill.GetProperty("winning").GetBoolean());

        // The same death reported twice is one kill.
        _s.Kill(0, 2);
        Assert.Empty(Events("kill"));

        _s.Kill(1, 3, new FeedPlace { X = 50, Y = 0, Room = "LifeSupp" });
        var second = Events("kill").Single();
        Assert.True(second.GetProperty("winning").GetBoolean());     // 2 impostors, 2 crew
        Assert.Equal("O2", second.GetProperty("room").GetString());
        Assert.False(second.GetProperty("screen").GetProperty("onScreen").GetBoolean());
    }

    [Fact]
    public void Meetings_and_ejections_with_the_reveal()
    {
        Start();
        _s.Kill(0, 2);
        Read();
        _s.MeetingCalled(3, 2);
        _s.MeetingCalled(3, 2);          // the second hook for the same meeting
        var meeting = Events("meeting").Single();
        Assert.Equal("Dana", meeting.GetProperty("caller").GetProperty("name").GetString());
        Assert.Equal("Carl", meeting.GetProperty("body").GetProperty("name").GetString());
        Assert.False(meeting.GetProperty("emergency").GetBoolean());

        _s.VotingComplete(new[] { new VoteCast(3, 0), new VoteCast(4, 0), new VoteCast(5, 0) }, 0, false);
        var eject = Events("eject").Single();
        Assert.Equal("Alice", eject.GetProperty("ejected").GetProperty("name").GetString());
        Assert.True(eject.GetProperty("wasImpostor").GetBoolean());
        Assert.Equal(1, eject.GetProperty("impAlive").GetInt32());
        _s.MeetingClosed();

        _s.MeetingCalled(4, null);
        Assert.True(Events("meeting").Single().GetProperty("emergency").GetBoolean());
        _s.VotingComplete(new[] { new VoteCast(3, VoteCast.SkippedVote) }, null, false);
        var skip = Events("eject").Single();
        Assert.True(skip.GetProperty("skipped").GetBoolean());
        Assert.Equal(JsonValueKind.Null, skip.GetProperty("ejected").ValueKind);
    }

    [Fact]
    public void Sabotages_start_and_get_fixed_with_the_time_left()
    {
        Start();
        Read();
        _s.Sabotage(1, "Reactor");
        _s.FeedTick(Frame(sabotages: new FeedSabotage { System = "Reactor", TimeLeft = 30 }));
        var start = Events("sabotage").Single();
        Assert.Equal("start", start.GetProperty("state").GetString());
        Assert.Equal("Reactor", start.GetProperty("system").GetString());
        Assert.Equal(30, start.GetProperty("timeLeft").GetDouble());
        Assert.Equal("Bob", start.GetProperty("by").GetProperty("name").GetString());

        _clock.Advance(18);
        _s.FeedTick(Frame(sabotages: new FeedSabotage { System = "Reactor", TimeLeft = 12 }));
        var snap = Read().Last(e => e.GetProperty("type").GetString() == "snap");
        Assert.Equal(12, snap.GetProperty("sabotage").GetProperty("timeLeft").GetDouble());
        Assert.True(snap.GetProperty("sabotage").GetProperty("critical").GetBoolean());

        _s.FeedTick(Frame(sabotages: new FeedSabotage { System = "Reactor", TimeLeft = 11.8f }));
        _s.FeedTick(Frame());
        var fixedIt = Events("sabotage").Single();
        Assert.Equal("fixed", fixedIt.GetProperty("state").GetString());
        Assert.Equal(11.8, fixedIt.GetProperty("timeLeft").GetDouble(), 1);

        _s.FeedTick(Frame(sabotages: new FeedSabotage { System = "LifeSupp", TimeLeft = 40 }));
        Assert.Equal("O2", Events("sabotage").Single().GetProperty("system").GetString());
    }

    [Fact]
    public void Vents_are_seen_going_in_and_out()
    {
        Start();
        Read();
        _s.FeedTick(Frame(move: p => { p[1].InVent = true; p[1].Room = "UpperEngine"; }));
        var enter = Events("vent").Single();
        Assert.Equal("enter", enter.GetProperty("action").GetString());
        Assert.Equal("Bob", enter.GetProperty("player").GetProperty("name").GetString());
        Assert.Equal("Upper Engine", enter.GetProperty("room").GetString());
        _s.FeedTick(Frame(move: p => p[1].InVent = true));
        Assert.Empty(Events("vent"));
        _s.FeedTick(Frame());
        Assert.Equal("exit", Events("vent").Single().GetProperty("action").GetString());
    }

    [Fact]
    public void Kill_ready_follows_the_cooldown_from_start_kills_and_meetings()
    {
        Start();
        Read();
        _clock.Advance(9);
        _s.FeedTick(Frame());
        Assert.Empty(Events("killReady"));
        _clock.Advance(1.5);
        _s.FeedTick(Frame());
        var ready = Events("killReady");
        Assert.Equal(2, ready.Count);                         // both impostors, 10 s in
        Assert.True(ready[0].GetProperty("estimated").GetBoolean());

        _s.Kill(0, 2);
        _clock.Advance(24);
        _s.FeedTick(Frame());
        Assert.Empty(Events("killReady"));                   // Alice: 25 s after her kill
        _clock.Advance(1.5);
        _s.FeedTick(Frame());
        Assert.Equal("Alice", Events("killReady").Single().GetProperty("impostor").GetProperty("name").GetString());

        _s.MeetingCalled(3, 2);
        _s.VotingComplete(Array.Empty<VoteCast>(), null, false);
        _s.MeetingClosed();
        _clock.Advance(20);
        _s.FeedTick(Frame());
        Assert.Empty(Events("killReady"));                   // everyone's reset after a meeting
        _clock.Advance(6);
        _s.FeedTick(Frame());
        Assert.Equal(2, Events("killReady").Count);
    }

    [Fact]
    public void Danger_is_an_impostor_with_kill_ready_next_to_a_lone_crewmate()
    {
        Start();
        _clock.Advance(11);                                  // kill ready
        _s.FeedTick(Frame());
        Read();

        // Alice right next to Carl, nobody else near: danger.
        _s.FeedTick(Frame(move: p => { p[0].X = 20; p[0].Y = 1; p[2].Room = "Electrical"; }));
        var start = Events("danger").Single();
        Assert.Equal("start", start.GetProperty("state").GetString());
        Assert.Equal("Alice", start.GetProperty("impostor").GetProperty("name").GetString());
        Assert.Equal("Carl", start.GetProperty("crewmate").GetProperty("name").GetString());
        Assert.Equal("Electrical", start.GetProperty("room").GetString());

        // Still there: no repeat. The snapshot says so.
        _clock.Advance(1);
        _s.FeedTick(Frame(move: p => { p[0].X = 20; p[0].Y = 1; }));
        var items = Read();
        Assert.DoesNotContain(items, e => e.GetProperty("type").GetString() == "event");
        Assert.True(items.Single(e => e.GetProperty("type").GetString() == "snap").GetProperty("danger").GetBoolean());

        // Walked off: over once it's been gone a moment.
        _s.FeedTick(Frame());
        Assert.Empty(Events("danger"));
        _clock.Advance(2);
        _s.FeedTick(Frame());
        Assert.Equal("end", Events("danger").Single().GetProperty("state").GetString());

        // Someone else close by: not alone, so no danger.
        _s.FeedTick(Frame(move: p => { p[0].X = 20; p[0].Y = 1; p[3].X = 22; }));
        Assert.Empty(Events("danger"));
    }

    [Fact]
    public void No_danger_before_the_kill_is_ready()
    {
        Start();
        Read();
        _s.FeedTick(Frame(move: p => { p[0].X = 20; p[0].Y = 1; }));
        Assert.Empty(Events("danger"));
    }

    [Fact]
    public void Task_bar_and_game_end()
    {
        Start();
        Read();
        _s.TaskCompleted(2);                                 // 1 of 16
        Assert.Equal(6, Events("tasks").Single().GetProperty("pct").GetInt32());
        _s.TaskCompleted(0);                                 // impostors' fake tasks don't count
        Assert.Empty(Events("tasks"));

        _clock.Advance(100);
        _s.GameEnded("HumansByTask", _lobby);
        var end = Events("gameEnd").Single();
        Assert.Equal("Crewmates", end.GetProperty("winner").GetString());
        Assert.Equal("tasks", end.GetProperty("how").GetString());
        Assert.Equal(2, end.GetProperty("impostors").GetArrayLength());
        Assert.Equal(100, end.GetProperty("clock").GetDouble());
    }

    [Fact]
    public void Snapshots_come_once_a_second_in_the_lobby_too()
    {
        _s.FeedTick(Frame(VoicePhase.Lobby));
        _clock.Advance(0.4);
        _s.FeedTick(Frame(VoicePhase.Lobby));
        _clock.Advance(0.7);
        _s.FeedTick(Frame(VoicePhase.Lobby));
        var snaps = Read();
        Assert.Equal(2, snaps.Count);
        Assert.All(snaps, s => Assert.Equal("lobby", s.GetProperty("phase").GetString()));
        Assert.Equal(JsonValueKind.Null, snaps[0].GetProperty("alive").ValueKind);
    }

    [Fact]
    public void Screen_positions_are_tracked_for_replays()
    {
        Start();
        Read();
        for (int i = 0; i < 6; i++)
        {
            _clock.Advance(0.2);
            _s.FeedTick(Frame(move: p => { p[0].X = 0; p[0].Y = 0; p[2].X = 2.665f; p[2].Y = -1.5f; p[1].InVent = true; }));
        }
        var track = Read().Where(e => e.GetProperty("type").GetString() == "track").ToList();
        Assert.NotEmpty(track);
        var samples = track.SelectMany(t => t.GetProperty("samples").EnumerateArray()).ToList();
        Assert.True(samples.Count >= 5);
        var last = samples.Last().GetProperty("p").EnumerateArray().ToDictionary(p => p[0].GetInt32(), p => p);
        Assert.Equal(500, last[0][1].GetInt32());              // centre of the screen
        Assert.Equal(750, last[2][1].GetInt32());
        Assert.Equal(750, last[2][2].GetInt32());
        Assert.Equal(1, last[1][3].GetInt32());                // Bob in a vent
        _s.Kill(0, 3);
        _clock.Advance(1.2);
        _s.FeedTick(Frame());
        var after = Read().Where(e => e.GetProperty("type").GetString() == "track").SelectMany(t => t.GetProperty("samples").EnumerateArray()).Last();
        Assert.DoesNotContain(after.GetProperty("p").EnumerateArray(), p => p[0].GetInt32() == 3);   // the dead aren't tracked
    }

    [Fact]
    public void With_the_JSON_made_in_the_background_the_feed_is_the_same_and_in_order()
    {
        _s.Work.Start();
        Start();
        _s.Kill(0, 3);
        _clock.Advance(1.1);
        _s.FeedTick(Frame());
        Assert.True(_s.Work.Flush(TimeSpan.FromSeconds(5)));
        var items = Read();
        Assert.Contains(items, e => e.GetProperty("type").GetString() == "snap");
        Assert.Contains(items, e => e.GetProperty("type").GetString() == "event" && e.GetProperty("kind").GetString() == "kill");
        var seqs = items.Select(e => e.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(Enumerable.Range(1, seqs.Count).Select(i => (long)i), seqs);       // numbered in the order made
    }

    [Fact]
    public void Spectator_view_settings_switch_from_commands_and_show_in_the_snapshot()
    {
        Start();
        Assert.True(_s.Spectator.Lit);
        Assert.Equal("focus", _s.Spectator.Vision);
        _s.RunCommand("spec vision rings");
        _s.RunCommand("spec lit off");
        _s.RunCommand("spec eye");                        // no value: switches
        _s.RunCommand("spec focus 3");
        Assert.Equal("rings", _s.Spectator.Vision);
        Assert.False(_s.Spectator.Lit);
        Assert.False(_s.Spectator.Eye);
        Assert.Equal((byte)3, _s.FocusPlayer());
        _clock.Advance(1.1);
        _s.FeedTick(Frame());
        var spec = Read().Last(e => e.GetProperty("type").GetString() == "snap").GetProperty("spec");
        Assert.Equal("rings", spec.GetProperty("vision").GetString());
        Assert.Equal(3, spec.GetProperty("focusing").GetInt32());

        // Saved: a restarted session keeps them.
        var again = new TournamentSession(new TrackerSettings { TournamentName = "Fall Cup", LiveStatus = false, ControlPort = -1, Mode = TrackerMode.Tournament }, _dir.Path, NullLog.Instance, new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));
        Assert.Equal("rings", again.Spectator.Vision);
        again.Dispose();
    }

    [Fact]
    public void The_focus_picks_a_crewmate_in_danger_then_the_most_active_one()
    {
        Start();
        Assert.Equal((byte)2, _s.FocusPlayer());              // the first crewmate alive
        _s.TaskCompleted(4);
        Assert.Equal((byte)4, _s.FocusPlayer());              // Eve just did a task
        _clock.Advance(11);
        _s.FeedTick(Frame(move: p => { p[0].X = 50; p[0].Y = 1; }));   // Alice next to Finn (5), alone
        Assert.Equal((byte)5, _s.FocusPlayer());
        _s.RunCommand("spec focus 3");
        Assert.Equal((byte)3, _s.FocusPlayer());              // picked from the caster tab
        _s.Kill(0, 3);
        Assert.NotEqual((byte)3, _s.FocusPlayer());           // not once they're dead
    }

    [Fact]
    public void The_player_camera_is_off_until_turned_on_then_follows_the_pick_or_the_hunt()
    {
        Start();
        Assert.Null(_s.CamPlayer());                           // off by default: nothing drawn
        _s.RunCommand("spec cam auto");
        Assert.True(_s.Spectator.Cam);
        Assert.Equal((byte)2, _s.CamPlayer());                 // nothing going on: the first crewmate alive
        _clock.Advance(11);
        _s.FeedTick(Frame(move: p => { p[0].X = 50; p[0].Y = 1; }));   // Alice closing in on Finn, alone
        Assert.Equal((byte)0, _s.CamPlayer());                 // the hunting impostor
        _s.RunCommand("spec cam 4");
        Assert.Equal((byte)4, _s.CamPlayer());                 // the caster's pick wins
        _s.Kill(1, 4);
        Assert.Equal((byte)4, _s.CamPlayer());                 // kept a moment after they die, to see it
        _clock.Advance(5);
        Assert.NotEqual((byte)4, _s.CamPlayer());
        _s.RunCommand("spec cam off");
        Assert.Null(_s.CamPlayer());
        var spec = Read().Last(e => e.GetProperty("type").GetString() == "snap").GetProperty("spec");
        Assert.True(spec.TryGetProperty("cam", out _));
    }

    [Fact]
    public void Seeing_a_kill_or_a_vent_tells_the_caster_once()
    {
        Start();
        Read();
        _s.Witnessed("kill", 3, 0, new FeedPlace { X = 1, Y = 2, Room = "MedBay" });
        _s.Witnessed("kill", 3, 0, null);                       // the same sighting again
        _s.Witnessed("vent", 4, 1, new FeedPlace { X = 1, Y = 2, Room = "LifeSupp" });
        _s.Witnessed("vent", 0, 1, null);                       // an impostor "witnessing" doesn't count
        var items = Read().Where(e => e.GetProperty("type").GetString() == "event").ToList();
        Assert.Equal(2, items.Count);
        var kill = items[0];
        Assert.Equal("witnessed_kill", kill.GetProperty("kind").GetString());
        Assert.Equal("Dana", kill.GetProperty("witness").GetProperty("name").GetString());
        Assert.Equal("Alice", kill.GetProperty("impostor").GetProperty("name").GetString());
        Assert.Equal("MedBay", kill.GetProperty("room").GetString());
        Assert.Equal("witnessed_vent", items[1].GetProperty("kind").GetString());
        Assert.Equal("O2", items[1].GetProperty("room").GetString());
    }

    [Fact]
    public void Screen_points_are_measured_from_the_top_left_of_the_view()
    {
        var cam = new FeedCamera { X = 10, Y = 5, HalfWidth = 4, HalfHeight = 2 };
        Assert.Equal((0.0, 0.0, true), TournamentSession.ScreenPoint(cam, 6, 7));
        Assert.Equal((1.0, 1.0, true), TournamentSession.ScreenPoint(cam, 14, 3));
        Assert.Equal((0.5, 0.5, true), TournamentSession.ScreenPoint(cam, 10, 5));
        Assert.False(TournamentSession.ScreenPoint(cam, 20, 5)!.Value.OnScreen);
        Assert.Null(TournamentSession.ScreenPoint(null, 0, 0));
    }

    [Fact]
    public void Room_names_read_like_people_say_them()
    {
        Assert.Equal("Upper Engine", TournamentSession.RoomName("UpperEngine"));
        Assert.Equal("O2", TournamentSession.RoomName("LifeSupp"));
        Assert.Equal("Navigation", TournamentSession.RoomName("Nav"));
        Assert.Null(TournamentSession.RoomName(null));
    }
}
