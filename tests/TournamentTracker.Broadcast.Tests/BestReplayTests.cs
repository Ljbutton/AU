using System.Net;
using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Best replay's choice: the card's tier, a play viewers missed, its points, the newest; never one played or old.</summary>
public class BestReplayRankingTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 20, 0, 0, DateTimeKind.Utc);
    private readonly Dictionary<string, Card> _cards = new();
    private readonly PriorityConfig _config = new();

    private Clip Clip(string id, string rule, string tier, double points, int secondsAgo, bool shownLive = false, string state = "ready")
    {
        _cards[id + "c"] = new Card { Id = id + "c", Rule = rule, Tier = tier, Value = points, ShownAt = shownLive ? Now.AddSeconds(-secondsAgo) : null };
        return new Clip { Id = id, Lobby = "LJ", Rule = rule, CardId = id + "c", Title = $"[[4|Jake]] {rule} in Electrical", State = state, File = id + ".mp4", EventAt = Now.AddSeconds(-secondsAgo) };
    }

    private List<string> Order(IEnumerable<Clip> clips) => BestReplay.Rank(clips, id => _cards.GetValueOrDefault(id), _config, Now).Select(r => r.Clip.Id).ToList();

    [Fact]
    public void The_highest_tier_goes_first_then_a_play_viewers_missed_then_points_then_the_newest()
    {
        var clips = new List<Clip>
        {
            Clip("kill", "kill", "medium", 40, 10),
            Clip("seenEject", "eject", "must", 100, 60, shownLive: true),
            Clip("missedWin", "winningKill", "must", 100, 90),
            Clip("witness", "witnessedKill", "veryHigh", 80, 20),
            Clip("vent", "vent", "medium", 25, 5),
            Clip("kill2", "kill", "medium", 40, 30),
        };
        Assert.Equal(new[] { "missedWin", "seenEject", "witness", "kill", "kill2", "vent" }, Order(clips));
    }

    [Fact]
    public void Played_old_unsaved_instant_and_low_clips_are_never_picked()
    {
        var played = Clip("played", "eject", "must", 100, 10);
        played.PlayedAt = Now;
        var clips = new List<Clip>
        {
            played,
            Clip("old", "eject", "must", 100, (int)BestReplay.Window.TotalSeconds + 1),
            Clip("saving", "eject", "must", 100, 5, state: "saving"),
            Clip("failed", "eject", "must", 100, 5, state: "failed"),
            Clip("low", "inGame", "low", 12, 5),
            new Clip { Id = "instant", Lobby = "LJ", Rule = "instant", Title = "LJ: the last 8 seconds", State = "ready", File = "i.mp4", EventAt = Now },
            new Clip { Id = "montage", Rule = "montage", Title = "Round 1", State = "ready", File = "m.mp4", EventAt = Now },
            Clip("ok", "kill", "medium", 40, 170),
        };
        Assert.Equal(new[] { "ok" }, Order(clips));
        Assert.True(BestReplay.Saving(clips, id => _cards.GetValueOrDefault(id), _config, Now));
        Assert.Null(BestReplay.Pick(clips.Where(c => c.Id != "ok"), id => _cards.GetValueOrDefault(id), _config, Now));
    }

    [Fact]
    public void A_clip_without_its_card_is_rated_by_its_play_in_caster_priority()
    {
        var clips = new List<Clip>
        {
            new() { Id = "a", Lobby = "LJ", Rule = "kill", Title = "a", State = "ready", File = "a.mp4", EventAt = Now },
            new() { Id = "b", Lobby = "LJ", Rule = "gameEnd", Title = "b", State = "ready", File = "b.mp4", EventAt = Now.AddSeconds(-50) },
        };
        var best = BestReplay.Pick(clips, _ => null, _config, Now)!;
        Assert.Equal(("b", "must"), (best.Clip.Id, best.Tier));
        // Turned off in caster-priority.json: not worth a replay.
        _config.Rules["gameEnd"] = new PriorityRule { On = false, Points = 100 };
        _config.Rules["kill"] = new PriorityRule { On = false, Points = 40 };
        Assert.Null(BestReplay.Pick(clips, _ => null, _config, Now));
    }

    [Fact]
    public void Pressed_again_with_one_in_Preview_it_offers_the_next_one_down_and_round_again()
    {
        var clips = new List<Clip> { Clip("a", "eject", "must", 100, 10), Clip("b", "witnessedKill", "veryHigh", 80, 10), Clip("c", "kill", "medium", 40, 10) };
        string? Next(string? inPreview) => BestReplay.Pick(clips, id => _cards.GetValueOrDefault(id), _config, Now, inPreview)?.Clip.Id;
        Assert.Equal("a", Next(null));
        Assert.Equal("b", Next("a"));
        Assert.Equal("c", Next("b"));
        Assert.Equal("a", Next("c"));
        Assert.Equal("a", Next("gone"));
    }

    [Fact]
    public void Preview_names_the_moment_and_its_tier_and_the_Stream_Deck_label_leaves_the_names_out()
    {
        var c = Clip("a", "kill", "must", 100, 10);
        c.Title = "[[4|Jake]] killed [[1|Maria]] in Electrical";
        var staged = BestReplay.ForPreview(new BestReplay.Rated(c, "must", 100, true));
        Assert.Equal("Replay: LJ · Jake killed Maria in Electrical (MUST SHOW)", staged.Note);
        Assert.Equal("Replay: LJ: kill (MUST SHOW)", staged.Label);
        Assert.Equal("a", staged.ClipId);
        var pv = new PreviewState { Replay = staged };
        Assert.Equal("replay", pv.Layout);
        Assert.False(pv.IsEmpty);
        Assert.True(new PreviewState().IsEmpty);
    }

    [Fact]
    public void Auto_grid_leaves_a_replay_in_Preview_alone()
    {
        using var desk = new CasterDesk(null, () => Now, new PriorityConfig());
        void Snap(string lobby, string phase) => desk.Apply(JsonSerializer.Serialize(new
        {
            type = "snap", lobby, round = 1, game = lobby + "-1", t = new DateTimeOffset(Now).ToUnixTimeMilliseconds(), phase, crewAlive = 7, impAlive = 2, alive = 9, taskPct = 40,
        }));
        Snap("A", "ingame"); Snap("B", "ingame");
        desk.AutoGrid = true;
        desk.Show("A");
        desk.Tick();
        desk.StageReplay(new PreviewReplay { ClipId = "r1", Lobby = "A", Tier = "must", Note = "Replay: A · x (MUST SHOW)" });
        // Every lobby quiet: auto grid would suggest the grid, but only into an empty Preview.
        Snap("A", "meeting"); Snap("B", "lobby");
        desk.Tick();
        Assert.Equal("replay", desk.Preview.Layout);
        Assert.Equal("r1", desk.Preview.Replay!.ClipId);
        Assert.Null(desk.Take());                                    // the desk itself takes lobbies; the server plays a replay
    }
}

