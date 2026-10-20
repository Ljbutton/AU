using System.Net;
using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// The Stream Deck's links as the Red Alert plugin uses them: status codes a key can show, Preview
/// and Take, player cameras by lobby and slot (armed while a lobby is in a meeting), the long-polled
/// state, hold, panic and undo.
/// </summary>
public class StreamDeckTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private BroadcastServer _server = null!;
    private HttpClient _app = null!, _deck = null!;
    private string _key = "";
    private FakeObs? _obs;

    public async Task InitializeAsync()
    {
        // A swoosh video already there (none made with ffmpeg), and switches at once.
        string folder = Path.Combine(_dir.Path, "RedAlert");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "swoosh.webm"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(folder, ObsSettings.FileName), JsonSerializer.Serialize(new { swoosh = new { path = Path.Combine(folder, "swoosh.webm"), transitionPointMs = 0 } }));
        _server = new BroadcastServer(new BroadcastEnvironment { SettingsFile = Path.Combine(_dir.Path, "RedAlert", "settings.json"), Port = 0, CasterPort = 0, Version = "0.1.0" }, new HttpClient(new FakeHttp()));
        _app = AppHttp.Client(_server.Url, _server.Token);
        _deck = new HttpClient { BaseAddress = new Uri(_server.Url) };
        var admin = new SetupCode { Mode = "admin", TournamentId = "c", TournamentName = "Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results" };
        Assert.True((await Post("app/admin/code", new { code = admin.Encode() })).GetProperty("ok").GetBoolean());
        string link = (await Get("app/admin/deck")).GetProperty("links")[0].GetProperty("url").GetString()!;
        _key = link.Substring(link.IndexOf("key=") + 4);
    }

    public async Task DisposeAsync()
    {
        _server.Dispose();
        if (_obs != null) await _obs.DisposeAsync();
        _dir.Dispose();
    }

    private Task<JsonElement> Get(string path) => AppHttp.Get(_app, path);
    private Task<JsonElement> Post(string path, object body) => AppHttp.Post(_app, path, body);

    /// <summary>A Stream Deck press (POST, like the plugin; the key in the query).</summary>
    private async Task<(HttpStatusCode Status, JsonElement Body)> Press(string path, string more = "")
    {
        var r = await _deck.PostAsync($"deck/{path}?key={_key}{more}", new StringContent(""));
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 && text[0] == '{' ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private async Task<JsonElement> State(string more = "") => JsonDocument.Parse(await _deck.GetStringAsync($"deck/state?key={_key}{more}")).RootElement.Clone();

    private CasterDesk Desk => _server.Desk!;

    /// <summary>A lobby's snapshot as its host's mod sends it.</summary>
    private void Snap(string lobby, string phase, bool cam = true, int? camOn = 2)
    {
        Desk.Apply(JsonSerializer.Serialize(new
        {
            type = "snap", lobby, round = 1, game = lobby + "-1", phase, crewAlive = 6, impAlive = 2, taskPct = 40,
            spec = new { cam, camPick = (int?)null, camOn, on = true, audio = "wide" },
            // Colour order is the slot order: red (0), blue (1), green (2, dead).
            players = new object[]
            {
                new { id = 2, name = "Bob", color = 1, key = "bob#1" },
                new { id = 1, name = "Ann", color = 0, key = "ann#1", imp = true },
                new { id = 3, name = "Cy", color = 2, key = "cy#1", dead = true },
            },
        }));
    }

    private async Task ConnectObs()
    {
        _obs = new FakeObs();
        var r = await Post("app/admin/obs", new { host = "127.0.0.1", port = _obs.Port.ToString(), password = "secret" });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.GetProperty("message").GetString());
    }

    private static Task Until(Func<bool> done, int ms = 5000) => Wait.For(done, ms);

    [Fact]
    public async Task Hear_Preview_and_the_broadcast_delay_are_set_from_the_Stream_Deck_and_the_desk()
    {
        Assert.True((await State()).GetProperty("voice").GetProperty("cue").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Press("voice/cue/off")).Status);
        Assert.False((await State()).GetProperty("voice").GetProperty("cue").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Press("voice/cue/on")).Status);
        Assert.True((await State()).GetProperty("voice").GetProperty("cue").GetBoolean());
        var r = await Post("app/admin/voice", new { action = "cue", on = "false" });
        Assert.StartsWith("Hear Preview off", r.GetProperty("message").GetString());
        Assert.False(_server.Obs!.Settings.Voice.HearPreview);
        r = await Post("app/admin/obs", new { action = "delay", value = 1.5 });
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.StartsWith("Broadcast delay 1.5 s", r.GetProperty("message").GetString());
        Assert.Equal(1.5, (await Get("app/admin/desk")).GetProperty("obs").GetProperty("broadcastDelay").GetDouble());
    }

    [Fact]
    public async Task Every_answer_says_with_its_status_whether_it_happened()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _deck.PostAsync("deck/lobby/1?key=wrong", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Press("nonsense")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Press("lobby/1")).Status);                 // no live lobbies yet
        Snap("Lobby 3", "ingame");
        // OBS isn't connected (and this isn't simulation): nothing switches, and the key says so.
        var (noObs, body) = await Press("lobby/3");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noObs);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("none", Desk.OnAir.Layout);
        Assert.Equal(HttpStatusCode.NotFound, (await Press("lobby/9")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Press("take")).Status);                    // nothing in Preview
        // Preview needs no OBS; the same press straight after counts once.
        Assert.Equal(HttpStatusCode.OK, (await Press("pvw/lobby/3")).Status);
        var again = await Press("pvw/lobby/3");
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal("ignored repeat", again.Body.GetProperty("message").GetString());
        // Switching held. The old "where the action is" link is Best replay now: no clip yet, so refused.
        Assert.Equal(HttpStatusCode.OK, (await Press("hold/on")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Press("lobby/action")).Status);
        Assert.True((await State()).GetProperty("hold").GetBoolean());

        // Locked (no administration code): 423 for the plugin to say so.
        await Post("app/admin/code", new { code = "" });
        Assert.Equal(HttpStatusCode.Locked, (await Press("lobby/3")).Status);
        Assert.Equal(HttpStatusCode.Locked, (await _deck.GetAsync($"deck/state?key={_key}")).StatusCode);
    }

    [Fact]
    public async Task Player_slots_are_every_player_in_colour_order_and_a_pick_is_sent_until_the_host_has_it()
    {
        Snap("Lobby 3", "ingame");
        // Slot 2 is blue (Bob, id 2): red is slot 1, the dead green player is still slot 3.
        var (status, body) = await Press("cam/3/slot2", "&arm=1");
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Contains("Bob", body.GetProperty("message").GetString());
        Assert.Equal(2, Desk.CamPick("Lobby 3"));
        var lobby = (await State()).GetProperty("lobbies")[0];
        Assert.Equal(3, lobby.GetProperty("no").GetInt32());
        Assert.Equal(new[] { (1, "red", "Ann", false), (2, "blue", "Bob", false), (3, "green", "Cy", true) },
            lobby.GetProperty("players").EnumerateArray().Select(p => (p.GetProperty("slot").GetInt32(), p.GetProperty("colour").GetString()!, p.GetProperty("name").GetString()!, p.GetProperty("dead").GetBoolean())));
        Assert.Equal(2, lobby.GetProperty("cam").GetProperty("pick").GetInt32());
        // The host is told (over the desk page's link), once per kind: the newest pick replaces an older one.
        await Press("cam/3/red", "&arm=1");
        var sent = (await Get("app/admin/specout")).EnumerateArray().Where(x => x.GetProperty("command").GetString()!.StartsWith("spec cam")).ToList();
        Assert.Equal("spec cam 1", Assert.Single(sent).GetProperty("command").GetString());
        // Not taken by the host (its snapshot still says it picks by itself): asked again within a few seconds.
        bool again = false;
        for (int i = 0; i < 60 && !again; i++)
        {
            await Task.Delay(100);
            again = (await Get("app/admin/specout")).EnumerateArray().Any(x => x.GetProperty("command").GetString() == "spec cam 1");
        }
        Assert.True(again);
        Assert.Equal(HttpStatusCode.NotFound, (await Press("cam/3/slot9")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Press("cam/7/slot1")).Status);
    }

    [Fact]
    public async Task A_camera_is_refused_when_cameras_are_off_and_armed_in_a_meeting_until_play_resumes()
    {
        await ConnectObs();
        Snap("Lobby 3", "ingame", cam: false, camOn: null);
        Assert.Equal(HttpStatusCode.Conflict, (await Press("cam/3/slot1")).Status);            // the host isn't sending one
        await Post("app/admin/cam", new { all = "off" });
        Snap("Lobby 3", "ingame");
        Assert.Equal(HttpStatusCode.Conflict, (await Press("cam/3/slot2")).Status);            // player cameras off
        await Post("app/admin/cam", new { all = "on" });

        // In a meeting: armed, not on stream; the state shows it.
        Snap("Lobby 3", "meeting");
        await Task.Delay(350);                              // the same press straight after would count once
        var (status, body) = await Press("cam/3/slot2");
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Contains("meeting", body.GetProperty("message").GetString());
        Assert.NotEqual("cam", Desk.OnAir.Layout);
        var cam = (await State()).GetProperty("lobbies")[0].GetProperty("cam");
        Assert.True(cam.GetProperty("armed").GetBoolean());
        Assert.Equal(2, cam.GetProperty("pick").GetInt32());
        // Pressed again: taken back.
        await Task.Delay(350);
        Assert.Equal(HttpStatusCode.OK, (await Press("cam/3/slot2")).Status);
        Assert.False(Desk.Armed("Lobby 3").Armed);
        await Task.Delay(350);
        await Press("cam/3/slot2");

        // Play resumes: the camera goes on stream by itself, following Bob.
        Snap("Lobby 3", "ingame");
        await Until(() => Desk.OnAir.Layout == "cam");
        Assert.Equal("Lobby 3", Desk.OnAir.Slots[0]);
        Assert.False(Desk.Armed("Lobby 3").Armed);
        Assert.Equal(2, Desk.CamPick("Lobby 3"));
    }

    [Fact]
    public async Task Preview_is_shared_with_the_desk_and_Take_puts_it_on_stream()
    {
        await ConnectObs();
        Snap("Lobby 1", "ingame");
        Snap("Lobby 2", "ingame");
        Snap("Lobby 3", "meeting");
        Assert.Equal(HttpStatusCode.OK, (await Press("pvw/lobby/1")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("pvw/add/3")).Status);
        var state = await State();
        Assert.Equal("2up", state.GetProperty("preview").GetProperty("layout").GetString());
        Assert.Equal(new[] { "Lobby 1", "Lobby 3" }, state.GetProperty("preview").GetProperty("slots").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("pvw", state.GetProperty("lobbies").EnumerateArray().Single(l => l.GetProperty("no").GetInt32() == 3).GetProperty("tally").GetString());
        // The desk page sees the same Preview.
        Assert.Equal(new[] { "Lobby 1", "Lobby 3" }, (await Get("app/admin/live")).GetProperty("desk").GetProperty("preview").GetProperty("slots").EnumerateArray().Select(x => x.GetString()));

        // Take as a quad, with a straight cut: on stream, Preview empty, tally red.
        Assert.Equal(HttpStatusCode.OK, (await Press("take", "&as=quad&t=cut")).Status);
        Assert.Equal("4up", Desk.OnAir.Layout);
        Assert.Equal(new[] { "Lobby 1", "Lobby 3" }, Desk.OnAir.Slots.Take(2));
        Assert.Empty(Desk.Preview.Slots);
        await Until(() => _obs!.Program == "TT Quad");
        state = await State();
        Assert.Equal("pgm", state.GetProperty("lobbies").EnumerateArray().Single(l => l.GetProperty("no").GetInt32() == 1).GetProperty("tally").GetString());
        Assert.Equal("4up", state.GetProperty("program").GetProperty("layout").GetString());

        // Layout with nothing staged: what's on, in that layout (and Take puts it on).
        Assert.Equal(HttpStatusCode.OK, (await Press("layout/2up")).Status);
        Assert.Equal("2up", Desk.Preview.Layout);
        Assert.Equal(HttpStatusCode.OK, (await Press("take")).Status);
        Assert.Equal("2up", Desk.OnAir.Layout);
    }

    [Fact]
    public async Task Auto_grid_fills_Preview_like_any_other_and_the_Take_key_puts_it_on()
    {
        await ConnectObs();
        Snap("Lobby 1", "ingame");
        Snap("Lobby 2", "ingame");
        Desk.AutoGrid = true;
        Desk.Show("Lobby 1");
        await Until(() => _obs!.Program == "TT Full");
        Snap("Lobby 1", "meeting");
        Snap("Lobby 2", "lobby");
        Desk.Tick();
        // Every lobby quiet: the grid is in Preview (green tally, the Grid layout's), nothing switched.
        var state = await State();
        Assert.Equal("full", state.GetProperty("program").GetProperty("layout").GetString());
        Assert.Equal("grid", state.GetProperty("preview").GetProperty("layout").GetString());
        Assert.Equal(new[] { "Lobby 1", "Lobby 2" }, state.GetProperty("preview").GetProperty("slots").EnumerateArray().Select(x => x.GetString()));
        Assert.False(state.GetProperty("preview").TryGetProperty("note", out _));        // the same Preview as ever
        Assert.Equal("pvw", state.GetProperty("lobbies").EnumerateArray().Single(l => l.GetProperty("no").GetInt32() == 2).GetProperty("tally").GetString());
        Assert.Equal("pgm", state.GetProperty("lobbies").EnumerateArray().Single(l => l.GetProperty("no").GetInt32() == 1).GetProperty("tally").GetString());
        Assert.All(state.GetProperty("lobbies").EnumerateArray().SelectMany(l => l.GetProperty("players").EnumerateArray()), p => Assert.False(p.TryGetProperty("imp", out _)));
        // The desk page says what it is.
        Assert.Equal(CasterDesk.GridNote, (await Get("app/admin/live")).GetProperty("desk").GetProperty("preview").GetProperty("note").GetString());
        Assert.Equal("full", Desk.OnAir.Layout);

        // Take: on stream.
        Assert.Equal(HttpStatusCode.OK, (await Press("take")).Status);
        Assert.Equal("grid", Desk.OnAir.Layout);
        await Until(() => _obs!.Program == "TT Grid");
        Assert.Empty(Desk.Preview.Slots);
    }

    [Fact]
    public async Task The_state_waits_for_a_change_and_leaves_out_impostors_unless_asked()
    {
        Snap("Lobby 3", "ingame");
        var first = await State();
        int rev = first.GetProperty("rev").GetInt32();
        Assert.All(first.GetProperty("lobbies")[0].GetProperty("players").EnumerateArray(), p => Assert.False(p.TryGetProperty("imp", out _)));
        var withImp = await State("&imp=1");
        Assert.True(withImp.GetProperty("lobbies")[0].GetProperty("players").EnumerateArray().Single(p => p.GetProperty("id").GetInt32() == 1).GetProperty("imp").GetBoolean());

        // Nothing new: the request waits.
        var started = DateTime.UtcNow;
        var waiting = State($"&since={rev}");
        await Task.Delay(400);
        Assert.False(waiting.IsCompleted);
        // A press changes it: the answer comes at once, with a newer revision.
        await Press("pvw/lobby/3");
        var changed = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(rev, changed.GetProperty("rev").GetInt32());
        Assert.Equal("Lobby 3", changed.GetProperty("preview").GetProperty("slots")[0].GetString());
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(_server.Port, changed.GetProperty("port").GetInt32());

        // Key pictures.
        var svg = await _deck.GetStringAsync($"deck/img/lobby/3.svg?key={_key}");
        Assert.StartsWith("<svg", svg);
        Assert.Contains("Lobby 3", svg);
        Assert.Contains("stroke=\"#1fbf4a\" stroke-width=\"8\"", svg);      // a green border: in Preview
        var head = await _deck.GetStringAsync($"deck/img/cam/3/slot2.svg?key={_key}");
        Assert.Contains("data:image/png;base64,", head);
        Assert.Contains("Bob", head);
        Assert.Contains("OFF", await _deck.GetStringAsync($"deck/img/toggle/mute.svg?key={_key}"));
    }

    [Fact]
    public async Task Panic_fades_to_be_right_back_mutes_and_holds_and_undo_puts_it_back()
    {
        await ConnectObs();
        Snap("Lobby 3", "ingame");
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/3", "&t=cut")).Status);
        Assert.Equal("full", Desk.OnAir.Layout);
        var (status, _) = await Press("panic");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("slate", Desk.OnAir.Layout);
        Assert.True(Desk.Held);
        Assert.True(Desk.Alerts.Settings.Paused);
        var state = await State();
        Assert.True(state.GetProperty("voice").GetProperty("muteAll").GetBoolean());
        Assert.True(state.GetProperty("hold").GetBoolean());
        await Until(() => _obs!.Program == "TT Be Right Back");

        Assert.Equal(HttpStatusCode.OK, (await Press("undo")).Status);
        Assert.Equal("full", Desk.OnAir.Layout);
        Assert.Equal("Lobby 3", Desk.OnAir.Slots[0]);
        Assert.False(Desk.Held);
        Assert.False(Desk.Alerts.Settings.Paused);
        Assert.False((await State()).GetProperty("voice").GetProperty("muteAll").GetBoolean());
        // Nothing else to undo.
        await Task.Delay(350);
        Assert.Equal(HttpStatusCode.Conflict, (await Press("undo")).Status);
    }

    [Fact]
    public async Task Panic_put_right_by_hand_is_over_so_the_next_panic_works_and_undo_undoes_the_last_switch()
    {
        await ConnectObs();
        Snap("Lobby 2", "ingame");
        Snap("Lobby 3", "ingame");
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/3", "&t=cut")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("panic")).Status);
        Assert.Equal("slate", Desk.OnAir.Layout);

        // Put right by hand instead of Undo: another lobby, voice back, hold released.
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/2", "&t=cut")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("voice/mute/off")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("hold/off")).Status);
        Assert.False(Desk.Held);

        // A second incident: Panic does it all again (not "already on the be-right-back screen").
        var (status, body) = await Press("panic");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Already", body.GetProperty("message").GetString());
        Assert.Equal("slate", Desk.OnAir.Layout);
        Assert.True(Desk.Held);
        Assert.True((await State()).GetProperty("voice").GetProperty("muteAll").GetBoolean());
        // Undo goes back to the lobby that was on when it came, not to the first panic's.
        Assert.Equal(HttpStatusCode.OK, (await Press("undo")).Status);
        Assert.Equal("full", Desk.OnAir.Layout);
        Assert.Equal("Lobby 2", Desk.OnAir.Slots[0]);
        Assert.False(Desk.Held);

        // Put right by hand again, then Undo: the last switch is undone, the voice and the hold stay as set.
        await Task.Delay(350);
        Assert.Equal(HttpStatusCode.OK, (await Press("panic")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/3", "&t=cut")).Status);
        await Task.Delay(350);
        Assert.Equal(HttpStatusCode.OK, (await Press("voice/mute/off")).Status);
        await Task.Delay(350);
        Assert.Equal(HttpStatusCode.OK, (await Press("hold/off")).Status);
        var (undone, said) = await Press("undo");
        Assert.Equal(HttpStatusCode.OK, undone);
        Assert.DoesNotContain("Panic", said.GetProperty("message").GetString());
        Assert.False(Desk.Held);
        Assert.False((await State()).GetProperty("voice").GetProperty("muteAll").GetBoolean());
    }

    [Fact]
    public async Task A_panic_put_right_by_hand_is_forgotten_so_a_BRB_hold_or_mute_set_later_is_the_casters_own()
    {
        await ConnectObs();
        Snap("Lobby 2", "ingame");
        Snap("Lobby 3", "ingame");
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/3", "&t=cut")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("panic")).Status);
        // Put right by hand: another lobby, the hold released, the voice back on.
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/2", "&t=cut")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("hold/off")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("voice/mute/off")).Status);

        // A planned pause later (BRB, hold and Mute all, each by hand) isn't the panic: the desk shows no panic.
        Assert.Equal(HttpStatusCode.OK, (await Press("break/brb", "&t=cut")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("hold/on")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("voice/mute/on")).Status);
        Assert.False((await State()).GetProperty("panic").GetBoolean());
        await Task.Delay(200);                              // past the desk answer's moment of reuse
        Assert.False((await Get("app/admin/live")).GetProperty("desk").GetProperty("panic").GetBoolean());
        // Undo takes back the BRB (to Lobby 2, not to the lobby from before the panic); the caster's own
        // mute and hold stay.
        var (status, body) = await Press("undo");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Panic", body.GetProperty("message").GetString());
        Assert.Equal(("full", "Lobby 2"), (Desk.OnAir.Layout, Desk.OnAir.Slots[0]));
        Assert.True((await State()).GetProperty("voice").GetProperty("muteAll").GetBoolean());
        Assert.Equal("Held from the Stream Deck", Desk.HoldWhy);

        // A second incident with the voice muted by hand before it: a new panic, and its Undo leaves the
        // voice muted and the caster's hold on, as they were (not the first panic's "voice on, no hold").
        await Task.Delay(350);
        Assert.Equal(HttpStatusCode.OK, (await Press("panic")).Status);
        Assert.Equal("slate", Desk.OnAir.Layout);
        await Task.Delay(350);
        Assert.Equal(HttpStatusCode.OK, (await Press("undo")).Status);
        Assert.Equal(("full", "Lobby 2"), (Desk.OnAir.Layout, Desk.OnAir.Slots[0]));
        Assert.True((await State()).GetProperty("voice").GetProperty("muteAll").GetBoolean());
        Assert.Equal("Held from the Stream Deck", Desk.HoldWhy);
    }

    [Fact]
    public async Task A_mute_turned_off_and_on_again_by_hand_after_a_panic_is_left_alone_by_its_undo()
    {
        await ConnectObs();
        Snap("Lobby 2", "ingame");
        Snap("Lobby 3", "ingame");
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/3", "&t=cut")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("panic")).Status);
        // The lobby and the voice by hand; the voice then muted again by hand; the panic's hold still on.
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/2", "&t=cut")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("voice/mute/off")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("voice/mute/on")).Status);
        // A BRB by hand isn't the panic's own: Undo doesn't put the lobby from before the panic back.
        Assert.Equal(HttpStatusCode.OK, (await Press("break/brb", "&t=cut")).Status);
        var (status, body) = await Press("undo");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Panic undone", body.GetProperty("message").GetString());
        Assert.Equal("slate", Desk.OnAir.Layout);
        Assert.False(Desk.Held);
        Assert.True((await State()).GetProperty("voice").GetProperty("muteAll").GetBoolean());
        // Nothing of the panic left: the next Undo takes back the BRB.
        await Task.Delay(350);
        Assert.Equal(HttpStatusCode.OK, (await Press("undo")).Status);
        Assert.Equal(("full", "Lobby 2"), (Desk.OnAir.Layout, Desk.OnAir.Slots[0]));
    }

    [Fact]
    public async Task The_panic_keys_own_undo_takes_back_a_panic_and_nothing_else()
    {
        await ConnectObs();
        Snap("Lobby 2", "ingame");
        Snap("Lobby 3", "ingame");
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/2", "&t=cut")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/3", "&t=cut")).Status);
        // No panic on: refused, and the last switch isn't undone.
        Assert.Equal(HttpStatusCode.Conflict, (await Press("panic/undo")).Status);
        Assert.Equal("Lobby 3", Desk.OnAir.Slots[0]);
        Assert.Equal(HttpStatusCode.OK, (await Press("panic")).Status);
        Assert.True((await State()).GetProperty("panic").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Press("panic/undo")).Status);
        Assert.Equal(("full", "Lobby 3"), (Desk.OnAir.Layout, Desk.OnAir.Slots[0]));
        Assert.False(Desk.Held);
        Assert.False((await State()).GetProperty("panic").GetBoolean());
        // Pressed again (the key a moment behind): nothing more happens.
        await Task.Delay(350);
        Assert.Equal(HttpStatusCode.Conflict, (await Press("panic/undo")).Status);
        Assert.Equal("Lobby 3", Desk.OnAir.Slots[0]);
    }

    [Fact]
    public async Task Undo_of_a_panic_from_the_Stream_Deck_waits_for_OBS_and_the_desk_says_OBS_didnt_switch()
    {
        await ConnectObs();
        Snap("Lobby 3", "ingame");
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/3", "&t=cut")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("panic")).Status);
        await Until(() => _obs!.Program == "TT Be Right Back");
        await Post("app/admin/obs", new { action = "disconnect" });
        // OBS gone: the key is refused and nothing is undone, as with any other switch.
        var (status, body) = await Press("undo");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("slate", Desk.OnAir.Layout);
        Assert.Equal("Panic", Desk.HoldWhy);
        Assert.True((await State()).GetProperty("voice").GetProperty("muteAll").GetBoolean());
        // The desk undoes it anyway (Red Alert's own pages switch), and says OBS didn't.
        var desk = await Post("app/admin/do", new { path = "undo" });
        Assert.True(desk.GetProperty("ok").GetBoolean(), desk.GetProperty("message").GetString());
        Assert.Contains("OBS isn't connected", desk.GetProperty("message").GetString());
        Assert.Equal(("full", "Lobby 3"), (Desk.OnAir.Layout, Desk.OnAir.Slots[0]));
        Assert.False(Desk.Held);
    }

    [Fact]
    public async Task A_panic_survives_OBS_reconnecting_and_the_Stream_Deck_undoes_it_once_OBS_is_back()
    {
        await ConnectObs();
        Snap("Lobby 3", "ingame");
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/3", "&t=cut")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("panic")).Status);
        await Until(() => _obs!.Program == "TT Be Right Back");
        await Post("app/admin/obs", new { action = "disconnect" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Press("undo")).Status);
        // OBS back: it reads back the be-right-back screen, which is still the panic's own.
        var again = await Post("app/admin/obs", new { host = "127.0.0.1", port = _obs!.Port.ToString(), password = "secret" });
        Assert.True(again.GetProperty("ok").GetBoolean(), again.GetProperty("message").GetString());
        Assert.True((await State()).GetProperty("panic").GetBoolean());
        await Task.Delay(350);
        var (status, body) = await Press("undo");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("back to what was on", body.GetProperty("message").GetString());
        Assert.Equal(("full", "Lobby 3"), (Desk.OnAir.Layout, Desk.OnAir.Slots[0]));
        await Until(() => _obs.Program == "TT Full");
    }

    [Fact]
    public async Task Undo_after_a_panic_puts_back_the_hold_that_was_on_before_it_with_its_reason_and_time()
    {
        await ConnectObs();
        Snap("Lobby 2", "ingame");
        Snap("Lobby 3", "ingame");
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/3", "&t=cut")).Status);
        // Held for five minutes before the panic.
        Desk.SetHold(true, TimeSpan.FromMinutes(5), "Five minutes");
        var before = Desk.HoldNow();
        Assert.Equal(HttpStatusCode.OK, (await Press("panic")).Status);
        Assert.Equal("Panic", Desk.HoldWhy);
        Assert.True((await State()).GetProperty("panic").GetBoolean());
        await Task.Delay(200);                              // past the desk answer's moment of reuse
        var desk = (await Get("app/admin/live")).GetProperty("desk");
        Assert.True(desk.GetProperty("panic").GetBoolean());
        Assert.Equal("Panic", desk.GetProperty("hold").GetProperty("why").GetString());

        // Undo: the hold is the one from before (its reason and its end), and no panic is shown any more.
        Assert.Equal(HttpStatusCode.OK, (await Press("undo")).Status);
        Assert.Equal("Lobby 3", Desk.OnAir.Slots[0]);
        Assert.Equal(before, Desk.HoldNow());
        Assert.Equal("Five minutes", Desk.HoldWhy);
        Assert.False((await State()).GetProperty("panic").GetBoolean());
        await Task.Delay(200);
        desk = (await Get("app/admin/live")).GetProperty("desk");
        Assert.False(desk.GetProperty("panic").GetBoolean());
        Assert.Equal("Five minutes", desk.GetProperty("hold").GetProperty("why").GetString());

        // Held with H, then Panic, then another lobby and the voice back by hand: the panic's hold is still
        // there, so it isn't over. The desk's Panic is Panic again (not on the be-right-back screen), and
        // Panic puts it all back on; Undo then goes back to that lobby and the caster's own hold.
        await Task.Delay(350);
        Assert.Equal(HttpStatusCode.OK, (await Press("hold/on")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("panic")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/2", "&t=cut")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("voice/mute/off")).Status);
        Assert.Equal("Panic", Desk.HoldWhy);
        Assert.False((await State()).GetProperty("panic").GetBoolean());
        await Task.Delay(350);
        var (status, body) = await Press("panic");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Already", body.GetProperty("message").GetString());
        Assert.Equal("slate", Desk.OnAir.Layout);
        Assert.True((await State()).GetProperty("voice").GetProperty("muteAll").GetBoolean());
        await Task.Delay(350);
        Assert.Equal(HttpStatusCode.OK, (await Press("undo")).Status);
        Assert.Equal("Lobby 2", Desk.OnAir.Slots[0]);
        Assert.Equal("Held from the Stream Deck", Desk.HoldWhy);
        Assert.Null(Desk.HoldNow().Until);
        Assert.False((await State()).GetProperty("voice").GetProperty("muteAll").GetBoolean());
    }

    [Fact]
    public async Task Panic_after_the_hold_was_released_and_a_lobby_taken_by_hand_does_it_all_again()
    {
        await ConnectObs();
        Snap("Lobby 2", "ingame");
        Snap("Lobby 3", "ingame");
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/3", "&t=cut")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("panic")).Status);
        // The hold released (H) and a lobby taken, the voice left muted.
        Assert.Equal(HttpStatusCode.OK, (await Press("hold/off")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/2", "&t=cut")).Status);
        Assert.False((await State()).GetProperty("panic").GetBoolean());
        await Task.Delay(350);
        var (status, body) = await Press("panic");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Already", body.GetProperty("message").GetString());
        Assert.Equal("slate", Desk.OnAir.Layout);
        Assert.Equal("Panic", Desk.HoldWhy);
        Assert.True((await State()).GetProperty("panic").GetBoolean());
        await Task.Delay(350);
        Assert.Equal(HttpStatusCode.OK, (await Press("undo")).Status);
        Assert.Equal("Lobby 2", Desk.OnAir.Slots[0]);
        Assert.False(Desk.Held);
        Assert.False((await State()).GetProperty("voice").GetProperty("muteAll").GetBoolean());
    }

    [Fact]
    public async Task The_desk_switches_without_OBS_and_says_so_while_the_Stream_Deck_is_refused()
    {
        Snap("Lobby 2", "ingame");
        Snap("Lobby 3", "ingame");
        // The Stream Deck: 503, nothing switched.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Press("lobby/3")).Status);
        Assert.Equal("none", Desk.OnAir.Layout);
        // The desk (cut, Take, quad, Back, a camera chip): Red Alert switches, and says OBS didn't.
        var cut = await Post("app/admin/do", new { path = "lobby/3", query = new { t = "cut" } });
        Assert.True(cut.GetProperty("ok").GetBoolean(), cut.GetProperty("message").GetString());
        Assert.Contains("OBS isn't connected", cut.GetProperty("message").GetString());
        Assert.Equal(("full", "Lobby 3"), (Desk.OnAir.Layout, Desk.OnAir.Slots[0]));
        Assert.True((await Post("app/admin/do", new { path = "pvw/lobby/2" })).GetProperty("ok").GetBoolean());
        var take = await Post("app/admin/do", new { path = "take" });
        Assert.True(take.GetProperty("ok").GetBoolean(), take.GetProperty("message").GetString());
        Assert.Equal("Lobby 2", Desk.OnAir.Slots[0]);
        Assert.True((await Post("app/admin/do", new { path = "quad" })).GetProperty("ok").GetBoolean());
        Assert.Equal("4up", Desk.OnAir.Layout);
        var back = await Post("app/admin/do", new { path = "undo" });
        Assert.True(back.GetProperty("ok").GetBoolean(), back.GetProperty("message").GetString());
        Assert.Equal(("full", "Lobby 2"), (Desk.OnAir.Layout, Desk.OnAir.Slots[0]));
        var cam = await Post("app/admin/cam", new { lobby = "Lobby 2", follow = "slot2" });
        Assert.True(cam.GetProperty("ok").GetBoolean(), cam.GetProperty("message").GetString());
        Assert.Equal("cam", Desk.OnAir.Layout);
        // Panic still needs OBS (it's OBS that mutes the voice), from the desk too.
        Assert.Equal(503, (await Post("app/admin/do", new { path = "panic" })).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Lobby_numbers_are_set_in_Settings_and_the_lobby_keys_follow()
    {
        Snap("Lobby 3", "ingame");
        Snap("LJ", "ingame");
        int No(JsonElement state, string name) => state.GetProperty("lobbies").EnumerateArray().Single(l => l.GetProperty("name").GetString() == name).GetProperty("no").GetInt32();
        var before = await State();
        Assert.Equal(3, No(before, "Lobby 3"));
        Assert.Equal(1, No(before, "LJ"));
        var numbers = (await Get("app/admin/deck")).GetProperty("numbers");
        Assert.Equal(new[] { ("LJ", 1), ("Lobby 3", 3) }, numbers.EnumerateArray().Select(n => (n.GetProperty("lobby").GetString()!, n.GetProperty("no").GetInt32())));

        // Lobby 3 to 1: LJ, which had it, takes 3. The lobby keys follow.
        var set = await Post("app/admin/numbers", new { numbers = new Dictionary<string, int> { ["Lobby 3"] = 1 } });
        Assert.True(set.GetProperty("ok").GetBoolean(), set.GetProperty("message").GetString());
        var after = await State();
        Assert.Equal((1, 3), (No(after, "Lobby 3"), No(after, "LJ")));
        Assert.Equal(HttpStatusCode.OK, (await Press("pvw/lobby/1")).Status);
        Assert.Equal("Lobby 3", Desk.Preview.Slots[0]);
        Assert.False((await Post("app/admin/numbers", new { numbers = new Dictionary<string, int> { ["LJ"] = 0 } })).GetProperty("ok").GetBoolean());

        // Renumbered: the online lobbies 1…N in their order.
        var re = await Post("app/admin/numbers", new { action = "renumber" });
        Assert.True(re.GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { ("Lobby 3", 1), ("LJ", 2) }, re.GetProperty("numbers").EnumerateArray().Select(n => (n.GetProperty("lobby").GetString()!, n.GetProperty("no").GetInt32())));
    }

    [Fact]
    public async Task Hello_says_it_is_Red_Alert_without_the_key_and_nothing_else_does()
    {
        var hello = await _deck.GetAsync("deck/hello");
        Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
        Assert.StartsWith("Red Alert ", JsonDocument.Parse(await hello.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _deck.GetAsync("deck/hello?key=")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _deck.GetAsync("deck/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _deck.GetAsync("deck/hello/x")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _deck.PostAsync("deck/lobby/1", new StringContent(""))).StatusCode);
    }

    [Fact]
    public async Task Panic_goes_to_be_right_back_even_when_OBS_doesnt_answer_the_voice_mute_and_the_key_hears_why()
    {
        await ConnectObs();
        Snap("Lobby 3", "ingame");
        var obs = _server.Obs!;
        obs.VoiceFeeds = () => new List<(string, string)> { ("Lobby 3", "https://vdo.ninja/?view=v3&password=x") };
        await obs.EnsureVoiceAsync();
        Assert.Contains("TT Voice Lobby 3", _obs!.Inputs.Keys);
        Assert.Equal(HttpStatusCode.OK, (await Press("lobby/3", "&t=cut")).Status);
        await Until(() => !obs.Switching && _obs.Muted.GetValueOrDefault("TT Voice Lobby 3", true) == false);

        // OBS stuck: the voice mute is never answered. The picture still goes, and the key gets an answer.
        lock (_obs) _obs.Hang.Add("SetInputMute");
        var (status, body) = await Press("panic");
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Contains("voice", body.GetProperty("message").GetString());
        Assert.Equal("slate", Desk.OnAir.Layout);
        Assert.True(Desk.Held);
        Assert.True(Desk.Alerts.Settings.Paused);

        // OBS answers again: Panic again mutes the voice (it isn't "already" done).
        lock (_obs) _obs.Hang.Clear();
        (status, body) = await Press("panic");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("Already", body.GetProperty("message").GetString());
        Assert.True(_obs.Muted["TT Voice Lobby 3"]);
        Assert.Equal(HttpStatusCode.OK, (await Press("undo")).Status);
        Assert.Equal("Lobby 3", Desk.OnAir.Slots[0]);
        await Until(() => !_obs.Muted["TT Voice Lobby 3"], 8000);
    }

    [Fact]
    public async Task The_newest_replay_is_named_by_its_lobby_and_play_unless_impostors_are_asked_for()
    {
        Snap("Lobby 3", "ingame");
        // A kill clip's title is its card's text, which names the killer.
        _server.Replays!.Keep(new Clip { Id = "r900", Lobby = "Lobby 3", Rule = "winningKill", Title = "Ann killed Cy in Electrical", State = "ready", File = "x.mp4" });
        _server.DeckPoke();
        var plain = await State();
        var last = plain.GetProperty("replay").GetProperty("last");
        Assert.Equal("Lobby 3: winning kill", last.GetProperty("title").GetString());
        Assert.Equal("r900", last.GetProperty("id").GetString());
        Assert.DoesNotContain("killed", plain.GetRawText());
        Assert.Equal("Ann killed Cy in Electrical", (await State("&imp=1")).GetProperty("replay").GetProperty("last").GetProperty("title").GetString());
    }
}
