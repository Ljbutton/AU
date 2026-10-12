using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.Discord;
using TournamentTracker.Setup;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Discord's rate limits, timeouts and lost answers, as the bot's REST calls handle them.</summary>
public class RestReliabilityTests
{
    private const string Hook = "https://discord.com/api/webhooks/1/abc";
    private readonly FakeHttp _http = new();
    private readonly List<TimeSpan> _waits = new();

    private DiscordRest Rest() => new(new HttpClient(_http), NullLog.Instance, delay: (t, _) => { lock (_waits) _waits.Add(t); return Task.CompletedTask; });

    [Fact]
    public async Task A_global_rate_limit_holds_every_route_not_only_the_one_that_hit_it()
    {
        _http.Responses.Enqueue(_ => FakeHttp.Json((HttpStatusCode)429, "{\"retry_after\":2.5,\"global\":true}", ("X-RateLimit-Global", "true")));
        var rest = Rest();
        Assert.True((await rest.AddReactionAsync("tok", "c1", "m1", "✅")).Ok);
        Assert.Contains(_waits, w => Math.Abs(w.TotalSeconds - 2.5) < 0.01);

        lock (_waits) _waits.Clear();
        Assert.True((await rest.PostMessageAsync("tok", "c2", "hello")).Ok);   // another route, straight after
        var wait = Assert.Single(_waits);
        Assert.InRange(wait.TotalSeconds, 1.5, 2.5);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_webhook_post_is_not_sent_again_when_it_may_have_gone_through(HttpStatusCode status)
    {
        _http.Responses.Enqueue(_ => FakeHttp.Json(status, "{}"));
        var result = await Rest().ExecuteWebhookAsync(Hook, new WebhookMessage { Content = "Game 1: crewmates win" });
        Assert.False(result.Ok);
        Assert.Single(_http.Requests);              // a public report shown twice is worse than a failure in the log
    }

    [Fact]
    public async Task A_webhook_post_is_sent_again_when_it_surely_never_got_there()
    {
        _http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        _http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.InternalServerError, "{}", (DiscordRest.NotSentHeader, "1")));
        var result = await Rest().ExecuteWebhookAsync(Hook, new WebhookMessage { Content = "Game 1" });
        Assert.True(result.Ok);
        Assert.Equal(3, _http.Requests.Count);
    }

    [Fact]
    public async Task A_game_file_is_sent_again_because_readers_count_it_once()
    {
        _http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.InternalServerError, "{}"));
        var result = await Rest().ExecuteWebhookWithFileAsync(Hook, new WebhookMessage { Content = "Game 1" }, "game-1.json", Encoding.UTF8.GetBytes("{}"));
        Assert.True(result.Ok);
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task The_bots_own_posts_carry_one_nonce_so_a_resend_never_posts_twice()
    {
        _http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.InternalServerError, "{}"));
        var result = await Rest().PostMessageAsync("tok", "c1", "!start 2");
        Assert.True(result.Ok);
        var sent = _http.Requests.ToList();
        Assert.Equal(2, sent.Count);
        var nonces = sent.Select(r => JsonDocument.Parse(r.Body).RootElement).Select(j => (j.GetProperty("nonce").GetString(), j.GetProperty("enforce_nonce").GetBoolean())).ToList();
        Assert.Equal(nonces[0], nonces[1]);
        Assert.True(nonces[0].Item2);
        Assert.InRange(nonces[0].Item1!.Length, 1, 25);              // Discord's limit
    }

    [Fact]
    public async Task A_mute_that_waited_for_the_rate_limit_sends_what_the_game_wants_by_then()
    {
        bool mute = true;
        _http.Responses.Enqueue(_ => { mute = false; return FakeHttp.Json((HttpStatusCode)429, "{\"retry_after\":0.01}"); });   // the meeting ended meanwhile
        var result = await Rest().SetVoiceStateAsync("tok", "g1", "200", () => (mute, false), "Among Us automute");
        Assert.True(result.Ok);
        var sent = _http.Requests.ToList();
        Assert.Contains("\"mute\":true", sent[0].Body);
        Assert.Contains("\"mute\":false", sent[1].Body);
    }

    private sealed class Hang : HttpMessageHandler
    {
        public int Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public async Task A_request_Discord_never_answers_gives_up_in_its_own_time_not_the_clients()
    {
        var hang = new Hang();
        var rest = new DiscordRest(new HttpClient(hang) { Timeout = TimeSpan.FromMinutes(10) }, NullLog.Instance, delay: (_, _) => Task.CompletedTask)
        {
            RequestTimeout = TimeSpan.FromMilliseconds(100),
        };
        var watch = Stopwatch.StartNew();
        var (messages, result) = await rest.ReadMessagesAsync("tok", "c1");
        Assert.Null(messages);
        Assert.False(result.Ok);
        Assert.Equal(5, hang.Count);                                  // tried again, each try cut short
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }
}

