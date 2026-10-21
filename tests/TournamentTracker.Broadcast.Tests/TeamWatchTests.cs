using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// Team watch: the Watch page on the network behind its code (off by default, a token on every route,
/// wrong codes limited, a new code signs everyone out), impostors only behind the code, the team's
/// suggestions reaching the Live desk (Into Preview, Cut, Dismiss, the Stream Deck's suggest/next), the
/// pictures taken only while someone watches, and the watch code kept out of support bundles.
/// </summary>
public class TeamWatchTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private BroadcastServer _server = null!;
    private HttpClient _app = null!;
    private HttpClient? _watch;
    private FakeObs? _obs;

    public async Task InitializeAsync()
    {
        string folder = Path.Combine(_dir.Path, "RedAlert");
        Directory.CreateDirectory(folder);
        _server = new BroadcastServer(new BroadcastEnvironment
        {
            SettingsFile = Path.Combine(folder, "settings.json"), Port = 0, CasterPort = 0, WatchPort = 0, WatchCapture = false, Version = "0.3.19",
            SupportFolder = Path.Combine(_dir.Path, "Desktop"), ObsFolder = Path.Combine(_dir.Path, "obs-studio"),
        }, new HttpClient(new FakeHttp()));
        _app = AppHttp.Client(_server.Url, _server.Token);
        _app.Timeout = TimeSpan.FromSeconds(15);
        var admin = new SetupCode { Mode = "admin", TournamentId = "c", TournamentName = "Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results" };
        Assert.True((await AppHttp.Post(_app, "app/admin/code", new { code = admin.Encode() })).GetProperty("ok").GetBoolean());
    }

    public async Task DisposeAsync()
    {
        _watch?.Dispose();
        _server.Dispose();
        if (_obs != null) await _obs.DisposeAsync();
        _dir.Dispose();
    }

    private CasterDesk Desk => _server.Desk!;

    /// <summary>A lobby's snapshot: Ann (red) is the impostor, Bob (blue) a crewmate, Cy (green) dead.</summary>
    private void Snap(string lobby, string phase = "ingame")
    {
        Desk.Apply(JsonSerializer.Serialize(new
        {
            type = "snap", lobby, round = 1, game = lobby + "-1", phase, crewAlive = 6, impAlive = 2, taskPct = 40,
            spec = new { cam = true, camPick = (int?)null, camOn = 2, on = true, audio = "wide" },
            players = new object[]
            {
                new { id = 1, name = "Ann", color = 0, key = "ann#1", imp = true },
                new { id = 2, name = "Bob", color = 1, key = "bob#1" },
                new { id = 3, name = "Cy", color = 2, key = "cy#1", dead = true },
            },
        }));
    }

    /// <summary>Team watch on (as the Settings switch does), and a client for the Watch page that keeps no cookies of its own.</summary>
    private async Task<HttpClient> TurnOn()
    {
        var r = await AppHttp.Post(_app, "app/admin/teamwatch", new { action = "on" });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        Assert.True(_server.Watch.Listening);
        _watch = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri($"http://127.0.0.1:{_server.Watch.Port}/"), Timeout = TimeSpan.FromSeconds(15) };
        return _watch;
    }

    private string Code => _server.Watch.Code;

    private static async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Raw)> Send(HttpClient http, HttpMethod method, string path, object? body = null, string? token = null, string? cookie = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (token != null) req.Headers.Add("X-Watch-Token", token);
        if (cookie != null) req.Headers.Add("Cookie", "ttwatch=" + cookie);
        var r = await http.SendAsync(req);
        string text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 && text[0] == '{' ? JsonDocument.Parse(text).RootElement.Clone() : default, r);
    }

    private static async Task<string> SignIn(HttpClient http, string code, string name)
    {
        var (status, body, _) = await Send(http, HttpMethod.Post, "watch/signin", new { code, name });
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("token").GetString()!;
    }

    private static async Task<JsonElement> WatchState(HttpClient http, string token)
    {
        var (status, body, _) = await Send(http, HttpMethod.Get, "watch/state", token: token);
        Assert.Equal(HttpStatusCode.OK, status);
        return body;
    }

    [Fact]
    public async Task Team_watch_is_off_by_default_and_not_listening_and_off_again_stops_listening()
    {
        Assert.False(_server.Watch.Listening);
        Assert.Equal(0, _server.Watch.Port);
        var st = await AppHttp.Get(_app, "app/admin/teamwatch");
        Assert.False(st.GetProperty("on").GetBoolean());
        Assert.False(st.GetProperty("listening").GetBoolean());
        Assert.Empty(st.GetProperty("urls").EnumerateArray());
        // A fresh settings file says off too (and keeps the code it made).
        var saved = BroadcastAppSettings.Load(Path.Combine(_dir.Path, "RedAlert", "settings.json"));
        Assert.False(saved.TeamWatch);
        Assert.Equal(Code, saved.WatchCode);
        // The default port is 8780 (the next free one up to 8789).
        Assert.Equal(8780, new BroadcastEnvironment().WatchPort);

        await TurnOn();
        int port = _server.Watch.Port;
        using (var open = new TcpClient())
        using (var limit = new CancellationTokenSource(10000))
            await open.ConnectAsync(IPAddress.Loopback, port, limit.Token);
        Assert.True(BroadcastAppSettings.Load(Path.Combine(_dir.Path, "RedAlert", "settings.json")).TeamWatch);

        var off = await AppHttp.Post(_app, "app/admin/teamwatch", new { action = "off" });
        Assert.True(off.GetProperty("ok").GetBoolean());
        Assert.False(_server.Watch.Listening);
        using var closed = new TcpClient();
        using var limit2 = new CancellationTokenSource(10000);
        await Assert.ThrowsAnyAsync<Exception>(async () => await closed.ConnectAsync(IPAddress.Loopback, port, limit2.Token));
    }

    [Fact]
    public void A_wrong_code_is_refused_and_too_many_from_one_address_wait_a_minute_even_with_the_right_one()
    {
        var clock = new FakeClock();
        using var watch = new WatchServer(0, null, () => clock.Now);
        Assert.True(WatchServer.IsCode(watch.Code));
        Assert.Equal(WatchServer.CodeLength, watch.Code.Length);
        for (int i = 0; i < WatchServer.WrongPerMinute; i++)
            Assert.Equal(401, watch.SignIn("10.0.0.5", "WRONG123", "Sam").Status);
        // Locked out for the rest of the minute: even the right code waits.
        var (status, message, who) = watch.SignIn("10.0.0.5", watch.Code, "Sam");
        Assert.Equal(429, status);
        Assert.Null(who);
        Assert.Contains("Wait a minute", message);
        // Another address isn't held up.
        Assert.Equal(200, watch.SignIn("10.0.0.6", watch.Code, "Jo").Status);
        // A minute later: in, with the code typed loosely (small letters, a space, a dash).
        clock.Advance(61);
        string loose = watch.Code.ToLowerInvariant().Insert(4, "- ");
        Assert.Equal(200, watch.SignIn("10.0.0.5", loose, "Sam").Status);
        // A name is needed.
        Assert.Equal(400, watch.SignIn("10.0.0.7", watch.Code, "  ").Status);
    }

    [Fact]
    public async Task Over_the_network_a_wrong_code_is_401_and_the_right_one_gives_a_token_and_a_cookie()
    {
        var http = await TurnOn();
        var (bad, badBody, _) = await Send(http, HttpMethod.Post, "watch/signin", new { code = "AAAAAAAA", name = "Sam" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad);
        Assert.False(badBody.TryGetProperty("token", out _));
        var (ok, body, raw) = await Send(http, HttpMethod.Post, "watch/signin", new { code = Code, name = "Sam" });
        Assert.Equal(HttpStatusCode.OK, ok);
        string token = body.GetProperty("token").GetString()!;
        Assert.True(token.Length >= 24);
        var cookie = raw.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("ttwatch=" + token, cookie);
        Assert.Contains("HttpOnly", cookie);
        Assert.Contains("SameSite=Strict", cookie);
        Assert.Contains(_server.Watch.Watchers(), w => w.Name == "Sam" && w.Ip == "127.0.0.1");
        // Settings shows who's watching (name, address, last seen).
        var st = await AppHttp.Get(_app, "app/admin/teamwatch");
        var w0 = st.GetProperty("watchers").EnumerateArray().Single();
        Assert.Equal("Sam", w0.GetProperty("name").GetString());
        Assert.Equal("127.0.0.1", w0.GetProperty("ip").GetString());
        Assert.True(w0.GetProperty("watching").GetBoolean());
    }

    [Fact]
    public async Task A_new_code_signs_everyone_out()
    {
        var http = await TurnOn();
        string old = Code;
        string token = await SignIn(http, old, "Sam");
        await WatchState(http, token);
        var r = await AppHttp.Post(_app, "app/admin/teamwatch", new { action = "code" });
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.NotEqual(old, Code);
        Assert.Equal(Code, BroadcastAppSettings.Load(Path.Combine(_dir.Path, "RedAlert", "settings.json")).WatchCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, HttpMethod.Get, "watch/state", token: token)).Status);
        Assert.Empty(_server.Watch.Watchers());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, HttpMethod.Post, "watch/signin", new { code = old, name = "Sam" })).Status);
        await WatchState(http, await SignIn(http, Code, "Sam"));
    }

    [Fact]
    public async Task Every_watch_route_needs_the_token_and_changes_need_it_in_the_header()
    {
        Snap("LJ");
        var http = await TurnOn();
        foreach (var path in new[] { "watch/state", "watch/me", "watch/frame?lobby=LJ", "watch/sim?lobby=LJ" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, HttpMethod.Get, path)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, HttpMethod.Get, path, token: "not-a-token")).Status);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, HttpMethod.Post, "watch/suggest", new { lobby = "LJ", reason = "kill" })).Status);
        // A Host header of anything at all makes no difference: only the token counts.
        var req = new HttpRequestMessage(HttpMethod.Get, "watch/state");
        req.Headers.Host = "localhost";
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(req)).StatusCode);

        string token = await SignIn(http, Code, "Sam");
        // The page's own requests: the header. The pictures: the cookie (GET only).
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Get, "watch/state", token: token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Get, "watch/state", cookie: token)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(http, HttpMethod.Get, "watch/frame?lobby=LJ", cookie: token)).Status);
        // A change with only the cookie (as a page elsewhere could send it) is refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, HttpMethod.Post, "watch/suggest", new { lobby = "LJ", reason = "kill" }, cookie: token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Post, "watch/suggest", new { lobby = "LJ", reason = "kill" }, token: token)).Status);
        // The page itself (the sign-in) needs nothing, and says nothing about the games.
        string page = await http.GetStringAsync("/");
        Assert.Contains("Who's watching?", page);
        Assert.DoesNotContain("Ann", page);
        // The Red Alert desk's own routes aren't on this port.
        Assert.Equal(HttpStatusCode.NotFound, (await Send(http, HttpMethod.Get, "app/state", token: token)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(http, HttpMethod.Get, "deck/state", token: token)).Status);
    }

    [Fact]
    public async Task Impostors_are_in_the_watch_state_only_behind_the_code()
    {
        Snap("LJ");
        var http = await TurnOn();
        var (status, body, raw) = await Send(http, HttpMethod.Get, "watch/state");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        string text = await raw.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Ann", text);
        Assert.DoesNotContain("imp", text);

        var state = await WatchState(http, await SignIn(http, Code, "Sam"));
        var lobby = state.GetProperty("lobbies").EnumerateArray().Single();
        Assert.Equal("LJ", lobby.GetProperty("lobby").GetString());
        var players = lobby.GetProperty("players").EnumerateArray().ToList();
        Assert.True(players.Single(p => p.GetProperty("name").GetString() == "Ann").GetProperty("imp").GetBoolean());
        Assert.False(players.Single(p => p.GetProperty("name").GetString() == "Bob").GetProperty("imp").GetBoolean());
        Assert.True(players.Single(p => p.GetProperty("name").GetString() == "Cy").GetProperty("dead").GetBoolean());
        Assert.Equal(6, lobby.GetProperty("crew").GetInt32());
        Assert.Equal(2, lobby.GetProperty("imps").GetInt32());
        Assert.Equal(40, lobby.GetProperty("tasks").GetInt32());
        Assert.Equal("Cup", state.GetProperty("tournament").GetString());
    }

    [Fact]
    public async Task A_suggestion_reaches_the_desk_state_and_the_watch_state()
    {
        Snap("LJ"); Snap("MAL");
        var http = await TurnOn();
        string sam = await SignIn(http, Code, "Sam");
        var (status, said, _) = await Send(http, HttpMethod.Post, "watch/suggest", new { lobby = "lj", reason = "kill", text = "red is stalking blue" }, token: sam);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Sent: LJ, Kill coming.", said.GetProperty("message").GetString());

        var live = await AppHttp.Get(_app, "app/admin/live");
        var s = live.GetProperty("desk").GetProperty("suggestions").GetProperty("live").EnumerateArray().Single();
        Assert.Equal("LJ", s.GetProperty("lobby").GetString());
        Assert.Equal("Sam", s.GetProperty("who").GetString());
        Assert.Equal("Kill coming", s.GetProperty("label").GetString());
        Assert.Equal("red is stalking blue", s.GetProperty("text").GetString());

        // Every watcher sees it on the card (someone else sending the same adds their name).
        string jo = await SignIn(http, Code, "Jo");
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Post, "watch/suggest", new { lobby = "LJ", reason = "kill" }, token: jo)).Status);
        JsonElement seen = default;
        await Wait.Until(async () =>
        {
            seen = (await WatchState(http, jo)).GetProperty("suggestions").GetProperty("live");
            return seen.GetArrayLength() == 1 && seen[0].GetProperty("also").GetArrayLength() == 1;
        }, 5000);
        Assert.Equal("Jo", seen[0].GetProperty("also")[0].GetString());

        // A lobby on stream already isn't suggested (its camera can be).
        Desk.Show("MAL");
        var (onAir, why, _) = await Send(http, HttpMethod.Post, "watch/suggest", new { lobby = "MAL", reason = "clutch" }, token: await SignIn(http, Code, "Kim"));
        Assert.Equal(HttpStatusCode.Conflict, onAir);
        Assert.Equal("MAL is on stream already.", why.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Into_Preview_from_a_suggestion_stages_the_lobby_or_its_camera_on_the_player()
    {
        Snap("LJ"); Snap("MAL");
        var http = await TurnOn();
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Post, "watch/suggest", new { lobby = "LJ", reason = "clutch" }, token: await SignIn(http, Code, "Sam"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Post, "watch/suggest", new { lobby = "MAL", reason = "cam", player = 2 }, token: await SignIn(http, Code, "Jo"))).Status);
        // A dead player's camera isn't suggested.
        var (dead, deadWhy, _) = await Send(http, HttpMethod.Post, "watch/suggest", new { lobby = "MAL", reason = "cam", player = 3 }, token: await SignIn(http, Code, "Kim"));
        Assert.Equal(HttpStatusCode.Conflict, dead);
        Assert.Equal("Cy is dead: pick a living player.", deadWhy.GetProperty("message").GetString());

        var list = Desk.Suggestions.Live();
        var lobby = list.Single(x => x.Lobby == "LJ");
        var cam = list.Single(x => x.Lobby == "MAL");
        Assert.Equal("Cam on Bob", cam.Label);

        var r = await AppHttp.Post(_app, "app/admin/suggestion", new { id = lobby.Id, action = "preview" });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        Assert.Equal(new[] { "LJ" }, Desk.Preview.Slots.ToArray());
        Assert.True(Desk.Suggestions.Find(lobby.Id)!.Previewed);

        r = await AppHttp.Post(_app, "app/admin/suggestion", new { id = cam.Id, action = "preview" });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        Assert.Equal(new[] { "MAL" }, Desk.Preview.Slots.ToArray());
        Assert.Equal("cams", Desk.Preview.Layout);
        Assert.Equal(2, Desk.CamPick("MAL"));
        // Take: its camera on stream, and the suggestion clears itself (its lobby's camera is on Program).
        Assert.NotNull(Desk.Take());
        Assert.Equal("cam", Desk.OnAir.Layout);
        Assert.DoesNotContain(Desk.Suggestions.Live(), x => x.Id == cam.Id);
        Assert.Equal("program", Desk.Suggestions.Earlier().Single(x => x.Id == cam.Id).Cleared);

        // Cut: the desk's own direct cut (the lobby full screen); Dismiss: under Earlier.
        r = await AppHttp.Post(_app, "app/admin/suggestion", new { id = lobby.Id, action = "cut" });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        Assert.Equal("LJ", Desk.OnAir.Slots[0]);
        Assert.Empty(Desk.Suggestions.Live());
        r = await AppHttp.Post(_app, "app/admin/suggestion", new { id = lobby.Id, action = "preview" });
        Assert.Equal("That suggestion has gone.", r.GetProperty("message").GetString());
    }

    [Fact]
    public void Suggestions_expire_after_two_minutes_clear_when_their_lobby_goes_on_Program_and_can_be_dismissed()
    {
        var clock = new FakeClock();
        var air = new OnAir();
        var box = new SuggestionBox(() => clock.Now, () => air);
        Assert.Equal(200, box.Add("w1", "Sam", "LJ", "kill", null).Status);
        clock.Advance(5);
        Assert.Equal(200, box.Add("w2", "Jo", "MAL", "meeting", "vote coming").Status);
        clock.Advance(5);
        Assert.Equal(200, box.Add("w3", "Kim", "SOG", "funny", null).Status);
        // Newest first.
        Assert.Equal(new[] { "SOG", "MAL", "LJ" }, box.Live().Select(s => s.Lobby).ToArray());

        // MAL goes on stream: its suggestion clears itself.
        clock.Advance(1);
        air = new OnAir { Layout = "full", Slots = new List<string?> { "MAL" }, Since = clock.Now };
        Assert.DoesNotContain(box.Live(), s => s.Lobby == "MAL");
        Assert.Equal("program", box.Earlier().Single(s => s.Lobby == "MAL").Cleared);
        // A lobby that was on stream before the suggestion (its camera suggested, say) isn't cleared by that.
        clock.Advance(1);
        Assert.Equal(200, box.Add("w2", "Jo", "MAL", "cam", null, 4, "Dee", 6).Status);
        Assert.Contains(box.Live(), s => s.Lobby == "MAL" && s.IsCam);

        // Dismissed by the caster.
        var sog = box.Live().Single(s => s.Lobby == "SOG");
        Assert.True(box.Dismiss(sog.Id));
        Assert.False(box.Dismiss(sog.Id));
        Assert.Equal("dismissed", box.Earlier().First(s => s.Lobby == "SOG").Cleared);

        // Two minutes after it was sent: LJ's times out.
        clock.Advance(SuggestionBox.ExpireSeconds - 13);
        Assert.Contains(box.Live(), s => s.Lobby == "LJ");
        clock.Advance(1);
        Assert.DoesNotContain(box.Live(), s => s.Lobby == "LJ");
        Assert.Equal("expired", box.Earlier().First(s => s.Lobby == "LJ").Cleared);
        // The state the desk and the Watch page read.
        var st = JsonSerializer.SerializeToElement(box.State(), TestJson.Camel);
        Assert.Single(st.GetProperty("live").EnumerateArray());
        Assert.Equal(3, st.GetProperty("earlier").GetArrayLength());
    }

    [Fact]
    public void One_person_can_send_one_suggestion_every_few_seconds_and_a_few_a_minute_and_the_text_is_short()
    {
        var clock = new FakeClock();
        var box = new SuggestionBox(() => clock.Now, () => new OnAir());
        Assert.Equal(200, box.Add("w1", "Sam", "L1", "kill", null).Status);
        Assert.Equal(429, box.Add("w1", "Sam", "L2", "kill", null).Status);
        Assert.Equal(200, box.Add("w2", "Jo", "L2", "kill", null).Status);       // someone else isn't held up
        int sent = 1;
        for (int i = 3; sent < SuggestionBox.PerMinute; i++)
        {
            clock.Advance(SuggestionBox.MinGapSeconds);
            Assert.Equal(200, box.Add("w1", "Sam", "L" + i, "clutch", null).Status);
            sent++;
        }
        clock.Advance(SuggestionBox.MinGapSeconds);
        Assert.Equal(429, box.Add("w1", "Sam", "L99", "clutch", null).Status);      // the minute's share used
        clock.Advance(60);
        Assert.Equal(400, box.Add("w1", "Sam", "L1", "funny", new string('x', SuggestionBox.MaxText + 1)).Status);
        Assert.Equal(400, box.Add("w3", "Kim", "L1", "", "  ").Status);
        Assert.Equal(400, box.Add("w4", "Lee", "L1", "dance", null).Status);
        Assert.Equal(400, box.Add("w5", "Max", "L1", "cam", null).Status);           // a camera needs its player
        // Text is one line, without control characters.
        var (_, _, made) = box.Add("w6", "Ola", "L50", "", "look\nat\tthis");
        Assert.Equal("look at this", made!.Text);
        // Bounded: never more than MaxLive at once.
        for (int i = 0; i < SuggestionBox.MaxLive + 5; i++) box.Add("x" + i, "P" + i, "M" + i, "kill", null);
        Assert.Equal(SuggestionBox.MaxLive, box.Live().Count);
        Assert.True(box.Earlier().Count <= SuggestionBox.MaxEarlier);
    }

    [Fact]
    public async Task The_Stream_Deck_suggest_next_puts_the_newest_suggestion_into_Preview_and_says_409_without()
    {
        Snap("LJ"); Snap("MAL");
        string link = (await AppHttp.Get(_app, "app/admin/deck")).GetProperty("links").EnumerateArray().Select(l => l.GetProperty("url").GetString()!).Single(u => u.Contains("/deck/suggest/next"));
        string key = link.Substring(link.IndexOf("key=") + 4);
        using var deck = new HttpClient { BaseAddress = new Uri(_server.Url), Timeout = TimeSpan.FromSeconds(15) };
        async Task<(HttpStatusCode, JsonElement)> Press()
        {
            var r = await deck.PostAsync($"deck/suggest/next?key={key}", new StringContent(""));
            return (r.StatusCode, JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone());
        }
        var (none, said) = await Press();
        Assert.Equal(HttpStatusCode.Conflict, none);
        Assert.Equal("No suggestions.", said.GetProperty("message").GetString());

        Desk.Suggestions.Add("w1", "Sam", "LJ", "kill", "red is about to kill");
        Desk.Suggestions.Add("w2", "Jo", "MAL", "clutch", null);
        // The same press again straight after counts once (any key): wait that out.
        await Task.Delay(BroadcastServer.DeckRepeat + TimeSpan.FromMilliseconds(50));
        var (ok, _) = await Press();
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(new[] { "MAL" }, Desk.Preview.Slots.ToArray());
        // Again (after the repeat window): the next one not yet in Preview.
        await Task.Delay(BroadcastServer.DeckRepeat + TimeSpan.FromMilliseconds(50));
        Assert.Equal(HttpStatusCode.OK, (await Press()).Item1);
        Assert.Equal(new[] { "LJ" }, Desk.Preview.Slots.ToArray());

        // The state keys read: how many, and the next one by lobby and reason; its words only with &imp=1.
        Desk.Suggestions.Add("w3", "Kim", "SOG", "funny", "red is about to kill");
        var plain = JsonDocument.Parse(await deck.GetStringAsync($"deck/state?key={key}")).RootElement;
        var s = plain.GetProperty("suggestions");
        Assert.Equal(3, s.GetProperty("count").GetInt32());
        Assert.Equal("SOG", s.GetProperty("next").GetProperty("lobby").GetString());
        Assert.Equal("Funny", s.GetProperty("next").GetProperty("label").GetString());
        Assert.False(s.GetProperty("next").TryGetProperty("text", out _));
        Assert.DoesNotContain("about to kill", plain.GetRawText());
        Assert.DoesNotContain("\"imp\"", plain.GetRawText());
        var full = JsonDocument.Parse(await deck.GetStringAsync($"deck/state?key={key}&imp=1")).RootElement;
        Assert.Contains("about to kill", full.GetRawText());
    }

    [Fact]
    public async Task The_pictures_are_taken_from_OBS_only_while_someone_watches()
    {
        var obs = _obs = new FakeObs();
        var desk = new CasterDesk(null, null, new PriorityConfig());
        var feeds = new List<(string Lobby, string Url)> { ("LJ", "https://vdo.ninja/?view=a&password=x"), ("MAL", "https://vdo.ninja/?view=b&password=x") };
        await using var director = new ObsDirector(Path.Combine(_dir.Path, "obs-watch.json"), desk, () => feeds.ToList());
        foreach (var l in new[] { "LJ", "MAL" }) desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = l, round = 1, phase = "ingame" }));
        await director.ConnectAsync("127.0.0.1", obs.Port, "secret");
        bool watching = false;
        using var capture = new WatchCapture(() => watching && director.Connected, () => new[] { "LJ", "MAL" }, director.LobbyShotAsync, run: false);

        int before;
        lock (obs) before = obs.Screenshots;
        await capture.StepAsync();
        await capture.StepAsync();
        lock (obs) Assert.Equal(before, obs.Screenshots);
        Assert.False(capture.Running);
        Assert.Null(capture.Latest("LJ"));

        watching = true;
        var wait = await capture.StepAsync();
        Assert.True(capture.Running);
        lock (obs)
        {
            Assert.Equal(before + 2, obs.Screenshots);
            Assert.Equal(new[] { "TT Lobby LJ", "TT Lobby MAL" }, obs.ShotSources.TakeLast(2).ToArray());
        }
        Assert.Equal(0xFF, capture.Latest("LJ")![0]);
        Assert.NotNull(capture.Latest("MAL"));
        Assert.True(wait <= TimeSpan.FromSeconds(1.0 / WatchCapture.FpsFor(2)));

        watching = false;
        await capture.StepAsync();
        lock (obs) Assert.Equal(before + 2, obs.Screenshots);
        // The rate: 5 a second per lobby, fewer with many lobbies (never more than the total).
        Assert.Equal(WatchCapture.MaxFps, WatchCapture.FpsFor(4));
        Assert.True(WatchCapture.FpsFor(12) * 12 <= WatchCapture.MaxShotsPerSecond);
        desk.Dispose();
    }

    [Fact]
    public void Someone_counts_as_watching_only_while_their_page_keeps_asking()
    {
        var clock = new FakeClock();
        using var watch = new WatchServer(0, null, () => clock.Now);
        Assert.True(watch.Start());
        Assert.False(watch.AnyoneWatching);
        var (_, _, who) = watch.SignIn("10.0.0.5", watch.Code, "Sam");
        Assert.True(watch.AnyoneWatching);
        clock.Advance(WatchServer.WatchingFor.TotalSeconds + 1);
        Assert.False(watch.AnyoneWatching);
        Assert.NotNull(watch.Check(who!.Token));
        Assert.True(watch.AnyoneWatching);
        // Signed out from Settings.
        Assert.True(watch.SignOut(who.Id));
        Assert.Null(watch.Check(who.Token));
        Assert.False(watch.AnyoneWatching);
        // Bounded: MaxWatchers at once (room is made from whoever has been gone longest).
        for (int i = 0; i < WatchServer.MaxWatchers; i++) Assert.Equal(200, watch.SignIn("10.0.1." + i, watch.Code, "P" + i).Status);
        Assert.Equal(409, watch.SignIn("10.0.2.1", watch.Code, "Late").Status);
        clock.Advance(180);
        Assert.Equal(200, watch.SignIn("10.0.2.1", watch.Code, "Late").Status);
        Assert.Equal(WatchServer.MaxWatchers, watch.Watchers().Count);
        watch.Stop();
        Assert.False(watch.AnyoneWatching);
        Assert.Empty(watch.Watchers());
    }

    [Fact]
    public void The_event_feed_says_kills_bodies_meetings_votes_ejections_and_the_end_with_names()
    {
        var clock = new FakeClock();
        var desk = new CasterDesk(null, () => clock.Now, new PriorityConfig());
        void Ev(string kind, object data)
        {
            var d = JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
            d["type"] = "event"; d["lobby"] = "LJ"; d["kind"] = kind; d["round"] = 1; d["game"] = "LJ-1";
            desk.Apply(JsonSerializer.Serialize(d));
        }
        desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = "LJ", round = 1, phase = "ingame", players = new object[] { new { id = 1, name = "Ann", color = 0, key = "a" }, new { id = 2, name = "Bob", color = 1, key = "b" }, new { id = 3, name = "Cy", color = 2, key = "c" } } }));
        Ev("gameStart", new { map = "Polus" });
        Ev("kill", new { killer = new { id = 1, name = "Ann", color = 0 }, victim = new { id = 3, name = "Cy", color = 2 }, room = "Electrical" });
        Ev("meeting", new { caller = new { id = 2, name = "Bob", color = 1 }, body = new { id = 3, name = "Cy", color = 2 }, emergency = false });
        Ev("eject", new { votes = new object[] { new { voter = 2, target = 1 }, new { voter = 1, target = 2 }, new { voter = 3, target = (int?)null } }, ejected = new { id = 1, name = "Ann", color = 0 }, wasImpostor = true });
        Ev("gameEnd", new { winner = "Crewmates", how = "vote" });
        var lines = desk.Events.Recent("LJ", 10);
        Assert.Equal(new[] { "end", "eject", "votes", "body", "kill", "start" }, lines.Select(e => e.Kind).ToArray());
        Assert.Equal("[[0|Ann]] killed [[2|Cy]] in Electrical", lines.Single(e => e.Kind == "kill").Text);
        Assert.Equal("[[1|Bob]] reported [[2|Cy]]'s body", lines.Single(e => e.Kind == "body").Text);
        Assert.Equal("Votes: [[0|Ann]] 1, [[1|Bob]] 1, skipped 1", lines.Single(e => e.Kind == "votes").Text);
        Assert.Equal("[[0|Ann]] ejected: Impostor", lines.Single(e => e.Kind == "eject").Text);
        Assert.Equal("Crewmates win by vote", lines[0].Text);
        Assert.Equal("Game started on Polus", lines.Last().Text);
        desk.ForgetSim();
        desk.Dispose();
    }

    [Fact]
    public async Task The_support_bundle_has_no_watch_code()
    {
        await TurnOn();
        string code = Code;
        var r = await AppHttp.Post(_app, "app/support", new { });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        string zip = Path.Combine(_dir.Path, "Desktop", r.GetProperty("file").GetString()!);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var e in archive.Entries)
        {
            using var reader = new StreamReader(e.Open());
            string text = reader.ReadToEnd();
            Assert.False(text.Contains(code), e.FullName + " has the watch code");
            if (e.FullName == "versions.txt") Assert.Contains("Team watch: on, port", text);
        }
        // Nor in the log.
        string log = File.ReadAllText(Path.Combine(_dir.Path, "RedAlert", "red-alert.log"));
        Assert.DoesNotContain(code, log);
        Assert.Contains("Team watch is on", log);
    }

    [Fact]
    public async Task Simulation_works_with_the_Watch_page()
    {
        var http = await TurnOn();
        await AppHttp.Post(_app, "app/admin/sim", new { on = "true" });
        await Wait.Until(() => Desk.Board.Ranking().Count(r => r.Online) == 4, 10000);
        string token = await SignIn(http, Code, "Sam");
        var state = await WatchState(http, token);
        var lobbies = state.GetProperty("lobbies").EnumerateArray().ToList();
        Assert.Equal(new[] { "SIM-1", "SIM-2", "SIM-3", "SIM-4" }, lobbies.Select(l => l.GetProperty("lobby").GetString()).ToArray());
        Assert.True(state.GetProperty("sim").GetBoolean());
        // Without OBS, each lobby's stand-in picture comes from the Watch page's own port.
        Assert.Equal("/watch/sim?lobby=SIM-1", lobbies[0].GetProperty("video").GetString());
        var (status, _, raw) = await Send(http, HttpMethod.Get, "watch/sim?lobby=SIM-1", cookie: token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Simulated lobby", await raw.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Post, "watch/suggest", new { lobby = "SIM-2", reason = "funny" }, token: token)).Status);
        // Simulation off: its suggestions go with it.
        await AppHttp.Post(_app, "app/admin/sim", new { on = "false" });
        Assert.Empty(Desk.Suggestions.Live());
        Assert.Empty(Desk.Suggestions.Earlier());
    }
}

