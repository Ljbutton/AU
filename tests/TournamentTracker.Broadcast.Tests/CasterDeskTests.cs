using System.Text.Json;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>The caster tab: cards, history, what's on stream and the SHOWN badge.</summary>
public class CasterDeskTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly CasterDesk _desk;
    private readonly List<OnAir> _switched = new();

    public CasterDeskTests()
    {
        _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        _desk.Switch = air => _switched.Add(air);
    }

    public void Dispose() => _desk.Dispose();

    private static object P(string colour, bool imp = false) => new { id = 1, name = colour, color = 0, colorName = colour, imp };

    private void Send(string lobby, string type, string? kind, object data)
    {
        var msg = JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        msg["type"] = type; msg["lobby"] = lobby; msg["round"] = 1; msg["game"] = lobby + "-1";
        msg["t"] = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds();
        if (kind != null) msg["kind"] = kind;
        _desk.Apply(JsonSerializer.Serialize(msg));
    }

    private void Snap(string lobby, string phase = "ingame", int crew = 7, int imps = 2, int tasks = 40, object? sabotage = null, bool danger = false) =>
        Send(lobby, "snap", null, new { phase, crewAlive = crew, impAlive = imps, alive = crew + imps, taskPct = tasks, sabotage, danger });

    private JsonElement State() => JsonSerializer.SerializeToElement(_desk.State(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    private List<JsonElement> Cards() => State().GetProperty("cards").EnumerateArray().ToList();
    private List<JsonElement> History() => State().GetProperty("history").EnumerateArray().ToList();

    [Fact]
    public void Plays_become_cards_with_the_lobby_counts_highest_first()
    {
        Snap("A"); Snap("B");
        Send("A", "event", "kill", new { killer = P("Purple", true), victim = P("Lime"), room = "Electrical", winning = false });
        Snap("B", danger: true);
        var cards = Cards();
        Assert.Equal(2, cards.Count);
        Assert.Equal("B", cards[0].GetProperty("lobby").GetString());          // very high first
        Assert.Equal("veryHigh", cards[0].GetProperty("tier").GetString());
        var kill = cards[1];
        Assert.Equal("Purple killed Lime in Electrical", kill.GetProperty("text").GetString()!.Plain());
        Assert.Equal(7, kill.GetProperty("crew").GetInt32());
        Assert.Equal(2, kill.GetProperty("imps").GetInt32());
        Assert.Equal(40, kill.GetProperty("taskPct").GetInt32());
        // "Playing" and "in the lobby" never make cards.
        Assert.DoesNotContain(cards, c => c.GetProperty("rule").GetString() is "inGame" or "lobby");
    }

    [Fact]
    public void Repeats_update_one_card()
    {
        Snap("A", sabotage: new { system = "Reactor", critical = true, timeLeft = 14.0 });
        _clock.Advance(1);
        Snap("A", sabotage: new { system = "Reactor", critical = true, timeLeft = 13.0 });
        _clock.Advance(1);
        Snap("A", sabotage: new { system = "Reactor", critical = true, timeLeft = 12.0 });
        var card = Cards().Single();
        Assert.Equal("Reactor 12s, nobody fixing", card.GetProperty("text").GetString()!.Plain());
        Assert.Equal(2, card.GetProperty("ago").GetInt32());

        Send("A", "event", "vent", new { player = P("Purple", true), action = "enter", room = "MedBay" });
        Send("A", "event", "vent", new { player = P("Purple", true), action = "enter", room = "Admin" });
        Assert.Equal(2, Cards().Single(c => c.GetProperty("rule").GetString() == "vent").GetProperty("repeats").GetInt32());
    }

    [Fact]
    public void Calmed_down_cards_move_to_the_history_and_dismissed_ones_too()
    {
        Snap("A");
        Send("A", "event", "kill", new { killer = P("Purple", true), victim = P("Lime"), room = "Electrical", winning = false });
        Send("A", "event", "vent", new { player = P("Purple", true), action = "enter", room = "MedBay" });
        Assert.Equal(2, Cards().Count);
        var vent = Cards().Single(c => c.GetProperty("rule").GetString() == "vent").GetProperty("id").GetString()!;
        _desk.Dismiss(vent);
        Assert.Single(Cards());
        Assert.Equal(vent, History().Single().GetProperty("id").GetString());

        _clock.Advance(60); Snap("A");
        Assert.Empty(Cards());
        Assert.Equal("Purple killed Lime in Electrical", History()[0].GetProperty("text").GetString()!.Plain());
    }

    [Fact]
    public void On_air_labels_say_how_each_lobby_is_on()
    {
        foreach (var l in new[] { "A", "B", "C", "D", "E" }) Snap(l);
        Snap("C", phase: "meeting");
        Snap("D", danger: true);

        var air = _desk.Show("A");
        Assert.Equal("full", air.Layout);
        Assert.Equal("LIVE (full)", air.Label("A"));
        Assert.Null(air.Label("B"));
        Assert.Equal("A", _switched.Last().Slots[0]);

        // Quad with A in slot 2: the other slots fill with the top scorers (D danger, C meeting, then the rest).
        air = _desk.Show("A", "4up", 2);
        Assert.Equal(new[] { "D", "A", "C", "B" }, air.Slots);
        Assert.Equal("LIVE (quad, slot 2)", air.Label("A"));
        Assert.Equal("LIVE (quad, slot 1)", air.Label("D"));

        // Putting E in slot 4 keeps the others where they are.
        air = _desk.Show("E", "4up", 4);
        Assert.Equal(new[] { "D", "A", "C", "E" }, air.Slots);

        air = _desk.Show("B", "2up", 1);
        Assert.Equal("LIVE (2-up, slot 1)", air.Label("B"));
        Assert.Equal(2, air.Slots.Count);

        var lobbies = State().GetProperty("lobbies").EnumerateArray().ToList();
        Assert.Equal("LIVE (2-up, slot 1)", lobbies.Single(l => l.GetProperty("lobby").GetString() == "B").GetProperty("onAir").GetString());
    }

    [Fact]
    public void Plays_while_on_stream_are_marked_shown()
    {
        Snap("A"); Snap("B");
        _desk.Show("A");
        _clock.Advance(3);
        Send("A", "event", "kill", new { killer = P("Purple", true), victim = P("Lime"), room = "Electrical", winning = false });
        Send("B", "event", "kill", new { killer = P("Red", true), victim = P("Blue"), room = "Admin", winning = false });
        var cards = Cards();
        var a = cards.Single(c => c.GetProperty("lobby").GetString() == "A");
        Assert.Equal(_clock.Now.ToString("o"), a.GetProperty("shown").GetString());
        Assert.Equal("LIVE (full)", a.GetProperty("shownHow").GetString());
        Assert.Equal("LIVE (full)", a.GetProperty("onAir").GetString());
        var b = cards.Single(c => c.GetProperty("lobby").GetString() == "B");
        Assert.Equal(JsonValueKind.Null, b.GetProperty("shown").ValueKind);

        // Switching to B: its live card counts as shown from then.
        _clock.Advance(2);
        _desk.Show("B");
        b = Cards().Single(c => c.GetProperty("lobby").GetString() == "B");
        Assert.Equal(_clock.Now.ToString("o"), b.GetProperty("shown").GetString());
        Assert.Equal(JsonValueKind.Null, Cards().Single(c => c.GetProperty("lobby").GetString() == "A").GetProperty("onAir").ValueKind);
    }

    [Fact]
    public void A_switch_in_OBS_keeps_on_air_right()
    {
        Snap("A"); Snap("B");
        _desk.ObsChanged(new OnAir { Layout = "2up", Slots = new List<string?> { "B", "A" } });
        var air = State().GetProperty("onAir");
        Assert.Equal("obs", air.GetProperty("by").GetString());
        Assert.Equal("LIVE (2-up, slot 2)", _desk.OnAir.Label("A"));
    }

    [Fact]
    public void Offline_lobbies_show_on_their_cards_and_rows()
    {
        Snap("A", danger: true); Snap("B");
        _clock.Advance(7);
        Snap("B");
        var a = State().GetProperty("lobbies").EnumerateArray().Single(l => l.GetProperty("lobby").GetString() == "A");
        Assert.False(a.GetProperty("online").GetBoolean());
        Assert.Equal("Offline: no data from this lobby", a.GetProperty("line").GetString());
    }

    [Fact]
    public void Simulation_feeds_four_lobbies_and_makes_cards()
    {
        var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        desk.Simulate(true);
        try
        {
            for (int i = 0; i < 120; i++) { _clock.Advance(1); desk.SimTick(); }
            var state = JsonSerializer.SerializeToElement(desk.State(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.True(state.GetProperty("simulating").GetBoolean());
            Assert.Equal(4, state.GetProperty("lobbies").GetArrayLength());
            Assert.True(state.GetProperty("cards").GetArrayLength() + state.GetProperty("history").GetArrayLength() > 0);
            Assert.All(state.GetProperty("lobbies").EnumerateArray(), l => Assert.StartsWith("SIM-", l.GetProperty("lobby").GetString()));
        }
        finally { desk.Dispose(); }
    }

    [Fact]
    public void Stopping_simulation_leaves_nothing_fake_behind()
    {
        var dir = Directory.CreateTempSubdirectory("ra-sim-").FullName;
        var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), dataFolder: dir);
        var switched = new List<OnAir>();
        desk.Switch = air => switched.Add(air);
        Func<string, bool>? stopped = null;
        desk.SimStopped += w => stopped = w;
        try
        {
            // A real lobby is on too, and stays.
            var real = JsonSerializer.Serialize(new { type = "snap", lobby = "LJ", round = 1, phase = "ingame", crewAlive = 7, impAlive = 2, taskPct = 10, t = 1 });
            desk.Simulate(true);
            for (int i = 0; i < 900; i++) { _clock.Advance(1); desk.SimTick(); if (i % 5 == 0) desk.Apply(real.Replace("\"t\":1", $"\"t\":{i + 2}")); if (i % 5 == 0) desk.Tick(); }
            desk.Show("SIM-1", "2up", null, new List<string> { "SIM-1", "LJ" });
            desk.Sponsors.Begin("grid:1", new Sponsor { Name = "S", Placements = { "grid" } }, "grid");
            Assert.NotEmpty(desk.Archive.Games);
            Assert.True(Directory.GetFiles(Path.Combine(dir, "broadcast-games")).Length > 0);
            Assert.NotEmpty(desk.Health.Lobbies.Where(CasterDesk.IsSimLobby));
            Assert.NotEmpty(desk.Games);
            int switches = switched.Count;

            desk.Simulate(false);

            var state = JsonSerializer.SerializeToElement(desk.State(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            string all = state.GetRawText() + JsonSerializer.Serialize(desk.StoryState()) + JsonSerializer.Serialize(desk.VoiceState()) + JsonSerializer.Serialize(desk.HealthState());
            Assert.DoesNotContain("SIM-", all);
            Assert.False(desk.Simulating);
            Assert.Equal(new[] { "LJ" }, state.GetProperty("lobbies").EnumerateArray().Select(l => l.GetProperty("lobby").GetString()));
            Assert.Empty(desk.Archive.Games.Where(g => CasterDesk.IsSimLobby(g.Lobby)));
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "broadcast-games")));
            Assert.Empty(desk.Games);
            Assert.Empty(desk.Roster.Extra);
            Assert.Empty(desk.Alerts.Active());
            Assert.Null(desk.TwitchOf("SIM-1"));
            // What was on stream: the real lobby stays in its slot, nothing is switched in OBS.
            Assert.Equal(new string?[] { null, "LJ" }, desk.OnAir.Slots);
            Assert.Equal(switches, switched.Count);
            Assert.NotNull(stopped);
            Assert.True(stopped!("SIM-3"));
            Assert.False(stopped("LJ"));
            // Nothing from the simulation reached the sponsor log.
            desk.Sponsors.EndAll("grid:");
            Assert.Empty(desk.Sponsors.Appearances());
        }
        finally { desk.Dispose(); try { Directory.Delete(dir, true); } catch (Exception) { } }
    }
}