/// <summary>A voice API that waits (as for a rate limit) before it asks what to send.</summary>
public sealed class LateVoiceApi : ILateVoiceApi
{
    public ConcurrentQueue<(string User, VoiceState State)> Sent { get; } = new();
    public Func<string, VoiceState, DiscordResult>? Respond { get; set; }
    public TaskCompletionSource? Gate { get; set; }

    public Task<DiscordResult> SetVoiceStateAsync(string botToken, string userId, VoiceState state, CancellationToken ct) =>
        SetVoiceStateAsync(botToken, userId, () => state, ct);

    public async Task<DiscordResult> SetVoiceStateAsync(string botToken, string userId, Func<VoiceState> state, CancellationToken ct)
    {
        if (Gate != null) await Gate.Task.WaitAsync(ct);
        var s = state();
        Sent.Enqueue((userId, s));
        return Respond?.Invoke(userId, s) ?? new DiscordResult { Ok = true, Status = 204 };
    }
}

public class MuteDispatcherReliabilityTests
{
    private static readonly VoiceState Muted = new(true, false);
    private readonly FakeClock _clock = new();
    private readonly LateVoiceApi _api = new();

    private MuteDispatcher Dispatcher() => new(_api, new[] { "tok" }, NullLog.Instance, () => _clock.Now, lanesPerBot: 1);

    [Fact]
    public async Task A_change_held_back_by_Discord_goes_out_as_the_game_wants_it_when_it_is_sent()
    {
        _api.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var d = Dispatcher();
        d.SetDesired("200", Muted);                                   // the meeting starts...
        await Task.Delay(100);
        d.SetDesired("200", VoiceState.Open);                         // ...and ends while the mute waits
        _api.Gate.SetResult();
        Assert.True(await d.WaitIdleAsync(TimeSpan.FromSeconds(3)));
        var sent = Assert.Single(_api.Sent);
        Assert.Equal(VoiceState.Open, sent.State);
        Assert.Equal(VoiceState.Open, d.Applied("200"));
    }

    [Fact]
    public async Task Someone_who_left_voice_is_not_retried_all_evening_and_gets_their_state_when_back()
    {
        bool inVoice = false;
        _api.Respond = (_, _) => inVoice
            ? new DiscordResult { Ok = true, Status = 204 }
            : new DiscordResult { Ok = false, Status = 400, ErrorCode = DiscordResult.NotInVoice };
        using var d = Dispatcher();
        d.NotInVoiceTries = 1;
        d.SetDesired("200", Muted);
        await Wait.Until(() => _api.Sent.Count == 1 && d.Pending == 0);   // its retry is booked
        _clock.Advance(10);                                           // one timed retry
        await Wait.Until(() => _api.Sent.Count == 2 && d.Pending == 0);
        _clock.Advance(3600);                                         // then none, however long
        await Task.Delay(1300);
        Assert.Equal(2, _api.Sent.Count);
        Assert.True(d.WaitingForVoice);

        d.InVoice(new[] { "300" });                                   // the gateway sees they're gone
        d.SetDesired("200", VoiceState.Open);                         // a new phase: nothing sent while they're out
        await Task.Delay(300);
        Assert.Equal(2, _api.Sent.Count);

        inVoice = true;
        d.InVoice(new[] { "300", "200" });                            // back: the newest state at once
        await Wait.Until(() => _api.Sent.Count == 3);
        Assert.Equal(VoiceState.Open, _api.Sent.Last().State);
        Assert.False(d.WaitingForVoice);
    }

