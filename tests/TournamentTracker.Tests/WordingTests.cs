using System.Text.RegularExpressions;
using TournamentTracker.App;
using TournamentTracker.Discord;
using TournamentTracker.Overlay;
using TournamentTracker.Stats;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Messages that pointed at things that are gone: chat commands, sharing the entire screen, the bot only in a host code, "Referee: referee", the overlay's size.</summary>
public class WordingTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly List<IDisposable> _owned = new();

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _dir.Dispose();
    }

    private TournamentSession Session(Action<TrackerSettings>? configure = null)
    {
        var settings = new TrackerSettings { TournamentName = "Cup", LiveStatus = false, ControlPort = -1, VerifyResults = false, OverlayPort = 0 }.WithBot();
        configure?.Invoke(settings);
        string dir = Path.Combine(_dir.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var s = new TournamentSession(settings, dir, NullLog.Instance, new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));
        _owned.Add(s);
        return s;
    }

    private static string ButtonPage()
    {
        using var stream = typeof(AppServer).Assembly.GetManifestResourceStream("ui/index.html")!;
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void Unmute_all_and_referee_mode_point_to_the_Automute_button_not_a_chat_command()
    {
        var s = Session();
        s.RunCommand("unmuteall");
        Assert.Contains(s.Pump(), r => r.Text == "Everyone unmuted; automute is OFF. Press Automute on The Button's Home to turn it back on.");
        s.RunCommand("ref on");
        Assert.Contains(s.Pump(), r => r.Text == "Automute is off: press Automute on The Button's Home first.");
        // That's the round button on Home's Automute card.
        Assert.Matches(new Regex("<button class=\"round\" id=\"m-enabled\">.*</span>Automute</button>"), ButtonPage());
    }

    [Fact]
    public void Feed_on_says_to_share_the_Among_Us_window_as_The_Button_does()
    {
        var s = Session(c => { c.Mode = TrackerMode.Tournament; c.ResultsChannelId = "results"; });
        Assert.True(s.FeedAvailable);
        s.RunCommand("feed on");
        string reply = s.Pump().Last().Text;
        Assert.Equal("Sending your game to the caster. " + SendPage.Instructions(SendPage.ShareWindow, voiceOn: true), reply);
        Assert.DoesNotContain("Entire screen", reply);
    }

    [Fact]
    public void The_Automute_card_knows_bots_come_from_Your_Discord_too()
    {
        string page = ButtonPage();
        Assert.Contains("'Off: it needs a Discord bot. Add one in Settings → Your Discord, or use a setup code with a bot in it.'", page);
        Assert.DoesNotContain("comes with a tournament host code", page);
    }

    [Fact]
    public async Task A_referee_change_without_a_reason_is_a_Referee_change_in_the_breakdown()
    {
        var s = Session();
        var lobby = Players.Lobby();
        s.GameStarted("ABCDEF", "Polus", lobby);
        _clock.Advance(300);
        var game = s.GameEnded("HumansByTask", lobby)!;
        await s.PendingPosts;
        s.Pump();
        string key = game.Players[0].Key;

        s.RunCommand($"adjust {game.Name} {key} 1");                       // + on the Referee tab
        s.RunCommand($"adjust {game.Name} {key} -0.5 referee");            // − from an older Button, which sent "referee" as the reason
        s.RunCommand($"adjust {game.Name} {key} -2 left early");
        var lines = s.LastGame!.ByKey(key)!.PointBreakdown.Where(l => l.Rule.StartsWith("Referee")).Select(l => (l.Rule, l.Points));
        Assert.Equal(new[] { ("Referee change", 1.0), ("Referee change", -0.5), ("Referee: left early", -2.0) }, lines);
        var replies = s.Pump().Select(r => r.Text).ToList();
        Assert.Contains($"Points changed: {game.Players[0].Name} +1 in game {game.Name}.", replies);
        Assert.DoesNotContain(replies, r => r.Contains("(referee)"));
        Assert.DoesNotContain(s.LastGame!.ByKey(key)!.PointBreakdown, l => l.Rule == "Referee: referee");

        // The Referee tab's + and − send no reason now.
        string page = ButtonPage();
        Assert.Contains("cmd(`adjust ${name} ${key} ${step.dataset.step < 0 ? '-' : ''}${n}`);", page);
        Assert.DoesNotContain("${n} referee`", page);
    }

    [Fact]
    public void A_results_channel_adjust_without_a_reason_is_a_Referee_change_too()
    {
        var game = new GameRecord { Id = "g3", Host = "LJ", GameNumber = 3, StartedUtc = _clock.Now, Winner = "Crewmates", Players = { new GamePlayer { Key = "k0", Name = "Alice", ColorId = 0 } } };
        var plain = RefereeAdjustment.TryParse("!adjust LJ-3 red +1", "1", _clock.Now)!;
        Assert.Equal("", plain.Reason);
        var why = RefereeAdjustment.TryParse("!adjust LJ-3 red -2 meta call", "2", _clock.Now.AddSeconds(1))!;
        Assert.All(Standings.Apply(new[] { game }, new[] { plain, why }), r => Assert.True(r.Applied));
        Assert.Equal(new[] { "Referee change", "Referee: meta call" }, game.Players[0].PointBreakdown.Select(l => l.Rule));
        Assert.Equal("Referee change", PointLine.RefereeRule("  "));
    }

    [Fact]
    public void The_overlay_reply_gives_a_size_the_page_fits_in()
    {
        var s = Session();
        s.RunCommand("overlay on");
        string reply = s.Pump().Last().Text;
        Assert.EndsWith("Size 360×720.", reply);
        Assert.Contains("*{box-sizing:border-box}", OverlayPage.Html);                     // the panels' width includes their padding
        int width = int.Parse(Regex.Match(OverlayPage.Html, @"\.wrap\{[^}]*width:(\d+)px").Groups[1].Value);
        Assert.Equal(360, width);                                                          // the reply's width, padding included
        Assert.Contains("everything stays on a 720p screen", OverlayPage.Html);
    }
}
