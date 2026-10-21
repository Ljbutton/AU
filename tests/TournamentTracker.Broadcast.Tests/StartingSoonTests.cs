using System.Net;
using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// Starting soon, the screen before the show: on at once like the other breaks, a countdown that ends on
/// "Starting now" without switching by itself, the tournament's name, the caster's line and the break
/// sponsors taking turns on the graphics page, and its own OBS scene.
/// </summary>
public class StartingSoonTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _dir.Dispose();

    private CasterDesk Desk() => new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path);

    private void Snap(CasterDesk desk, string lobby, string phase) =>
        desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby, round = 1, phase, crewAlive = 6, impAlive = 2, taskPct = 20, t = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds() }));

    private static JsonElement State(BroadcastApp app) => JsonSerializer.SerializeToElement(app.State(), TestJson.Camel);

    [Fact]
    public void Start_times_are_minutes_from_now_or_a_time_of_day()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
        var now = new DateTime(2026, 10, 20, 17, 0, 0, DateTimeKind.Utc);        // 19:00 there
        foreach (var v in new[] { "10", "in 10", "10 min", " 10 minutes " })
            Assert.Equal(now.AddMinutes(10), CasterDesk.ParseStart(v, now, zone));
        Assert.Equal(now, CasterDesk.ParseStart("0", now, zone));
        Assert.Equal(new DateTime(2026, 10, 20, 17, 30, 0, DateTimeKind.Utc), CasterDesk.ParseStart("19:30", now, zone));
        Assert.Equal(new DateTime(2026, 10, 20, 17, 30, 0, DateTimeKind.Utc), CasterDesk.ParseStart("7:30pm", now, zone));
        Assert.Equal(new DateTime(2026, 10, 20, 18, 0, 0, DateTimeKind.Utc), CasterDesk.ParseStart("8pm", now, zone));
        // A time already gone today is tomorrow's.
        Assert.Equal(new DateTime(2026, 10, 21, 16, 0, 0, DateTimeKind.Utc), CasterDesk.ParseStart("18:00", now, zone));
        foreach (var bad in new[] { "", "soon", "25:00", "19:61", "13pm", "99999", "-5" })
            Assert.Null(CasterDesk.ParseStart(bad, now, zone));
    }

    [Fact]
    public void It_goes_on_at_once_counts_down_to_starting_now_and_never_switches_by_itself()
    {
        using var desk = Desk();
        foreach (var l in new[] { "LJ", "MAL" }) Snap(desk, l, "lobby");
        desk.AutoGrid = true;
        var (ok, said) = desk.SetSoon("1", null, "  Doors   open at seven  ", TimeZoneInfo.Utc);
        Assert.True(ok);
        Assert.Equal("Starting soon: starts at 18:01, text \"Doors open at seven\".", said);
        desk.ShowStartingSoon();
        Assert.Equal("soon", desk.OnAir.Layout);
        Assert.Empty(desk.OnAir.Slots);
        Assert.Equal(60, desk.SoonLeft());

        // Every lobby between games for minutes, auto grid on: nothing moves.
        for (int i = 0; i < 90; i++) { _clock.Advance(2); desk.Tick(); }
        Assert.Equal("soon", desk.OnAir.Layout);
        Assert.Empty(desk.Preview.Slots);
        Assert.Equal(0, desk.SoonLeft());
        var soon = JsonSerializer.SerializeToElement(desk.SoonState(), TestJson.Camel);
        Assert.True(soon.GetProperty("on").GetBoolean());
        Assert.Equal(0, soon.GetProperty("left").GetInt32());
        Assert.Equal("Doors open at seven", soon.GetProperty("text").GetString());

        // The caster takes a lobby: the show starts.
        desk.PreviewOnly("LJ");
        Assert.Equal("full", desk.Take()!.Layout);
        Assert.False(JsonSerializer.SerializeToElement(desk.SoonState(), TestJson.Camel).GetProperty("on").GetBoolean());
    }

    [Fact]
    public void Its_settings_are_checked_and_ending_it_goes_back_to_what_was_on()
    {
        using var desk = Desk();
        Snap(desk, "LJ", "ingame");
        Assert.Equal((false, "Type minutes (10) or a time (19:30)."), desk.SetSoon(null, "soon", null));
        Assert.Null(desk.SoonAt);
        Assert.Equal((true, "Starting soon: no countdown, no text."), desk.SetSoon("", null, ""));
        Assert.Equal(CasterDesk.SoonTextMax, desk.SetSoonText(new string('x', 300)).Length);

        desk.Show("LJ");
        desk.Show("", "soon");                    // the desk's show route too
        Assert.Equal("soon", desk.OnAir.Layout);
        Assert.Equal("LJ", desk.EndBreak()!.Slots[0]);
        // Undo after a switch away from it puts it back.
        desk.ShowStartingSoon("deck");
        desk.Show("LJ", by: "deck");
        Assert.Equal("soon", desk.Back()!.Layout);
    }

    [Fact]
    public void The_graphics_page_shows_the_name_countdown_line_and_the_break_sponsors_in_turn()
    {
        File.WriteAllText(Path.Combine(_dir.Path, SponsorBook.FileName), "{\"sponsors\":[{\"name\":\"Acme\",\"placements\":[\"break\"]},{\"name\":\"Bolt\",\"placements\":[\"break\",\"grid\"]},{\"name\":\"Quiet\",\"placements\":[\"grid\"]}]}");
        File.WriteAllText(Path.Combine(_dir.Path, BroadcastSettings.FileName), "{\"theme\":{\"tournamentName\":\"Spooky Cup\"}}");
        using var desk = Desk();
        var app = new BroadcastApp(desk, () => null, Path.Combine(_dir.Path, BroadcastSettings.FileName));
        Snap(desk, "LJ", "ingame");
        desk.Show("LJ");
        desk.Graphics.Add("storyline", null, "A note", "Jake is on a roll");
        Assert.Equal("storyline", State(app).GetProperty("big").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, State(app).GetProperty("soon").ValueKind);

        desk.SetSoon("5", null, "Grab a snack");
        desk.ShowStartingSoon();
        var s = State(app);
        Assert.Equal("soon", s.GetProperty("layout").GetString());
        Assert.Empty(s.GetProperty("slots").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("big").ValueKind);         // tables and notes wait for a lobby
        var soon = s.GetProperty("soon");
        Assert.Equal("Spooky Cup", soon.GetProperty("title").GetString());
        Assert.Equal(300, soon.GetProperty("left").GetInt32());
        Assert.False(soon.GetProperty("now").GetBoolean());
        Assert.Equal("Grab a snack", soon.GetProperty("text").GetString());
        Assert.Equal("Acme", soon.GetProperty("sponsor").GetProperty("name").GetString());
        _clock.Advance(BroadcastApp.SoonSponsorSeconds);
        Assert.Equal("Bolt", State(app).GetProperty("soon").GetProperty("sponsor").GetProperty("name").GetString());
        _clock.Advance(BroadcastApp.SoonSponsorSeconds);
        Assert.Equal("Acme", State(app).GetProperty("soon").GetProperty("sponsor").GetProperty("name").GetString());
        _clock.Advance(300);
        soon = State(app).GetProperty("soon");
        Assert.True(soon.GetProperty("now").GetBoolean());
        Assert.Equal(0, soon.GetProperty("left").GetInt32());

        // Off it: the last sponsor's appearance is logged as Starting soon's.
        desk.Show("LJ");
        Assert.Equal(JsonValueKind.Null, State(app).GetProperty("soon").ValueKind);
        var (csv, summary) = desk.Sponsors.Export();
        Assert.Contains("Acme,startingsoon,", csv);
        Assert.Contains("Bolt,startingsoon,", csv);
        Assert.Contains("Starting soon screen", summary);
        Assert.DoesNotContain("Quiet", csv);
    }

    [Fact]
    public void Sponsors_listed_for_starting_soon_take_the_place_of_the_break_sponsors()
    {
        File.WriteAllText(Path.Combine(_dir.Path, SponsorBook.FileName), "{\"sponsors\":[{\"name\":\"Acme\",\"placements\":[\"break\"]},{\"name\":\"Early\",\"placements\":[\"startingsoon\"]}]}");
        using var desk = Desk();
        var app = new BroadcastApp(desk, () => null, null);
        desk.ShowStartingSoon();
        for (int i = 0; i < 3; i++, _clock.Advance(BroadcastApp.SoonSponsorSeconds))
            Assert.Equal("Early", State(app).GetProperty("soon").GetProperty("sponsor").GetProperty("name").GetString());
        // No countdown set: none on screen.
        Assert.Equal(JsonValueKind.Null, State(app).GetProperty("soon").GetProperty("left").ValueKind);
    }

    [Fact]
    public void The_graphics_page_draws_it_over_everything_else()
    {
        using var s = typeof(BroadcastServer).Assembly.GetManifestResourceStream("ui/broadcast.html")!;
        string page = new StreamReader(s).ReadToEnd();
        Assert.Contains("if (S.layout === 'soon') draw('startingSoon', startingSoon);", page);
        Assert.Contains("now ? 'Starting now' : 'Starting soon'", page);
    }
}