    [Fact]
    public async Task When_the_gateway_says_they_are_in_voice_all_along_timed_retries_carry_on_slowly()
    {
        int calls = 0;
        _api.Respond = (_, _) => Interlocked.Increment(ref calls) <= 2
            ? new DiscordResult { Ok = false, Status = 400, ErrorCode = DiscordResult.NotInVoice }
            : new DiscordResult { Ok = true, Status = 204 };
        using var d = Dispatcher();
        d.NotInVoiceTries = 1;
        d.SetDesired("200", Muted);
        await Wait.Until(() => _api.Sent.Count == 1 && d.Pending == 0);   // its retry is booked
        _clock.Advance(10);
        await Wait.Until(() => _api.Sent.Count == 2 && d.Pending == 0);
        d.InVoice(new[] { "200" });                                   // never seen out
        _clock.Advance(d.MaxRetry.TotalSeconds);
        await Wait.Until(() => _api.Sent.Count == 3);
        Assert.Equal(Muted, d.Applied("200"));
    }

    [Fact]
    public async Task Resending_after_The_Button_reopens_leaves_alone_who_was_never_touched()
    {
        using var d = Dispatcher();
        d.SetDesired("200", VoiceState.Open);
        d.SetDesired("201", Muted);
        Assert.True(await d.WaitIdleAsync(TimeSpan.FromSeconds(3)));
        int before = _api.Sent.Count;
        d.ResendAll();
        Assert.True(await d.WaitIdleAsync(TimeSpan.FromSeconds(3)));
        var again = _api.Sent.Skip(before).ToList();
        Assert.Equal(new[] { "201" }, again.Select(a => a.User));
    }
}

/// <summary>The live status and live data aren't counted as sent when Discord didn't take them.</summary>
public class LiveRetryTests : IDisposable
{
    private const string Webhook = "https://discord.test/api/webhooks/1/abc";
    private readonly TempDir _dir = new();
    private readonly FakeHttp _http = new();
    private readonly FakeClock _clock = new();
    private readonly List<PlayerSnapshot> _lobby = Players.Lobby();
    private TournamentSession? _session;

    public void Dispose()
    {
        _session?.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task A_failed_status_edit_is_tried_again_after_a_wait_with_the_newest_status()
    {
        bool fail = false;
        _http.Default = r => r.Method == HttpMethod.Post ? FakeHttp.Json(HttpStatusCode.OK, """{"id":"555"}""")
            : r.Method.Method == "PATCH" && fail ? FakeHttp.Json(HttpStatusCode.Forbidden, """{"code":50013}""")
            : FakeHttp.Json(HttpStatusCode.OK, "{}");
        var settings = new TrackerSettings { TournamentName = "Cup", StatsWebhookUrl = Webhook, LiveStatus = true, ControlPort = -1, VerifyResults = false };
        settings.AutoMute.Enabled = true;
        settings.AutoMute.GuildId = "g1";
        settings.AutoMute.BotTokens.Add("tok");
        settings.AutoMute.LinkMenu = false;
        settings.AutoMute.AutoLinkByName = false;
        var s = _session = new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(_http), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1") { Connected = true });

        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        Assert.Single(_http.Requests);

        fail = true;
        _lobby.RemoveAt(5);                                           // Finn leaves; Discord turns the edit down
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        Assert.Equal(2, _http.Requests.Count);

        _clock.Advance(2);                                            // not due yet: nothing sent on every tick
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        Assert.Equal(2, _http.Requests.Count);

        fail = false;
        _lobby.RemoveAt(4);                                           // Eve leaves too
        _clock.Advance(5);
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        var edit = _http.Requests.Last();
        Assert.Equal("PATCH", edit.Method.Method);
        string text = JsonDocument.Parse(edit.Body).RootElement.GetProperty("embeds")[0].GetProperty("description").GetString()!;
        Assert.DoesNotContain("Finn", text);
        Assert.DoesNotContain("Eve", text);
        Assert.Equal(3, _http.Requests.Count);                        // one retry, with the newest status
    }
}

/// <summary>The organiser view finds lobbies whose live message is further up the channel.</summary>
public class OrganizerReachTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly List<IDisposable> _owned = new();
    private int _ownReads;
    private long _sent;

