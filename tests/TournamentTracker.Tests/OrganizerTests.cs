using System.Net;
using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.Discord;
using TournamentTracker.Setup;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

public class OrganizerTests : IDisposable
{
    private const string Webhook = "https://discord.test/api/webhooks/1/abc";
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly FakeDiscord _discord = new();
    private readonly FakeHttp _http = new();
    private readonly List<IDisposable> _owned = new();

    public OrganizerTests() => _http.Default = _discord.Handle;

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _dir.Dispose();
    }

    private TournamentSession Host(string label)
    {
        var code = new SetupCode
        {
            Mode = "tournament", TournamentId = "fall-cup", TournamentName = "Fall Cup", Webhook = Webhook,
            GuildId = "g1", BotTokens = new() { "tok-" + label }, ResultsChannelId = "results", AdvanceCount = 5, GamesPerRound = 3,
        };
        var dir = Path.Combine(_dir.Path, label);
        Directory.CreateDirectory(dir);
        var s = new TournamentSession(new TrackerSettings { LiveStatus = false, ControlPort = -1, LobbyLabel = label }, dir, NullLog.Instance,
            new HttpClient(_http), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"), code);
        _owned.Add(s);
        return s;
    }

    private Organizer Organiser()
    {
        var admin = new SetupCode { Mode = "admin", TournamentId = "fall-cup", TournamentName = "Fall Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results", AdvanceCount = 5, GamesPerRound = 3 };
        Assert.True(SetupCode.TryParse(admin.Encode(), out var read, out var error), error);
        var o = new Organizer(read, new HttpClient(_http), () => DateTime.UtcNow, start: false);
        _owned.Add(o);
        return o;
    }

    private bool HasLive(string label) =>
        _discord.Messages.ToList().Any(m => m.Embeds.HasValue && m.Embeds.Value.GetArrayLength() > 0 && FakeDiscord.Title(JsonDocument.Parse("{\"embeds\":" + m.Embeds.Value.GetRawText() + "}").RootElement) == TournamentSession.LiveTitlePrefix + label);

    [Fact]
    public void Awards_list_the_top_three_in_each_category()
    {
        var t = new TournamentTracker.Stats.GameTracker(new TournamentTracker.ScoringRules());
        var lobby = Players.Lobby();
        var start = new DateTime(2026, 10, 3, 19, 0, 0, DateTimeKind.Utc);
        var g = t.Start(1, "Cup", "X", "Polus", lobby, start);
        t.Kill(0, 2, start.AddSeconds(30));                          // Alice (impostor) kills Carl
        lobby[2].IsDead = true;
        var game = t.End("ImpostorByKill", TournamentTracker.Stats.Outcome.Impostors, lobby, start.AddMinutes(5))!;
        game.Round = 1;

        var awards = JsonSerializer.SerializeToElement(Organizer.Awards(new[] { game }), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var byTitle = awards.EnumerateArray().ToDictionary(a => a.GetProperty("title").GetString()!, a => a.GetProperty("top"));
        Assert.Equal(7, byTitle.Count);
        Assert.Equal("Alice", byTitle["Most kills"][0].GetProperty("name").GetString());
        Assert.Equal("1 kill", byTitle["Most kills"][0].GetProperty("value").GetString());
        Assert.Equal(1, byTitle["Most kills"].GetArrayLength());     // nobody else killed
        Assert.True(byTitle["Top score"].GetArrayLength() <= 3);
        Assert.Empty(byTitle["Sharpest voter"].EnumerateArray());   // no meetings, no votes
    }

    [Fact]
    public void Only_a_well_formed_video_link_is_used()
    {
        JsonElement D(string vdo) => JsonDocument.Parse(JsonSerializer.Serialize(new { vdo })).RootElement;
        Assert.NotNull(Organizer.VideoUrl(D("ttabc:key123")));
        // The sound is the same link without the picture; the picture never carries sound.
        Assert.Equal("https://vdo.ninja/?view=ttabc&password=key123&novideo&cleanoutput", Organizer.SoundUrl(D("ttabc:key123")));
        Assert.Contains("&noaudio", Organizer.VideoUrl(D("ttabc:key123")));
        Assert.Null(Organizer.SoundUrl(D("nocolon")));
        Assert.Null(Organizer.VideoUrl(D("tt&x=1:key")));
        Assert.Null(Organizer.VideoUrl(D("nocolon")));
        Assert.Null(Organizer.VideoUrl(JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public async Task Referee_commands_from_the_organiser_are_posted_and_the_hosts_act_on_them()
    {
        var lj = Host("LJ");
        var org = Organiser();
        Assert.StartsWith("Commands start with !", await org.CommandAsync("hello"));
        Assert.Equal("Posted: !start 2", await org.CommandAsync("!start 2"));
        var posted = _discord.Messages.ToList().Last(m => m.Content == "!start 2");
        Assert.True(posted.Bot);
        lj.HandleChannelMessage(new ChannelMessage { Id = posted.Id, ChannelId = "results", Content = posted.Content, AuthorIsBot = true });
        Assert.Equal(2, lj.Round);                                                             // a bot's !start counts
        lj.HandleChannelMessage(new ChannelMessage { Id = "5", ChannelId = "results", Content = "!resetleaderboard\n(reset)", AuthorIsBot = true });
        Assert.Equal(2, lj.Round);
    }

    [Fact]
    public void Admin_codes_unlock_the_organiser_and_are_not_host_codes()
    {
        var admin = new SetupCode { Mode = "admin", TournamentId = "fall-cup", TournamentName = "Fall Cup", BotTokens = new() { "a.b.c" }, ResultsChannelId = "results" };
        Assert.True(SetupCode.TryParse(admin.Encode(), out var read, out _));
        Assert.True(read.IsAdmin);
        Assert.Equal("Fall Cup (administration)", read.Describe());
        var missing = new SetupCode { Mode = "admin", TournamentId = "x", TournamentName = "X" };
        Assert.False(SetupCode.TryParse(missing.Encode(), out _, out var error));
        Assert.Contains("no bot or results channel", error);

        // Saved as a host's setup code by mistake: the game ignores it.
        SetupCode.Save(_dir.Path, admin.Encode());
        Assert.Null(SetupCode.Load(_dir.Path, NullLog.Instance));
    }
}
