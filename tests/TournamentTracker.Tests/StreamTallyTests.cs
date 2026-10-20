using System.Net;
using System.Text;
using System.Text.Json;
using TournamentTracker.App;
using Xunit;
using static TournamentTracker.Tests.ButtonApp;

namespace TournamentTracker.Tests;

/// <summary>
/// The caster's tally on The Button's Home: red ON STREAM (and how) while Red Alert has this lobby on Program,
/// NEXT while it's in Preview, nothing otherwise, and nothing once Red Alert has gone quiet for 10 s.
/// </summary>
public class StreamTallyTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static readonly DateTime T0 = new DateTime(2026, 10, 20, 18, 0, 0, DateTimeKind.Utc);

    private static JsonElement? View(StreamTally t, DateTime now) => t.View(now) is { } v ? JsonSerializer.SerializeToElement(v, TestJson.Camel) : null;

    [Fact]
    public void It_says_how_the_lobby_is_on_stream_or_that_it_is_next()
    {
        var t = new StreamTally();
        Assert.Null(t.View(T0));                                     // an older Red Alert sends nothing: nothing shown
        foreach (var (view, words) in new[] { ("full", "On stream (full screen)"), ("2up", "On stream (in the 2-up)"), ("4up", "On stream (in the quad)"), ("grid", "On stream (in the grid)"), ("cam", "On stream (player camera)"), ("cams", "On stream (player camera)"), ("break", "On stream (sponsor break)"), ("pip", "On stream") })
        {
            Assert.True(t.Set("pgm", view, T0));
            var v = View(t, T0)!.Value;
            Assert.Equal("pgm", v.GetProperty("state").GetString());
            Assert.Equal("ON STREAM", v.GetProperty("badge").GetString());
            Assert.Equal(words, v.GetProperty("text").GetString());
            Assert.Equal("You're on stream: stay in the game window, no menus.", v.GetProperty("hint").GetString());
        }
        Assert.True(t.Set("pvw", null, T0));
        var next = View(t, T0)!.Value;
        Assert.Equal(("pvw", "NEXT", "Next: the caster has your lobby in Preview."), (next.GetProperty("state").GetString(), next.GetProperty("badge").GetString(), next.GetProperty("text").GetString()));
        Assert.Equal(JsonValueKind.Null, next.GetProperty("hint").ValueKind);
        Assert.True(t.Set("none", null, T0));
        Assert.Null(t.View(T0));
    }

    [Fact]
    public void Anything_odd_is_refused_and_it_goes_10_s_after_the_last_word()
    {
        var t = new StreamTally();
        Assert.True(t.Set("pgm", "full", T0));
        Assert.False(t.Set("live", "full", T0.AddSeconds(1)));        // not a state: the last one stays
        Assert.Equal("On stream (full screen)", View(t, T0.AddSeconds(1))!.Value.GetProperty("text").GetString());
        Assert.True(t.Set("pgm", "<b>quad</b>", T0.AddSeconds(2)));    // a view that isn't one: plain "On stream"
        Assert.Equal("On stream", View(t, T0.AddSeconds(2))!.Value.GetProperty("text").GetString());
        // Refreshed every few seconds while the link is up; 10 s without (the link dropped): gone.
        Assert.NotNull(t.View(T0.AddSeconds(12)));
        Assert.Null(t.View(T0.AddSeconds(12.5)));
        Assert.True(t.Set("pvw", null, T0.AddSeconds(20)));
        Assert.NotNull(t.View(T0.AddSeconds(25)));
        t.Clear();
        Assert.Null(t.View(T0.AddSeconds(25)));
    }

    [Fact]
    public async Task The_send_page_hands_it_on_Home_shows_it_and_switching_sending_off_clears_it()
    {
        using var app = new AppServer(Env(_dir.Path), new HttpClient(new FakeHttp()));
        using var http = Client(app);
        http.Timeout = TimeSpan.FromSeconds(20);
        Assert.Equal(JsonValueKind.Null, (await AppHttp.Get(http, "app/state")).GetProperty("tally").ValueKind);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("app/sendfeed?since=0&page=aaa")).StatusCode);
        var ok = await AppHttp.Post(http, "app/tally?page=aaa", new { state = "pgm", view = "4up" });
        Assert.True(ok.GetProperty("ok").GetBoolean());
        var tally = (await AppHttp.Get(http, "app/state")).GetProperty("tally");
        Assert.Equal("On stream (in the quad)", tally.GetProperty("text").GetString());
        Assert.False((await AppHttp.Post(http, "app/tally?page=aaa", new { state = "maybe" })).GetProperty("ok").GetBoolean());
        // A send page that was replaced can't set it.
        var stale = await http.PostAsync("app/tally?page=bbb", new StringContent("{\"state\":\"pvw\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("pgm", (await AppHttp.Get(http, "app/state")).GetProperty("tally").GetProperty("state").GetString());

        await http.PostAsync("app/feed", new StringContent("{\"on\":\"false\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(JsonValueKind.Null, (await AppHttp.Get(http, "app/state")).GetProperty("tally").ValueKind);
    }

    [Fact]
    public void The_send_page_passes_on_only_a_well_formed_tally()
    {
        Assert.Contains("if(got&&got.ttt&&typeof got.ttt==='object'){const t=got.ttt;if(/^(pgm|pvw|none)$/.test(t.state)&&(t.view==null||/^[a-z0-9]{1,12}$/.test(t.view)))post('/app/tally',{state:t.state,view:t.view||''}).catch(()=>{});return;}", SendPage.Html);
        // Before the spectator commands: a tally is never taken for one.
        Assert.True(SendPage.Html.IndexOf("got.ttt") < SendPage.Html.IndexOf("post('/app/command',{command:cmd})"));
        using var s = typeof(AppServer).Assembly.GetManifestResourceStream("ui/index.html")!;
        string page = new StreamReader(s).ReadToEnd();
        Assert.Contains("<div id=\"tally\" class=\"tally\" role=\"status\" hidden></div>", page);
        Assert.Contains("renderTally();", page);
    }
}
