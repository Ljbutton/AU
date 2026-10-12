using System.Net;
using System.Text;
using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.App.Voice;
using TournamentTracker.Control;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// The Button's send page and what's behind it: one live tab at a time, the lobby sound and what it costs
/// while nobody listens, and the mod's connection read once instead of on every call.
/// </summary>
public class ButtonSendPageTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    public void Dispose() => _dir.Dispose();

    private AppEnvironment Env(string? steam = null) => new()
    {
        SettingsFile = Path.Combine(_dir.Path, "app", "app.json"), SteamRoot = steam ?? Path.Combine(_dir.Path, "none"),
        EpicManifests = Path.Combine(_dir.Path, "none"), Fallbacks = Array.Empty<string>(), Downloads = Path.Combine(_dir.Path, "Downloads"),
    };

    private static HttpClient Client(AppServer app)
    {
        var http = new HttpClient { BaseAddress = new Uri(app.Url) };
        http.DefaultRequestHeaders.Add("X-App-Token", app.Token);
        return http;
    }

    [Fact]
    public async Task Only_the_newest_send_page_is_served_and_switching_off_retires_it()
    {
        using (var app = new AppServer(Env(), new HttpClient(new FakeHttp())))
        {
            using var http = Client(app);
            // The first tab to ask (after a restart nothing has been opened yet) is taken as the live one.
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("app/sendfeed?since=0&page=aaa")).StatusCode);
            // Another tab (an older one, still open) is told to stop, for everything it asks.
            foreach (var path in new[] { "app/sendfeed?since=0&page=bbb", "app/sendinfo?page=bbb", "app/voice/pcm?page=bbb", "app/cam?after=0&page=bbb", "app/voice?page=bbb" })
                Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync("app/command?page=bbb", new StringContent("{\"command\":\"spec map\"}"))).StatusCode);
            // The live tab and a page from before page ids carry on; so do The Button's own screens.
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("app/sendinfo?page=aaa")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("app/sendfeed?since=0")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("app/command", new StringContent("{\"command\":\"r2\"}"))).StatusCode);

            // Sending switched off: the open tab is retired too, so it stops sharing the screen and the lobby voice.
            await http.PostAsync("app/feed", new StringContent("{\"on\":\"false\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync("app/sendfeed?since=0&page=aaa")).StatusCode);
        }
        // The Button restarted (an update): same token and port, and the tab that's still open keeps working.
        using var again = new AppServer(Env(), new HttpClient(new FakeHttp()));
        using var http2 = Client(again);
        Assert.Equal(HttpStatusCode.OK, (await http2.GetAsync("app/sendfeed?since=0&page=aaa")).StatusCode);
    }

    [Fact]
    public void The_send_page_knows_its_id_hands_sound_to_a_worker_and_uses_a_fixed_sdk()
    {
        string html = SendPage.Html;
        // A pinned VDO.Ninja SDK, never whatever is newest.
        Assert.Contains("https://unpkg.com/@vdoninja/sdk@1.6.2/vdoninja-sdk.min.js", html);
        Assert.DoesNotContain("@vdoninja/sdk/", html);
        // Every ask carries the tab's id; a 409 retires the tab.
        Assert.Contains("'&page='+encodeURIComponent(page)", html);
        Assert.Contains("r.status===409", html);
        Assert.Contains("id=\"retired\"", html);
        // The lobby sound is pulled by a worker that hands it straight to the audio worklet (a background tab's
        // own timers run once a second), and camera pictures are handed over, not copied.
        Assert.Contains("const PULL_WORKER=", html);
        Assert.Contains("node.port.postMessage({port:ch.port1},[ch.port1])", html);
        Assert.Contains("port.postMessage(a,[a.buffer])", html);
        Assert.Contains("postMessage(bmp,[bmp])", html);
        Assert.DoesNotContain("async function pull(", html);
        // The first feed ask doesn't download the mod's whole buffer.
        Assert.Contains("since??Number.MAX_SAFE_INTEGER", html);
    }

    [Fact]
    public void Samples_are_kept_newest_first_out_oldest_first_in_whole_runs()
    {
        var ring = new SampleRing(8);
        ring.Write(new short[] { 1, 2, 3, 4, 5 }, 5);
        var out1 = new short[3];
        Assert.Equal(3, ring.Read(out1, 3));
        Assert.Equal(new short[] { 1, 2, 3 }, out1);
        // Round the end of the buffer, and past what it holds: the oldest go.
        ring.Write(new short[] { 6, 7, 8, 9, 10, 11, 12 }, 7);
        Assert.Equal(8, ring.Count);
        var out2 = new short[10];
        Assert.Equal(8, ring.Read(out2, 10));
        Assert.Equal(new short[] { 5, 6, 7, 8, 9, 10, 11, 12 }, out2.Take(8).ToArray());
        Assert.Equal(0, ring.Count);
        // More than it holds in one go: only the newest.
        ring.Write(Enumerable.Range(1, 20).Select(i => (short)i).ToArray(), 20);
        var out3 = new short[8];
        ring.Read(out3, 8);
        Assert.Equal(Enumerable.Range(13, 8).Select(i => (short)i).ToArray(), out3);
        // Only the first count samples of the buffer are taken.
        ring.Write(new short[] { 7, 8, 9 }, 2);
        Assert.Equal(2, ring.Count);
    }

    [Fact]
    public void Capturing_stops_when_the_send_page_stops_reading_and_starts_again_when_asked()
    {
        using var mix = new VoiceCapture(() => _clock.Now);
        mix.Start();
        Assert.True(mix.Running);
        _clock.Advance(20);
        mix.Read();                                             // the page is reading
        _clock.Advance(20);
        Assert.False(mix.StopIfIdle(TimeSpan.FromSeconds(30)));
        Assert.True(mix.Running);
        _clock.Advance(15);                                     // 35 s without a read: the page was closed
        Assert.True(mix.StopIfIdle(TimeSpan.FromSeconds(30)));
        Assert.False(mix.Running);
        Assert.False(mix.StopIfIdle(TimeSpan.FromSeconds(30)));
        mix.Start();                                            // the page asked again
        Assert.True(mix.Running);
        Assert.False(mix.StopIfIdle(TimeSpan.FromSeconds(30)));
        // Open but not reading the sound yet (no push link so far): it still asks every second, and capturing goes on.
        for (int i = 0; i < 60; i++) { _clock.Advance(1); mix.Touch(); }
        Assert.False(mix.StopIfIdle(TimeSpan.FromSeconds(30)));
        Assert.True(mix.Running);
    }

    [Fact]
    public async Task The_mods_connection_is_read_again_when_Among_Us_restarts_on_another_port()
    {
        string game = Path.Combine(_dir.Path, "AU");
        string data = ModInstaller.DataDir(game);
        var mod = new ModClient(new HttpClient());
        using (var first = new ControlServer(0, data, () => "{\"run\":1}", _ => Task.FromResult("{}"), NullLog.Instance))
        {
            Assert.Equal("{\"run\":1}", await mod.StatusAsync(game));
            Assert.Equal("{\"run\":1}", await mod.StatusAsync(game));
        }
        using var second = new ControlServer(0, data, () => "{\"run\":2}", _ => Task.FromResult("{}"), NullLog.Instance);
        string? seen = null;
        // At most a second or so (the file's time is looked at once a second; a failed call reads it straight away).
        for (int i = 0; i < 15 && seen != "{\"run\":2}"; i++) { seen = await mod.StatusAsync(game); if (seen != "{\"run\":2}") await Task.Delay(100); }
        Assert.Equal("{\"run\":2}", seen);
        // Among Us closed: nothing to read, no answer.
        File.Delete(Path.Combine(data, ControlServer.FileName));
        await Task.Delay(1100);
        Assert.Null(await mod.StatusAsync(game));
    }

    [Fact]
    public async Task Found_game_folders_are_only_looked_for_while_Among_Us_isnt_found()
    {
        string steam = Path.Combine(_dir.Path, "Steam");
        string game = Path.Combine(steam, "steamapps", "common", "Among Us");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Among Us.exe"), "");
        using var app = new AppServer(Env(steam), new HttpClient(new FakeHttp()));
        using var http = Client(app);
        var g = JsonDocument.Parse(await http.GetStringAsync("app/state")).RootElement.GetProperty("game");
        Assert.True(g.GetProperty("found").GetBoolean());
        Assert.Equal(0, g.GetProperty("candidates").GetArrayLength());
        Assert.False(ModInstaller.GameRunningLately());          // no Among Us in tests
    }

    [Fact]
    public async Task The_app_page_has_host_authority_and_one_void_reason_box()
    {
        using var app = new AppServer(Env(), new HttpClient(new FakeHttp()));
        using var anon = new HttpClient { BaseAddress = new Uri(app.Url) };
        string page = await anon.GetStringAsync("/");
        Assert.Contains("id=\"t-hostauth\"", page);
        Assert.Contains("onOff($('t-hostauth'), !!ha.on, 'hostauth')", page);
        Assert.Contains("Restart hosting from the main menu so host authority takes effect", page);
        Assert.Contains("With host authority on, players without the mod see the referee as a dead (crossed-out) card in meetings and never on the map; only modded games hide the card completely.", page);
        // One reason box for every Void on the Referee tab (the results channel's own, for other lobbies, aside).
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "id=\"ref-why\""));
        Assert.DoesNotContain("id=\"void-reason\"", page);
    }
}
