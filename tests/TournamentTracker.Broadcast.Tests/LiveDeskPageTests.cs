using System.Text.Json;
using System.Text.RegularExpressions;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// The pages Red Alert serves, read as text: every element their scripts look up by id is on the page
/// (and no id is there twice), and the Live desk's quick answer (/app/admin/live) has everything the
/// one-screen desk draws from it: Preview and Program with each lobby's tally, the lobby tiles' phase,
/// counts, sabotage, health and camera, the hold, the ready replays and the Stream Deck's port.
/// </summary>
public class LiveDeskPageTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private BroadcastServer _server = null!;
    private HttpClient _app = null!;

    public async Task InitializeAsync()
    {
        _server = new BroadcastServer(new BroadcastEnvironment { SettingsFile = Path.Combine(_dir.Path, "RedAlert", "settings.json"), Port = 0, CasterPort = 0, Version = "0.1.0" }, new HttpClient(new FakeHttp()));
        _app = AppHttp.Client(_server.Url, _server.Token);
        var admin = new SetupCode { Mode = "admin", TournamentId = "c", TournamentName = "Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results" };
        Assert.True((await Post("app/admin/code", new { code = admin.Encode() })).GetProperty("ok").GetBoolean());
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        _dir.Dispose();
        return Task.CompletedTask;
    }

    private Task<JsonElement> Get(string path) => AppHttp.Get(_app, path);
    private Task<JsonElement> Post(string path, object body) => AppHttp.Post(_app, path, body);

    private static string Embedded(string name)
    {
        using var s = typeof(BroadcastServer).Assembly.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Ids the page's scripts look up that it doesn't have, and ids its markup has twice.</summary>
    private static (List<string> Missing, List<string> Twice) Ids(string html)
    {
        string markup = Regex.Replace(html, "<script>.*?</script>", "", RegexOptions.Singleline);
        var inMarkup = Regex.Matches(markup, "\\sid=\"([^\"$]+)\"").Select(m => m.Groups[1].Value).ToList();
        var all = Regex.Matches(html, "\\sid=\"([^\"$]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        var used = Regex.Matches(html, "\\$\\('([A-Za-z0-9_-]+)'\\)|getElementById\\('([A-Za-z0-9_-]+)'\\)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToHashSet();
        return (used.Where(id => !all.Contains(id)).OrderBy(x => x).ToList(), inMarkup.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList());
    }

    [Fact]
    public async Task The_desk_page_has_every_element_its_script_uses()
    {
        string page = await _app.GetStringAsync("/");
        Assert.Contains(_server.Token, page);
        var (missing, twice) = Ids(page);
        Assert.Empty(missing);
        Assert.Empty(twice);
        // The one-screen Live desk: Preview, Take and Program, one tile per lobby, warnings in one rail.
        foreach (var id in new[] { "pvw-sch", "stage-send", "pgm-sch", "pgm-img", "mv-grid", "ld-alerts", "desk-warn", "cams-list", "rp-list", "desk-panic", "desk-hold", "deck-build" })
            Assert.Contains($"id=\"{id}\"", page);
        // It polls the quick answer, and gets OBS's picture only through the program picture route.
        Assert.Contains("/app/admin/live", page);
        Assert.Contains("/app/admin/program.jpg", page);
    }

    [Fact]
    public void The_overlay_pages_have_every_element_their_scripts_use()
    {
        foreach (var name in new[] { "ui/broadcast.html" })
        {
            var (missing, twice) = Ids(Embedded(name));
            Assert.Empty(missing);
            Assert.Empty(twice);
        }
        // The REPLAY tag only rewrites itself when the replay changes, one request at a time.
        Assert.Contains("shownKey", TournamentTracker.App.CasterPages.ReplayTag);
        Assert.Contains("if(busy)return;", TournamentTracker.App.CasterPages.ReplayTag);
    }

    [Fact]
    public async Task The_live_answer_has_what_the_desk_draws_and_tallies_follow_Preview_and_Program()
    {
        Assert.True((await Post("app/admin/sim", new { on = "true" })).GetProperty("ok").GetBoolean());
        JsonElement live = default;
        for (int i = 0; i < 100; i++)
        {
            live = await Get("app/admin/live");
            if (live.GetProperty("desk").GetProperty("lobbies").GetArrayLength() >= 2) break;
            await Task.Delay(50);
        }
        var desk = live.GetProperty("desk");
        foreach (var key in new[] { "onAir", "preview", "hold", "lobbies", "cards", "history", "health", "intermission", "autoGrid", "simulating" })
            Assert.True(desk.TryGetProperty(key, out _), "desk." + key);
        Assert.Equal("none", desk.GetProperty("preview").GetProperty("layout").GetString());
        Assert.False(desk.GetProperty("hold").GetProperty("on").GetBoolean());
        var tile = desk.GetProperty("lobbies")[0];
        foreach (var key in new[] { "no", "lobby", "online", "phase", "crew", "imps", "taskPct", "tier", "score", "health", "tally", "cam", "sabotage", "sabotageLeft", "sabotageCritical", "players", "people", "spec" })
            Assert.True(tile.TryGetProperty(key, out _), "lobby." + key);
        foreach (var key in new[] { "on", "following", "pick", "armed" })
            Assert.True(tile.GetProperty("cam").TryGetProperty(key, out _), "cam." + key);
        foreach (var key in new[] { "now", "ready", "problem" })
            Assert.True(live.GetProperty("replay").TryGetProperty(key, out _), "replay." + key);
        Assert.True(live.GetProperty("voice").TryGetProperty("obs", out _));
        Assert.True(live.GetProperty("broadcast").TryGetProperty("queue", out _));
        Assert.True(live.GetProperty("broadcast").TryGetProperty("afterGame", out _));
        Assert.True(live.TryGetProperty("reads", out _));
        Assert.True(live.GetProperty("deck").TryGetProperty("port", out _));

        // A click on tile 1 is Preview (green tally); Take puts it on stream (red), as the desk page does it.
        int no = tile.GetProperty("no").GetInt32();
        string lobby = tile.GetProperty("lobby").GetString()!;
        var pvw = await Post("app/admin/do", new { path = $"pvw/lobby/{no}", query = new { } });
        Assert.True(pvw.GetProperty("ok").GetBoolean(), pvw.GetProperty("message").GetString());
        string Tally(JsonElement l) => l.GetProperty("desk").GetProperty("lobbies").EnumerateArray().First(x => x.GetProperty("lobby").GetString() == lobby).GetProperty("tally").GetString()!;
        live = await Get("app/admin/live");
        Assert.Equal("pvw", Tally(live));
        Assert.Equal(new[] { lobby }, live.GetProperty("desk").GetProperty("preview").GetProperty("slots").EnumerateArray().Select(x => x.GetString()));
        var take = await Post("app/admin/do", new { path = "take", query = new { t = "cut" } });
        Assert.True(take.GetProperty("ok").GetBoolean(), take.GetProperty("message").GetString());
        live = await Get("app/admin/live");
        Assert.Equal("pgm", Tally(live));
        Assert.Equal("none", live.GetProperty("desk").GetProperty("preview").GetProperty("layout").GetString());
        // H on the desk: the hold shows in the answer straight away (an action makes the next answer new).
        Assert.True((await Post("app/admin/do", new { path = "hold/toggle", query = new { } })).GetProperty("ok").GetBoolean());
        Assert.True((await Get("app/admin/live")).GetProperty("desk").GetProperty("hold").GetProperty("on").GetBoolean());
    }
}
