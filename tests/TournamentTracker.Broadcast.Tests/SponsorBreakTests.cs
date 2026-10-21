using System.Text.Json;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// A sponsor break can't be interrupted: a lobby picked meanwhile goes into its picture (the sponsor, its
/// time and its appearance carry on), whole-screen switches are refused, and only its time running out,
/// End break and Panic end it. Auto switch off a dropped lobby swaps the break's lobby, and the break goes on.
/// </summary>
public class SponsorBreakTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly CasterDesk _desk;
    private readonly List<OnAir> _switched = new();

    public SponsorBreakTests()
    {
        File.WriteAllText(Path.Combine(_dir.Path, SponsorBook.FileName), "{\"sponsors\":[{\"name\":\"Acme\",\"placements\":[\"break\"],\"breakSeconds\":30},{\"name\":\"Bolt\",\"placements\":[\"break\"],\"breakSeconds\":30}]}");
        _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path);
        _desk.Sponsors.Refresh(force: true);
        _desk.Switch = air => _switched.Add(air);
    }

    public void Dispose() { _desk.Dispose(); _dir.Dispose(); }

    private void Snap(string lobby, string phase = "ingame") =>
        _desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby, phase, crewAlive = 6, impAlive = 2, taskPct = 20, t = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds() }));

    /// <summary>Time passes a second at a time; the given lobbies keep sending, and the desk ticks.</summary>
    private void Pass(int seconds, params string[] sending)
    {
        for (int i = 0; i < seconds; i++)
        {
            _clock.Advance(1);
            foreach (var l in sending) Snap(l);
            _desk.Tick();
        }
    }

    private (string Sponsor, DateTime Until, string? Lobby) Break => _desk.Break is { } b ? (b.Sponsor.Name, b.Until, b.Lobby) : default;

    private DateTime OnBreak()
    {
        foreach (var l in new[] { "LJ", "MAL", "ZED" }) Snap(l);
        _desk.Show("LJ");
        Assert.StartsWith("Sponsor break: Acme for 30s", _desk.StartBreak());
        Assert.Equal(("break", "LJ"), (_desk.OnAir.Layout, _desk.OnAir.Slots[0]));
        return _desk.Break!.Value.Until;
    }

    [Fact]
    public void A_lobby_picked_during_a_break_goes_into_its_picture_and_the_break_carries_on()
    {
        var until = OnBreak();
        Pass(12, "LJ", "MAL", "ZED");

        // A cut to a lobby (a tile's double-click, a Stream Deck lobby key).
        var air = _desk.Show("MAL", "full", null, null, "deck");
        Assert.Equal(("break", "MAL"), (air.Layout, air.Slots[0]));
        Assert.Equal(("Acme", until, "MAL"), Break);
        Assert.Equal("Sponsor break on (18 s left): MAL is in the break's picture now.", _desk.BreakSaid(air));
        Assert.Same(air, _switched.Last());                                   // OBS is told: the break's scene with MAL

        // Take with several lobbies: the first of them.
        _desk.PreviewOnly("ZED");
        _desk.PreviewAdd("LJ");
        Assert.Equal(("break", "ZED"), (_desk.Take()!.Layout, _desk.OnAir.Slots[0]));
        // The grid, a quad, 2-up picked by hand: the first lobby picked.
        _desk.ShowPicked(new[] { "LJ", "MAL", "ZED" }, "button", "grid");
        Assert.Equal(("break", "LJ"), (_desk.OnAir.Layout, _desk.OnAir.Slots[0]));
        _desk.Show("", "2up", null, new[] { "MAL", "ZED" });
        Assert.Equal(("break", "MAL"), (_desk.OnAir.Layout, _desk.OnAir.Slots[0]));
        // "Quad now" (nothing picked): the break's lobby stays.
        _desk.Show("", "4up", null, null, "deck");
        Assert.Equal(("break", "MAL"), (_desk.OnAir.Layout, _desk.OnAir.Slots[0]));
        // A player camera cut: that lobby, in the break.
        _desk.Show("ZED", "cam", null, null, "deck");
        Assert.Equal(("break", "ZED"), (_desk.OnAir.Layout, _desk.OnAir.Slots[0]));

        // The sponsor, its time and its appearance all carried on.
        Assert.Equal(("Acme", until, "ZED"), Break);
        Assert.Empty(_desk.Sponsors.Appearances());                           // still open: logged when it ends
        var state = JsonSerializer.SerializeToElement(_desk.State(), TestJson.Camel).GetProperty("break");
        Assert.Equal(("Acme", 18, "ZED"), (state.GetProperty("sponsor").GetString(), state.GetProperty("left").GetInt32(), state.GetProperty("lobby").GetString()));

        // Its time up: it ends as before, to the lobby it has now, full screen.
        Pass(17, "LJ", "MAL", "ZED");
        Assert.Equal("break", _desk.OnAir.Layout);
        Pass(1, "LJ", "MAL", "ZED");
        Assert.Equal(("full", "ZED", "auto"), (_desk.OnAir.Layout, _desk.OnAir.Slots[0], _desk.OnAir.By));
        Assert.Null(_desk.Break);
        var shown = _desk.Sponsors.Appearances().Single();
        Assert.Equal(("Acme", "break", 30), (shown.Sponsor, shown.Placement, (int)shown.Seconds));
    }

    [Fact]
    public void Whole_screen_switches_are_refused_during_a_break()
    {
        OnBreak();
        Pass(12, "LJ", "MAL", "ZED");
        const string refused = "A sponsor break is on: 18 s left. End break first.";
        Assert.Equal(refused, _desk.BreakRefusal());
        int before = _switched.Count;
        _desk.ShowSlate();
        _desk.ShowIntermission();
        _desk.ShowStartingSoon();
        _desk.Show("", "intermission");
        _desk.Show("", "slate");
        Assert.Equal(refused, _desk.StartBreak("Bolt"));
        Assert.Equal(("break", "LJ", "Acme"), (_desk.OnAir.Layout, _desk.OnAir.Slots[0], _desk.Break!.Value.Sponsor.Name));
        Assert.Equal(before, _switched.Count);                                // nothing went to OBS
        Assert.Empty(_desk.Sponsors.Appearances());
        // Undo to what was on before the break (intermission here) is refused too.
        _desk.EndBreak("button");
        _desk.ShowIntermission();
        _desk.StartBreak();
        Assert.Equal(_desk.BreakRefusal(), _desk.BackRefused());
        Assert.Equal("break", _desk.OnAir.Layout);
    }

    [Fact]
    public async Task A_replay_or_montage_is_refused_during_a_break()
    {
        OnBreak();
        await using var director = new ObsDirector(Path.Combine(_dir.Path, ObsSettings.FileName), _desk, () => new List<(string, string)>());
        using var replays = new ReplayManager(_desk, director, () => _clock.Now, loop: false);
        var clip = new Clip { Id = "r1", Lobby = "MAL", Rule = "kill", Title = "x", File = Path.Combine(_dir.Path, "r1.mp4"), State = "ready" };
        Assert.Equal("A sponsor break is on: 30 s left. End break first.", await replays.PlayAsync(clip).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("A sponsor break is on: 30 s left. End break first.", await replays.PlayVideoAsync("m1", Path.Combine(_dir.Path, "m1.mp4"), "Round 1").WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(replays.Now);
        Assert.Equal(("break", "LJ"), (_desk.OnAir.Layout, _desk.OnAir.Slots[0]));
    }

    [Fact]
    public void Panic_and_End_break_end_it_at_once()
    {
        OnBreak();
        Pass(5, "LJ", "MAL", "ZED");
        var brb = _desk.ShowSlate("deck", "fade", force: true);              // Panic's be-right-back screen
        Assert.Equal("slate", brb.Layout);
        Assert.Null(_desk.Break);
        Assert.Equal(5, (int)_desk.Sponsors.Appearances().Single().Seconds);

        _desk.Show("LJ");
        _desk.StartBreak();
        _desk.Show("MAL");
        Pass(3, "LJ", "MAL", "ZED");
        var back = _desk.EndBreak("button")!;
        Assert.Equal(("full", "MAL"), (back.Layout, back.Slots[0]));
        Assert.Null(_desk.Break);
        Assert.Equal(2, _desk.Sponsors.Appearances().Count);
        Assert.Null(_desk.BreakRefusal());
    }

    [Fact]
    public void A_must_show_play_during_a_break_doesnt_end_it()
    {
        OnBreak();
        var made = new List<Card>();
        _desk.CardMade += made.Add;
        _desk.Apply(JsonSerializer.Serialize(new { type = "event", kind = "eject", lobby = "MAL", round = 1, game = "MAL-1", ejected = new { id = 3, name = "Sam", color = 2 }, wasImpostor = true, t = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds() }));
        Pass(3, "LJ", "MAL", "ZED");
        Assert.Contains(made, c => c.Lobby == "MAL" && c.Tier == "must");
        Assert.Equal(("break", "LJ"), (_desk.OnAir.Layout, _desk.OnAir.Slots[0]));
        Assert.NotNull(_desk.Break);
    }

    [Fact]
    public void Auto_switch_off_a_dropped_lobby_during_a_break_swaps_the_breaks_lobby_and_the_break_carries_on()
    {
        var until = OnBreak();
        // LJ (in the break) stops sending: it goes red, and a healthy lobby goes into the break's picture.
        Pass(12, "MAL", "ZED");
        Assert.True(_desk.IsDown("LJ"));
        Assert.Equal("break", _desk.OnAir.Layout);
        Assert.Equal("health", _desk.OnAir.By);
        string now = _desk.OnAir.Slots[0]!;
        Assert.Contains(now, new[] { "MAL", "ZED" });
        Assert.Equal(("Acme", until, now), Break);
        Assert.Empty(_desk.Sponsors.Appearances());                           // the sponsor carries on
        Assert.Contains("the sponsor break carries on", _desk.AutoSwitched!.Text);

        // Every lobby down: the break stays on (RECONNECTING on its picture), still with its sponsor.
        Pass(12);
        Assert.Equal(("break", "Acme"), (_desk.OnAir.Layout, _desk.Break!.Value.Sponsor.Name));
        // Its time up: it ends as usual.
        Pass(10);
        Assert.Null(_desk.Break);
        Assert.Single(_desk.Sponsors.Appearances());
    }
}
