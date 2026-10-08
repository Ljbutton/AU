using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Stats;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Part 22: backup for lobby drops.</summary>
public class LobbyHealthTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly CasterDesk _desk;
    private readonly List<OnAir> _switched = new();
    private readonly Dictionary<string, long> _seq = new();
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public LobbyHealthTests()
    {
        _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        _desk.Switch = air => _switched.Add(air);
    }

    public void Dispose() => _desk.Dispose();

    private long Ms(DateTime at) => new DateTimeOffset(at).ToUnixTimeMilliseconds();

    /// <summary>A message as a referee's page passes it on: numbered, from one run of the mod.</summary>
    private Dictionary<string, object?> Msg(string lobby, string type, string? kind, object? data = null, DateTime? at = null, string src = "run1", long? seq = null)
    {
        var msg = data == null ? new Dictionary<string, object?>() : JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        msg["v"] = TournamentTracker.Broadcast.FeedProtocol.Version;
        msg["type"] = type; msg["lobby"] = lobby; msg["round"] = 1; if (!msg.ContainsKey("game")) msg["game"] = lobby + "-1";
        msg["t"] = Ms(at ?? _clock.Now);
        msg["src"] = src;
        string key = lobby + src;
        msg["seq"] = seq ?? (_seq[key] = _seq.GetValueOrDefault(key) + 1);
        if (kind != null) msg["kind"] = kind;
        return msg;
    }

    private void Send(Dictionary<string, object?> msg) => _desk.Apply(JsonSerializer.Serialize(msg));
    private void Send(string lobby, string type, string? kind, object? data = null) => Send(Msg(lobby, type, kind, data));
    private void Snap(string lobby, string phase = "ingame", bool danger = false) =>
        Send(lobby, "snap", null, new { phase, crewAlive = 7, impAlive = 2, alive = 9, taskPct = 40, danger });
    private void Video(string lobby, string state) => _desk.Apply(JsonSerializer.Serialize(new { type = "health", lobby, t = Ms(_clock.Now), video = state }));
    private static object P(string colour, bool imp = false) => new { id = 1, name = colour, color = 0, colorName = colour, imp };

    /// <summary>Time passes; the given lobbies keep sending a snapshot a second.</summary>
    private void Pass(double seconds, params string[] sending)
    {
        for (double s = 0; s < seconds; s += 1)
        {
            _clock.Advance(1);
            foreach (var l in sending) Snap(l);
        }
    }

    private JsonElement State() => JsonSerializer.SerializeToElement(_desk.State(), Camel);
    private List<JsonElement> Cards() => State().GetProperty("cards").EnumerateArray().ToList();

    [Fact]
    public void A_host_on_an_older_feed_still_shows_but_is_marked_host_needs_update()
    {
        Snap("A");
        Assert.Null(_desk.HostNeedsUpdate("A"));
        var old = Msg("A", "snap", null, new { phase = "ingame" });
        old.Remove("v");                                             // a mod from before versions
        Send(old);
        Assert.StartsWith("host needs update", _desk.HostNeedsUpdate("A"));
        Assert.True(State().GetProperty("lobbies").EnumerateArray().Single().GetProperty("online").GetBoolean());
        var lobby = State().GetProperty("health").GetProperty("lobbies").EnumerateArray().Single();
        Assert.StartsWith("host needs update", lobby.GetProperty("update").GetString());
    }

    [Fact]
    public void Each_lobby_is_green_yellow_or_red_for_plain_reasons()
    {
        var cfg = new HealthSettings();
        var h = new LobbyHealth(() => _clock.Now, () => cfg);
        Assert.Equal("red", h.Status("A").Level);                     // never heard from

        h.Data("A", Ms(_clock.Now));
        Assert.Equal("green", h.Status("A").Level);
        _clock.Advance(3);
        var st = h.Status("A");
        Assert.Equal("yellow", st.Level);
        Assert.Equal("no data for 3s", st.Problems.Single());
        _clock.Advance(9);
        st = h.Status("A");
        Assert.Equal("red", st.Level);
        Assert.Equal("no data for 12s", st.Problems.Single());
        Assert.NotNull(st.DownSince);

        h.Data("A", Ms(_clock.Now));
        Assert.Equal("green", h.Status("A").Level);
        Assert.Null(h.Status("A").DownSince);

        // Video: lost for a moment is fine; lost for 3 s is down.
        h.Video("A", "lost");
        _clock.Advance(1); h.Data("A", Ms(_clock.Now));
        Assert.Equal("green", h.Status("A").Level);
        _clock.Advance(2.5); h.Data("A", Ms(_clock.Now));
        Assert.Equal("video lost", h.Status("A").Problems.Single());
        Assert.Equal("red", h.Status("A").Level);
        h.Video("A", "ok");
        Assert.Equal("green", h.Status("A").Level);

        // Audio silent a long time while sending: degraded. A voice problem: degraded.
        h.Audio("A", true, -60, -60, null);
        _clock.Advance(121); h.Data("A", Ms(_clock.Now)); h.Video("A", "ok"); h.Audio("A", true, -60, -60, null);
        Assert.Equal(new[] { "audio silent" }, h.Status("A").Problems);
        Assert.Equal("yellow", h.Status("A").Level);
        h.Audio("A", true, -20, -60, null);
        Assert.Equal("green", h.Status("A").Level);
        h.Audio("A", true, -20, -60, "voice stream dropped");
        Assert.Equal("audio: voice stream dropped", h.Status("A").Problems.Single());

        // Lag: later than this lobby's best: degraded; so late it isn't live: down. Sent again after a drop: not lag.
        var b = new LobbyHealth(() => _clock.Now, () => cfg);
        b.Data("B", Ms(_clock.Now) - 100);
        b.Data("B", Ms(_clock.Now) - 2600);
        Assert.Equal("high lag (2.5s)", b.Status("B").Problems.Single());
        b.Data("B", Ms(_clock.Now) - 9100);
        Assert.Equal("data 9s late", b.Status("B").Problems.Single());
        Assert.Equal("red", b.Status("B").Level);
        b.Data("B", Ms(_clock.Now) - 100);
        b.Data("B", Ms(_clock.Now) - 30000, resent: true);
        Assert.Equal("green", b.Status("B").Level);
    }

    [Fact]
    public void Messages_sent_again_after_a_drop_count_once_and_gaps_are_waited_for()
    {
        Snap("A");
        var v1 = Msg("A", "event", "vent", new { player = P("Purple", true), action = "enter", room = "MedBay" });
        var v2 = Msg("A", "event", "vent", new { player = P("Purple", true), action = "enter", room = "Admin" });
        Send(v1); Send(v2);
        // The referee's page didn't hear back in time and sends both again.
        v1["re"] = true; v2["re"] = true;
        Send(v1); Send(v2);
        Assert.Equal(2, Cards().Single(c => c.GetProperty("rule").GetString() == "vent").GetProperty("repeats").GetInt32());
        Assert.Equal(3, _desk.Acks("A")["run1"]);

        // 5 arrives before 4: the caster says "up to 3" until 4 comes.
        var m4 = Msg("A", "snap", null, new { phase = "ingame" });
        var m5 = Msg("A", "snap", null, new { phase = "ingame" });
        Send(m5);
        Assert.Equal(3, _desk.Acks("A")["run1"]);
        Send(m4);
        Assert.Equal(5, _desk.Acks("A")["run1"]);
        Send(m5);                                                   // a late copy: ignored
        Assert.Equal(5, _desk.Acks("A")["run1"]);

        // Among Us restarted: a new run, counted from its own start.
        Send(Msg("A", "snap", null, new { phase = "lobby" }, src: "run2", seq: 1));
        Assert.Equal(1, _desk.Acks("A")["run2"]);
    }

    [Fact]
    public void Late_messages_count_for_the_stats_with_their_own_time_but_are_never_shown_as_live()
    {
        var start = _clock.Now;
        Send("A", "event", "gameStart", new { map = "Polus", players = new[] { P("Purple", true), P("Lime") } });
        Snap("A");
        // The link drops for 30 s; meanwhile there's a kill and a meeting.
        var kill = Msg("A", "event", "kill", new { killer = P("Purple", true), victim = P("Lime"), room = "Office", winning = false, crewAlive = 6, impAlive = 2 }, at: start.AddSeconds(5));
        var snap = Msg("A", "snap", null, new { phase = "meeting", crewAlive = 6, impAlive = 2, alive = 8, taskPct = 50 }, at: start.AddSeconds(6));
        _clock.Advance(30);
        kill["re"] = true; snap["re"] = true;
        Send(kill); Send(snap);
        Assert.DoesNotContain(Cards(), c => c.GetProperty("rule").GetString() == "kill");          // no card for a 25 s old kill
        Assert.Single(_desk.Archive.Live("A")!.Kills);                                               // but it's in the game's stats
        var lobby = State().GetProperty("lobbies").EnumerateArray().Single();
        Assert.Equal("ingame", lobby.GetProperty("phase").GetString());                              // the old meeting isn't "now"
        Assert.False(lobby.GetProperty("online").GetBoolean());
    }

    [Fact]
    public void A_lobby_on_stream_that_drops_is_switched_away_from_and_cards_say_so()
    {
        Snap("A", danger: true); Snap("B", danger: true); Snap("C");
        _desk.Show("A");
        for (int i = 0; i < 11; i++) { _clock.Advance(1); Snap("B", danger: true); Snap("C"); _desk.Tick(); }
        var air = _desk.OnAir;
        Assert.Equal("full", air.Layout);
        Assert.Equal("B", air.Slots[0]);                                                              // the next hot lobby
        Assert.Equal("health", air.By);
        var down = Cards().Single(c => c.GetProperty("rule").GetString() == "lobbyDown");
        Assert.Equal("LOBBY A DOWN: no data for 10s. Switched to B.", down.GetProperty("text").GetString());
        Assert.True(down.GetProperty("system").GetBoolean());

        // B and C go too: nothing to show mid-game, so "be right back".
        for (int i = 0; i < 11; i++) { _clock.Advance(1); _desk.Tick(); }
        Assert.Equal("slate", _desk.OnAir.Layout);

        // C comes back: a card, and off the slate (C isn't hot: the grid).
        Snap("C"); Video("C", "ok");
        _desk.Tick();
        Assert.Contains(Cards(), c => c.GetProperty("rule").GetString() == "lobbyBack" && c.GetProperty("lobby").GetString() == "C");
        Assert.DoesNotContain(Cards(), c => c.GetProperty("rule").GetString() == "lobbyDown" && c.GetProperty("lobby").GetString() == "C");
        Assert.Equal("grid", _desk.OnAir.Layout);
        // A and B dropped less than 45 s ago: their tiles stay, marked RECONNECTING.
        Assert.Equal(new[] { "A", "B", "C" }, _desk.OnAir.Slots.Where(x => x != null).ToArray());

        // A coming back doesn't take over by itself.
        Snap("A", danger: true);
        _desk.Tick();
        Assert.Equal("grid", _desk.OnAir.Layout);
        Assert.Contains(Cards(), c => c.GetProperty("rule").GetString() == "lobbyBack" && c.GetProperty("lobby").GetString() == "A");
    }

    [Fact]
    public void With_auto_switch_off_the_caster_decides_and_nobody_mid_game_means_intermission()
    {
        _desk.HealthConfig.AutoSwitch = false;
        Snap("A"); Snap("B");
        _desk.Show("A");
        Pass(11, "B"); _desk.Tick();
        Assert.Equal("A", _desk.OnAir.Slots[0]);
        Assert.Contains(Cards(), c => c.GetProperty("rule").GetString() == "lobbyDown");

        _desk.HealthConfig.AutoSwitch = true;
        _desk.Show("B");
        Snap("B", "lobby"); Snap("A", "lobby");
        Pass(11); _desk.Tick();
        Assert.Equal("intermission", _desk.OnAir.Layout);                                             // between games: intermission, not the slate
    }

    private int HealthSwitches() => _switched.Count(a => a.By == "health");
    private void Ticks(int n, params string[] sending) { for (int i = 0; i < n; i++) { _clock.Advance(1); foreach (var l in sending) Snap(l); _desk.Tick(); } }

    [Fact]
    public void A_lobby_the_caster_puts_on_while_its_already_down_stays_on()
    {
        Snap("LJ");
        Ticks(15);                                                        // LJ goes red with nothing on stream
        Assert.True(_desk.IsDown("LJ"));
        foreach (var layout in new[] { "full", "2up", "4up" })
        {
            _desk.Show("LJ", layout);
            Ticks(65);
            Assert.Equal(layout, _desk.OnAir.Layout);                     // the caster's choice wins, every tick
            Assert.Equal("LJ", _desk.OnAir.Slots[0]);
        }
        Assert.Contains("LJ is down: ", _desk.DownNote(_desk.OnAir.Slots));               // still said, on the switch
        Assert.Contains("no data for", _desk.DownNote(_desk.OnAir.Slots));
        _desk.ShowGrid("button", new[] { "LJ" });
        Ticks(65);
        Assert.Equal("grid", _desk.OnAir.Layout);                         // not sent off to intermission
        Assert.Equal(0, HealthSwitches());
        Assert.Null(_desk.AutoSwitched);
    }

    [Fact]
    public void A_lobby_that_drops_while_on_stream_is_left_once_and_not_again_until_a_fresh_outage()
    {
        Snap("LJ");
        _desk.Show("LJ");
        Ticks(12);                                                        // drops on stream
        Assert.Equal(1, HealthSwitches());
        Assert.NotEqual("full", _desk.OnAir.Layout);
        var note = _desk.AutoSwitched!;
        Assert.Equal("LJ", note.Lobby);
        Assert.StartsWith("Auto switch: LJ: ", note.Text);
        Assert.Contains("no data for", note.Text);
        Assert.Contains("→", note.Text);
        Assert.Equal("LJ", note.Previous.Slots[0]);
        var desk = JsonSerializer.SerializeToElement(_desk.State(), Camel).GetProperty("autoSwitched");
        Assert.Equal(note.Text, desk.GetProperty("text").GetString());

        // The caster puts it back by hand: it stays, the same outage never moves it again.
        _desk.Show("LJ");
        Ticks(70);
        Assert.Equal(("full", "LJ"), (_desk.OnAir.Layout, _desk.OnAir.Slots[0]));
        Assert.Equal(1, HealthSwitches());

        // It comes back (the banner goes), then drops again: a fresh outage, moved once more.
        Snap("LJ"); Video("LJ", "ok");
        Ticks(5, "LJ");
        Assert.False(_desk.IsDown("LJ"));
        Assert.Null(_desk.AutoSwitched);
        Ticks(12);
        Assert.Equal(2, HealthSwitches());
        Assert.NotEqual("full", _desk.OnAir.Layout);
        Ticks(60);
        Assert.Equal(2, HealthSwitches());                                // and only once
    }

    [Fact]
    public void Undo_puts_back_what_the_caster_had_and_keeps_it_there()
    {
        Snap("LJ"); Snap("MAL");
        _desk.Show("", "2up", null, new[] { "LJ", "MAL" });
        Ticks(12);                                                        // both drop: auto switch moves off them
        Assert.Equal(1, HealthSwitches());
        Assert.Equal("LJ", _desk.AutoSwitched!.Lobby);
        var back = _desk.UndoAutoSwitch()!;
        Assert.Equal(("2up", "button"), (back.Layout, back.By));
        Assert.Equal(new string?[] { "LJ", "MAL" }, back.Slots.ToArray());
        Assert.Null(_desk.AutoSwitched);
        Ticks(70);
        Assert.Equal("2up", _desk.OnAir.Layout);
        Assert.Equal(1, HealthSwitches());
        Assert.Null(_desk.UndoAutoSwitch());                             // nothing more to undo
    }

    private void Page(string lobby, string video, string? data = null) =>
        _desk.Apply(JsonSerializer.Serialize(new { type = "health", lobby, t = Ms(_clock.Now), video, data }));

    [Fact]
    public void A_send_page_that_never_got_game_data_says_so_in_plain_words()
    {
        // Health reports only (the page lost The Button before any game data): no overflowed number.
        Page("LJ", "unknown");
        _clock.Advance(1);
        var st = _desk.Health.Status("LJ");
        Assert.Equal("red", st.Level);
        Assert.Contains("never sent game data", st.Problems);
        Assert.DoesNotContain(st.Problems, p => p.Contains("-"));
        Assert.Null(st.DataAge);
        Assert.Null(_desk.Health.DataAge("LJ"));

        // Video going out, but no game data: the host's page lost The Button.
        Page("LJ", "ok");
        var why = Assert.Single(_desk.Health.Status("LJ").Problems);
        Assert.Equal("video OK, but no game data (the host's send page lost The Button: they should click Open again)", why);

        // Data that stopped (not never): the page says it lost The Button.
        Snap("MAL");
        for (int i = 0; i < 12; i++) { _clock.Advance(1); Page("MAL", "unknown", "lost"); }
        Assert.Contains(_desk.Health.Status("MAL").Problems, p => p.StartsWith("video OK, but no game data"));
        // And plain silence still reads as before.
        Snap("KAI");
        _clock.Advance(12);
        Assert.Contains("no data for 12s", _desk.Health.Status("KAI").Problems);
    }

    [Fact]
    public void A_game_is_only_interrupted_when_the_host_doesnt_come_back_and_cleared_if_it_carries_on()
    {
        Send("LJ", "event", "gameStart", new { });
        Snap("LJ");
        // Data stops: red at 10 s, but not interrupted within the next minute.
        for (int i = 0; i < 65; i++) { _clock.Advance(1); Page("LJ", "ok"); _desk.Tick(); }
        Assert.True(_desk.IsDown("LJ"));
        Assert.Empty(_desk.Interruptions);
        Assert.DoesNotContain(Cards(), c => c.GetProperty("rule").GetString() == "interrupted");

        // Still nothing after the red threshold plus a minute: interrupted.
        for (int i = 0; i < 10; i++) { _clock.Advance(1); _desk.Tick(); }
        var it = Assert.Single(_desk.Interruptions);
        Assert.Equal("LJ-1", it.Game);
        Assert.Contains("didn't come back", it.Why);
        Assert.Contains(Cards(), c => c.GetProperty("rule").GetString() == "interrupted");

        // The host's page reconnects and the same game carries on: the mark and its card go.
        Snap("LJ");
        _desk.Tick();
        Assert.Empty(_desk.Interruptions);
        Assert.DoesNotContain(Cards(), c => c.GetProperty("rule").GetString() == "interrupted");
        Assert.Contains(Cards(), c => c.GetProperty("text").GetString()!.Contains("LJ-1 carries on"));
        // A "restarted mid-game" that turns out to be the same game carrying on is taken back too.
        Snap("LJ", "lobby");
        Assert.Single(_desk.Interruptions);
        Snap("LJ");
        Assert.Empty(_desk.Interruptions);
    }

    [Fact]
    public void A_dropped_lobby_keeps_its_grid_tile_for_a_while_then_the_rest_close_up()
    {
        Snap("A"); Snap("B"); Snap("C");
        _desk.ShowGrid();
        Assert.Equal(3, _desk.OnAir.Slots.Count(x => x != null));
        Pass(11, "B", "C"); _desk.Tick();
        Assert.Contains("A", _desk.OnAir.Slots);                                                       // RECONNECTING on its tile
        Assert.True(_desk.IsDown("A"));
        Pass(46, "B", "C"); _desk.Tick();
        Assert.DoesNotContain("A", _desk.OnAir.Slots);
        Assert.Equal(new[] { "B", "C" }, _desk.OnAir.Slots.Where(x => x != null).ToArray());

        // In a quad, its slot goes to the next lobby after the same wait.
        Snap("A"); Snap("D");
        _desk.Show("", "4up", null, new[] { "A", "B", "C", "" });
        Pass(11, "B", "C", "D"); _desk.Tick();
        Assert.Equal("A", _desk.OnAir.Slots[0]);
        Pass(46, "B", "C", "D"); _desk.Tick();
        Assert.Equal(new string?[] { "D", "B", "C", null }, _desk.OnAir.Slots.ToArray());
    }

    [Fact]
    public void A_game_cut_short_by_a_crash_is_held_out_of_the_standings_until_the_caster_decides()
    {
        var games = new List<GameRecord>
        {
            new() { Host = "A", GameNumber = 1, Round = 1, Winner = "Crewmates" },
            new() { Host = "B", GameNumber = 1, Round = 1, Winner = "Impostors" },
        };
        _desk.ExternalGames = () => games;
        Send(Msg("A", "event", "gameStart", new { map = "Polus", game = "A-1", players = new[] { P("Purple", true) } }));
        Snap("A");
        // The referee's game restarts: a new run of the mod, back in the lobby, the game never ended.
        Send(Msg("A", "snap", null, new { phase = "lobby" }, src: "run2", seq: 1));
        var card = Cards().Single(c => c.GetProperty("rule").GetString() == "interrupted");
        Assert.StartsWith("INTERRUPTED: A-1 (round 1): the referee's game restarted mid-game.", card.GetProperty("text").GetString());
        string id = card.GetProperty("interruption").GetString()!;
        Assert.DoesNotContain(_desk.Games, g => g.Name == "A-1");
        Assert.Contains(_desk.Games, g => g.Name == "B-1");

        var (cmd, msg) = _desk.Decide(id, "count");
        Assert.Null(cmd);
        Assert.Equal("A-1 counts: it's in the standings.", msg);
        Assert.Contains(_desk.Games, g => g.Name == "A-1");
        Assert.DoesNotContain(Cards(), c => c.GetProperty("rule").GetString() == "interrupted");

        // A new game starting before the last ended, and an abandoned game, are interrupted too.
        Send(Msg("B", "event", "gameStart", new { game = "B-2" }));
        Send(Msg("B", "event", "gameStart", new { game = "B-3" }));
        Send(Msg("B", "event", "gameEnd", new { game = "B-3", abandoned = true }));
        var held = _desk.Interruptions.Where(i => i.Lobby == "B").ToList();
        Assert.Equal(new[] { "B-2", "B-3" }, held.Select(i => i.Game).ToArray());
        Assert.Equal("!void B-2 interrupted", _desk.Decide(held[0].Id, "void").Command);
        Assert.Equal("!void B-3 interrupted, to be replayed", _desk.Decide(held[1].Id, "replay").Command);
        Assert.Contains(Cards(), c => c.GetProperty("rule").GetString() == "replayGame");
    }

    [Fact]
    public void The_overlay_marks_a_dropped_lobby_and_the_slate_is_be_right_back()
    {
        var app = new BroadcastApp(_desk, () => null, null);
        Snap("A"); Snap("B");
        _desk.Show("", "2up", null, new[] { "A", "B" });
        Pass(4, "B");
        var s = JsonSerializer.SerializeToElement(app.State(), Camel);
        Assert.True(s.GetProperty("lobbies").GetProperty("A").GetProperty("reconnecting").GetBoolean());   // 4 s without data: marked already
        Assert.False(s.GetProperty("lobbies").GetProperty("B").GetProperty("reconnecting").GetBoolean());
        _desk.ShowSlate();
        s = JsonSerializer.SerializeToElement(app.State(), Camel);
        Assert.Equal("slate", s.GetProperty("layout").GetString());
        Assert.True(s.GetProperty("intermission").GetProperty("slate").GetBoolean());
        Assert.Empty(s.GetProperty("slots").EnumerateArray());
    }

    [Fact]
    public void Simulation_drops_video_audio_data_lags_and_crashes_lobbies_and_brings_them_back()
    {
        _desk.Simulate(true);
        void Run(double seconds) { for (double t = 0; t < seconds; t += 0.5) { _clock.Advance(0.5); _desk.SimTick(); } }
        HealthStatus H(string l) => _desk.Health.Status(l);
        Run(8);
        Assert.All(new[] { "SIM-1", "SIM-2", "SIM-3", "SIM-4" }, l => Assert.Equal("green", H(l).Level));

        Assert.True(_desk.SimFail("SIM-1", "data"));
        Assert.True(_desk.SimFail("SIM-2", "video"));
        Assert.True(_desk.SimFail("SIM-3", "audio"));
        Assert.True(_desk.SimFail("SIM-4", "lag"));
        Run(12);
        Assert.Equal("red", H("SIM-1").Level);
        Assert.StartsWith("no data for", H("SIM-1").Problems.Single());
        Assert.Equal("video lost", H("SIM-2").Problems.Single());
        Assert.Equal("audio: voice stream dropped", H("SIM-3").Problems.Single());
        Assert.Equal("yellow", H("SIM-4").Level);
        Assert.StartsWith("high lag", H("SIM-4").Problems.Single());

        // Back: SIM-1's held messages arrive with their own times, each once.
        foreach (var l in new[] { "SIM-1", "SIM-2", "SIM-3", "SIM-4" }) _desk.SimFail(l, "reconnect");
        Run(6);
        Assert.All(new[] { "SIM-1", "SIM-2", "SIM-3", "SIM-4" }, l => Assert.Equal("green", H(l).Level));

        // The referee's game crashes mid-game and comes back in the lobby: interrupted.
        for (int i = 0; i < 240 && _desk.Board.Lobby("SIM-2")?.Phase != "ingame"; i++) Run(0.5);
        Assert.Equal("ingame", _desk.Board.Lobby("SIM-2")!.Phase);
        _desk.SimFail("SIM-2", "crash");
        Run(12);
        Assert.Equal("red", H("SIM-2").Level);
        _desk.SimFail("SIM-2", "reconnect");
        Run(2);
        Assert.Contains(_desk.Interruptions, i => i.Lobby == "SIM-2" && i.Decision == null);
        Assert.Equal("green", H("SIM-2").Level);
    }
}
