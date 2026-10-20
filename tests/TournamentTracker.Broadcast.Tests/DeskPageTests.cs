using System.Text;
using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// The desk page carries every lobby's data link: when it crashes, hangs or is loaded again, that isn't every
/// lobby dropping. Auto switch, the LOBBY DOWN cards and interrupted games wait until it's back and its links
/// have had a short grace to connect; a lobby still down after that is dealt with as usual.
/// </summary>
public class DeskPageTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private sealed class Page : IDisposable
    {
        public readonly FakeClock Clock = new();
        public readonly CasterDesk Desk;
        private readonly Dictionary<string, long> _seq = new();
        public Page() { Desk = new CasterDesk(null, () => Clock.Now, new PriorityConfig()); Desk.Switch = _ => { }; }
        public void Dispose() => Desk.Dispose();

        public void Snap(string lobby)
        {
            long seq = _seq[lobby] = _seq.GetValueOrDefault(lobby) + 1;
            Desk.Apply(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["v"] = TournamentTracker.Broadcast.FeedProtocol.Version, ["type"] = "snap", ["lobby"] = lobby, ["round"] = 1, ["game"] = lobby + "-1",
                ["t"] = new DateTimeOffset(Clock.Now).ToUnixTimeMilliseconds(), ["src"] = "run1", ["seq"] = seq,
                ["phase"] = "ingame", ["crewAlive"] = 7, ["impAlive"] = 2, ["alive"] = 9, ["taskPct"] = 40,
            }));
        }

        /// <summary>Seconds pass: the page polls (when it's up), the lobbies it carries send, the desk ticks.</summary>
        public void Pass(int seconds, bool pageUp, params string[] sending)
        {
            for (int i = 0; i < seconds; i++)
            {
                Clock.Advance(1);
                if (pageUp) Desk.PageSeen();
                foreach (var l in sending) Snap(l);
                Desk.Tick();
            }
        }

        public int Down() => JsonSerializer.SerializeToElement(Desk.State(), TestJson.Camel).GetProperty("cards").EnumerateArray().Count(c => c.GetProperty("rule").GetString() == "lobbyDown");
    }

    [Fact]
    public void A_page_crash_and_reload_isnt_every_lobby_dropping()
    {
        using var p = new Page();
        p.Pass(5, true, "LJ", "MAL");
        p.Desk.Show("LJ");
        p.Pass(3, true, "LJ", "MAL");

        // The renderer crashes: the window says so, and the page (with every data link) is gone for 15 s.
        p.Desk.PageFailed();
        p.Pass(15, false);
        Assert.True(p.Desk.IsDown("LJ"));                                 // no data, as far as health goes…
        Assert.True(p.Desk.PageWait);
        // …but nothing switched, no LOBBY DOWN cards, no games cut short.
        Assert.Equal(("full", "LJ"), (p.Desk.OnAir.Layout, p.Desk.OnAir.Slots[0]));
        Assert.Null(p.Desk.AutoSwitched);
        Assert.Equal(0, p.Down());

        // Loaded again: its links take a few seconds to connect.
        p.Desk.PageLoaded();
        p.Pass(6, true);
        Assert.Equal(("full", "LJ"), (p.Desk.OnAir.Layout, p.Desk.OnAir.Slots[0]));
        p.Pass(30, true, "LJ", "MAL");
        Assert.False(p.Desk.PageWait);
        Assert.Equal(("full", "LJ"), (p.Desk.OnAir.Layout, p.Desk.OnAir.Slots[0]));
        Assert.Null(p.Desk.AutoSwitched);
        Assert.Equal(0, p.Down());
        Assert.Empty(p.Desk.Interruptions);
    }

    [Fact]
    public void A_page_that_hangs_without_a_word_from_the_window_counts_as_gone_after_6_seconds()
    {
        using var p = new Page();
        p.Pass(5, true, "LJ", "MAL");
        p.Desk.Show("LJ");
        p.Pass(5, true, "LJ", "MAL");
        p.Pass(40, false);                                                // polls and data stop together
        Assert.Null(p.Desk.AutoSwitched);
        Assert.Equal(("full", "LJ"), (p.Desk.OnAir.Layout, p.Desk.OnAir.Slots[0]));
        Assert.Equal(0, p.Down());
        Assert.NotNull(p.Desk.PageSeen());                                // back: said, and the grace starts
        Assert.True(p.Desk.PageWait);
    }

    [Fact]
    public void A_lobby_still_down_once_the_grace_is_over_is_dealt_with_as_usual()
    {
        using var p = new Page();
        p.Pass(5, true, "LJ", "MAL");
        p.Desk.Show("LJ");
        p.Desk.PageFailed();
        p.Pass(12, false);
        p.Desk.PageLoaded();
        // MAL's link connects again; LJ's host really did drop.
        p.Pass((int)CasterDesk.PageGraceSeconds - 2, true, "MAL");
        Assert.Null(p.Desk.AutoSwitched);
        p.Pass(4, true, "MAL");
        Assert.False(p.Desk.PageWait);
        Assert.Equal("LJ", p.Desk.AutoSwitched!.Lobby);                  // once the grace is over
        Assert.Equal("health", p.Desk.OnAir.By);
        Assert.Equal(1, p.Down());
    }

    // ---- Through Red Alert's server: loading the page, and the window's word ------------------------

    private static SetupCode Admin() => new() { Mode = "admin", TournamentId = "c", TournamentName = "Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results" };

    private BroadcastServer Server() =>
        new(new BroadcastEnvironment { SettingsFile = Path.Combine(_dir.Path, "RedAlert", "settings.json"), Port = 0, CasterPort = 0, Version = "0.3.18" },
            new HttpClient(new FakeHttp()) { Timeout = TimeSpan.FromSeconds(20) });

    /// <summary>A request as the desk page sends it (straight to the server's router: no sockets).</summary>
    private static async Task<JsonElement> Call(BroadcastServer server, string method, string path, object? body = null)
    {
        var headers = new Dictionary<string, string> { ["x-app-token"] = server.Token };
        var (_, _, bytes) = await server.Route(method, path, headers, body == null ? "" : JsonSerializer.Serialize(body)).WaitAsync(TimeSpan.FromSeconds(20));
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    [Fact]
    public async Task Loading_the_desk_page_and_the_window_saying_it_crashed_start_the_wait()
    {
        using (var server = Server())
        {
            await Call(server, "POST", "/app/admin/code", new { code = Admin().Encode() });
            var desk = server.Desk!;
            Assert.False(desk.PageWait);
            var (status, _, page) = await server.Route("GET", "/", new Dictionary<string, string>(), "").WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(200, status);
            Assert.Contains("Red Alert", Encoding.UTF8.GetString(page));
            Assert.True(desk.PageWait);                                   // its data links are starting
        }

        using var again = Server();
        Assert.NotNull(again.Desk);                                       // unlocked with the saved code
        await Call(again, "GET", "/app/state");                           // the page asks for something
        Assert.False(again.Desk!.PageWait);
        again.PageFailed();                                               // the window: its renderer crashed
        Assert.True(again.Desk.PageWait);
        await Call(again, "GET", "/app/state");                           // loaded again, it asks: back
        Assert.True(again.Desk.PageWait);                                 // and the grace runs
        Assert.Null(again.Desk.PageSeen());                               // (said once)
    }
}
