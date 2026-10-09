using System.Net;
using TournamentTracker.Discord;
using TournamentTracker.Stats;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

public class SessionTests : IDisposable
{
    private const string Webhook = "https://discord.test/api/webhooks/1/abc";
    private readonly TempDir _dir = new();
    private readonly FakeHttp _http = new();
    private readonly FakeVoiceApi _voice = new();
    private readonly FakeClock _clock = new();
    private readonly List<TournamentSession> _sessions = new();
    private VoicePresenceState Presence { get; } = new("g1");

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        _dir.Dispose();
    }

    private TournamentSession Session(bool bot = true, Action<TrackerSettings>? configure = null)
    {
        var settings = new TrackerSettings { TournamentName = "Fall Cup", StatsWebhookUrl = Webhook, LiveStatus = false, PublicChat = true, ControlPort = -1, VerifyResults = false };
        if (bot)
        {
            settings.AutoMute.Enabled = true;
            settings.AutoMute.GuildId = "g1";
            settings.AutoMute.BotTokens.Add("tok");
        }
        configure?.Invoke(settings);
        var s = new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(_http), () => _clock.Now, _voice, Presence);
        _sessions.Add(s);
        return s;
    }

    private static async Task<List<ChatReply>> PumpUntilReply(TournamentSession s)
    {
        var replies = new List<ChatReply>();
        await Wait.Until(() => { replies.AddRange(s.Pump()); return replies.Count > 0; });
        return replies;
    }

    [Fact]
    public async Task A_finished_game_is_saved_posted_and_counted()
    {
        var s = Session();
        var players = Players.Lobby();
        s.GameStarted("ABCDEF", "Polus", players);
        _clock.Advance(20);
        s.Kill(0, 2);
        s.MeetingCalled(3, 2);
        s.VotingComplete(new[] { new VoteCast(3, 0), new VoteCast(4, 0), new VoteCast(5, 1) }, 0, false);
        s.MeetingClosed();
        _clock.Advance(100);
        var game = s.GameEnded("HumansByVote", players)!;
        await s.PendingPosts;

        Assert.Equal(Outcome.Crewmates, game.Winner);
        Assert.Equal(1, s.Store.GamesRecorded);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir.Path, "games", "fall-cup"), "game-1-*.json"));

        var posts = _http.Requests.Where(r => r.Url.StartsWith(Webhook)).ToList();
        Assert.Equal(2, posts.Count);
        Assert.Contains("Crewmates win", posts[0].Body);
        Assert.Contains("Leaderboard", posts[1].Body);

        // The next session (game restarted) picks the tournament up where it left off.
        var next = Session();
        Assert.Equal(1, next.Store.GamesRecorded);
        next.GameStarted("ABCDEF", "Polus", players);
        Assert.Equal(2, next.Tracker.Current!.GameNumber);
    }

    [Fact]
    public void Switching_automute_off_is_remembered()
    {
        var s = Session();
        s.RunCommand("automute off");
        Assert.False(s.AutoMute!.Enabled);
        var again = Session();
        Assert.False(again.AutoMute!.Enabled);                   // still off after a restart
        again.RunCommand("automute on");
        Assert.True(Session().AutoMute!.Enabled);
    }

    [Fact]
    public async Task Unmuteall_releases_everyone_and_switches_automute_off()
    {
        var s = Session();
        var lobby = Players.Lobby();
        s.Links.Link(lobby[2].Key, "Carl", "102", "carl");
        s.VoiceTick(VoicePhase.Tasks, lobby);
        _clock.Advance(5);                       // past the game-start delay
        s.VoiceTick(VoicePhase.Tasks, lobby);
        await Wait.Until(() => _voice.Calls.Any(c => c.State.Mute));

        s.RunCommand("!unmuteall");
        Assert.False(s.AutoMute!.Enabled);
        await Wait.Until(() => _voice.Calls.LastOrDefault().State == VoiceState.Open);

        s.RunCommand("!automute on");
        Assert.True(s.AutoMute.Enabled);
        Assert.Equal("Automute is ON.", s.Pump().Last().Text);
    }

    [Fact]
    public void Automute_stays_off_when_half_configured()
    {
        var s = Session(bot: false, configure: c => c.AutoMute.Enabled = true);
        Assert.Null(s.AutoMute);
        s.RunCommand("!automute on");
        Assert.Contains("isn't set up", s.Pump().Single().Text);
    }

}