    public OrganizerReachTests() => _sent = new DateTimeOffset(_clock.Now).ToUnixTimeSeconds();

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
    }

    private static object Plain(int i) => new { id = (5000 + i).ToString(), content = "chat " + i, author = new { id = "ref", bot = false }, embeds = Array.Empty<object>(), attachments = Array.Empty<object>() };

    private object Live(string id) => new
    {
        id,
        content = "",
        author = new { id = "host-bot", bot = true },
        embeds = new[] { new { title = TournamentSession.LiveTitlePrefix + "LJ", description = "```json\n{\"v\":1,\"lobby\":\"LJ\",\"phase\":\"Tasks\",\"t\":" + _sent + "}\n```" } },
        attachments = Array.Empty<object>(),
    };

    private HttpResponseMessage Handle(HttpRequestMessage r)
    {
        string path = r.RequestUri!.AbsolutePath;
        string query = r.RequestUri.Query;
        if (path.EndsWith("/channels/results/messages/42"))
        {
            Interlocked.Increment(ref _ownReads);
            return FakeHttp.Json(HttpStatusCode.OK, JsonSerializer.Serialize(Live("42")));
        }
        if (path.EndsWith("/channels/results/messages"))
        {
            // The newest 100 are chat; the lobby's live message (42) is on the page before them.
            var page = query.Contains("before=")
                ? new object[] { Live("42") }
                : Enumerable.Range(0, 100).Select(Plain).ToArray();
            return FakeHttp.Json(HttpStatusCode.OK, JsonSerializer.Serialize(page));
        }
        return FakeHttp.Json(HttpStatusCode.OK, "{}");
    }

    [Fact]
    public async Task A_lobby_further_up_than_the_newest_100_messages_is_still_followed()
    {
        var admin = new SetupCode { Mode = "admin", TournamentId = "fall-cup", TournamentName = "Fall Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results", AdvanceCount = 5, GamesPerRound = 3 };
        Assert.True(SetupCode.TryParse(admin.Encode(), out var read, out var error), error);
        var http = new FakeHttp { Default = Handle };
        var o = new Organizer(read, new HttpClient(http), () => _clock.Now, start: false);
        _owned.Add(o);

        await o.PollLiveAsync();                                      // the first read looks a little further back
        var lobby = Assert.Single(o.LiveLobbies());
        Assert.Equal("LJ", lobby.Label);
        long first = _sent;
        Assert.Equal(first, lobby.Sent);

        _sent = first + 10;                                           // the host edits its message in place
        await o.PollLiveAsync();
        Assert.Equal(0, _ownReads);                                   // not on every poll
        _clock.Advance(7);
        await o.PollLiveAsync();
        Assert.Equal(1, _ownReads);
        Assert.Equal(first + 10, Assert.Single(o.LiveLobbies()).Sent);
        Assert.Null(o.Problem);
    }
}

public class ButtonFileTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void The_Buttons_address_file_is_read_again_only_when_it_changes()
    {
        ButtonBridge.Write(_dir.Path, 1111, "token-a");
        Assert.Equal(1111, ButtonBridge.Read(_dir.Path)!.Port);
        Assert.Same(ButtonBridge.Read(_dir.Path), ButtonBridge.Read(_dir.Path));   // not read again

        ButtonBridge.Write(_dir.Path, 2222, "token-a");                            // same size, rewritten
        Assert.Equal(2222, ButtonBridge.Read(_dir.Path)!.Port);

        // Another program (a new Button) rewrites it.
        string file = Path.Combine(_dir.Path, ButtonBridge.FileName);
        File.WriteAllText(file, JsonSerializer.Serialize(new { port = 3333, token = "token-b" }));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(("token-b", 3333), (ButtonBridge.Read(_dir.Path)!.Token, ButtonBridge.Read(_dir.Path)!.Port));

        File.Delete(file);
        Assert.Null(ButtonBridge.Read(_dir.Path));
    }
}

/// <summary>A handler that fails the way a network does.</summary>
public sealed class FailingHttp : HttpMessageHandler
{
    public Func<Exception> Error { get; set; } = () => new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));
    public int Count;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Count);
        throw Error();
    }
}

