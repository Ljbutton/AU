using System.Net;
using System.Text;
using System.Text.Json;
using TournamentTracker.Broadcast;
using TournamentTracker.Discord;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>The feed after a tracker restart, the player camera at a kill, the caster's quiet switches, closing the game.</summary>
public class SessionFixTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly FakeHttp _http = new();
    public void Dispose() => _dir.Dispose();

    private TournamentSession Session(int port = -1, Action<TrackerSettings>? configure = null)
    {
        var settings = new TrackerSettings { TournamentName = "Fall Cup", LiveStatus = false, ControlPort = port, Mode = TrackerMode.Tournament };
        configure?.Invoke(settings);
        return new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(_http), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));
    }

    private static FeedFrame Frame() => new()
    {
        Phase = VoicePhase.Tasks,
        Players = Enumerable.Range(0, 6).Select(i => new FeedPlayer { Id = (byte)i, X = i * 10, Y = 0, Room = "Cafeteria" }).ToList(),
        Camera = new FeedCamera { X = 0, Y = 0, HalfWidth = 5.33f, HalfHeight = 3 },
        KillCooldown = 25,
        KillDistance = 1.8f,
    };

    private static List<JsonElement> Feed(TournamentSession s) =>
        JsonDocument.Parse(s.FeedSince(0)).RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();

    [Fact]
    public void A_tracker_restarted_in_the_same_game_tags_its_feed_as_a_new_run()
    {
        // The setup changed mid-event: the mod starts a new session in the same Among Us. Its messages
        // count from 1 again, so they need a new run tag, or Red Alert drops them all as repeats.
        string first, second;
        using (var s = Session())
        {
            s.GameStarted("QWERTY", "The Skeld", Players.Lobby());
            s.FeedTick(Frame());
            var items = Feed(s);
            first = items[0].GetProperty("src").GetString()!;
            Assert.All(items, e => Assert.Equal(first, e.GetProperty("src").GetString()));
        }
        using (var s = Session())
        {
            s.GameStarted("QWERTY", "The Skeld", Players.Lobby());
            var items = Feed(s);
            Assert.Equal(1, items[0].GetProperty("seq").GetInt64());
            second = items[0].GetProperty("src").GetString()!;
        }
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_casters_pick_is_still_followed_for_a_few_seconds_after_they_die()
    {
        using var s = Session();
        s.GameStarted("QWERTY", "The Skeld", Players.Lobby());
        s.FeedTick(Frame());
        s.RunCommand("spec cam 4");
        Assert.Equal((byte)4, s.CamPlayer());
        _clock.Advance(30);                                   // picked long before the kill
        Assert.Equal((byte)4, s.CamPlayer());
        s.Kill(1, 4);
        Assert.Equal((byte)4, s.CamPlayer());                 // the kill is seen
        _clock.Advance(3);
        Assert.Equal((byte)4, s.CamPlayer());
        _clock.Advance(2);
        Assert.NotEqual((byte)4, s.CamPlayer());              // then the camera moves on
    }

    private static async Task<(JsonElement Answer, List<ChatReply> Chat)> AppCommand(TournamentSession s, HttpClient http, string command)
    {
        var chat = new List<ChatReply>();
        var post = http.PostAsync("api/command", new StringContent(JsonSerializer.Serialize(new { command }), Encoding.UTF8, "application/json"));
        while (!post.IsCompleted) { chat.AddRange(s.Pump()); await Task.Delay(10); }
        chat.AddRange(s.Pump());
        return (JsonDocument.Parse(await (await post).Content.ReadAsStringAsync()).RootElement.Clone(), chat);
    }

    [Fact]
    public async Task The_casters_switches_answer_the_app_but_stay_out_of_the_hosts_chat()
    {
        // The referee's screen is on stream, and its mini chat shows the host's chat.
        using var s = Session(port: 0);
        s.VoiceTick(VoicePhase.Lobby, Players.Lobby(), "ABCDEF", "Polus");
        using var http = AppHttp.Control(_dir.Path);

        var (answer, chat) = await AppCommand(s, http, "spec cam 3");
        Assert.True(answer.GetProperty("ok").GetBoolean());
        Assert.Contains(answer.GetProperty("replies").EnumerateArray(), r => r.GetString()!.StartsWith("Spectator view:"));
        Assert.Empty(chat);
        Assert.Equal(3, s.Spectator.CamPlayer);

        (_, chat) = await AppCommand(s, http, "spec audio cam");
        Assert.Empty(chat);
        Assert.True(s.Spectator.CamAudio);
        (_, chat) = await AppCommand(s, http, "spec nonsense");
        Assert.Empty(chat);

        // The host's own buttons still answer in their chat.
        (_, chat) = await AppCommand(s, http, "r2");
        Assert.Contains(chat, r => r.Text.StartsWith("Round 2 started"));
    }

    [Fact]
    public void The_game_sound_switch_isnt_written_to_disk()
    {
        // Red Alert sends it at every switch of what's on stream, and it isn't kept.
        using var s = Session();
        s.RunCommand("spec cam 2");
        var state = Directory.GetFiles(_dir.Path, "state-*.json").Single();
        var written = File.GetLastWriteTimeUtc(state);
        File.SetLastWriteTimeUtc(state, written.AddMinutes(-5));
        s.RunCommand("spec audio cam");
        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(state));
        s.RunCommand("spec cam 3");
        Assert.NotEqual(written.AddMinutes(-5), File.GetLastWriteTimeUtc(state));
    }

    [Fact]
    public async Task Closing_the_game_still_sends_the_lobby_closed_status()
    {
        const string webhook = "https://discord.test/api/webhooks/1/abc";
        _http.Default = r => r.Method == HttpMethod.Post ? FakeHttp.Json(HttpStatusCode.OK, """{"id":"555"}""") : FakeHttp.Json(HttpStatusCode.OK, "{}");
        using var s = Session(configure: c => { c.LiveStatus = true; c.StatsWebhookUrl = webhook; });
        s.Work.Start();                                       // as in the game: the status is made off the main thread
        s.VoiceTick(VoicePhase.Lobby, Players.Lobby(), "ABCDEF", "Polus");
        s.Work.Flush(TimeSpan.FromSeconds(5));
        await s.PendingPosts;
        Assert.Equal(HttpMethod.Post, Assert.Single(_http.Requests).Method);

        await s.ShutdownAsync(TimeSpan.FromSeconds(5));
        var last = _http.Requests.Last();
        Assert.Equal("PATCH", last.Method.Method);
        Assert.Equal(webhook + "/messages/555", last.Url);
        Assert.Contains("closed the lobby", JsonDocument.Parse(last.Body).RootElement.GetProperty("embeds")[0].GetProperty("description").GetString());
    }

    private static string StatusText(Sent sent) =>
        JsonDocument.Parse(sent.Body).RootElement.GetProperty("embeds")[0].GetProperty("description").GetString() ?? "";

    [Fact]
    public async Task The_lobby_closed_status_goes_out_even_when_the_edit_before_it_fails_as_the_game_closes()
    {
        // An edit is on the wire as the game closes, and it fails (here The Button isn't open). That
        // sets a wait before the status is sent again, which only the game's ticks end, and none come
        // now. The "lobby closed" edit queued behind it still goes out.
        const string webhook = "https://discord.test/api/webhooks/1/abc";
        using var onWire = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int patches = 0;
        _http.Default = r =>
        {
            if (r.Method == HttpMethod.Post) return FakeHttp.Json(HttpStatusCode.OK, """{"id":"555"}""");
            if (Interlocked.Increment(ref patches) > 1) return FakeHttp.Json(HttpStatusCode.OK, "{}");
            onWire.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return FakeHttp.Json(HttpStatusCode.ServiceUnavailable, "{}", (ButtonBridge.OfflineHeader, "1"));
        };
        using var s = Session(configure: c => { c.LiveStatus = true; c.StatsWebhookUrl = webhook; });
        s.Work.Start();
        s.VoiceTick(VoicePhase.Lobby, Players.Lobby(), "ABCDEF", "Polus");
        s.Work.Flush(TimeSpan.FromSeconds(5));
        await s.PendingPosts;

        s.VoiceTick(VoicePhase.Lobby, Players.Lobby().Take(4).ToList(), "ABCDEF", "Polus");   // two left: an edit
        Assert.True(onWire.Wait(TimeSpan.FromSeconds(5)));
        var closing = s.ShutdownAsync(TimeSpan.FromSeconds(5));     // "lobby closed" is queued behind it
        release.Set();                                               // and the edit on the wire fails
        await closing;

        var edits = _http.Requests.Where(r => r.Method.Method == "PATCH").ToList();
        Assert.Equal(2, edits.Count);
        Assert.Equal(webhook + "/messages/555", edits[1].Url);
        Assert.Contains("closed the lobby", StatusText(edits[1]));
    }

    [Fact]
    public async Task A_lobby_closed_status_that_fails_is_tried_once_more_before_the_game_closes()
    {
        // No tick comes to send it again once the game is closing: it's tried once more at once, not forever.
        const string webhook = "https://discord.test/api/webhooks/1/abc";
        _http.Default = r => r.Method == HttpMethod.Post
            ? FakeHttp.Json(HttpStatusCode.OK, """{"id":"555"}""")
            : FakeHttp.Json(HttpStatusCode.ServiceUnavailable, "{}", (ButtonBridge.OfflineHeader, "1"));
        using var s = Session(configure: c => { c.LiveStatus = true; c.StatsWebhookUrl = webhook; });
        s.Work.Start();
        s.VoiceTick(VoicePhase.Lobby, Players.Lobby(), "ABCDEF", "Polus");
        s.Work.Flush(TimeSpan.FromSeconds(5));
        await s.PendingPosts;

        await s.ShutdownAsync(TimeSpan.FromSeconds(8));
        var edits = _http.Requests.Where(r => r.Method.Method == "PATCH").ToList();
        Assert.Equal(2, edits.Count);
        Assert.All(edits, e => Assert.Contains("closed the lobby", StatusText(e)));
        Assert.True(s.PendingPosts.IsCompleted);                    // ShutdownAsync waited for the second try
    }

    [Fact]
    public void The_casters_camera_and_vision_picks_last_one_game_and_the_camera_pick_isnt_saved()
    {
        // Player ids are used again in the next game, and Red Alert forgets its pick then: the host's
        // camera mustn't keep following "player 4" into a game where that's someone else (and a cut
        // that keeps who the camera follows would then send "auto" and move it).
        using (var s = Session())
        {
            s.GameStarted("QWERTY", "The Skeld", Players.Lobby());
            s.FeedTick(Frame());
            s.RunCommand("spec cam 4");
            s.RunCommand("spec focus 3");
            Assert.Equal((byte)4, s.CamPlayer());
            Assert.Equal((byte)3, s.FocusPlayer());
            Assert.DoesNotContain("CamPlayer", File.ReadAllText(Directory.GetFiles(_dir.Path, "state-*.json").Single()));

            s.GameEnded("HumansByTask", Players.Lobby());
            s.RunCommand("spec cam 5");                               // picked between games: for the next one
            Assert.Equal(5, s.Spectator.CamPlayer);
            s.GameStarted("QWERTY", "The Skeld", Players.Lobby());
            Assert.Null(s.Spectator.CamPlayer);                       // Red Alert sends it again for this game
            Assert.Null(s.Spectator.Focus);
            Assert.True(s.Spectator.Cam);
            s.RunCommand("spec cam 5");
        }
        using (var s = Session())
            Assert.Null(s.Spectator.CamPlayer);                       // a restart doesn't bring an old pick back
    }
}
