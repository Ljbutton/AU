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
        Assert.False(s.GetProperty("elements").GetProperty("statusBar").GetBoolean());   // the top 3 and the ticker took its place
        Assert.True(s.GetProperty("elements").GetProperty("top3").GetBoolean());
        Assert.True(s.GetProperty("elements").GetProperty("ticker").GetBoolean());
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

    [Fact]
    public void A_hosts_Twitch_channel_comes_with_their_lobby_and_bad_names_are_dropped()
    {
        Snap("LJ"); Snap("MAL");
        _desk.Apply(JsonSerializer.Serialize(new { v = 1, type = "host", lobby = "LJ", t = 0, twitch = "@LJ_Plays" }));
        _desk.Apply(JsonSerializer.Serialize(new { v = 1, type = "host", lobby = "MAL", t = 0, twitch = "not a name!" }));
        var l = State().GetProperty("lobbies");
        Assert.Equal("LJ_Plays", l.GetProperty("LJ").GetProperty("twitch").GetString());
        Assert.Equal(JsonValueKind.Null, l.GetProperty("MAL").GetProperty("twitch").ValueKind);
        _desk.Apply(JsonSerializer.Serialize(new { v = 1, type = "host", lobby = "LJ", t = 0, twitch = (string?)null }));   // removed in their Button
        Assert.Null(_desk.TwitchOf("LJ"));
        Assert.Equal("someone", TournamentTracker.Broadcast.FeedProtocol.TwitchHandle("https://www.twitch.tv/someone?ref=x"));
        Assert.Null(TournamentTracker.Broadcast.FeedProtocol.TwitchHandle("ab"));
    }

    [Fact]
    public void The_multiview_picker_puts_exactly_the_picked_lobbies_on_in_the_layout_their_number_needs()
    {
        foreach (var x in new[] { "A", "B", "C", "D", "E" }) Snap(x);
        Assert.Equal("full", _desk.ShowPicked(new[] { "C" }).Layout);
        var two = _desk.ShowPicked(new[] { "D", "A" });
        Assert.Equal("2up", two.Layout); Assert.Equal(new[] { "D", "A" }, two.Slots.ToArray());
        var four = _desk.ShowPicked(new[] { "E", "B", "A", "C" });
        Assert.Equal("4up", four.Layout); Assert.Equal(new[] { "E", "B", "A", "C" }, four.Slots.ToArray());
        var three = _desk.ShowPicked(new[] { "B", "C", "E" });
        Assert.Equal("4up", three.Layout);                                                    // 3 or 4 picked: the quad
        Assert.Equal(new string?[] { "B", "C", "E", null }, three.Slots.ToArray());          // the fourth quarter is left empty
        var five = _desk.ShowPicked(new[] { "A", "B", "C", "D", "E", "A" });
        Assert.Equal("grid", five.Layout);
        Assert.Equal(5, five.Slots.Count(x => x != null));
    }

    [Fact]
    public void A_view_chosen_before_Send_wins_over_the_count()
    {
        foreach (var x in new[] { "A", "B", "C", "D", "E" }) Snap(x);
        var full = _desk.ShowPicked(new[] { "C", "A" }, layout: "full");
        Assert.Equal(("full", "C"), (full.Layout, full.Slots[0]));
        var two = _desk.ShowPicked(new[] { "E", "D", "C" }, layout: "2up");
        Assert.Equal(new string?[] { "E", "D" }, two.Slots.ToArray());                       // extra lobbies left out
        var quad = _desk.ShowPicked(new[] { "B" }, layout: "4up");
        Assert.Equal(new string?[] { "B", null, null, null }, quad.Slots.ToArray());
        Assert.Equal("grid", _desk.ShowPicked(new[] { "A", "B" }, layout: "grid").Layout);
        Assert.Equal("2up", _desk.ShowPicked(new[] { "A", "B" }, layout: "nonsense").Layout);  // unknown: by the count
        Assert.Equal(new[] { "full", "2up", "4up", "4up", "grid", "grid" }, new[] { 1, 2, 3, 4, 5, 9 }.Select(CasterDesk.LayoutFor));
    }

    [Fact]
    public void A_multiview_of_four_or_more_is_presented_by_its_sponsor()
    {
        File.WriteAllText(Path.Combine(_dir.Path, SponsorBook.FileName), "{\"sponsors\":[{\"name\":\"Among Us All Stars\",\"placements\":[\"multiview\",\"replay\"]}]}");
        using var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path);
        var app = new BroadcastApp(desk, () => null, Path.Combine(_dir.Path, BroadcastSettings.FileName));
        JsonElement State() => JsonSerializer.SerializeToElement(app.State(), Camel);
        foreach (var x in new[] { "A", "B", "C", "D" }) desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = x, round = 1, phase = "ingame", t = 0 }));
        var _desk = desk;
        _desk.ShowPicked(new[] { "A", "B" });
        Assert.Equal(JsonValueKind.Null, State().GetProperty("extras").GetProperty("multiviewSponsor").ValueKind);
        _desk.ShowPicked(new[] { "A", "B", "C", "D" });
        Assert.Equal("Among Us All Stars", State().GetProperty("extras").GetProperty("multiviewSponsor").GetProperty("name").GetString());
        _clock.Advance(40);
        _desk.ShowPicked(new[] { "A" });
        Assert.Equal(JsonValueKind.Null, State().GetProperty("extras").GetProperty("multiviewSponsor").ValueKind);
        var (csv, _) = _desk.Sponsors.Export();
        Assert.Contains("Among Us All Stars,multiview,", csv);
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
            Assert.Equal(new[] { "TT Broadcast", "TT Swoosh" }, _obs.Scenes[scene].TakeLast(2).Select(i => i.Source));
        _feeds.Add(("MAL", "https://vdo.ninja/?view=b&password=x&cleanoutput"));
        await _director.BuildAsync();
        Assert.Contains(_obs.Scenes["TT Quad"], i => i.Source == "TT Lobby MAL");
        Assert.Equal(new[] { "TT Broadcast", "TT Swoosh" }, _obs.Scenes["TT Quad"].TakeLast(2).Select(i => i.Source));
        Assert.Equal("http://127.0.0.1:8767/broadcast", _obs.Inputs["TT Broadcast"]["url"]!.ToString());
    }

}
