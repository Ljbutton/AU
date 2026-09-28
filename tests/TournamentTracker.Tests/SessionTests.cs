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
        var settings = new TrackerSettings { TournamentName = "Fall Cup", StatsWebhookUrl = Webhook, LiveStatus = false };
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
    public async Task Live_feed_posts_every_event_when_configured()
    {
        var s = Session(configure: c => c.LiveFeedWebhookUrl = "https://discord.test/api/webhooks/2/live");
        s.GameStarted("ABCDEF", "Polus", Players.Lobby());
        s.Kill(0, 2);
        await s.PendingPosts;
        var live = _http.Requests.Where(r => r.Url.Contains("/webhooks/2/")).ToList();
        Assert.Equal(2, live.Count);
        Assert.Contains("killed", live[1].Body);
    }

    [Fact]
    public async Task Player_links_themselves_by_discord_id()
    {
        var s = Session();
        var carl = Players.Lobby()[2];
        _http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.OK, """{"user":{"id":"123456789012345678","username":"carl"},"nick":"Carl C"}"""));

        Assert.True(s.HandleChat(carl, fromHost: false, "!link <@123456789012345678>"));
        var replies = await PumpUntilReply(s);

        var reply = Assert.Single(replies);
        Assert.True(reply.Public);
        Assert.Equal("Linked Green (Carl) to @Carl C.", reply.Text);
        Assert.Equal("123456789012345678", s.Links.Find(carl.Key)!.DiscordUserId);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "links.json")));
    }

    [Fact]
    public async Task Linking_by_name_needs_one_clear_match()
    {
        var s = Session();
        var carl = Players.Lobby()[2];
        _http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.OK,
            """[{"user":{"id":"1","username":"carl"}},{"user":{"id":"2","username":"carlos"}}]"""));
        s.HandleChat(carl, false, "!link @carl");
        await PumpUntilReply(s);
        Assert.Equal("1", s.Links.Find(carl.Key)!.DiscordUserId);

        _http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.OK,
            """[{"user":{"id":"1","username":"carl"}},{"user":{"id":"2","username":"carlos"}}]"""));
        s.HandleChat(carl, false, "!link car");
        var replies = await PumpUntilReply(s);
        Assert.Contains("matches 2 people", replies.Single().Text);
    }

    [Fact]
    public async Task Host_links_another_player_by_colour()
    {
        var s = Session();
        var lobby = Players.Lobby();
        s.VoiceTick(VoicePhase.Lobby, lobby);
        _http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.OK, """{"user":{"id":"555555555555555555","username":"alice"}}"""));

        s.HandleChat(lobby[3], fromHost: true, "!link red 555555555555555555");
        var reply = (await PumpUntilReply(s)).Single();

        Assert.True(reply.Public);  // Alice has to see it
        Assert.Equal("555555555555555555", s.Links.Find(lobby[0].Key)!.DiscordUserId);
        Assert.Null(s.Links.Find(lobby[3].Key));
    }

    [Fact]
    public void Without_a_bot_only_numeric_ids_can_be_linked()
    {
        var s = Session(bot: false);
        var carl = Players.Lobby()[2];
        s.HandleChat(carl, false, "!link carl");
        Assert.Contains("numeric Discord user ID", s.Pump().Single().Text);
        s.HandleChat(carl, false, "!link 123456789012345678");
        Assert.Equal("Linked Green (Carl) to Discord ID 123456789012345678.", s.Pump().Single().Text);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public void Host_commands_are_ignored_from_players_and_plain_chat_passes_through()
    {
        var s = Session();
        var carl = Players.Lobby()[2];
        Assert.False(s.HandleChat(carl, false, "!unmuteall"));
        Assert.False(s.HandleChat(carl, false, "where was the body"));
        Assert.False(s.HandleChat(carl, false, "!"));
        Assert.True(s.AutoMute!.Enabled);
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

        s.HandleChat(lobby[0], fromHost: true, "!unmuteall");
        Assert.False(s.AutoMute!.Enabled);
        await Wait.Until(() => _voice.Calls.LastOrDefault().State == VoiceState.Open);

        s.HandleChat(lobby[0], true, "!automute on");
        Assert.True(s.AutoMute.Enabled);
        Assert.Equal("Automute is ON.", s.Pump().Last().Text);
    }

    [Fact]
    public void Stats_command_reports_totals()
    {
        var s = Session();
        var lobby = Players.Lobby();
        s.GameStarted("X", "Polus", lobby);
        s.Kill(0, 2);
        s.GameEnded("ImpostorByKill", lobby);
        s.VoiceTick(VoicePhase.Lobby, lobby);

        s.HandleChat(lobby[3], false, "!stats red");
        var lines = s.Pump();
        Assert.Equal("Alice: #1, 6 pts, 1W-0L, 1 kills", lines[0].Text);
    }

    [Fact]
    public void Resetstats_needs_confirmation_and_archives()
    {
        var s = Session();
        var lobby = Players.Lobby();
        s.GameStarted("X", "Polus", lobby);
        s.GameEnded("HumansByTask", lobby);

        s.HandleChat(lobby[0], true, "!resetstats");
        Assert.Equal(1, s.Store.GamesRecorded);
        s.HandleChat(lobby[0], true, "!resetstats confirm");
        Assert.Equal(0, s.Store.GamesRecorded);
        Assert.Single(Directory.GetFiles(_dir.Path, "stats-fall-cup-archived-*.json"));
    }

    [Fact]
    public void Automute_stays_off_when_half_configured()
    {
        var s = Session(bot: false, configure: c => c.AutoMute.Enabled = true);
        Assert.Null(s.AutoMute);
        s.HandleChat(Players.Lobby()[0], true, "!automute on");
        Assert.Contains("isn't set up", s.Pump().Single().Text);
    }

    [Fact]
    public void Replies_never_look_like_commands()
    {
        // Public replies go out as the host's chat and come back through the chat hook.
        var s = Session();
        var lobby = Players.Lobby();
        s.HandleChat(lobby[2], false, "!help");
        s.HandleChat(lobby[0], true, "!help");
        s.HandleChat(lobby[2], false, "!link");
        Assert.All(s.Pump(), r => Assert.False(r.Text.StartsWith("!")));
    }
}
