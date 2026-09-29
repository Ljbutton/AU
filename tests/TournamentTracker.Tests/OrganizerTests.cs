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
        var o = new Organizer(read, new HttpClient(_http), () => DateTime.UtcNow, casterPort: 0, start: false);
        _owned.Add(o);
        return o;
    }

    private bool HasLive(string label) =>
        _discord.Messages.ToList().Any(m => m.Embeds.HasValue && m.Embeds.Value.GetArrayLength() > 0 && FakeDiscord.Title(JsonDocument.Parse("{\"embeds\":" + m.Embeds.Value.GetRawText() + "}").RootElement) == TournamentSession.LiveTitlePrefix + label);

    [Fact]
    public async Task The_organiser_sees_every_lobby_live_and_the_caster_overlay_follows_the_pick()
    {
        var lj = Host("LJ");
        var mal = Host("MAL");
        var lobby = Players.Lobby();
        lj.RunCommand("r1");
        lj.VoiceTick(VoicePhase.Lobby, lobby, "QWERTY", "Polus");
        mal.VoiceTick(VoicePhase.Lobby, Players.Lobby(), "ZZZZZZ", "The Skeld");
        await Wait.Until(() => HasLive("LJ") && HasLive("MAL"), 5000);

        var org = Organiser();
        await org.PollLiveAsync();
        var state = JsonSerializer.SerializeToElement(org.State(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var lobbies = state.GetProperty("lobbies").EnumerateArray().ToList();
        Assert.Equal(new[] { "LJ", "MAL" }, lobbies.Select(l => l.GetProperty("label").GetString()));
        Assert.Equal("QWERTY", lobbies[0].GetProperty("data").GetProperty("code").GetString());
        Assert.Equal(1, lobbies[0].GetProperty("data").GetProperty("round").GetInt32());

        // A game starts in LJ's lobby: the live data follows (a few seconds later), roles only in the full view.
        _clock.Advance(5);
        lj.GameStarted("QWERTY", "Polus", lobby);
        lj.Kill(0, 2);
        lobby[2].IsDead = true;
        lj.VoiceTick(VoicePhase.Tasks, lobby, "QWERTY", "Polus");
        await Wait.Until(async () => { await org.PollLiveAsync(); var s = JsonSerializer.Serialize(org.State()); return s.Contains("\"Tasks\""); }, 10000);

        org.Cast("LJ");
        using var http = new HttpClient();
        string url = JsonSerializer.SerializeToElement(org.State(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }).GetProperty("casterUrl").GetString()!;
        var safe = JsonDocument.Parse(await http.GetStringAsync(url + "state")).RootElement;
        var full = JsonDocument.Parse(await http.GetStringAsync(url + "state?full=1")).RootElement;
        Assert.Equal("LJ", safe.GetProperty("lobby").GetString());
        Assert.All(safe.GetProperty("players").EnumerateArray(), p => Assert.False(p.GetProperty("impostor").GetBoolean()));
        Assert.False(safe.GetProperty("players")[2].GetProperty("dead").GetBoolean());         // nobody has found the body yet
        Assert.True(full.GetProperty("players")[0].GetProperty("impostor").GetBoolean());
        Assert.True(full.GetProperty("players")[2].GetProperty("dead").GetBoolean());
        Assert.Contains("overlay", await http.GetStringAsync(url));                            // the same overlay page OBS uses

        org.Cast("MAL");
        Assert.Equal("MAL", JsonDocument.Parse(await http.GetStringAsync(url + "state")).RootElement.GetProperty("lobby").GetString());
    }

    [Fact]
    public async Task Referee_commands_from_the_organiser_are_posted_and_the_hosts_act_on_them()
    {
        var lj = Host("LJ");
        var org = Organiser();
        Assert.StartsWith("Commands start with !", await org.CommandAsync("hello"));
        Assert.Equal("Posted: !start 2", await org.CommandAsync("!start 2"));
        var posted = _discord.Messages.Last();
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
