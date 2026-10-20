using System.Text.Json;
using System.Text.RegularExpressions;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// The tally each host's Button is told about its own lobby: on stream (and in which view), in Preview, or
/// neither; built from Program and Preview as they change, nothing about other lobbies or the players, and
/// sent by the desk page over each lobby's own link only.
/// </summary>
public class HostTallyTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly CasterDesk _desk;

    public HostTallyTests()
    {
        _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        foreach (var l in new[] { "LJ", "MAL", "KAI", "BO" })
            _desk.Apply(JsonSerializer.Serialize(new
            {
                type = "snap", lobby = l, round = 1, phase = "ingame", crewAlive = 6, impAlive = 2, taskPct = 20, t = 0,
                players = new object[] { new { id = 1, name = "Ann", color = 0, key = "ann#1", imp = true }, new { id = 2, name = "Bob", color = 1, key = "bob#1" } },
            }));
    }

    public void Dispose() => _desk.Dispose();

    private (string State, string? View) Of(string lobby) { var m = HostTally.For(lobby, _desk.OnAir, _desk.Preview); return (m.State, m.View); }

    /// <summary>Each lobby's tally as the desk page reads it (desk.lobbies[].hostTally).</summary>
    private Dictionary<string, JsonElement> FromDesk()
    {
        var desk = JsonSerializer.SerializeToElement(_desk.State(), TestJson.Camel);
        return desk.GetProperty("lobbies").EnumerateArray().ToDictionary(l => l.GetProperty("lobby").GetString()!, l => l.GetProperty("hostTally"));
    }

    [Fact]
    public void It_follows_Program_and_Preview_and_says_which_view()
    {
        Assert.Equal(("none", null), Of("LJ"));
        _desk.PreviewOnly("LJ");
        Assert.Equal(("pvw", null), Of("LJ"));
        Assert.Equal(("none", null), Of("MAL"));
        _desk.Take();
        Assert.Equal(("pgm", "full"), Of("LJ"));
        _desk.PreviewAdd("MAL");
        Assert.Equal(("pvw", null), Of("MAL"));
        _desk.ShowPicked(new[] { "LJ", "MAL", "KAI" }, "deck", "4up");
        Assert.Equal(("pgm", "4up"), Of("KAI"));
        Assert.Equal(("none", null), Of("BO"));
        _desk.ShowPicked(new[] { "LJ", "MAL" });
        Assert.Equal(("pgm", "2up"), Of("MAL"));
        _desk.Show("", "grid");
        Assert.Equal(("pgm", "grid"), Of("BO"));
        // Breaks with no lobby, and a replay (not live): nothing.
        foreach (var brk in new[] { "slate", "intermission", "soon" })
        {
            _desk.Show("", brk);
            Assert.Equal(("none", null), Of("LJ"));
        }
        _desk.ReplayOn("LJ");
        Assert.Equal(("none", null), Of("LJ"));
    }

    [Fact]
    public void The_desk_gives_each_lobby_only_its_own_state_and_view()
    {
        _desk.Show("LJ");
        _desk.PreviewOnly("MAL");
        var all = FromDesk();
        Assert.Equal("pgm", all["LJ"].GetProperty("state").GetString());
        Assert.Equal("full", all["LJ"].GetProperty("view").GetString());
        Assert.Equal("pvw", all["MAL"].GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, all["MAL"].GetProperty("view").ValueKind);
        Assert.Equal("none", all["KAI"].GetProperty("state").GetString());
        // Only the version, the state and the view: no lobby names, players or impostors.
        foreach (var t in all.Values)
            Assert.Equal(new[] { "v", "state", "view" }, t.EnumerateObject().Select(p => p.Name));
        Assert.Equal(HostTally.Version, all["LJ"].GetProperty("v").GetInt32());

        // A change shows at once in the next answer.
        _desk.Take();
        all = FromDesk();
        Assert.Equal("pgm", all["MAL"].GetProperty("state").GetString());
        Assert.Equal("none", all["LJ"].GetProperty("state").GetString());
    }

    [Fact]
    public void The_desk_page_sends_each_lobby_its_own_tally_when_it_changes_and_every_few_seconds()
    {
        using var s = typeof(BroadcastServer).Assembly.GetManifestResourceStream("ui/caster.html")!;
        string page = new StreamReader(s).ReadToEnd();
        var m = Regex.Match(page, "function sendTallies\\(\\) \\{.*?\\n\\}\\n", RegexOptions.Singleline);
        Assert.True(m.Success);
        string fn = m.Value;
        // Over each lobby's own data link, only its own entry; refreshed every 3 s; nothing while Red Alert isn't answering.
        Assert.Contains("for (const [lobby, f] of Object.entries(RECV))", fn);
        Assert.Contains("(DESK.lobbies || []).find(x => x.lobby === lobby)", fn);
        Assert.Contains("{ v: l.hostTally.v, state: l.hostTally.state, view: l.hostTally.view || null }", fn);
        Assert.Contains("now - was.at < 3000", fn);
        Assert.Contains("performance.now() - Math.max(liveAt, fullAt) > 6000) return;", fn);
        Assert.Contains("f.contentWindow.postMessage({ sendData: { ttt: t }, type: 'rpcs' }, '*');", fn);
        Assert.Contains("setInterval(sendTallies, 500);", page);
    }
}
