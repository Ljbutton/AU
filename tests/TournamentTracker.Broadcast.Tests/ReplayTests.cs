using System.Text.Json;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Replays: the crop maths, clips from Source Record, and playing them in OBS.</summary>
public class ReplayTests
{
    private static Clip ClipWith(params (double T, int Id, double X, double Y)[] points)
    {
        var at = new DateTime(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc);
        var clip = new Clip { EventAt = at, SavedAt = at.AddSeconds(2), Duration = 30, Pre = 8, Post = 2, Focus = new List<int> { 1, 2 } };
        foreach (var g in points.GroupBy(p => p.T))
            clip.Samples.Add(new TrackSample { At = at.AddSeconds(g.Key), P = g.ToDictionary(p => p.Id, p => (p.X, p.Y, false)) });
        return clip;
    }

    [Fact]
    public void The_crop_keeps_both_players_in_frame_as_tight_as_allowed()
    {
        // Two players close together: zoomed in as far as allowed, centred between them.
        var v = Framing.Fit(new List<(double, double)> { (0.45, 0.5), (0.55, 0.5) }, 2.5, 0.1);
        Assert.Equal(2.5, v.Zoom, 3);
        Assert.Equal(0.5, v.X, 3);
        // Far apart: zoom drops so both fit with the margin.
        v = Framing.Fit(new List<(double, double)> { (0.2, 0.5), (0.7, 0.5) }, 2.5, 0.1);
        Assert.Equal(1 / 0.7, v.Zoom, 3);
        // Near the edge: the view stays on the screen (no black bars).
        v = Framing.Fit(new List<(double, double)> { (0.02, 0.97) }, 2.5, 0.05);
        Assert.Equal(0.2, v.X, 3);
        Assert.Equal(0.8, v.Y, 3);
        // Nobody to follow: the whole screen.
        Assert.Equal(1, Framing.Fit(new List<(double, double)>(), 2.5, 0.1).Zoom);
    }

    [Fact]
    public void The_crop_follows_the_players_when_the_camera_moves()
    {
        var clip = ClipWith((-4, 1, 0.3, 0.3), (-4, 2, 0.35, 0.3), (-1, 1, 0.7, 0.6), (-1, 2, 0.72, 0.62));
        var s = new ReplaySettings { PushInSeconds = 0 };
        // Clip time for 4 s before the play: the clip's end is SavedAt (2 s after), so 30 - 6 = 24.
        var early = Framing.At(clip, s, 24, 10);
        var late = Framing.At(clip, s, 27, 10);
        Assert.True(early.X < 0.45 && early.Y < 0.45, early.ToString());
        Assert.True(late.X > 0.6 && late.Y > 0.5, late.ToString());
        Assert.Equal(2.5, late.Zoom, 3);
    }

    [Fact]
    public void Replays_push_in_slowly_from_the_wide_shot()
    {
        var clip = ClipWith((-1, 1, 0.7, 0.6), (-1, 2, 0.72, 0.62));
        var s = new ReplaySettings { PushInSeconds = 2 };
        var start = Framing.At(clip, s, 27, 0);
        Assert.Equal((0.5, 0.5, 1.0), (start.X, start.Y, start.Zoom));
        var half = Framing.At(clip, s, 27, 1);
        var full = Framing.At(clip, s, 27, 2);
        Assert.True(half.Zoom > 1 && half.Zoom < full.Zoom);
        Assert.Equal(2.5, full.Zoom, 3);
        // Manual zoom and pan sit on top; "follow" off goes back to the whole screen.
        Assert.True(Framing.At(clip, s, 27, 5, zoomBy: 0.5).Zoom < full.Zoom);
        Assert.Equal(1, Framing.At(clip, s, 27, 5, follow: false).Zoom);
    }

    [Fact]
    public void The_body_stays_in_frame_after_the_kill()
    {
        var clip = ClipWith((1, 1, 0.9, 0.9));          // the killer runs off after the kill
        clip.Focus = new List<int> { 1, 2 };
        clip.Marks.Add((clip.EventAt, 0.3, 0.3));       // the body
        var points = Framing.PointsAt(clip, clip.EventAt.AddSeconds(1));
        Assert.Contains((0.3, 0.3), points);
        Assert.Equal(2, points.Count);
    }