/// <summary>The Button never leaves players muted: not when it closes, crashes, or the game does.</summary>
public class CrashSafeMuteTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeHttp _discord = new();
    private readonly ConcurrentQueue<FakeBot> _bots = new();
    private readonly string _modDir;
    private readonly List<IDisposable> _owned = new();

    public CrashSafeMuteTests()
    {
        _modDir = Path.Combine(_dir.Path, "BepInEx", "config", "TournamentTracker");
        Directory.CreateDirectory(_modDir);
    }

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _dir.Dispose();
    }

    private string MutedFile => Path.Combine(_modDir, DiscordBridge.MutedFileName);

    private async Task<DiscordBridge> Bridge(HttpMessageHandler? discord = null, TimeSpan? releaseAfter = null)
    {
        var bridge = new DiscordBridge(() => new BotConfig("bot-token", "100", false), () => _modDir, new HttpClient(discord ?? _discord),
            c => { var b = new FakeBot(c); _bots.Enqueue(b); return b; })
        {
            Wait = TimeSpan.FromSeconds(1),
            ReleaseAfter = releaseAfter ?? TimeSpan.FromMinutes(5),
        };
        _owned.Add(bridge);
        await Wait.Until(() => Task.FromResult(ButtonBridge.Read(_modDir) != null));
        return bridge;
    }

    private DiscordRest Rest() => new(ButtonBridge.CreateClient(_modDir), NullLog.Instance, delay: (_, _) => Task.CompletedTask);

    private IEnumerable<Sent> Unmutes(string user) =>
        _discord.Requests.Where(r => r.Method.Method == "PATCH" && r.Url.EndsWith("/members/" + user) && r.Body.Contains("\"mute\":false") && r.Body.Contains("\"deaf\":false"));

    private void LeftOver(params string[] users) =>
        File.WriteAllText(MutedFile, JsonSerializer.Serialize(new { muted = users.Select(u => new { guild = "100", user = u }) }));

    private static Dictionary<string, string> Headers(DiscordBridge bridge, string? target = null)
    {
        var h = new Dictionary<string, string> { ["x-tt-token"] = bridge.Token };
        if (target != null) h["x-tt-target"] = target;
        return h;
    }

    private static Task<DiscordBridge.Answer> Poll(DiscordBridge bridge, string epoch) =>
        bridge.RouteAsync("POST", "/gateway", Headers(bridge), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token = "bot-token", guild = "100", since = 0, epoch })));

    [Fact]
    public async Task Who_The_Button_mutes_is_kept_on_disk_without_the_bot_token()
    {
        await Bridge();
        await Rest().SetVoiceStateAsync("bot-token", "100", "200", mute: true, deaf: false);
        string text = File.ReadAllText(MutedFile);
        Assert.Contains("\"200\"", text);
        Assert.DoesNotContain("bot-token", text);

        await Rest().SetVoiceStateAsync("bot-token", "100", "200", mute: false, deaf: false);
        Assert.False(File.Exists(MutedFile));                         // nobody left muted: no file
    }

    [Fact]
    public async Task After_a_crash_the_next_start_unmutes_them_when_no_game_comes_back()
    {
        LeftOver("200", "201");
        var bridge = await Bridge(releaseAfter: TimeSpan.FromMilliseconds(200));
        await Task.Delay(300);
        bridge.CheckReleases();
        await Wait.Until(() => Unmutes("200").Any() && Unmutes("201").Any() && !File.Exists(MutedFile));
        Assert.All(_discord.Requests, r => Assert.Equal("Bot bot-token", r.Auth));
    }

    [Fact]
    public async Task A_game_started_afresh_after_a_crash_lets_them_go_at_once()
    {
        LeftOver("200");
        var bridge = await Bridge();
        await Poll(bridge, epoch: "");                                // a new game: it never knew them
        await Wait.Until(() => Unmutes("200").Any());
    }

    [Fact]
    public async Task A_game_still_running_from_before_takes_them_over_and_they_are_let_go_when_it_goes_quiet()
    {
        LeftOver("200");
        var bridge = await Bridge();
        await Poll(bridge, epoch: "the-old-buttons");                 // it sends everyone's state again itself
        bridge.CheckReleases();
        await Task.Delay(300);
        Assert.Empty(Unmutes("200"));
        Assert.True(File.Exists(MutedFile));

        bridge.ReleaseAfter = TimeSpan.FromMilliseconds(100);         // then Among Us crashes
        await Task.Delay(200);
        bridge.CheckReleases();
        await Wait.Until(() => Unmutes("200").Any());
    }

    [Fact]
    public async Task Closing_waits_out_Discords_rate_limit_instead_of_leaving_people_muted()
    {
        var bridge = await Bridge();
        foreach (var user in new[] { "200", "201", "202" })
            await Rest().SetVoiceStateAsync("bot-token", "100", user, mute: true, deaf: true);
        _discord.Responses.Enqueue(_ => FakeHttp.Json((HttpStatusCode)429, "{\"retry_after\":0.05}"));
        bridge.Dispose();
        foreach (var user in new[] { "200", "201", "202" }) Assert.NotEmpty(Unmutes(user));
        Assert.Equal(4, new[] { "200", "201", "202" }.Sum(u => Unmutes(u).Count()));   // the one turned away, sent again
        Assert.False(File.Exists(MutedFile));
    }

    [Fact]
    public async Task The_mods_requests_say_whether_Discord_could_have_got_them()
    {
        var failing = new FailingHttp();
        var bridge = await Bridge(failing);
        var refused = await bridge.RouteAsync("POST", "/discord", Headers(bridge, "https://discord.com/api/webhooks/1/abc"), Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(503, refused.Status);
        Assert.Contains(refused.Headers, h => h.Item1 == DiscordRest.NotSentHeader);

        failing.Error = () => new TaskCanceledException("timed out");
        var timedOut = await bridge.RouteAsync("POST", "/discord", Headers(bridge, "https://discord.com/api/webhooks/1/abc"), Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(504, timedOut.Status);
        Assert.DoesNotContain(timedOut.Headers, h => h.Item1 == DiscordRest.NotSentHeader);

        // The mod's report: not sent again after the timeout, so it can't show twice.
        int before = failing.Count;
        var report = await Rest().ExecuteWebhookAsync("https://discord.com/api/webhooks/1/abc", new WebhookMessage { Content = "Game 1" });
        Assert.False(report.Ok);
        Assert.Equal(before + 1, failing.Count);
    }

    [Fact]
    public async Task A_burst_of_news_goes_back_to_the_mod_as_one_answer()
    {
        var bridge = await Bridge();
        var first = JsonDocument.Parse((await Poll(bridge, epoch: "")).Body).RootElement;
        string epoch = first.GetProperty("epoch").GetString()!;
        await Wait.Until(() => _bots.Count > 0);
        var bot = _bots.Last();
        bot.Fire("READY", "{\"user\":{\"id\":\"9\"},\"application\":{\"id\":\"8\"}}");
        var caught = JsonDocument.Parse((await Poll(bridge, epoch)).Body).RootElement;
        long last = caught.GetProperty("last").GetInt64();

        var waiting = bridge.RouteAsync("POST", "/gateway", Headers(bridge),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token = "bot-token", guild = "100", since = last, epoch })));
        await Task.Delay(150);
        bot.Fire("VOICE_STATE_UPDATE", "{\"guild_id\":\"100\",\"user_id\":\"1\",\"channel_id\":\"vc\"}");
        await Task.Delay(20);
        bot.Fire("VOICE_STATE_UPDATE", "{\"guild_id\":\"100\",\"user_id\":\"2\",\"channel_id\":\"vc\"}");
        var answer = JsonDocument.Parse((await waiting).Body).RootElement;
        Assert.Equal(2, answer.GetProperty("events").GetArrayLength());
    }
}

