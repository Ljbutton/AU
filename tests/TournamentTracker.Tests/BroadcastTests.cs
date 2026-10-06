using System.Text.Json;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>The on-stream graphics app: where each lobby is on screen, what it shows, the theme and switches.</summary>
public class BroadcastTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly CasterDesk _desk;
    private readonly BroadcastApp _app;
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public BroadcastTests()
    {
        File.WriteAllText(Path.Combine(_dir.Path, Roster.FileName), Roster.Header + "\nJake Rivera,111,,,\n");
        _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), Path.Combine(_dir.Path, Roster.FileName));
        _app = new BroadcastApp(_desk, () => null, Path.Combine(_dir.Path, BroadcastSettings.FileName));
    }

    public void Dispose() { _desk.Dispose(); _dir.Dispose(); }

    private void Snap(string lobby, string phase = "ingame", object? sabotage = null, params object[] players) =>
        _desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby, round = 1, phase, crewAlive = 7, impAlive = 2, taskPct = 30, t = 0, sabotage, players }));

    private JsonElement State() => JsonSerializer.SerializeToElement(_app.State(), Camel);

    [Fact]
    public void Slots_follow_the_layout_on_stream()
    {
        foreach (var l in new[] { "A", "B", "C" }) Snap(l);
        Assert.Empty(State().GetProperty("slots").EnumerateArray());          // nothing on yet
        _desk.Show("B");
        var full = State().GetProperty("slots").EnumerateArray().Single();
        Assert.Equal(("B", 0.0, 1920.0), (full.GetProperty("lobby").GetString(), full.GetProperty("x").GetDouble(), full.GetProperty("w").GetDouble()));
        _desk.Show("", "4up", null, new List<string> { "C", "A", "", "B" });
        var quad = State().GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal(4, quad.Count);
        Assert.Equal("C", quad[0].GetProperty("lobby").GetString());
        Assert.Equal(JsonValueKind.Null, quad[2].GetProperty("lobby").ValueKind);
        Assert.True(quad[3].GetProperty("x").GetDouble() > 900 && quad[3].GetProperty("y").GetDouble() > 500);
    }

    [Fact]
    public void Each_lobby_brings_its_numbers_impostors_by_real_name_and_sabotage()
    {
        Snap("LJ", sabotage: new { system = "Reactor", critical = true, timeLeft = 12.4, fixing = 0.5 },
            players: new object[] { new { id = 1, name = "jk", color = 8, key = "fox#1", discord = "111", imp = true, dead = false },
                                    new { id = 2, name = "Bo", color = 2, key = "b#2", discord = (string?)null, imp = true, dead = true },
                                    new { id = 3, name = "Cy", color = 3, key = "c#3", discord = (string?)null, imp = false, dead = false } });
        var l = State().GetProperty("lobbies").GetProperty("LJ");
        Assert.Equal(1, l.GetProperty("number").GetInt32());
        var imps = l.GetProperty("impostors").EnumerateArray().ToList();
        Assert.Equal(2, imps.Count);
        Assert.Equal("Jake Rivera", imps[0].GetProperty("name").GetString());       // roster name, not the in-game one
        Assert.Equal(8, imps[0].GetProperty("color").GetInt32());
        Assert.True(imps[1].GetProperty("dead").GetBoolean());
        Assert.Equal(12.4, l.GetProperty("sabotage").GetProperty("left").GetDouble());
        Assert.Equal(0.5, l.GetProperty("sabotage").GetProperty("fixing").GetDouble());
    }

    [Fact]
    public void Theme_and_switches_come_from_broadcast_json()
    {
        var s = State();
        Assert.True(s.GetProperty("elements").GetProperty("statusBar").GetBoolean());
        Assert.Equal("#1fa143", s.GetProperty("theme").GetProperty("primary").GetString());
        _app.Settings.Set("statusBar", false);
        Assert.False(State().GetProperty("elements").GetProperty("statusBar").GetBoolean());
        // Edited by hand: picked up; elements left out keep their defaults.
        string path = Path.Combine(_dir.Path, BroadcastSettings.FileName);
        File.WriteAllText(path, """{ "theme": { "primary": "#ff8800", "font": "Oswald", "logo": "https://example.com/logo.png" } }""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        _app.Settings.Refresh(force: true);
        var t = State();
        Assert.Equal("#ff8800", t.GetProperty("theme").GetProperty("primary").GetString());
        Assert.Equal("https://example.com/logo.png", t.GetProperty("logo").GetString());
        Assert.True(t.GetProperty("elements").GetProperty("impostorTags").GetBoolean());
    }
}

public class BroadcastObsTests : IAsyncLifetime
{
    private FakeObs _obs = null!;
    private CasterDesk _desk = null!;
    private ObsDirector _director = null!;
    private readonly List<(string, string)> _feeds = new() { ("LJ", "https://vdo.ninja/?view=a&password=x&cleanoutput") };

    public async Task InitializeAsync()
    {
        _obs = new FakeObs();
        _desk = new CasterDesk(null, null, new PriorityConfig());
        _director = new ObsDirector(null, _desk, () => _feeds.ToList()) { BroadcastUrl = "http://127.0.0.1:8767/broadcast" };
        await _director.ConnectAsync("127.0.0.1", _obs.Port, "secret");
    }

    public async Task DisposeAsync() { await _director.DisposeAsync(); await _obs.DisposeAsync(); _desk.Dispose(); }

    [Fact]
    public async Task The_graphics_are_the_top_layer_of_every_TT_scene_even_after_new_lobbies()
    {
        foreach (var scene in new[] { "TT Full", "TT 2-up", "TT Quad", "TT Replay" })
            Assert.Equal("TT Broadcast", _obs.Scenes[scene].Last().Source);
        _feeds.Add(("MAL", "https://vdo.ninja/?view=b&password=x&cleanoutput"));
        await _director.BuildAsync();
        Assert.Contains(_obs.Scenes["TT Quad"], i => i.Source == "TT Lobby MAL");
        Assert.Equal("TT Broadcast", _obs.Scenes["TT Quad"].Last().Source);
        Assert.Equal("http://127.0.0.1:8767/broadcast", _obs.Inputs["TT Broadcast"]["url"]!.ToString());
    }
}