/// <summary>
/// Everything the Live desk's Happening now showed that isn't a play is still there: the desk's own items
/// (lobby down, back, interrupted with count/void/replay, the replay decision, chat's pick) stay in the
/// desk's state and at the top of the Suggestions column; plays are in the Plays drawer.
/// </summary>
public class SuggestionsColumnTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly CasterDesk _desk;
    private readonly Dictionary<string, long> _seq = new();

    public SuggestionsColumnTests() { _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig()); }

    public void Dispose() => _desk.Dispose();

    private void Send(string lobby, string type, string? kind, object? data = null, string src = "run1")
    {
        var msg = data == null ? new Dictionary<string, object?>() : JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        msg["v"] = TournamentTracker.Broadcast.FeedProtocol.Version;
        msg["type"] = type; msg["lobby"] = lobby; msg["round"] = 1; if (!msg.ContainsKey("game")) msg["game"] = lobby + "-1";
        msg["t"] = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds();
        msg["src"] = src;
        msg["seq"] = _seq[lobby + src] = _seq.GetValueOrDefault(lobby + src) + 1;
        if (kind != null) msg["kind"] = kind;
        _desk.Apply(JsonSerializer.Serialize(msg));
    }

    private void Snap(string lobby, string phase = "ingame", string src = "run1") => Send(lobby, "snap", null, new { phase, crewAlive = 7, impAlive = 2, taskPct = 40 }, src);

    private List<JsonElement> Cards() => JsonSerializer.SerializeToElement(_desk.State(), TestJson.Camel).GetProperty("cards").EnumerateArray().ToList();

    private static string Page()
    {
        using var s = typeof(BroadcastServer).Assembly.GetManifestResourceStream("ui/caster.html")!;
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public void Every_desk_item_Happening_now_showed_is_still_in_the_desk_state()
    {
        _desk.HealthConfig.AutoSwitch = false;
        Snap("A"); Snap("B");
        // A drops: LOBBY DOWN. It comes back: "A is back".
        for (int i = 0; i < 11; i++) { _clock.Advance(1); Snap("B"); _desk.Tick(); }
        Assert.Contains(Cards(), c => c.GetProperty("rule").GetString() == "lobbyDown" && c.GetProperty("system").GetBoolean());
        Snap("A");
        _desk.Tick();
        Assert.Contains(Cards(), c => c.GetProperty("rule").GetString() == "lobbyBack" && c.GetProperty("system").GetBoolean());
        // B's referee's game restarts mid-game: INTERRUPTED, with its decision; Replay leaves a REPLAY card.
        Send("B", "event", "gameStart", new { map = "Polus" });
        Snap("B");
        Snap("B", "lobby", src: "run2");
        var it = Cards().Single(c => c.GetProperty("rule").GetString() == "interrupted");
        Assert.True(it.GetProperty("system").GetBoolean());
        Assert.False(string.IsNullOrEmpty(it.GetProperty("interruption").GetString()));
        _desk.Decide(it.GetProperty("interruption").GetString()!, "replay");
        Assert.Contains(Cards(), c => c.GetProperty("rule").GetString() == "replayGame" && c.GetProperty("system").GetBoolean());
        // Chat's pick, with its into-Preview button.
        _desk.AddNotice("A", "twitchLobby", "high", "Chat picked A.", 60, 60, "show:A");
        Assert.Contains(Cards(), c => c.GetProperty("rule").GetString() == "twitchLobby" && c.GetProperty("action").GetString() == "show:A");
        // The after-game table prompts are in the desk's state as before (the action panel shows them).
        Assert.True(JsonSerializer.SerializeToElement(_desk.PromptState(), TestJson.Camel).TryGetProperty("prompts", out _));
    }

    [Fact]
    public void The_Suggestions_column_draws_the_desk_items_first_then_the_team_and_plays_are_in_the_drawer()
    {
        string page = Page();
        Assert.Contains("<h2>Suggestions</h2>", page);
        Assert.DoesNotContain("<h2>Happening now</h2>", page);
        int items = page.IndexOf("id=\"desk-cards\"", StringComparison.Ordinal), team = page.IndexOf("id=\"sugg-list\"", StringComparison.Ordinal), earlier = page.IndexOf("id=\"sugg-earlier\"", StringComparison.Ordinal);
        Assert.True(items > 0 && items < team && team < earlier);
        // The desk's own items keep their buttons: decide, watch the game, into Preview, dismiss.
        Assert.Contains("const items = d.cards.filter(c => c.system);", page);
        foreach (var button in new[] { "data-decide=\"count\"", "data-decide=\"void\"", "data-decide=\"replay\"", "data-grwatch=", "data-cardshow=", "data-dismiss=" })
            Assert.Contains(button, page);
        // The team's: Into Preview, Cut, Dismiss.
        foreach (var act in new[] { "data-sgdo=\"preview\"", "data-sgdo=\"cut\"", "data-sgdo=\"dismiss\"" }) Assert.Contains(act, page);
        Assert.Contains("api('POST', '/app/admin/suggestion'", page);
        // Plays now and earlier in the drawer: replay, into Preview, the player's card, Watch again.
        Assert.Contains("<summary><b>Plays</b>", page);
        string drawer = page.Substring(page.IndexOf("function renderHistory()", StringComparison.Ordinal));
        drawer = drawer.Substring(0, drawer.IndexOf("function renderSuggestions()", StringComparison.Ordinal));
        foreach (var bit in new[] { "replayButton(c)", "data-cardshow", "data-pcard", "data-watch" }) Assert.Contains(bit, drawer);
        // The after-game prompts are still in the action panel; the count on the menu is new suggestions.
        Assert.Contains("id=\"ag-prompts\"", page);
        Assert.Contains("new suggestion${fresh.length === 1 ? '' : 's'} from your team", page);
        // Settings → Team watch, with the firewall said.
        Assert.Contains("id=\"team-card\"", page);
        Assert.Contains("Windows Firewall asks", page);
    }

    [Fact]
    public void The_Watch_page_and_the_desk_page_scripts_parse()
    {
        foreach (var name in new[] { "ui/watch.html", "ui/caster.html" })
        {
            using var s = typeof(BroadcastServer).Assembly.GetManifestResourceStream(name)!;
            string page = new StreamReader(s).ReadToEnd();
            var scripts = System.Text.RegularExpressions.Regex.Matches(page, "<script>(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.NotEmpty(scripts);
            foreach (System.Text.RegularExpressions.Match m in scripts)
            {
                string file = Path.Combine(Path.GetTempPath(), $"page-{Guid.NewGuid():N}.js");
                File.WriteAllText(file, m.Groups[1].Value);
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("node", $"--check \"{file}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    System.Diagnostics.Process p;
                    try { p = System.Diagnostics.Process.Start(psi)!; }
                    catch (System.ComponentModel.Win32Exception) { return; }          // no node on this machine
                    using (p)
                    {
                        string err = p.StandardError.ReadToEnd();
                        Assert.True(p.WaitForExit(30000), "node timed out");
                        Assert.True(p.ExitCode == 0, name + ": " + err);
                    }
                }
                finally { File.Delete(file); }
            }
        }
    }
}
