using System.Net;
using System.Text;
using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Red Alert's own server: its screen, the administration code, its own updates.</summary>
public class BroadcastServerTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private BroadcastServer Server() => new(new BroadcastEnvironment { SettingsFile = Path.Combine(_dir.Path, "RedAlert", "settings.json"), Port = 0, CasterPort = 0, Version = "0.1.0" },
        new HttpClient(new FakeHttp()));

    private static async Task<JsonElement> Call(HttpClient http, HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = body == null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        var r = await http.SendAsync(req);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Its_screen_needs_its_token_and_the_desk_opens_with_the_administration_code()
    {
        using var server = Server();
        using var anon = new HttpClient { BaseAddress = new Uri(server.Url) };
        string page = await anon.GetStringAsync("/");
        Assert.Contains("Red Alert", page);
        Assert.Contains(server.Token, page);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("app/state")).StatusCode);
        var head = await anon.GetByteArrayAsync("/crew/7.png");                       // players' colours
        Assert.Equal((byte)0x89, head[0]);

        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-App-Token", server.Token);
        Assert.Equal(JsonValueKind.Null, (await Call(http, HttpMethod.Get, "app/state")).GetProperty("admin").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("app/admin/desk")).StatusCode);
        Assert.False((await Call(http, HttpMethod.Post, "app/admin/show", new { lobby = "A" })).GetProperty("ok").GetBoolean());

        var host = new SetupCode { Mode = "tournament", TournamentId = "c", TournamentName = "Cup", Webhook = "https://discord.test/api/webhooks/1/a" };
        Assert.False((await Call(http, HttpMethod.Post, "app/admin/code", new { code = host.Encode() })).GetProperty("ok").GetBoolean());
        var admin = new SetupCode { Mode = "admin", TournamentId = "c", TournamentName = "Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results" };
        Assert.True((await Call(http, HttpMethod.Post, "app/admin/code", new { code = admin.Encode() })).GetProperty("ok").GetBoolean());
        Assert.Equal("Cup", (await Call(http, HttpMethod.Get, "app/state")).GetProperty("admin").GetProperty("tournament").GetString());
        var desk = await Call(http, HttpMethod.Get, "app/admin/desk");
        Assert.Equal("none", desk.GetProperty("desk").GetProperty("onAir").GetProperty("layout").GetString());
        Assert.NotNull(server.CasterUrl);
        Assert.True((await Call(http, HttpMethod.Post, "app/admin/sim", new { on = "true" })).GetProperty("ok").GetBoolean());
        Assert.True(server.Desk!.Simulating);

        // Remembered for next time.
        using var again = Server();
        using var http2 = new HttpClient { BaseAddress = new Uri(again.Url) };
        http2.DefaultRequestHeaders.Add("X-App-Token", again.Token);
        Assert.Equal("Cup", (await Call(http2, HttpMethod.Get, "app/state")).GetProperty("admin").GetProperty("tournament").GetString());
    }

    [Fact]
    public void It_picks_its_own_releases_not_The_Buttons()
    {
        var releases = JsonDocument.Parse("""
            [ { "tag_name": "v0.1.30", "draft": false, "assets": [ { "name": "TheButton.exe", "browser_download_url": "https://x/TheButton.exe" } ] },
              { "tag_name": "broadcast-v0.1.3", "draft": true, "assets": [ { "name": "RedAlert.exe", "browser_download_url": "https://x/draft" } ] },
              { "tag_name": "broadcast-v0.1.2", "draft": false, "assets": [ { "name": "RedAlert.exe", "browser_download_url": "https://x/012" } ] } ]
            """).RootElement;
        Assert.Equal(("broadcast-v0.1.2", "https://x/012"), BroadcastServer.PickRelease(releases));
        Assert.True(BroadcastServer.Newer("broadcast-v0.1.2", "0.1.0"));
        Assert.False(BroadcastServer.Newer("broadcast-v0.1.0", "0.1.0"));
    }

    [Fact]
    public async Task It_only_updates_when_asked_and_never_without_an_exe()
    {
        using var server = Server();
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-App-Token", server.Token);
        var update = (await Call(http, HttpMethod.Get, "app/state")).GetProperty("update");
        Assert.False(update.GetProperty("supported").GetBoolean());     // tests have no RedAlert.exe
        Assert.False((await Call(http, HttpMethod.Post, "app/update")).GetProperty("ok").GetBoolean());
        Assert.False((await Call(http, HttpMethod.Post, "app/restart")).GetProperty("ok").GetBoolean());
        var r = await http.PostAsync("app/autoupdate", new StringContent("{}"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Stream_Deck_links_need_their_key_and_switch_lobbies_and_the_player_camera()
    {
        using var server = Server();
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-App-Token", server.Token);
        var admin = new SetupCode { Mode = "admin", TournamentId = "c", TournamentName = "Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results" };
        Assert.True((await Call(http, HttpMethod.Post, "app/admin/code", new { code = admin.Encode() })).GetProperty("ok").GetBoolean());
        await Call(http, HttpMethod.Post, "app/admin/sim", new { on = "true" });
        var desk = server.Desk!;
        string? busy = null;
        for (int i = 0; i < 100 && busy == null; i++)
        {
            busy = new[] { "SIM-1", "SIM-2", "SIM-3", "SIM-4" }.FirstOrDefault(l => desk.Board.Lobby(l)?.People.Values.Any(p => p.Dead != true) == true);
            if (busy == null) await Task.Delay(100);
        }
        Assert.NotNull(busy);

        var links = await Call(http, HttpMethod.Get, "app/admin/deck");
        string lobby1 = links.GetProperty("links")[0].GetProperty("url").GetString()!;
        string key = lobby1.Substring(lobby1.IndexOf("key=") + 4);
        using var deck = new HttpClient { BaseAddress = new Uri(server.Url) };          // no app token, like a Stream Deck
        Assert.Equal(HttpStatusCode.Unauthorized, (await deck.GetAsync("deck/lobby/1?key=wrong")).StatusCode);

        int two = desk.NumberOf(busy!);
        Assert.True((await Call(deck, HttpMethod.Get, $"deck/lobby/{two}?key={key}")).GetProperty("ok").GetBoolean());
        Assert.Equal("full", desk.OnAir.Layout);
        Assert.Equal(busy, desk.OnAir.Slots[0]);
        await Call(deck, HttpMethod.Get, $"deck/lobby/next?key={key}");
        Assert.NotEqual(busy, desk.OnAir.Slots[0]);
        await Call(deck, HttpMethod.Get, $"deck/lobby/{two}?key={key}");

        // The camera on the lobby on stream: the host is told who to follow (over the desk page's link).
        Assert.True((await Call(deck, HttpMethod.Get, $"deck/cam?key={key}")).GetProperty("ok").GetBoolean());
        Assert.Equal("cam", desk.OnAir.Layout);
        Assert.Equal(busy, desk.OnAir.Slots[0]);
        var sent = await Call(http, HttpMethod.Get, "app/admin/specout");
        Assert.Equal("spec cam auto", sent[0].GetProperty("command").GetString());
        Assert.Equal(0, (await Call(http, HttpMethod.Get, "app/admin/specout")).GetArrayLength());     // handed out once
        var next = await Call(deck, HttpMethod.Get, $"deck/cam/next?key={key}");
        Assert.True(next.GetProperty("ok").GetBoolean(), next.GetProperty("message").GetString());
        Assert.Matches("^spec cam \\d+$", (await Call(http, HttpMethod.Get, "app/admin/specout"))[0].GetProperty("command").GetString()!);
        await Call(deck, HttpMethod.Get, $"deck/wide?key={key}");
        Assert.Equal("full", desk.OnAir.Layout);

        // The key stays the same after a restart (the Stream Deck keeps working), until a new one is asked for.
        server.Dispose();
        using var again = Server();
        using var http2 = new HttpClient { BaseAddress = new Uri(again.Url) };
        http2.DefaultRequestHeaders.Add("X-App-Token", again.Token);
        string after = (await Call(http2, HttpMethod.Get, "app/admin/deck")).GetProperty("links")[0].GetProperty("url").GetString()!;
        Assert.EndsWith("key=" + key, after);
        await Call(http2, HttpMethod.Post, "app/admin/deck");
        string fresh = (await Call(http2, HttpMethod.Get, "app/admin/deck")).GetProperty("links")[0].GetProperty("url").GetString()!;
        Assert.DoesNotContain(key, fresh);
    }
}