/// <summary>
/// Best replay on the desk and the Stream Deck (one choice, made by Red Alert): into Preview, named, and
/// Take plays it; the old "where the action is" links do the same. Camera keys into Preview.
/// </summary>
public class BestReplayDeckTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private BroadcastServer _server = null!;
    private HttpClient _app = null!, _deck = null!;
    private string _key = "";
    private FakeObs? _obs;

    public async Task InitializeAsync()
    {
        string folder = Path.Combine(_dir.Path, "RedAlert");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "swoosh.webm"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(folder, ObsSettings.FileName), JsonSerializer.Serialize(new { swoosh = new { path = Path.Combine(folder, "swoosh.webm"), transitionPointMs = 0 } }));
        _server = new BroadcastServer(new BroadcastEnvironment { SettingsFile = Path.Combine(folder, "settings.json"), Port = 0, CasterPort = 0, Version = "0.1.0" }, new HttpClient(new FakeHttp()));
        _app = AppHttp.Client(_server.Url, _server.Token);
        _deck = new HttpClient { BaseAddress = new Uri(_server.Url), Timeout = TimeSpan.FromSeconds(20) };
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
    private CasterDesk Desk => _server.Desk!;

    private async Task<(HttpStatusCode Status, string Message)> Press(string path, string more = "")
    {
        var r = await _deck.PostAsync($"deck/{path}?key={_key}{more}", new StringContent(""));
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, JsonDocument.Parse(text).RootElement.GetProperty("message").GetString() ?? "");
    }

    private async Task<JsonElement> State(string more = "")
    {
        _server.DeckPoke();
        return JsonDocument.Parse(await _deck.GetStringAsync($"deck/state?key={_key}{more}")).RootElement.Clone();
    }

    private void Snap(string lobby)
    {
        Desk.Apply(JsonSerializer.Serialize(new
        {
            type = "snap", lobby, round = 1, game = lobby + "-1", phase = "ingame", crewAlive = 6, impAlive = 2, taskPct = 40,
            spec = new { cam = true, camPick = (int?)null, camOn = 2, on = true, audio = "wide" },
            players = new object[] { new { id = 1, name = "Ann", color = 0, key = "ann#1", imp = true }, new { id = 2, name = "Bob", color = 1, key = "bob#1" } },
        }));
    }

    private async Task ConnectObs()
    {
        _obs = new FakeObs();
        var r = await Post("app/admin/obs", new { host = "127.0.0.1", port = _obs.Port.ToString(), password = "secret" });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.GetProperty("message").GetString());
    }

    private Clip Keep(string id, string rule, string title, int secondsAgo = 5)
    {
        var clip = new Clip { Id = id, Lobby = "Lobby 3", Rule = rule, Title = title, State = "ready", File = id + ".mp4", EventAt = DateTime.UtcNow.AddSeconds(-secondsAgo) };
        _server.Replays!.Keep(clip);
        return clip;
    }

    [Fact]
    public async Task Nothing_worth_a_replay_is_said_and_nothing_changes()
    {
        Snap("Lobby 3");
        Desk.PreviewOnly("Lobby 3");
        var (status, message) = await Press("replay/best");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("No replay worth showing yet.", message);
        Assert.Equal(new[] { "Lobby 3" }, Desk.Preview.Slots);
        // Held or not, the old links say the same (they switch nothing).
        Desk.SetHold(true);
        Assert.Equal((HttpStatusCode.Conflict, "No replay worth showing yet."), await Press("lobby/action"));
        var desk = await Post("app/admin/do", new { path = "replay/best" });
        Assert.False(desk.GetProperty("ok").GetBoolean());
        Assert.Equal("No replay worth showing yet.", desk.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_best_replay_goes_into_Preview_named_and_the_Stream_Deck_state_names_nobody_unless_asked()
    {
        Snap("Lobby 3");
        Keep("r1", "kill", "[[0|Ann]] killed [[1|Bob]] in Admin");
        Keep("r2", "winningKill", "[[0|Ann]] killed [[1|Bob]] in Electrical", 30);
        Keep("r3", "instant", "Lobby 3: the last 8 seconds", 1);
        var plain = await State();
        Assert.Equal("Lobby 3: winning kill", plain.GetProperty("replay").GetProperty("best").GetProperty("title").GetString());
        Assert.Equal("must", plain.GetProperty("replay").GetProperty("best").GetProperty("tier").GetString());

        // The desk's N and the Stream Deck's key make the same choice.
        var desk = await Post("app/admin/do", new { path = "replay/best" });
        Assert.True(desk.GetProperty("ok").GetBoolean());
        Assert.Equal("Replay: Lobby 3 · Ann killed Bob in Electrical (MUST SHOW). In Preview: Take plays it.", desk.GetProperty("message").GetString());
        Assert.Equal("r2", Desk.Preview.Replay!.ClipId);
        Assert.Equal("replay", Desk.Preview.Layout);
        var live = (await Get("app/admin/live")).GetProperty("desk").GetProperty("preview");
        Assert.Equal("replay", live.GetProperty("layout").GetString());
        Assert.Equal("Replay: Lobby 3 · Ann killed Bob in Electrical (MUST SHOW)", live.GetProperty("replay").GetProperty("note").GetString());

        var state = await State();
        Assert.Equal("replay", state.GetProperty("preview").GetProperty("layout").GetString());
        Assert.Equal("Replay: Lobby 3: winning kill (MUST SHOW)", state.GetProperty("preview").GetProperty("replay").GetProperty("title").GetString());
        Assert.DoesNotContain("killed", state.GetRawText());
        Assert.Contains("Ann killed Bob in Electrical", (await State("&imp=1")).GetProperty("preview").GetProperty("replay").GetProperty("title").GetString());

        // Pressed again (the old link, from the Stream Deck): the next one down.
        var (status, message) = await Press("lobby/action");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("Replay: Lobby 3 · Ann killed Bob in Admin (MEDIUM)", message);
        Assert.Equal("r1", Desk.Preview.Replay!.ClipId);
        // A lobby picked after replaces it.
        Assert.Equal(HttpStatusCode.OK, (await Press($"pvw/lobby/{Desk.NumberOf("Lobby 3")}")).Status);
        Assert.Null(Desk.Preview.Replay);
    }

    [Fact]
    public async Task Take_plays_the_replay_in_Preview_like_Play_last_and_it_is_not_offered_again()
    {
        Snap("Lobby 3");
        Keep("r2", "eject", "[[1|Bob]] ejected: Crewmate");
        Assert.Equal(HttpStatusCode.OK, (await Press("replay/best")).Status);
        // Without OBS the replay can't play (not even from the desk): refused, and it stays in Preview.
        Assert.Equal(503, (await Post("app/admin/do", new { path = "take" })).GetProperty("status").GetInt32());
        Assert.NotNull(Desk.Preview.Replay);

        await ConnectObs();
        var (status, message) = await Press("take");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Replay: Lobby 3 · Bob ejected: Crewmate.", message);
        Assert.Equal("replay", Desk.OnAir.Layout);
        Assert.Equal("r2", _server.Replays!.Now!.Id);
        Assert.True(Desk.Preview.IsEmpty);
        Assert.NotNull(_server.Replays.Find("r2")!.PlayedAt);
        Assert.Equal((HttpStatusCode.Conflict, "No replay worth showing yet."), await Press("action"));
        // Play last still plays the newest, played or not.
        Assert.Equal(HttpStatusCode.OK, (await Press("replay/last")).Status);
    }

    [Fact]
    public async Task A_replay_gone_from_Preview_before_Take_clears_Preview_and_says_so()
    {
        Snap("Lobby 3");
        Keep("r2", "eject", "[[1|Bob]] ejected: Crewmate");
        await ConnectObs();
        Assert.Equal(HttpStatusCode.OK, (await Press("replay/best")).Status);
        _server.Replays!.Forget("r2");
        Assert.Equal((HttpStatusCode.Conflict, "That replay is gone: Preview is cleared."), await Press("take"));
        Assert.True(Desk.Preview.IsEmpty);
    }

    [Fact]
    public async Task A_camera_key_can_put_the_camera_into_Preview_and_Take_puts_it_on()
    {
        Snap("Lobby 3");
        int no = Desk.NumberOf("Lobby 3");
        var (status, message) = await Press($"cam/{no}/slot2", "&to=pvw");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("Preview: Lobby 3's player camera on Bob. Take puts it on.", message);
        Assert.Equal(("cams", "Lobby 3"), (Desk.Preview.Layout, Desk.Preview.Slots.Single()));
        Assert.Equal(2, Desk.CamPick("Lobby 3"));
        Assert.Equal("none", Desk.OnAir.Layout);
        await ConnectObs();
        Assert.Equal(HttpStatusCode.OK, (await Press("take")).Status);
        Assert.Equal(("cam", "Lobby 3"), (Desk.OnAir.Layout, Desk.OnAir.Slots[0]));
        // An explicit cut still cuts (no Preview).
        Assert.True((int)(await Press($"cam/{no}/slot1")).Status is >= 200 and < 300);
        Assert.Equal(1, Desk.CamPick("Lobby 3"));
        Assert.True(Desk.Preview.IsEmpty);
    }

    [Fact]
    public async Task Settings_lists_the_best_replay_link_and_the_camera_into_Preview()
    {
        var links = (await Get("app/admin/deck")).GetProperty("links").EnumerateArray().Select(l => l.GetProperty("url").GetString()!).ToList();
        Assert.Contains(links, u => u.Contains("/deck/replay/best?key="));
        Assert.Contains(links, u => u.Contains("/deck/cam/1/slot1?key=") && u.EndsWith("&to=pvw"));
        Assert.DoesNotContain(links, u => u.Contains("/deck/lobby/action?"));
    }
}
