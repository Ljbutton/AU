using System.Text.Json;
using System.Text.Json.Nodes;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Red Alert's broadcast delay: the links OBS gets, the graphics' delayed data, and the setting.</summary>
public class BroadcastDelayTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void VDO_Ninja_links_get_a_playout_buffer_of_the_delay_and_nothing_else_changes()
    {
        const string link = "https://vdo.ninja/?view=a&password=x&cleanoutput";
        Assert.Equal(link + "&buffer=3000", BroadcastDelay.WithBuffer(link, 3));
        Assert.Equal(link + "&buffer=1500", BroadcastDelay.WithBuffer(link, 1.5));
        Assert.Equal(link, BroadcastDelay.WithBuffer(link, 0));
        // At most 4 s: Chromium's cap on the jitter buffer target VDO.Ninja sets.
        Assert.Equal(link + "&buffer=4000", BroadcastDelay.WithBuffer(link, 9));
        // A link with its own buffer, and simulation's stand-in pages, are left alone.
        Assert.Equal(link + "&buffer=500", BroadcastDelay.WithBuffer(link + "&buffer=500", 3));
        Assert.Equal("http://127.0.0.1:8767/sim?lobby=SIM-1", BroadcastDelay.WithBuffer("http://127.0.0.1:8767/sim?lobby=SIM-1", 3));
        // A view id that happens to have "buffer" in it is still a link to delay.
        Assert.Equal("https://vdo.ninja/?view=buffer1&password=x&buffer=2000", BroadcastDelay.WithBuffer("https://vdo.ninja/?view=buffer1&password=x", 2));
        Assert.Equal(new[] { 0, 0, 2.5, 4, 0 }, new[] { -1, double.NaN, 2.3, 4.2, 0.2 }.Select(BroadcastDelay.Clamp));
        Assert.Equal(("3 s", "1.5 s", "off"), (BroadcastDelay.Say(3), BroadcastDelay.Say(1.5), BroadcastDelay.Say(0)));
    }

    [Fact]
    public void The_delay_is_3_s_by_default_and_kept_in_obs_json()
    {
        string path = Path.Combine(_dir.Path, ObsSettings.FileName);
        File.WriteAllText(path, "{\"host\":\"127.0.0.1\"}");        // an older Red Alert's file
        Assert.Equal(3, ObsSettings.Load(path).BroadcastDelay);
        File.WriteAllText(path, "{\"broadcastDelay\":1.5}");
        Assert.Equal(1.5, ObsSettings.Load(path).BroadcastDelay);
        using var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        var director = new ObsDirector(path, desk, () => new List<(string, string)>());
        director.Settings.BroadcastDelay = 12;                          // edited by hand
        Assert.Equal(4, director.DelaySeconds);
    }

    [Fact]
    public void The_delay_line_answers_with_what_was_live_the_delay_ago()
    {
        var line = new DelayLine<string>();
        var t0 = new DateTime(2026, 10, 20, 20, 0, 0, DateTimeKind.Utc);
        var d = TimeSpan.FromSeconds(3);
        Assert.Equal("a", line.Push(t0, "a", d));                       // nothing older yet: the oldest
        Assert.Equal("a", line.Push(t0.AddSeconds(1), "b", d));
        Assert.Equal("a", line.Push(t0.AddSeconds(3.5), "c", d));       // 0.5 s back: a (from 0 s)
        Assert.Equal("b", line.Push(t0.AddSeconds(4.2), "d", d));       // 1.2 s back: b (from 1 s)
        Assert.True(line.Count <= 3);
        // Nobody looked for a long time (OBS was closed): not what was on minutes ago.
        Assert.Equal("e", line.Push(t0.AddSeconds(200), "e", d));
        Assert.Equal("e", line.Push(t0.AddSeconds(201), "f", d));
        Assert.Equal("f", line.Push(t0.AddSeconds(204.5), "g", d));
        // No delay: what's live, and nothing kept.
        Assert.Equal("h", line.Push(t0.AddSeconds(205), "h", TimeSpan.Zero));
        Assert.Equal(0, line.Count);
    }

    private static JsonObject Obj(object o) => JsonSerializer.SerializeToNode(o)!.AsObject();

    [Fact]
    public void The_stream_gets_the_games_as_they_were_on_the_screen_as_it_is()
    {
        var past = Obj(new
        {
            canvas = new { w = 1920, h = 1080 }, layout = "full", slots = new[] { new { lobby = "LJ", slot = 1, x = 0, y = 0, w = 1920, h = 1080 } },
            elements = new { impostorTags = true },
            lobbies = new Dictionary<string, object>
            {
                ["LJ"] = new { number = 1, order = 1, onScreen = true, imps = 2, impostors = new[] { new { name = "Ann", dead = false } } },
                ["MAL"] = new { number = 2, order = 2, onScreen = false, imps = 2, impostors = new[] { new { name = "Cy", dead = false } } },
            },
            alerts = new[] { new { id = "k1", lobby = "MAL", number = (int?)2, kind = "kill", text = "Cy killed Bo" } },
            big = new { kind = "afterGame", lobby = "LJ", id = "g1" },
            standingsChange = new { change = new { lobby = "LJ" }, at = new { x = 0, y = 0, w = 1920, h = 1080, full = true } },
            playerCard = new { id = "ann|1", lobby = "LJ", data = new { name = "Ann" }, x = 0, y = 0, w = 1920, h = 1080, full = true },
            wins = new { impostors = 3, crew = 4 },
        });
        var live = Obj(new
        {
            canvas = new { w = 1920, h = 1080 }, layout = "2up",
            slots = new[] { new { lobby = "MAL", slot = 1, x = 8, y = 10, w = 948, h = 533 }, new { lobby = "LJ", slot = 2, x = 964, y = 10, w = 948, h = 533 } },
            elements = new { impostorTags = false },
            lobbies = new Dictionary<string, object>
            {
                ["LJ"] = new { number = 2, order = 2, onScreen = true, imps = 1, impostors = new[] { new { name = "Ann", dead = true } } },
                ["MAL"] = new { number = 1, order = 1, onScreen = true, imps = 1, impostors = new[] { new { name = "Cy", dead = true } } },
                ["NEW"] = new { number = 3, order = 3, onScreen = false, imps = 2 },
            },
            alerts = Array.Empty<object>(),
            wins = new { impostors = 4, crew = 4 },
        });
        string pastWas = past.ToJsonString(), liveWas = live.ToJsonString();
        var o = BroadcastApp.Delayed(live, past, (_, _) => true);
        // The screen as it is now (the switch, which graphics are on) …
        Assert.Equal("2up", o["layout"]!.GetValue<string>());
        Assert.Equal(2, o["slots"]!.AsArray().Count);
        Assert.False(o["elements"]!["impostorTags"]!.GetValue<bool>());
        // … the games as they were: impostors alive, the wins before the last one, the alert.
        Assert.False(o["lobbies"]!["LJ"]!["impostors"]![0]!["dead"]!.GetValue<bool>());
        Assert.Equal(2, o["lobbies"]!["MAL"]!["imps"]!.GetValue<int>());
        Assert.Equal(3, o["wins"]!["impostors"]!.GetValue<int>());
        Assert.Single(o["alerts"]!.AsArray());
        // A lobby that wasn't there then isn't drawn yet (its live data would be early).
        Assert.Null(o["lobbies"]!["NEW"]);
        // Each lobby's number and place are where it is now; the graphics go on its tile now.
        Assert.Equal((2, true), (o["lobbies"]!["LJ"]!["number"]!.GetValue<int>(), o["lobbies"]!["MAL"]!["onScreen"]!.GetValue<bool>()));
        Assert.Equal(1, o["alerts"]![0]!["number"]!.GetValue<int>());
        Assert.Equal((964, false), (o["standingsChange"]!["at"]!["x"]!.GetValue<int>(), o["standingsChange"]!["at"]!["full"]!.GetValue<bool>()));
        Assert.Equal((964, 948, false), (o["playerCard"]!["x"]!.GetValue<int>(), o["playerCard"]!["w"]!.GetValue<int>(), o["playerCard"]!["full"]!.GetValue<bool>()));
        // Neither input changed.
        Assert.Equal((pastWas, liveWas), (past.ToJsonString(), live.ToJsonString()));

        // A lobby's big graphic that can't show on what's on now (its lobby isn't on screen any more) waits.
        var o2 = BroadcastApp.Delayed(live, past, (kind, lobby) => lobby != "LJ");
        Assert.Null(o2["big"]);
        Assert.Null(o2["standingsChange"]);
        // Replays, intermission and be right back have no lobby picture: they're as they are now.
        live["layout"] = "replay";
        var o3 = BroadcastApp.Delayed(live, past, (_, _) => true);
        Assert.True(o3["lobbies"]!["LJ"]!["impostors"]![0]!["dead"]!.GetValue<bool>());
        // Back from a replay: the replay's own player card doesn't stay up over the lobby.
        live["layout"] = "full";
        past["layout"] = "replay";
        Assert.Null(BroadcastApp.Delayed(live, past, (_, _) => true)["playerCard"]);

        // Starting soon has no lobby picture either: it's live, and gone the moment a lobby is taken.
        live["layout"] = "soon";
        live["soon"] = new JsonObject { ["title"] = "Starting soon" };
        Assert.NotNull(BroadcastApp.Delayed(live, past, (_, _) => true)["soon"]);
        past["layout"] = "soon";
        past["soon"] = new JsonObject { ["title"] = "Starting soon" };
        live["layout"] = "full";
        live["soon"] = null;
        Assert.Null(BroadcastApp.Delayed(live, past, (_, _) => true)["soon"]);
    }

    [Fact]
    public void The_graphics_page_gets_the_impostor_count_late_and_the_switch_at_once()
    {
        using var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        var director = new ObsDirector(null, desk, () => new List<(string, string)>());
        var app = new BroadcastApp(desk, () => director, Path.Combine(_dir.Path, BroadcastSettings.FileName)) { Clock = () => _clock.Now };
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        void Snap(string lobby, int imps) => desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby, round = 1, phase = "ingame", crewAlive = 6, impAlive = imps, taskPct = 30, t = 0 }));
        int Imps(JsonObject s, string lobby) => s["lobbies"]![lobby]!["imps"]!.GetValue<int>();
        Snap("LJ", 2);
        Snap("MAL", 2);
        desk.Show("LJ");
        Assert.Equal(3, director.DelaySeconds);
        Assert.Equal(2, Imps(app.OnStream(json), "LJ"));
        Snap("LJ", 1);
        _clock.Advance(1);
        Assert.Equal(1, Imps(JsonSerializer.SerializeToNode(app.State(), json)!.AsObject(), "LJ"));     // the desk: live
        Assert.Equal(2, Imps(app.OnStream(json), "LJ"));                                                   // the stream: not yet
        desk.Show("MAL");
        _clock.Advance(1);
        var s = app.OnStream(json);
        Assert.Equal("MAL", s["slots"]![0]!["lobby"]!.GetValue<string>());                                 // the switch: at once
        Assert.Equal(2, Imps(s, "LJ"));
        _clock.Advance(2);
        Assert.Equal(1, Imps(app.OnStream(json), "LJ"));                                                   // 3 s after the look that saw it: now
        // No delay: live.
        director.Settings.BroadcastDelay = 0;
        Snap("LJ", 0);
        Assert.Equal(0, Imps(app.OnStream(json), "LJ"));
    }
}

