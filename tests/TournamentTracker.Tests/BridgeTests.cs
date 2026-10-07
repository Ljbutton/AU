using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.Discord;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>A bot connection that never leaves the computer: the test says what Discord sent.</summary>
public sealed class FakeBot : IBotGateway
{
    public FakeBot(BotConfig config) { Config = config; State = new VoicePresenceState(config.GuildId); }
    public BotConfig Config { get; }
    public VoicePresenceState State { get; }
    public event Action<string, JsonElement>? Dispatched;
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public void Start() => Started = true;
    public void Fire(string type, string json)
    {
        var d = JsonDocument.Parse(json).RootElement.Clone();
        State.Dispatch(type, d);
        if (type == "READY") State.Connected = true;
        Dispatched?.Invoke(type, d);
    }
    public void Dispose() => Disposed = true;
}

public class BridgeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeHttp _discord = new();
    private readonly ConcurrentQueue<FakeBot> _bots = new();
    private readonly string _modDir;
    private BotConfig? _setup = new("bot-token", "100", true);

    public BridgeTests()
    {
        _modDir = Path.Combine(_dir.Path, "BepInEx", "config", "TournamentTracker");
        Directory.CreateDirectory(Path.GetDirectoryName(_modDir)!);
    }

    public void Dispose() => _dir.Dispose();

    private async Task<DiscordBridge> Bridge()
    {
        var bridge = new DiscordBridge(() => _setup, () => _modDir, new HttpClient(_discord), c => { var b = new FakeBot(c); _bots.Enqueue(b); return b; }) { Wait = TimeSpan.FromSeconds(1) };
        await Wait.Until(() => Task.FromResult(ButtonBridge.Read(_modDir) != null));
        return bridge;
    }

    private FakeBot Bot => _bots.Last();

    private DiscordRest Rest() => new(ButtonBridge.CreateClient(_modDir), NullLog.Instance, delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task The_mod_reaches_Discord_only_through_The_Button()
    {
        using (var bridge = await Bridge())
        {
            var result = await Rest().SetVoiceStateAsync("bot-token", "100", "200", mute: true, deaf: false);
            Assert.True(result.Ok);
            var sent = Assert.Single(_discord.Requests);
            Assert.Equal("PATCH", sent.Method.Method);
            Assert.Equal("https://discord.com/api/v10/guilds/100/members/200", sent.Url);
            Assert.Equal("Bot bot-token", sent.Auth);
            Assert.Contains("\"mute\":true", sent.Body);
            Assert.Equal(ButtonBridge.Read(_modDir)!.Port, bridge.Port);
        }
        // Closed: its file is gone and nothing reaches Discord. The answer comes at once, not after retries.
        Assert.Null(ButtonBridge.Read(_modDir));
        int before = _discord.Requests.Count(r => r.Method.Method == "POST");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var off = await Rest().PostMessageAsync("bot-token", "300", "hello");
        Assert.False(off.Ok);
        Assert.Equal(503, off.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(before, _discord.Requests.Count(r => r.Method.Method == "POST"));
    }

    [Fact]
    public async Task A_left_over_file_from_a_crashed_Button_counts_as_closed()
    {
        int port;
        using (var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        ButtonBridge.Write(_modDir, port, "stale");
        var off = await Rest().PostMessageAsync("bot-token", "300", "hello");
        Assert.Equal(503, off.Status);
        Assert.Empty(_discord.Requests);
    }

    [Fact]
    public async Task Rate_limits_and_files_come_through_as_Discord_sent_them()
    {
        using var bridge = await Bridge();
        _discord.Responses.Enqueue(_ => FakeHttp.Json((HttpStatusCode)429, "{\"retry_after\":0.01}", ("Retry-After", "0")));
        var file = Encoding.UTF8.GetBytes("{\"game\":\"Alice ✓\"}");
        var result = await Rest().PostFileAsync("bot-token", "300", "Game 1", "game-1.json", file);
        Assert.True(result.Ok);
        Assert.Equal(2, _discord.Requests.Count);                       // the 429 was seen by the mod and retried
        Assert.All(_discord.Requests, r => Assert.Contains("Alice ✓", r.Body));
    }

    [Fact]
    public async Task Only_Discord_and_only_with_the_token()
    {
        using var bridge = await Bridge();
        var headers = new Dictionary<string, string> { ["x-tt-token"] = bridge.Token, ["x-tt-target"] = "https://example.com/steal" };
        var a = await bridge.RouteAsync("GET", "/discord", headers, Array.Empty<byte>());
        Assert.Equal(400, a.Status);
        headers["x-tt-token"] = "wrong";
        headers["x-tt-target"] = "https://discord.com/api/v10/users/@me";
        Assert.Equal(403, (await bridge.RouteAsync("GET", "/discord", headers, Array.Empty<byte>())).Status);
        Assert.Empty(_discord.Requests);
    }

    [Fact]
    public async Task The_bot_is_online_before_Among_Us_and_the_mod_catches_up()
    {
        using var bridge = await Bridge();
        // The Button connects the setup code's bot by itself.
        await Wait.Until(() => Task.FromResult(_bots.Count == 1));
        Assert.True(Bot.Started);
        Assert.Equal(_setup, Bot.Config);
        Bot.Fire("READY", "{\"user\":{\"id\":\"900\",\"username\":\"tt-bot\",\"global_name\":\"TT Bot\"},\"application\":{\"id\":\"901\"}}");
        Bot.Fire("GUILD_CREATE", "{\"id\":\"100\",\"voice_states\":[{\"user_id\":\"200\",\"channel_id\":\"50\"}],\"members\":[{\"nick\":\"Ally\",\"user\":{\"id\":\"200\",\"username\":\"alice\"}}]}");
        var status = JsonSerializer.SerializeToElement(bridge.Status());
        Assert.True(status.GetProperty("Connected").GetBoolean());
        Assert.Equal("TT Bot", status.GetProperty("Name").GetString());
        Assert.False(status.GetProperty("Game").GetBoolean());

        // Among Us starts: the mod hears who's in voice and that the bot is ready (for its slash commands).
        int ready = 0;
        var messages = new ConcurrentQueue<ChannelMessage>();
        using var mod = new ButtonGateway(_modDir, "bot-token", "100", true, NullLog.Instance);
        mod.State.Ready += () => ready++;
        mod.State.MessageCreated += messages.Enqueue;
        mod.Start();
        await Wait.Until(() => Task.FromResult(mod.Connected && mod.Members.Count == 1));
        Assert.True(mod.ButtonOpen);
        Assert.Equal("Ally", mod.Members[0].DisplayName);
        Assert.Equal("901", mod.State.ApplicationId);
        Assert.Equal(1, ready);
        Assert.Single(_bots);                                           // same bot: no reconnect

        // Live events arrive while the mod waits.
        Bot.Fire("VOICE_STATE_UPDATE", "{\"guild_id\":\"100\",\"user_id\":\"201\",\"channel_id\":\"50\",\"member\":{\"user\":{\"id\":\"201\",\"username\":\"bob\"}}}");
        Bot.Fire("MESSAGE_CREATE", "{\"id\":\"1\",\"channel_id\":\"300\",\"content\":\"!lobbies\",\"author\":{\"id\":\"5\"}}");
        await Wait.Until(() => Task.FromResult(mod.Members.Count == 2 && messages.Count == 1));
        Assert.Equal("!lobbies", messages.Single().Content);
        Assert.True(JsonSerializer.SerializeToElement(bridge.Status()).GetProperty("Game").GetBoolean());
        Assert.Equal(1, ready);
    }

    [Fact]
    public async Task A_settings_file_bot_is_connected_when_the_game_asks_for_it()
    {
        _setup = null;                                                  // no setup code with a bot
        using var bridge = await Bridge();
        bridge.Reconcile();
        Assert.Empty(_bots);
        using var mod = new ButtonGateway(_modDir, "file-token", "777", false, NullLog.Instance);
        mod.Start();
        await Wait.Until(() => Task.FromResult(_bots.Count == 1));
        Assert.Equal(new BotConfig("file-token", "777", false), Bot.Config);
        Bot.Fire("READY", "{\"user\":{\"id\":\"9\"},\"application\":{\"id\":\"8\"}}");
        await Wait.Until(() => Task.FromResult(mod.Connected));
    }

    [Fact]
    public async Task Closing_The_Button_unmutes_whoever_it_muted()
    {
        var bridge = await Bridge();
        await Rest().SetVoiceStateAsync("bot-token", "100", "200", mute: true, deaf: true);
        await Rest().SetVoiceStateAsync("bot-token", "100", "201", mute: true, deaf: false);
        await Rest().SetVoiceStateAsync("bot-token", "100", "201", mute: false, deaf: false);   // already let go
        bridge.Dispose();
        var last = _discord.Requests.Last();
        Assert.Equal(4, _discord.Requests.Count);
        Assert.Equal("https://discord.com/api/v10/guilds/100/members/200", last.Url);
        Assert.Contains("\"mute\":false", last.Body);
        Assert.Contains("\"deaf\":false", last.Body);
        Assert.Equal("Bot bot-token", last.Auth);
    }

    [Fact]
    public void A_game_without_The_Button_plays_normally_and_tells_the_host()
    {
        var settings = new TrackerSettings { LiveStatus = false, ControlPort = -1, TournamentName = "Cup", Mode = TrackerMode.Tournament };
        settings.AutoMute.Enabled = true;
        settings.AutoMute.BotTokens = new List<string> { "bot-token" };
        settings.AutoMute.GuildId = "100";
        using var s = new TournamentSession(settings, _modDir, NullLog.Instance, voiceApi: new FakeVoiceApi());
        Assert.False(s.ButtonOpen);
        s.GameStarted("ABCDEF", "Skeld", Players.Lobby());
        var replies = s.Pump();
        Assert.Contains(replies, r => r.Text.Contains("The Button isn't open") && !r.Public);
        Assert.Empty(_discord.Requests);
    }

    [Fact]
    public async Task A_game_with_The_Button_open_posts_its_results_through_it()
    {
        using var bridge = await Bridge();
        var settings = new TrackerSettings { LiveStatus = false, ControlPort = -1, TournamentName = "Cup", StatsWebhookUrl = "https://discord.com/api/webhooks/1/abc" };
        using var s = new TournamentSession(settings, _modDir, NullLog.Instance);
        Assert.True(s.ButtonOpen);
        s.GameStarted("ABCDEF", "Skeld", Players.Lobby());
        Assert.DoesNotContain(s.Pump(), r => r.Text.Contains("The Button"));
        s.GameEnded("HumansByTask", Players.Lobby());
        await s.PendingPosts;
        Assert.Contains(_discord.Requests, r => r.Method == HttpMethod.Post && r.Url.StartsWith("https://discord.com/api/webhooks/1/abc") && r.Body.Contains("embeds"));
    }

    [Fact]
    public void The_bot_comes_from_a_host_setup_code_only()
    {
        Assert.Null(DiscordBridge.FromSetupCode(null));
        Assert.Null(DiscordBridge.FromSetupCode("not a code"));
    }
}