/// <summary>Starting soon from the Stream Deck and the desk, in its own OBS scene.</summary>
public class StartingSoonObsTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private BroadcastServer _server = null!;
    private HttpClient _app = null!, _deck = null!;
    private string _key = "";
    private FakeObs _obs = null!;

    public async Task InitializeAsync()
    {
        string folder = Path.Combine(_dir.Path, "RedAlert");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "swoosh.webm"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(folder, ObsSettings.FileName), JsonSerializer.Serialize(new { swoosh = new { path = Path.Combine(folder, "swoosh.webm"), transitionPointMs = 0 } }));
        _server = new BroadcastServer(new BroadcastEnvironment { SettingsFile = Path.Combine(folder, "settings.json"), Port = 0, CasterPort = 0, Version = "0.1.0" }, new HttpClient(new FakeHttp()));
        _app = AppHttp.Client(_server.Url, _server.Token);
        _app.Timeout = TimeSpan.FromSeconds(20);
        _deck = new HttpClient { BaseAddress = new Uri(_server.Url), Timeout = TimeSpan.FromSeconds(20) };
        var admin = new SetupCode { Mode = "admin", TournamentId = "c", TournamentName = "Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results" };
        Assert.True((await AppHttp.Post(_app, "app/admin/code", new { code = admin.Encode() })).GetProperty("ok").GetBoolean());
        var links = (await AppHttp.Get(_app, "app/admin/deck")).GetProperty("links");
        string link = links[0].GetProperty("url").GetString()!;
        _key = link.Substring(link.IndexOf("key=") + 4);
        _obs = new FakeObs();
        var r = await AppHttp.Post(_app, "app/admin/obs", new { host = "127.0.0.1", port = _obs.Port.ToString(), password = "secret" });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.GetProperty("message").GetString());
    }

    public async Task DisposeAsync()
    {
        _server.Dispose();
        await _obs.DisposeAsync();
        _dir.Dispose();
    }

    private CasterDesk Desk => _server.Desk!;

    private async Task<(HttpStatusCode Status, string Message)> Press(string path, string more = "")
    {
        var r = await _deck.PostAsync($"deck/{path}?key={_key}{more}", new StringContent(""));
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 && text[0] == '{' ? JsonDocument.Parse(text).RootElement.GetProperty("message").GetString() ?? "" : text);
    }

    [Fact]
    public async Task The_scene_is_built_and_the_Stream_Deck_puts_it_on_with_its_countdown_and_line()
    {
        Assert.True(_obs.Scenes.ContainsKey("TT Starting Soon"));
        Assert.Contains(_obs.Scenes["TT Starting Soon"], i => i.Source == "TT Broadcast");
        Assert.Contains((await AppHttp.Get(_app, "app/admin/deck")).GetProperty("links").EnumerateArray(), l => l.GetProperty("url").GetString()!.Contains("/deck/break/soon?"));

        Assert.Equal(HttpStatusCode.BadRequest, (await Press("soon/set", "&at=later")).Status);
        Assert.Null(Desk.SoonAt);
        var (status, message) = await Press("break/soon", "&in=10&text=Doors%20open");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("On stream: Starting soon (", message);
        Assert.Equal("soon", Desk.OnAir.Layout);
        Assert.InRange(Desk.SoonLeft() ?? 0, 590, 600);
        Assert.Equal("Doors open", Desk.SoonText);
        await Wait.For(() => _obs.Program == "TT Starting Soon", 5000);
        Assert.DoesNotContain(_obs.Shown("TT Starting Soon"), i => i.Source.StartsWith("TT Lobby "));

        // The desk's Set: the countdown and line change, nothing switches.
        var r = await AppHttp.Post(_app, "app/admin/do", new { path = "soon/set", query = new { at = "", text = "Back in a bit" } });
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("Starting soon: no countdown, text \"Back in a bit\".", r.GetProperty("message").GetString());
        Assert.Null(Desk.SoonAt);
        var desk = (await AppHttp.Get(_app, "app/admin/live")).GetProperty("desk");
        Assert.True(desk.GetProperty("soon").GetProperty("on").GetBoolean());
        Assert.Equal("Back in a bit", desk.GetProperty("soon").GetProperty("text").GetString());

        // End the break: back to the top lobby (nothing was on before).
        Assert.Equal(HttpStatusCode.OK, (await Press("break/off")).Status);
        Assert.NotEqual("soon", Desk.OnAir.Layout);
    }
}