/// <summary>A game that closes a round reads the results channel once, not once per post.</summary>
public class StandingsReadTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeDiscord _discord = new();
    private readonly FakeHttp _http = new();
    private readonly FakeClock _clock = new();
    private TournamentSession? _session;

    public StandingsReadTests() => _http.Default = _discord.Handle;

    public void Dispose()
    {
        _session?.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task The_round_summary_uses_the_read_the_lobby_standings_just_made()
    {
        var code = new SetupCode
        {
            Mode = "tournament", TournamentId = "fall-cup", TournamentName = "Fall Cup", Webhook = "https://discord.test/api/webhooks/1/abc",
            GuildId = "g1", BotTokens = new() { "tok" }, ResultsChannelId = "results", AdvanceCount = 5, GamesPerRound = 1, VerifyResults = false,
        };
        var s = _session = new TournamentSession(new TrackerSettings { LiveStatus = false, ControlPort = -1, LobbyLabel = "LJ", PublishLive = false }, _dir.Path, NullLog.Instance,
            new HttpClient(_http), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"), code);
        s.SetRound(1);
        await s.PendingPosts;
        int Reads() => _http.Requests.Count(r => r.Method == HttpMethod.Get && r.Url.Contains("/channels/results/messages"));
        int before = Reads();

        var lobby = Players.Lobby();
        s.GameStarted("ABCDEF", "Polus", lobby);
        s.GameEnded("HumansByTask", lobby);                           // the round's only game: the round closes
        await s.PendingPosts;

        Assert.Equal(1, Reads() - before);
        Assert.Contains(_http.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/channels/results/messages") && r.Body.Contains("round 1", StringComparison.OrdinalIgnoreCase) && r.Body.Contains("embeds"));
    }
}
