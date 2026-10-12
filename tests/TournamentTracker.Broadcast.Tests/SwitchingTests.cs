using System.Text.Json;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// The switching engine against a pretend OBS: presses at once end where the desk says, a switch
/// is one batch that only sends what changed, a failing upkeep doesn't lose the switch, mute all
/// goes out at once, what OBS is sending out (stream, recording, disk) is read and warned about,
/// and "replay that" with slow motion.
/// </summary>
public class SwitchingTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private FakeObs _obs = null!;
    private CasterDesk _desk = null!;
    private ObsDirector _director = null!;
    private readonly List<(string Lobby, string Url)> _feeds = new() { ("LJ", "https://vdo.ninja/?view=a&password=x&cleanoutput"), ("MAL", "https://vdo.ninja/?view=b&password=x&cleanoutput"), ("Soggy", "https://vdo.ninja/?view=c&password=x&cleanoutput") };
    private readonly List<(string Lobby, string Url)> _voices = new() { ("LJ", "https://vdo.ninja/?view=av&password=x"), ("MAL", "https://vdo.ninja/?view=bv&password=x"), ("Soggy", "https://vdo.ninja/?view=cv&password=x") };

    public async Task InitializeAsync()
    {
        _obs = new FakeObs();
        _desk = new CasterDesk(null, null, new PriorityConfig());
        _director = new ObsDirector(Path.Combine(_dir.Path, ObsSettings.FileName), _desk, () => _feeds.ToList()) { VoiceFeeds = () => _voices.ToList() };
        _director.Settings.Swoosh.TransitionPointMs = 30;
        // As Red Alert does it: each switch handed to OBS without waiting for it.
        _desk.Switch = air => { if (_director.Connected) _ = _director.ApplyAsync(air, air.Transition); };
        foreach (var l in new[] { "LJ", "MAL", "Soggy" })
            _desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = l, round = 1, phase = "ingame", crewAlive = 7, impAlive = 2, taskPct = 30 }));
        await _director.ConnectAsync("127.0.0.1", _obs.Port, "secret");
    }

    public async Task DisposeAsync()
    {
        await _director.DisposeAsync();
        await _obs.DisposeAsync();
        _desk.Dispose();
        _dir.Dispose();
    }

    private static async Task Until(Func<bool> done, int ms = 6000)
    {
        for (int i = 0; i < ms / 20 && !done(); i++) await Task.Delay(20);
        Assert.True(done());
    }

    private string? ShownIn(string scene) { lock (_obs) return _obs.Scenes[scene].Where(i => i.Enabled && i.Source.StartsWith("TT Lobby ")).Select(i => i.Source.Substring("TT Lobby ".Length)).SingleOrDefault(); }
    private List<string> RequestsSince(int count) { lock (_obs) return _obs.Requests.Skip(count).ToList(); }
    private int RequestCount { get { lock (_obs) return _obs.Requests.Count; } }
    private List<string> MutesSince(int count) { lock (_obs) return _obs.MuteLog.Skip(count).ToList(); }
    private int MuteCount { get { lock (_obs) return _obs.MuteLog.Count; } }

    [Fact]
    public async Task Twenty_presses_at_once_end_with_OBS_showing_what_the_desk_says()
    {
        _obs.RequestDelayMs = 3;                      // a busy OBS: switches overlap
        var lobbies = new[] { "LJ", "MAL", "Soggy" };
        var presses = Enumerable.Range(0, 20).Select(i => Task.Run(() => _desk.Show(lobbies[i % 3], "full", null, null, "deck"))).ToArray();
        await Task.WhenAll(presses);
        await Until(() => !_director.Switching && _director.SwitchingTo == null);
        Assert.Equal("full", _desk.OnAir.Layout);
        Assert.Equal("TT Full", _obs.Program);
        Assert.Equal(_desk.OnAir.Slots[0], ShownIn("TT Full"));
        // Its sound too: only the lobby on stream is heard.
        foreach (var l in lobbies) Assert.Equal(l != _desk.OnAir.Slots[0], _obs.Muted["TT Lobby " + l]);
        Assert.Equal("TT Full", _desk.OnAir.Scene);
    }

    [Fact]
    public async Task A_switch_is_one_batch_that_sends_only_what_changed()
    {
        await _director.ApplyAsync(new OnAir { Layout = "full", Slots = new List<string?> { "LJ" } });
        Assert.False(_obs.Muted["TT Voice LJ"]);
        int batches = _obs.Batches, before = RequestCount, mutesBefore = MuteCount;
        await _director.ApplyAsync(new OnAir { Layout = "full", Slots = new List<string?> { "MAL" } }, "cut");
        var sent = RequestsSince(before);
        Assert.Equal(batches + 1, _obs.Batches);
        Assert.Equal("MAL", ShownIn("TT Full"));
        // No source upkeep on the way (every lobby has its source) and no swoosh for a cut.
        Assert.DoesNotContain("GetInputList", sent);
        Assert.DoesNotContain("TriggerMediaInputAction", sent);
        // Only the mutes that changed: LJ's picture off and MAL's on (Soggy was already muted), then
        // the voices the same way.
        var mutes = MutesSince(mutesBefore);
        Assert.Equal(new[] { "TT Lobby LJ", "TT Lobby MAL" }, mutes.Take(2));
        Assert.Equal(new[] { "TT Voice LJ", "TT Voice MAL" }, mutes.Skip(2).OrderBy(x => x));
        Assert.True(_obs.Muted["TT Lobby LJ"]);
        Assert.False(_obs.Muted["TT Lobby MAL"]);
        Assert.False(_obs.Muted["TT Voice MAL"]);

        // Muted by hand in OBS: OBS says so, and the next switch puts it right.
        lock (_obs) _obs.Muted["TT Lobby MAL"] = true;
        await _obs.Event("InputMuteStateChanged", new { inputName = "TT Lobby MAL", inputMuted = true });
        await Task.Delay(200);
        mutesBefore = MuteCount;
        await _director.ApplyAsync(new OnAir { Layout = "full", Slots = new List<string?> { "MAL" } }, "cut");
        Assert.Equal(new[] { "TT Lobby MAL" }, MutesSince(mutesBefore));
        Assert.False(_obs.Muted["TT Lobby MAL"]);
    }

    [Fact]
    public async Task The_upkeep_reads_each_scene_once_and_leaves_the_voices_where_they_are()
    {
        // Settled after connecting: a full look again reads each scene at most once and moves nothing.
        await _director.BuildAsync();
        await _director.EnsureVoiceAsync();
        int before = RequestCount;
        await _director.BuildAsync();
        await _director.EnsureVoiceAsync();
        var sent = RequestsSince(before);
        Assert.True(sent.Count(r => r == "GetSceneItemList") <= _director.Settings.Scenes.Count + 1, string.Join(",", sent));
        Assert.DoesNotContain("SetSceneItemIndex", sent);
        // The voices at the bottom of every scene.
        foreach (var scene in new[] { "TT Full", "TT Quad", "TT Replay" })
            lock (_obs) Assert.All(_obs.Scenes[scene].Take(3), i => Assert.StartsWith("TT Voice", i.Source));
        // Nothing changed in OBS: the voice upkeep reads no scene and moves nothing.
        before = RequestCount;
        await _director.EnsureVoiceAsync();
        sent = RequestsSince(before);
        Assert.DoesNotContain("SetSceneItemIndex", sent);
        Assert.DoesNotContain("GetSceneItemList", sent);

        // Taken out of a scene by hand: OBS says so, and the next look puts it back (at the bottom).
        int gone;
        lock (_obs)
        {
            var item = _obs.Scenes["TT Full"].Single(i => i.Source == "TT Voice MAL");
            gone = item.Id;
            _obs.Scenes["TT Full"].Remove(item);
        }
        await _obs.Event("SceneItemRemoved", new { sceneName = "TT Full", sourceName = "TT Voice MAL", sceneItemId = gone });
        await Task.Delay(200);
        await _director.EnsureVoiceAsync();
        lock (_obs)
        {
            Assert.Contains(_obs.Scenes["TT Full"], i => i.Source == "TT Voice MAL");
            Assert.All(_obs.Scenes["TT Full"].Take(3), i => Assert.StartsWith("TT Voice", i.Source));
        }
    }

    [Fact]
    public async Task A_failing_upkeep_on_the_way_doesnt_lose_the_switch()
    {
        _feeds.Add(("Kai", "https://vdo.ninja/?view=d&password=x&cleanoutput"));
        _desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = "Kai", round = 1, phase = "ingame", crewAlive = 7, impAlive = 2, taskPct = 30 }));
        lock (_obs) _obs.Fail.Add("GetInputList");                   // the upkeep a new lobby needs fails
        await _director.ApplyAsync(new OnAir { Layout = "4up", Slots = new List<string?> { "LJ", "Kai", "MAL", null } });
        Assert.Equal("TT Quad", _obs.Program);
        Assert.Null(_director.SwitchingTo);                            // not left "switching" forever
        Assert.Null(_director.SwitchProblem);                          // the switch itself went through
        var shown = _obs.Shown("TT Quad").Select(i => i.Source).Where(s => s.StartsWith("TT Lobby ")).OrderBy(s => s);
        Assert.Equal(new[] { "TT Lobby LJ", "TT Lobby MAL" }, shown);
        lock (_obs) _obs.Fail.Clear();
    }

    [Fact]
    public async Task Mute_all_goes_out_at_once_without_the_voice_upkeep()
    {
        await _director.ApplyAsync(new OnAir { Layout = "full", Slots = new List<string?> { "LJ" } });
        Assert.False(_obs.Muted["TT Voice LJ"]);
        int before = RequestCount;
        _director.Settings.Voice.MuteAll = true;
        await _director.VoiceChangedAsync();
        var sent = RequestsSince(before);
        Assert.True(_obs.Muted["TT Voice LJ"]);
        Assert.Equal(new[] { "SetInputMute" }, sent.Distinct());
        _director.Settings.Voice.MuteAll = false;
        await _director.VoiceChangedAsync();
        Assert.False(_obs.Muted["TT Voice LJ"]);
    }

    [Fact]
    public async Task What_OBS_sends_out_is_read_and_warned_about()
    {
        lock (_obs) { _obs.Streaming = true; _obs.RecordingOn = false; _obs.DiskMb = 4000; _obs.StreamFrames = 1000; }
        await _director.OutputsAsync();
        lock (_obs) { _obs.StreamFrames = 2000; _obs.StreamSkipped = 50; _obs.StreamBytes = 1_000_000; }
        await _director.OutputsAsync();
        Assert.True(_director.Streaming);
        Assert.False(_director.Recording);
        var warnings = _director.OutputWarnings();
        Assert.Contains(warnings, w => w.Text.Contains("isn't recording"));
        Assert.Contains(warnings, w => w.Level == "bad" && w.Text.Contains("GB free"));
        Assert.Contains(warnings, w => w.Text.Contains("dropping frames"));
        // The stream stopping in OBS is known at once (its event), and said in red.
        bool told = false;
        _director.OutputChanged += () => told = true;
        await _obs.SetStreaming(false);
        await Until(() => !_director.Streaming);
        Assert.True(told);
        Assert.Contains(_director.OutputWarnings(), w => w.Level == "bad" && w.Text.Contains("stopped"));
        var view = JsonSerializer.SerializeToElement(_director.Status(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.True(view.GetProperty("output").GetProperty("known").GetBoolean());
    }

    [Fact]
    public async Task The_program_picture_comes_from_OBS_and_is_reused_for_a_moment()
    {
        var jpg = await _director.ProgramShotAsync();
        Assert.NotNull(jpg);
        Assert.Equal(0xFF, jpg![0]);
        Assert.Equal(0xD8, jpg[1]);
        await _director.ProgramShotAsync();
        await _director.ProgramShotAsync();
        Assert.Equal(1, _obs.Screenshots);
        await _director.DisconnectAsync();
        Assert.Null(await _director.ProgramShotAsync());
    }

    [Fact]
    public async Task Replay_that_saves_the_last_seconds_and_plays_them_in_slow_motion_and_last_plays_it_again()
    {
        using var replays = new ReplayManager(_desk, _director, loop: false);
        _desk.Show("LJ");
        await Until(() => !_director.Switching && _obs.Program == "TT Full");

        // "Replay that": the on-air lobby's last seconds, no card, played once saved.
        var clip = replays.SaveNow("LJ");
        Assert.Equal("instant", clip.Rule);
        await Until(() => replays.Now == clip && _obs.Program == "TT Replay");
        Assert.Equal("ready", clip.State);
        Assert.Null(clip.KeyPlayer);
        // From the seconds before it was asked for (the clip is 30 s, the last 8 or so played).
        Assert.InRange(_obs.MediaCursorMs, 21000, 22500);

        // Slow motion in turn: 50%, 25%, back to 100%; quiet while slowed.
        Assert.Null(await replays.ControlAsync("slow"));
        Assert.Equal(50, replays.Speed);
        lock (_obs) Assert.Equal("50", _obs.Inputs["TT Replay Clip"]["speed_percent"]?.ToString());
        Assert.True(_obs.Muted["TT Replay Clip"]);
        await replays.ControlAsync("slow");
        Assert.Equal(25, replays.Speed);
        await replays.ControlAsync("slow");
        Assert.Equal(100, replays.Speed);
        Assert.False(_obs.Muted["TT Replay Clip"]);

        // Back to live, then "last" is that clip again, from normal speed.
        await replays.ControlAsync("slow");
        Assert.Equal(50, replays.Speed);
        await replays.LiveAsync();
        await Until(() => !_director.Switching && _obs.Program == "TT Full");
        Assert.Equal("LJ", ShownIn("TT Full"));
        Assert.Same(clip, replays.Newest());
        await replays.PlayAsync(replays.Newest()!);
        Assert.Equal(100, replays.Speed);
        Assert.Equal("TT Replay", _obs.Program);
    }

    [Fact]
    public async Task Replay_that_stays_ready_when_a_panic_or_another_lobby_came_on_while_it_saved()
    {
        using var replays = new ReplayManager(_desk, _director, loop: false);
        _desk.Show("LJ");
        await Until(() => !_director.Switching && _obs.Program == "TT Full");

        // Panic while it saves: the be-right-back screen stays on, the clip waits for "last".
        var clip = replays.SaveNow("LJ");
        _desk.SetHold(true, null, "Panic");
        _desk.ShowSlate("deck", "fade");
        await Until(() => clip.State == "ready");
        await Task.Delay(300);
        Assert.Null(replays.Now);
        Assert.Equal("slate", _desk.OnAir.Layout);
        await Until(() => !_director.Switching);
        Assert.Equal("TT Be Right Back", _obs.Program);
        Assert.Same(clip, replays.Newest());

        // Another lobby put on while it saves: no replay over it either.
        _desk.SetHold(false);
        _desk.Show("LJ");
        var second = replays.SaveNow("LJ");
        _desk.Show("MAL");
        await Until(() => second.State == "ready");
        await Task.Delay(300);
        Assert.Null(replays.Now);
        Assert.Equal("MAL", _desk.OnAir.Slots[0]);
        Assert.Same(second, replays.Newest());

        // Held already when pressed (a caster who keeps the hold on): it still plays.
        _desk.SetHold(true);
        var third = replays.SaveNow("MAL");
        await Until(() => replays.Now == third && _obs.Program == "TT Replay");
    }

    [Fact]
    public async Task A_lobby_source_made_again_under_its_name_is_muted_by_the_next_switch()
    {
        await _director.ApplyAsync(new OnAir { Layout = "full", Slots = new List<string?> { "MAL" } }, "cut");
        Assert.True(_obs.Muted["TT Lobby LJ"]);
        // Deleted in OBS (a stuck source): OBS says so, and the upkeep makes it again. A new input is unmuted.
        lock (_obs)
        {
            _obs.Inputs.Remove("TT Lobby LJ");
            foreach (var scene in _obs.Scenes.Values) scene.RemoveAll(i => i.Source == "TT Lobby LJ");
            _obs.Muted.Remove("TT Lobby LJ");
        }
        await _obs.Event("InputRemoved", new { inputName = "TT Lobby LJ" });
        await Task.Delay(200);
        await _director.BuildAsync();
        Assert.Contains("TT Lobby LJ", _obs.Inputs.Keys);
        Assert.False(_obs.Muted.GetValueOrDefault("TT Lobby LJ"));
        // The next switch with LJ out of slot 1 mutes it: only MAL's game sound is heard.
        await _director.ApplyAsync(new OnAir { Layout = "2up", Slots = new List<string?> { "MAL", "LJ" } }, "cut");
        Assert.True(_obs.Muted.GetValueOrDefault("TT Lobby LJ"));
        Assert.False(_obs.Muted["TT Lobby MAL"]);
    }

    [Fact]
    public async Task Overlapping_cuts_and_fades_end_on_the_casters_own_transition()
    {
        lock (_obs) { _obs.Transitions.Add(("Luma", "wipe_transition")); _obs.CurrentTransition = "Luma"; }
        string Current() { lock (_obs) return _obs.CurrentTransition; }

        // A cut, and a fade straight after (before the cut's put-back): back to Luma, not to the cut.
        await _director.ApplyAsync(new OnAir { Layout = "full", Slots = new List<string?> { "LJ" } }, "cut");
        Assert.Equal("Cut", Current());
        await _director.ApplyAsync(new OnAir { Layout = "4up", Slots = new List<string?> { "LJ", "MAL", null, null } }, "fade");
        Assert.Equal("Fade", Current());
        await Task.Delay(2000);
        Assert.Equal("Luma", Current());

        // A fade, and a cut near the end of it: back to Luma, not to the fade.
        await _director.ApplyAsync(new OnAir { Layout = "full", Slots = new List<string?> { "MAL" } }, "fade");
        Assert.Equal("Fade", Current());
        await Task.Delay(1200);
        await _director.ApplyAsync(new OnAir { Layout = "4up", Slots = new List<string?> { "MAL", "LJ", null, null } }, "cut");
        Assert.Equal("Cut", Current());
        await Task.Delay(1000);
        Assert.Equal("Luma", Current());

        // Picked by hand in OBS while a cut of ours is in: it stays.
        await _director.ApplyAsync(new OnAir { Layout = "full", Slots = new List<string?> { "LJ" } }, "cut");
        lock (_obs) _obs.CurrentTransition = "Fade";
        await Task.Delay(700);
        Assert.Equal("Fade", Current());
    }
}

/// <summary>The desk's controls without OBS: the hold, Preview and Take, and lobby numbers that stay the same.</summary>
public class DeskControlTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    public void Dispose() => _dir.Dispose();

    private static void Snap(CasterDesk desk, string lobby, string phase) =>
        desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby, round = 1, phase, crewAlive = 7, impAlive = 2, taskPct = 30 }));

    [Fact]
    public void Hold_stops_every_automatic_switch_and_says_what_it_held_back()
    {
        using var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig()) { AutoGrid = true };
        Snap(desk, "LJ", "ingame");
        Snap(desk, "MAL", "ingame");
        desk.Show("LJ");
        // Both lobbies in a meeting: auto grid would come up, but switching is held.
        Snap(desk, "LJ", "meeting");
        Snap(desk, "MAL", "meeting");
        desk.SetHold(true);
        desk.Tick();
        Assert.Equal("full", desk.OnAir.Layout);
        Assert.Equal("Auto grid", desk.HeldBack?.Text);
        var hold = JsonSerializer.SerializeToElement(desk.HoldState());
        Assert.True(hold.GetProperty("On").GetBoolean());
        Assert.Equal("Auto grid", hold.GetProperty("HeldBack").GetProperty("Text").GetString());
        // The caster's own switches still go through.
        desk.Show("MAL");
        Assert.Equal("MAL", desk.OnAir.Slots[0]);

        // A hold for a while ends by itself.
        desk.SetHold(true, TimeSpan.FromSeconds(30));
        _clock.Advance(10);
        Snap(desk, "LJ", "meeting");
        Snap(desk, "MAL", "meeting");
        desk.Tick();
        Assert.Equal("full", desk.OnAir.Layout);
        Assert.True(desk.Held);
        _clock.Advance(25);
        Snap(desk, "LJ", "meeting");
        Snap(desk, "MAL", "meeting");
        Assert.False(desk.Held);
        desk.Tick();
        Assert.Equal("grid", desk.OnAir.Layout);
        Assert.Equal("auto", desk.OnAir.By);

        // The camera going back to the map waits too.
        desk.AutoGrid = false;
        desk.SetHold(true);
        desk.Show("LJ", "cam");
        desk.Tick();
        Assert.Equal("cam", desk.OnAir.Layout);
        Assert.Contains("camera", desk.HeldBack?.Text);
        desk.SetHold(false);
        Assert.Null(desk.HeldBack);
        desk.Tick();
        Assert.Equal("full", desk.OnAir.Layout);
        Assert.Equal("LJ", desk.OnAir.Slots[0]);
    }

    [Fact]
    public void An_armed_camera_waits_out_intermission_and_a_switch_by_hand_cancels_it()
    {
        using var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        Snap(desk, "LJ", "lobby");
        Snap(desk, "MAL", "meeting");
        desk.Show("LJ", "full", null, null, "deck");
        desk.Arm("MAL", null);
        // Intermission came by itself; MAL's meeting ends: the camera doesn't cut over intermission.
        desk.ShowIntermission("auto");
        Snap(desk, "MAL", "ingame");
        desk.Tick();
        Assert.Equal("intermission", desk.OnAir.Layout);
        Assert.True(desk.Armed("MAL").Armed);
        // Intermission over (by itself, as auto intermission ends it): then the armed camera goes on.
        desk.AutoIntermission = true;
        desk.Tick();
        Assert.Equal(("full", "MAL", "auto"), (desk.OnAir.Layout, desk.OnAir.Slots[0], desk.OnAir.By));
        desk.Tick();
        Assert.Equal(("cam", "MAL", "armed"), (desk.OnAir.Layout, desk.OnAir.Slots[0], desk.OnAir.By));
        desk.AutoIntermission = false;

        // Armed again in a meeting, then the caster takes another lobby by hand: the pick is cancelled
        // (and the desk is told), so it doesn't pull the stream off that lobby when the meeting ends.
        Snap(desk, "MAL", "meeting");
        Snap(desk, "LJ", "ingame");
        desk.Arm("MAL", null);
        desk.Arm("LJ", null);
        desk.Show("LJ", "full", null, null, "deck");
        Assert.False(desk.Armed("MAL").Armed);
        Assert.True(desk.Armed("LJ").Armed);                     // the lobby taken keeps its own
        Assert.Contains("MAL", desk.ArmedDropped?.Text);
        Assert.Contains("MAL", JsonSerializer.SerializeToElement(desk.State()).GetProperty("ArmedDropped").GetProperty("Text").GetString());
        Snap(desk, "MAL", "ingame");
        desk.Tick();
        Assert.Equal("LJ", desk.OnAir.Slots[0]);
        // The same for intermission put on by hand: nothing armed is left to cut over it.
        desk.Arm("MAL", null);
        desk.ShowIntermission("button");
        Assert.False(desk.Armed("MAL").Armed);
    }

    [Fact]
    public void Ending_intermission_goes_back_to_the_player_cameras_with_the_transition_asked_for()
    {
        using var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        foreach (var l in new[] { "LJ", "MAL", "Soggy" }) Snap(desk, l, "ingame");
        desk.Show("MAL", "cam", null, null, "deck");
        desk.ShowIntermission("deck");
        var back = desk.EndBreak("deck", "cut")!;
        Assert.Equal(("cam", "MAL", "deck", "cut"), (back.Layout, back.Slots[0], back.By, back.Transition));
        // Several cameras at once come back the same.
        desk.ShowPicked(new[] { "LJ", "Soggy" }, "deck", "cams");
        desk.ShowIntermission("deck");
        back = desk.EndIntermission("button", "fade");
        Assert.Equal("cams", back.Layout);
        Assert.Equal(new[] { "LJ", "Soggy" }, back.Slots.Where(x => x != null));
        Assert.Equal("fade", back.Transition);
    }

    [Fact]
    public void Lobby_numbers_stay_the_same_after_a_restart_and_a_label_keeps_its_own_number()
    {
        using (var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path))
        {
            Assert.Equal(1, desk.NumberOf("LJ"));
            Assert.Equal(3, desk.NumberOf("Lobby 3"));              // the number at the end of its label
            Assert.Equal(2, desk.NumberOf("MAL"));
            Assert.Equal(4, desk.NumberOf("Lobby 1"));              // 1 is taken: the lowest free one
            Assert.Equal(5, desk.NumberOf("SIM-2"));                // simulation's lobbies aren't kept
        }
        using (var again = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path))
        {
            Assert.Equal(2, again.NumberOf("MAL"));                 // asked first, still 2
            Assert.Equal(4, again.NumberOf("Lobby 1"));
            Assert.Equal(1, again.NumberOf("LJ"));
            Assert.Equal(3, again.NumberOf("Lobby 3"));
            Assert.Equal(5, again.NumberOf("Kai"));
            Assert.Equal("Lobby 1", again.LobbyNumbered(4, new[] { "LJ", "Lobby 1", "Kai" }));
        }
        // Days later, a label not seen for a long time gives its number up.
        _clock.Advance(TimeSpan.FromDays(2).TotalSeconds);
        using var later = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path);
        Assert.Equal(1, later.NumberOf("New"));
    }

    [Fact]
    public void Lobby_numbers_can_be_set_by_hand_and_the_online_lobbies_numbered_1_to_N_afresh()
    {
        using (var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path))
        {
            foreach (var l in new[] { "Ann", "Bo", "Cy" }) { Snap(desk, l, "ingame"); desk.NumberOf(l); }
            Assert.Equal(new[] { ("Ann", 1), ("Bo", 2), ("Cy", 3) }, desk.Numbers().Select(n => (n.Lobby, n.No)));
            // Cy to 1: Ann, which had it, takes Cy's 3.
            desk.SetNumbers(new[] { ("Cy", 1) });
            Assert.Equal(new[] { ("Cy", 1), ("Bo", 2), ("Ann", 3) }, desk.Numbers().Select(n => (n.Lobby, n.No)));
            Assert.Equal("Cy", desk.LobbyNumbered(1, new[] { "Ann", "Bo", "Cy" }));
            // Out of range: left as it is.
            desk.SetNumbers(new[] { ("Bo", 0), ("Bo", 100) });
            Assert.Equal(2, desk.NumberOf("Bo"));

            // The next day (or round): other lobbies, which got the next free numbers.
            _clock.Advance(TimeSpan.FromHours(1).TotalSeconds);
            foreach (var l in new[] { "Dee", "Eve" }) { Snap(desk, l, "ingame"); desk.NumberOf(l); }
            Assert.Equal((4, 5), (desk.NumberOf("Dee"), desk.NumberOf("Eve")));
            Assert.Equal(new[] { "Dee", "Eve" }, desk.Numbers().Where(n => n.Online).Select(n => n.Lobby));
            // Renumbered: the lobbies sending now are 1 and 2; the others give their numbers up (one still
            // on the desk gets the next free number after them).
            Assert.Equal(new[] { "Dee", "Eve" }, desk.RenumberOnline());
            Assert.Equal(new[] { ("Dee", 1), ("Eve", 2) }, desk.Numbers().Where(n => n.Online).Select(n => (n.Lobby, n.No)));
            Assert.All(desk.Numbers().Where(n => !n.Online), n => Assert.True(n.No > 2));
        }
        // Kept across a restart.
        using var again = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path);
        Assert.Equal(1, again.NumberOf("Dee"));
        Assert.Equal(2, again.NumberOf("Eve"));
    }

    [Fact]
    public void Preview_builds_up_and_Take_puts_it_on_in_its_layout()
    {
        using var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        foreach (var l in new[] { "LJ", "MAL", "Soggy" }) Snap(desk, l, "ingame");
        Assert.Null(desk.Take());
        desk.PreviewOnly("MAL");
        desk.PreviewAdd("LJ");
        Assert.Equal("2up", desk.Preview.Layout);
        Assert.Equal("pvw", CasterDesk.Tally("LJ", desk.OnAir, desk.Preview));
        desk.PreviewAdd("LJ");                                      // again: taken out
        Assert.Equal(new[] { "MAL" }, desk.Preview.Slots);
        desk.PreviewAdd("Soggy");
        var air = desk.Take("cams", "cut")!;
        Assert.Equal("cams", air.Layout);
        Assert.Equal("cut", air.Transition);
        Assert.Equal(new string?[] { "MAL", "Soggy", null, null }, air.Slots);
        Assert.Empty(desk.Preview.Slots);
        Assert.Equal("pgm", CasterDesk.Tally("MAL", desk.OnAir, desk.Preview));
        Assert.Equal("none", CasterDesk.Tally("LJ", desk.OnAir, desk.Preview));
        // A layout picked with nothing staged starts from what's on.
        desk.PreviewLayout("quad");
        Assert.Equal("4up", desk.Preview.Layout);
        Assert.Equal(new[] { "MAL", "Soggy" }, desk.Preview.Slots);
        desk.PreviewClear();
        // Undo: back to what was on before.
        desk.Show("LJ");
        Assert.Equal("cams", desk.Back()!.Layout);
        Assert.Equal("full", desk.Back()!.Layout);                  // and again: the undo itself undone
    }
}