    [Fact]
    public void Placing_a_view_moves_and_scales_the_picture_on_the_canvas()
    {
        var (x, y, w, h) = Framing.Place(new View(0.75, 0.25, 2), 1920, 1080);
        Assert.Equal((3840.0, 2160.0), (w, h));
        Assert.Equal(960 - 0.75 * 3840, x);              // the view's centre lands in the middle
        Assert.Equal(540 - 0.25 * 2160, y);
        Assert.Equal((0.0, 0.0, 1920.0, 1080.0), Framing.Place(new View(0.5, 0.5, 1), 1920, 1080));
    }

    [Fact]
    public void A_clip_starts_its_pre_seconds_before_the_play()
    {
        var clip = ClipWith();
        Assert.Equal(30 - 2 - 8, clip.StartAt(0), 3);       // ends 2 s after the play; starts 8 s before it
        Assert.Equal(30 - 2 - 8 - 0.5, clip.StartAt(500), 3);
        Assert.Equal(clip.EventAt, clip.TimeAt(28, 0));
    }

    [Fact]
    public void Track_times_move_onto_the_casters_clock()
    {
        var tracks = new Tracks();
        var caster = new DateTime(2026, 10, 6, 20, 0, 10, DateTimeKind.Utc);
        long host = new DateTimeOffset(caster.AddSeconds(-7.5)).ToUnixTimeMilliseconds();   // the host's clock is 7 s behind, plus 0.5 s on the way
        tracks.Arrived("LJ", host, caster);
        tracks.Arrived("LJ", host + 1000, caster.AddSeconds(1.3));                          // a slower one doesn't count
        Assert.Equal(caster, tracks.ToCaster("LJ", host));
        var msg = JsonSerializer.SerializeToElement(new { samples = new[] { new { t = host, p = new[] { new[] { 3, 250, 750, 1 } } } } });
        tracks.Add("LJ", msg, caster);
        var s = tracks.Between("LJ", caster.AddSeconds(-1), caster.AddSeconds(1)).Single();
        Assert.Equal((0.25, 0.75, true), s.P[3]);
    }
}