/// <summary>The grid: every active lobby at once, sized by count, kept up to date, and auto grid.</summary>
public class GridTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly CasterDesk _desk;
    private readonly List<OnAir> _switched = new();

    public GridTests()
    {
        _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        _desk.Switch = air => _switched.Add(air);
    }

    public void Dispose() => _desk.Dispose();

    private void Snap(string lobby, string phase = "ingame") =>
        _desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby, round = 1, phase, crewAlive = 7, impAlive = 2, taskPct = 30, t = 0 }));

    [Theory]
    [InlineData(1, 1, 1)] [InlineData(2, 2, 1)] [InlineData(3, 2, 2)] [InlineData(4, 2, 2)] [InlineData(5, 3, 2)] [InlineData(6, 3, 2)]
    [InlineData(7, 3, 3)] [InlineData(9, 3, 3)] [InlineData(10, 4, 3)] [InlineData(12, 4, 3)] [InlineData(13, 4, 4)] [InlineData(16, 4, 4)]
    public void The_grid_shape_follows_the_lobby_count(int count, int cols, int rows) =>
        Assert.Equal((cols, rows), ObsDirector.GridShape(count));

    [Fact]
    public void Grid_tiles_fill_the_canvas_row_by_row()
    {
        var boxes = ObsDirector.Slots("grid", 1920, 1080, 8, 7);
        Assert.Equal(9, boxes.Count);
        Assert.Equal(boxes[0].Y, boxes[2].Y);
        Assert.True(boxes[3].Y > boxes[0].Y);
        Assert.Equal(1920, boxes[2].X + boxes[2].W + 8, 3);
    }

    [Fact]
    public void The_grid_shows_every_active_lobby_with_empty_tiles_and_keeps_up()
    {
        foreach (var l in new[] { "E", "B", "A", "D", "C" }) Snap(l);
        Snap("F", phase: "menu");                                   // not in a lobby: left out
        var air = _desk.Show("", "grid");
        Assert.Equal("grid", air.Layout);
        Assert.Equal(new[] { "A", "B", "C", "D", "E", null }, air.Slots);   // 5 lobbies → 3×2
        Assert.Equal("LIVE (grid, tile 3)", air.Label("C"));

        // A lobby drops: the grid shrinks to 2×2.
        _clock.Advance(7);
        foreach (var l in new[] { "A", "B", "C", "D" }) Snap(l);
        _desk.Tick();
        Assert.Equal(new[] { "A", "B", "C", "D" }, _desk.OnAir.Slots);
    }

    [Fact]
    public void Auto_grid_comes_up_when_nobody_is_mid_game_and_goes_back_after()
    {
        Snap("A"); Snap("B");
        _desk.AutoGrid = true;
        _desk.Show("A");
        _desk.Tick();
        Assert.Equal("full", _desk.OnAir.Layout);                  // still playing
        Snap("A", "meeting"); Snap("B", "lobby");
        _desk.Tick();
        Assert.Equal("grid", _desk.OnAir.Layout);
        Snap("A", "ingame");
        _desk.Tick();
        Assert.Equal("full", _desk.OnAir.Layout);
        Assert.Equal("A", _desk.OnAir.Slots[0]);
    }
}