/// <summary>The broadcast delay in OBS, and Hear Preview (the lobby in Preview in the caster's headphones).</summary>
public class HearPreviewTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private FakeObs _obs = null!;
    private CasterDesk _desk = null!;
    private ObsDirector _director = null!;
    private readonly List<(string, string)> _feeds = new() { ("LJ", "https://vdo.ninja/?view=a&password=x&cleanoutput"), ("MAL", "https://vdo.ninja/?view=b&password=x&cleanoutput") };
    private readonly List<(string, string)> _voices = new() { ("LJ", "https://vdo.ninja/?view=av&password=x&cleanoutput&novideo"), ("MAL", "https://vdo.ninja/?view=bv&password=x&cleanoutput&novideo") };
    private string SettingsPath => Path.Combine(_dir.Path, ObsSettings.FileName);

    public async Task InitializeAsync()
    {
        _obs = new FakeObs();
        lock (_obs) { _obs.Inputs["Desktop Audio"] = new(); _obs.Kinds["Desktop Audio"] = "wasapi_output_capture"; _obs.Muted["Desktop Audio"] = true; }
        _desk = new CasterDesk(null, null, new PriorityConfig());
        _director = new ObsDirector(SettingsPath, _desk, () => _feeds.ToList()) { VoiceFeeds = () => _voices.ToList() };
        _desk.Switch = air => _director.ApplyAsync(air).Wait();
        foreach (var l in new[] { "LJ", "MAL" })
            _desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = l, round = 1, phase = "ingame", crewAlive = 7, impAlive = 2, taskPct = 30, t = 0 }));
        await _director.ConnectAsync("127.0.0.1", _obs.Port, "secret");
    }

    public async Task DisposeAsync()
    {
        await _director.DisposeAsync();
        await _obs.DisposeAsync();
        _desk.Dispose();
        _dir.Dispose();
    }

    private static Task Until(Func<bool> done) => Wait.For(done, 4000);
    private string Url(string input) { lock (_obs) return _obs.Inputs[input]["url"]!.ToString()!; }
    private bool Muted(string input) { lock (_obs) return _obs.Muted.GetValueOrDefault(input); }

    [Fact]
    public async Task Every_lobby_picture_and_voice_in_OBS_is_late_by_the_delay_and_changing_it_reloads_them()
    {
        Assert.EndsWith("&buffer=3000", Url("TT Lobby LJ"));
        Assert.EndsWith("&buffer=3000", Url("TT Voice MAL"));
        Assert.Contains("1.5 s late", await _director.SetDelayAsync(1.5));
        Assert.EndsWith("&videobitrate=8000&buffer=1500", Url("TT Lobby MAL"));
        Assert.EndsWith("&novideo&buffer=1500", Url("TT Voice LJ"));
        Assert.Equal(1.5, ObsSettings.Load(SettingsPath).BroadcastDelay);
        await _director.SetDelayAsync(0);
        Assert.DoesNotContain("buffer", Url("TT Lobby LJ"));
        Assert.DoesNotContain("buffer", Url("TT Voice LJ"));
        Assert.Equal("Broadcast delay off", _director.Checks.Single(c => c.Id == "delay").Title);
    }

    [Fact]
    public async Task The_lobby_in_Preview_is_in_the_headphones_live_and_never_on_stream()
    {
        _desk.Show("LJ");
        await Until(() => _desk.OnAir.Scene == "TT Full");
        await _director.SetVoicesAsync(_desk.OnAir);
        Assert.False(Muted("TT Voice LJ"));
        _desk.PreviewOnly("MAL");
        await Until(() => _director.CueHeard == "MAL");
        // Its own source, Monitor Only (OBS keeps it out of the stream and the recording), with the live link: no buffer.
        Assert.Equal(ObsDirector.MonitorOnly, _obs.MonitorType["TT Cue"]);
        Assert.Equal("https://vdo.ninja/?view=bv&password=x&cleanoutput&novideo", Url("TT Cue"));
        Assert.False(Muted("TT Cue"));
        List<string> log;
        lock (_obs) log = _obs.AudioLog.ToList();
        Assert.True(log.LastIndexOf("monitor TT Cue " + ObsDirector.MonitorOnly) < log.LastIndexOf("mute TT Cue false"), string.Join(" | ", log));
        Assert.Contains(_obs.Scenes["TT Quad"], i => i.Source == "TT Cue" && i.Enabled);
        // The stream is as it was: LJ's voice on, MAL's off, and nothing else is monitored.
        Assert.True(Muted("TT Voice MAL"));
        Assert.False(Muted("TT Voice LJ"));
        Assert.Equal(new[] { "TT Cue" }, _obs.MonitorType.Keys);

        // Another lobby in Preview: the same source plays it.
        _desk.PreviewOnly("LJ");
        await Until(() => _director.CueHeard == "LJ");
        Assert.Equal("https://vdo.ninja/?view=av&password=x&cleanoutput&novideo", Url("TT Cue"));

        // Set to "Monitor and Output" by hand in OBS (that's the stream too): muted at once, Monitor Only again, then back on.
        int mark;
        lock (_obs) { _obs.MonitorType["TT Cue"] = "OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT"; mark = _obs.AudioLog.Count; }
        await _obs.Event("InputAudioMonitorTypeChanged", new { inputName = "TT Cue", monitorType = "OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT" });
        await Until(() => { lock (_obs) return _obs.AudioLog.Skip(mark).Contains("mute TT Cue false"); });
        lock (_obs) log = _obs.AudioLog.Skip(mark).ToList();
        Assert.Equal("mute TT Cue true", log[0]);
        Assert.True(log.IndexOf("monitor TT Cue " + ObsDirector.MonitorOnly) < log.LastIndexOf("mute TT Cue false"), string.Join(" | ", log));
        Assert.Equal(ObsDirector.MonitorOnly, _obs.MonitorType["TT Cue"]);
        Assert.Equal("LJ", _director.CueHeard);

        // Mute all (the panic button) silences the headphones too.
        _director.Settings.Voice.MuteAll = true;
        await _director.VoiceChangedAsync();
        Assert.True(Muted("TT Cue"));
        Assert.Null(_director.CueHeard);
        Assert.Equal("about:blank", Url("TT Cue"));
        _director.Settings.Voice.MuteAll = false;
        await _director.VoiceChangedAsync();
        Assert.Equal("LJ", _director.CueHeard);

        // Turned off, or nothing in Preview: silent.
        _director.Settings.Voice.HearPreview = false;
        await _director.VoiceChangedAsync();
        Assert.True(Muted("TT Cue"));
        _director.Settings.Voice.HearPreview = true;
        await _director.VoiceChangedAsync();
        Assert.False(Muted("TT Cue"));
        _desk.PreviewClear();
        await Until(() => _director.CueHeard == null);
        Assert.True(Muted("TT Cue"));
    }

    [Fact]
    public async Task A_cue_left_in_OBS_from_before_is_silenced_on_connect()
    {
        await using var obs = new FakeObs();
        lock (obs) { obs.Inputs["TT Cue"] = new() { ["url"] = "https://vdo.ninja/?view=bv&password=x" }; obs.Muted["TT Cue"] = false; }
        using var desk = new CasterDesk(null, null, new PriorityConfig());
        await using var director = new ObsDirector(null, desk, () => _feeds.ToList()) { VoiceFeeds = () => _voices.ToList() };
        await director.ConnectAsync("127.0.0.1", obs.Port, "secret");
        lock (obs) Assert.True(obs.Muted["TT Cue"]);
        Assert.Null(director.CueHeard);
    }

    [Fact]
    public async Task Hear_Preview_waits_while_Desktop_Audio_could_carry_it_to_the_stream()
    {
        _desk.PreviewOnly("MAL");
        await Until(() => _director.CueHeard == "MAL");
        await _obs.Event("InputMuteStateChanged", new { inputName = "Desktop Audio", inputMuted = false });
        await Until(() => _director.CueHeard == null);
        Assert.True(Muted("TT Cue"));
        Assert.Contains("Desktop Audio", _director.CueProblem);
        await _obs.Event("InputMuteStateChanged", new { inputName = "Desktop Audio", inputMuted = true });
        await Until(() => _director.CueHeard == "MAL");
        Assert.Null(_director.CueProblem);
    }

    [Fact]
    public async Task The_setup_check_says_which_headphones_OBS_plays_Hear_Preview_in()
    {
        var check = _director.Checks.Single(c => c.Id == "monitor");
        Assert.True(check.Ok);                                          // Default: said, but most PCs play to the headphones
        Assert.Equal("Headphones (OBS monitoring device): Default", check.Title);
        Assert.Contains("Settings → Audio → Advanced → Monitoring Device", check.Detail);
        lock (_obs) { _obs.MonitorDeviceId = "{0.0.0.00000000}.{5d2c}"; _obs.MonitorDeviceName = "Headphones (USB Audio)"; }
        await _director.CheckSetupAsync();
        check = _director.Checks.Single(c => c.Id == "monitor");
        Assert.True(check.Ok);
        Assert.Equal("Headphones (OBS monitoring device): Headphones (USB Audio)", check.Title);
        lock (_obs) { _obs.MonitorDeviceId = "disabled"; _obs.MonitorDeviceName = "Disabled"; }
        await _director.CheckSetupAsync();
        Assert.Contains("it's off", _director.Checks.Single(c => c.Id == "monitor").Detail);
        Assert.False(_director.Checks.Single(c => c.Id == "monitor").Ok);
        _director.Settings.Voice.HearPreview = false;
        await _director.CheckSetupAsync();
        Assert.True(_director.Checks.Single(c => c.Id == "monitor").Ok);
    }
}