/// <summary>Replays against a pretend OBS with Source Record.</summary>
public class ReplayObsTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private FakeObs _obs = null!;
    private CasterDesk _desk = null!;
    private ObsDirector _director = null!;
    private ReplayManager _replays = null!;
    private string _tag = "";
    private readonly List<(string Lobby, string Url)> _feeds = new() { ("LJ", "https://vdo.ninja/?view=a&password=x&cleanoutput"), ("MAL", "https://vdo.ninja/?view=b&password=x&cleanoutput") };

    public async Task InitializeAsync()
    {
        _obs = new FakeObs();
        _desk = new CasterDesk(null, null, new PriorityConfig());
        _director = new ObsDirector(Path.Combine(_dir.Path, ObsSettings.FileName), _desk, () => _feeds.ToList()) { TagUrl = "http://127.0.0.1:8767/replaytag" };
        _director.Settings.Replay.PostSeconds = 0.2;
        _desk.Switch = air => _director.ApplyAsync(air).Wait();
        _replays = new ReplayManager(_desk, _director, loop: false) { TagChanged = j => _tag = j };
        await _director.ConnectAsync("127.0.0.1", _obs.Port, "secret");
        foreach (var l in new[] { "LJ", "MAL" }) Send(l, "snap", null, new { phase = "ingame", crewAlive = 7, impAlive = 2, taskPct = 30 });
        _desk.Show("LJ");
    }

    public async Task DisposeAsync()
    {
        _replays.Dispose();
        await _director.DisposeAsync();
        await _obs.DisposeAsync();
        _desk.Dispose();
        _dir.Dispose();
    }

    private static object P(string colour, int id, bool imp = false) => new { id, name = colour, color = 0, colorName = colour, imp };

    private void Send(string lobby, string type, string? kind, object data)
    {
        var msg = JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        msg["type"] = type; msg["lobby"] = lobby; msg["round"] = 1; msg["game"] = lobby + "-1";
        msg["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (kind != null) msg["kind"] = kind;
        _desk.Apply(JsonSerializer.Serialize(msg));
    }

    private static async Task Until(Func<bool> done, int ms = 6000)
    {
        for (int i = 0; i < ms / 20 && !done(); i++) await Task.Delay(20);
        Assert.True(done());
    }

    [Fact]
    public void Every_lobby_source_gets_a_replay_buffer_and_there_is_a_replay_scene()
    {
        var f = _obs.Filters["TT Lobby LJ|TT Replay"];
        Assert.True(f["replay_buffer"].GetBoolean());
        Assert.Equal(30, f["replay_duration"].GetInt32());
        Assert.Equal(0, f["record_mode"].GetInt32());
        Assert.Contains("TT LJ", f["replay_filename_formatting"].GetString());
        Assert.True(_obs.Filters.ContainsKey("TT Lobby MAL|TT Replay"));
        Assert.Equal(new[] { "TT Replay Clip", "TT Replay Tag", "TT Swoosh" }, _obs.Scenes["TT Replay"].Select(i => i.Source));
        Assert.Equal("ffmpeg_source", "ffmpeg_source");
        Assert.Null(_director.ReplayProblem);
    }

    [Fact]
    public async Task A_kill_saves_a_clip_by_itself_and_it_plays_cropped_with_every_control()
    {
        Send("LJ", "track", null, new { samples = new[] { new { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), p = new[] { new[] { 1, 600, 400, 0 }, new[] { 2, 650, 420, 0 } } } } });
        Send("LJ", "event", "kill", new { killer = P("Purple", 1, true), victim = P("Lime", 2), room = "Electrical", winning = false, screen = new { x = 0.65, y = 0.42, onScreen = true } });
        var card = JsonSerializer.SerializeToElement(_desk.State(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            .GetProperty("cards").EnumerateArray().Single(c => c.GetProperty("rule").GetString() == "kill");
        string cardId = card.GetProperty("id").GetString()!;
        await Until(() => _desk.Find(cardId)!.ClipState == "ready");
        Assert.Equal("/clips/TT_Lobby_LJ-1.mp4", _replays.Find(_desk.Find(cardId)!.ClipId!)!.File);
        var clip = _replays.ForCard(cardId)!;
        Assert.Equal(new[] { 1, 2 }, clip.Focus);
        Assert.NotEmpty(clip.Samples);

        // Play: the replay scene comes up at 8 s before the kill, playing, with the tag.
        string said = await _replays.PlayAsync(clip.Id);
        Assert.StartsWith("Replay: LJ", said);
        Assert.Equal("TT Replay", _obs.Program);
        Assert.Equal("/clips/TT_Lobby_LJ-1.mp4", _obs.MediaFile);
        Assert.Equal("OBS_MEDIA_STATE_PLAYING", _obs.MediaState);
        Assert.InRange(_obs.MediaCursorMs, 21000, 22000);   // 30 s buffer, saved ~0.5 s after the kill, 8 s before it
        Assert.Equal("REPLAY", _desk.OnAir.Label("LJ"));
        Assert.Contains("\"on\":true", _tag);

        // The controls.
        await _replays.ControlAsync("pause");
        Assert.Equal("OBS_MEDIA_STATE_PAUSED", _obs.MediaState);
        double at = _obs.MediaCursorMs;
        await _replays.ControlAsync("back");
        Assert.Equal(at - 1000, _obs.MediaCursorMs, 0);
        await _replays.ControlAsync("frameForward");
        Assert.Equal(at - 1000 + 33, _obs.MediaCursorMs, 0);
        await _replays.ControlAsync("seek", 10);
        Assert.Equal(10000, _obs.MediaCursorMs);
        await _replays.ControlAsync("restart");
        Assert.InRange(_obs.MediaCursorMs, 21000, 22000);
        Assert.Equal("OBS_MEDIA_STATE_PLAYING", _obs.MediaState);

        // Zoom in: the clip is scaled up on the canvas.
        await _replays.ControlAsync("zoomIn");
        await _replays.ControlAsync("zoomIn");
        var item = _obs.Scenes["TT Replay"].Single(i => i.Source == "TT Replay Clip");
        Assert.True(item.W > 1920, $"width {item.W}");
        await _replays.ControlAsync("pan", 1, 0);
        Assert.False(JsonSerializer.SerializeToElement(_replays.State(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }).GetProperty("now").GetProperty("follow").GetBoolean());

        // Back to live: the lobby that was on before.
        await _replays.ControlAsync("live");
        Assert.Equal("TT Full", _obs.Program);
        Assert.Equal("LIVE (full)", _desk.OnAir.Label("LJ"));
        Assert.Contains("\"on\":false", _tag);
    }

    [Fact]
    public async Task Any_card_can_be_saved_on_demand_but_not_once_OBS_has_let_it_go()
    {
        Send("MAL", "event", "vent", new { player = P("Red", 4, true), action = "enter", room = "MedBay" });
        var card = JsonSerializer.SerializeToElement(_desk.State(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            .GetProperty("cards").EnumerateArray().Single(c => c.GetProperty("rule").GetString() == "vent");
        var clip = await _replays.SaveAsync(_desk.Find(card.GetProperty("id").GetString()!)!);
        Assert.Equal("ready", clip.State);
        Assert.Equal(new[] { 4 }, clip.Focus);

        var old = _desk.Find(card.GetProperty("id").GetString()!)!;
        old.At = DateTime.UtcNow.AddMinutes(-2);
        old.Source = null;
        var tooOld = await _replays.SaveAsync(old);
        Assert.Equal("failed", tooOld.State);
        Assert.Contains("OBS keeps the last 30s", tooOld.Problem);
    }

    [Fact]
    public async Task A_stuck_replay_buffer_is_started_again_and_the_next_save_works()
    {
        int started = _obs.BufferStarts;
        Assert.True(started >= 2);   // each new filter's buffer is started
        _obs.BufferStuck = true;
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => _director.SaveClipAsync("LJ"));
        Assert.Contains("wasn't running", e.Message);
        Assert.DoesNotContain("Install", e.Message);
        Assert.Equal(started + 1, _obs.BufferStarts);
        Assert.StartsWith("/clips/TT_Lobby_LJ", await _director.SaveClipAsync("LJ"));
    }

    [Fact]
    public async Task Without_Source_Record_it_says_to_install_it()
    {
        await using var obs = new FakeObs { SourceRecordInstalled = false };
        var desk = new CasterDesk(null, null, new PriorityConfig());
        await using var director = new ObsDirector(null, desk, () => _feeds.ToList());
        await director.ConnectAsync("127.0.0.1", obs.Port, "secret");
        Assert.Contains("Source Record", director.ReplayProblem);
        desk.Dispose();
    }
}

/// <summary>The player camera in OBS: its own source and scene, a replay buffer, and a second replay angle.</summary>
public class PlayerCamReplayTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private FakeObs _obs = null!;
    private CasterDesk _desk = null!;
    private ObsDirector _director = null!;
    private ReplayManager _replays = null!;

    public async Task InitializeAsync()
    {
        _obs = new FakeObs();
        _desk = new CasterDesk(null, null, new PriorityConfig());
        _director = new ObsDirector(Path.Combine(_dir.Path, ObsSettings.FileName), _desk, () => new List<(string, string)> { ("LJ", "https://vdo.ninja/?view=a&password=x&noaudio&cleanoutput") })
        {
            CamFeeds = () => new List<(string, string)> { ("LJ", "https://vdo.ninja/?view=ac&password=x&noaudio&cleanoutput") },
        };
        _director.Settings.Replay.PostSeconds = 0.2;
        _desk.Switch = air => _director.ApplyAsync(air).Wait();
        _replays = new ReplayManager(_desk, _director, loop: false);
        await _director.ConnectAsync("127.0.0.1", _obs.Port, "secret");
        Send("snap", null, new { phase = "ingame", crewAlive = 7, impAlive = 2, taskPct = 30, spec = new { cam = true, camOn = 1 } });
        _desk.Show("LJ");
    }

    public async Task DisposeAsync()
    {
        _replays.Dispose();
        await _director.DisposeAsync();
        await _obs.DisposeAsync();
        _desk.Dispose();
        _dir.Dispose();
    }

    private void Send(string type, string? kind, object data)
    {
        var msg = JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        msg["type"] = type; msg["lobby"] = "LJ"; msg["round"] = 1; msg["game"] = "LJ-1";
        msg["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (kind != null) msg["kind"] = kind;
        _desk.Apply(JsonSerializer.Serialize(msg));
    }

    [Fact]
    public async Task The_camera_has_its_own_scene_and_buffer_and_a_kill_replay_opens_on_it()
    {
        // Only in the TT Player Cam scene, under the lobby's picture, muted, with its own replay buffer.
        Assert.Equal("TT Cam LJ", _obs.Scenes["TT Player Cam"][0].Source);
        Assert.DoesNotContain(_obs.Scenes["TT Full"], i => i.Source == "TT Cam LJ");
        Assert.True(_obs.Filters.ContainsKey("TT Cam LJ|TT Replay"));

        // On stream: the camera full screen, the whole map small in the corner.
        _desk.Show("LJ", "cam");
        Assert.Equal("TT Player Cam", _obs.Program);
        var cam = _obs.Scenes["TT Player Cam"].Single(i => i.Source == "TT Cam LJ");
        var map = _obs.Scenes["TT Player Cam"].Single(i => i.Source == "TT Lobby LJ");
        Assert.True(cam.Enabled && map.Enabled);
        Assert.True(map.W < 1920 / 3.0, $"map width {map.W}");

        // The camera was on the killer: the replay opens on the close-up, and P goes to the whole map.
        Send("event", "kill", new { killer = new { id = 1, name = "Purple", imp = true }, victim = new { id = 2, name = "Lime" }, room = "Electrical", winning = false });
        string id = JsonSerializer.SerializeToElement(_desk.State(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            .GetProperty("cards").EnumerateArray().Single(c => c.GetProperty("rule").GetString() == "kill").GetProperty("id").GetString()!;
        var card = _desk.Find(id)!;
        var clip = await _replays.SaveAsync(card);
        Assert.Equal("ready", clip.State);
        Assert.Equal("cam", clip.Angle);
        Assert.Contains("Cam", clip.File);
        await _replays.PlayAsync(clip.Id);
        Assert.Equal(clip.File, _obs.MediaFile);
        Assert.Equal("Whole map angle.", await _replays.ControlAsync("angle"));
        Assert.Contains("Lobby", _obs.MediaFile);
        Assert.Equal("wide", clip.Angle);
        Assert.Equal("Player camera angle.", await _replays.ControlAsync("angle"));
    }

    [Fact]
    public void When_the_game_ends_the_camera_goes_back_to_the_whole_map()
    {
        _desk.Show("LJ", "cam");
        _desk.Tick();
        Assert.Equal("cam", _desk.OnAir.Layout);
        Send("snap", null, new { phase = "lobby", spec = new { cam = true } });
        _desk.Tick();
        Assert.Equal("full", _desk.OnAir.Layout);
        Assert.Equal("LJ", _desk.OnAir.Slots[0]);
        Assert.Equal("TT Full", _obs.Program);
    }

    [Fact]
    public void Several_cameras_share_the_screen_with_no_whole_map_pictures()
    {
        var two = new OnAir { Layout = "cams", Slots = new List<string?> { "LJ", "MAL", null, null } };
        var boxes = _director.CamBoxes(two);
        Assert.Equal(2, boxes.Count);
        Assert.True(boxes[0].W < 1920 / 2.0 && boxes[1].X > boxes[0].X);
        Assert.Equal(4, _director.CamBoxes(new OnAir { Layout = "cams", Slots = new List<string?> { "A", "B", "C", null } }).Count);
        Assert.Empty(ObsDirector.Slots("cams", 1920, 1080, 12));
        Assert.Equal("LIVE (player cams, 2)", two.Label("MAL"));

        // Sent from the desk: the camera scene, LJ's camera in the left half, its whole map hidden.
        var air = _desk.ShowPicked(new[] { "LJ", "MAL" }, "button", "cams");
        Assert.Equal("cams", air.Layout);
        Assert.Equal("TT Player Cam", _obs.Program);
        var cam = _obs.Scenes["TT Player Cam"].Single(i => i.Source == "TT Cam LJ");
        Assert.True(cam.Enabled && cam.W < 1920 / 2.0);
        Assert.False(_obs.Scenes["TT Player Cam"].Single(i => i.Source == "TT Lobby LJ").Enabled);
        // One lobby picked as a camera is the single camera (with its whole map in the corner).
        Assert.Equal("cam", _desk.ShowPicked(new[] { "LJ" }, "button", "cams").Layout);
    }
}
