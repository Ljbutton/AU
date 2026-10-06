using System.Net;
using System.Text;
using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>TT Broadcast's own server: its screen, the administration code, its own updates.</summary>
public class BroadcastServerTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private BroadcastServer Server() => new(new BroadcastEnvironment { SettingsFile = Path.Combine(_dir.Path, "TTBroadcast", "settings.json"), Port = 0, CasterPort = 0, Version = "0.1.0" },
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
        Assert.Contains("TT Broadcast", page);
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
    public void It_updates_from_its_own_releases_not_The_Buttons()
    {
        var releases = JsonDocument.Parse("""
            [ { "tag_name": "v0.1.30", "draft": false, "assets": [ { "name": "TheButton.exe", "browser_download_url": "https://x/TheButton.exe" } ] },
              { "tag_name": "broadcast-v0.1.3", "draft": true, "assets": [ { "name": "TTBroadcast.exe", "browser_download_url": "https://x/draft" } ] },
              { "tag_name": "broadcast-v0.1.2", "draft": false, "assets": [ { "name": "TTBroadcast.exe", "browser_download_url": "https://x/012" } ] },
              { "tag_name": "broadcast-v0.1.1", "draft": false, "assets": [ { "name": "TTBroadcast.exe", "browser_download_url": "https://x/011" } ] } ]
            """).RootElement;
        Assert.Equal(("broadcast-v0.1.2", "https://x/012"), BroadcastServer.PickRelease(releases));
        Assert.True(BroadcastServer.Newer("broadcast-v0.1.2", "0.1.0"));
        Assert.False(BroadcastServer.Newer("broadcast-v0.1.0", "0.1.0"));
        Assert.True(BroadcastServer.Newer("broadcast-v1.0.0", "0.9.9"));
    }
}
