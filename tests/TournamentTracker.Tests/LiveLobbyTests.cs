using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TournamentTracker.Discord;
using TournamentTracker.Stats;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

public class VoicePresenceTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Guild_create_and_voice_updates_track_who_is_in_voice()
    {
        var state = new VoicePresenceState("g1");
        state.Dispatch("READY", J("""{"user":{"id":"bot1"}}"""));
        state.Dispatch("GUILD_CREATE", J("""
            {"id":"g1",
             "voice_states":[{"user_id":"1","channel_id":"vc"},{"user_id":"2","channel_id":"vc"},{"user_id":"bot1","channel_id":"vc"}],
             "members":[{"user":{"id":"1","username":"alice","global_name":"Alice A"},"nick":"Ali"},
                        {"user":{"id":"2","username":"bob"}},
                        {"user":{"id":"bot1","username":"mutebot","bot":true}}]}
            """));
        Assert.Equal("bot1", state.BotUserId);
        var members = state.Members.ToDictionary(m => m.UserId);
        Assert.Equal(3, members.Count);
        Assert.Equal("Ali", members["1"].DisplayName);
        Assert.Equal(new[] { "Ali", "Alice A", "alice" }, members["1"].Names);
        Assert.True(members["bot1"].IsBot);

        state.Dispatch("VOICE_STATE_UPDATE", J("""{"guild_id":"g1","user_id":"3","channel_id":"vc2","member":{"user":{"id":"3","username":"cara"}}}"""));
        state.Dispatch("VOICE_STATE_UPDATE", J("""{"guild_id":"g1","user_id":"2","channel_id":null}"""));
        state.Dispatch("VOICE_STATE_UPDATE", J("""{"guild_id":"other","user_id":"9","channel_id":"x"}"""));
        state.Dispatch("VOICE_STATE_UPDATE", J("""{"guild_id":"g1","user_id":"1","channel_id":"vc2"}"""));   // moved, no member object

        members = state.Members.ToDictionary(m => m.UserId);
        Assert.Equal(new[] { "1", "3", "bot1" }, members.Keys.OrderBy(k => k));
        Assert.Equal("vc2", members["1"].ChannelId);
        Assert.Equal("Ali", members["1"].DisplayName);        // name kept from before
    }

    [Fact]
    public async Task Gateway_identifies_heartbeats_and_reads_voice_states()
    {
        // A tiny local stand-in for Discord's gateway.
        int port = Random.Shared.Next(20000, 40000);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        JsonElement identify = default;
        bool heartbeat = false;

        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            async Task Send(string s) => await ws.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text, true, default);
            async Task<JsonElement> Receive()
            {
                var buf = new byte[8192];
                var r = await ws.ReceiveAsync(buf, default);
                return JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, r.Count)).RootElement;
            }
            await Send("""{"op":10,"d":{"heartbeat_interval":200}}""");
            bool identified = false;
            while (!(identified && heartbeat))
            {
                var msg = await Receive();
                int op = msg.GetProperty("op").GetInt32();
                if (op == 2)
                {
                    identify = msg.Clone();
                    identified = true;
                    await Send("""{"op":0,"s":1,"t":"READY","d":{"user":{"id":"bot1"}}}""");
                    await Send("""{"op":0,"s":2,"t":"GUILD_CREATE","d":{"id":"g1","voice_states":[{"user_id":"1","channel_id":"vc"}],"members":[{"user":{"id":"1","username":"alice"}}]}}""");
                }
                else if (op == 1)
                {
                    heartbeat = true;
                    await Send("""{"op":11}""");
                }
            }
        });

        using var gateway = new VoiceGateway("tok", "g1", NullLog.Instance, $"ws://127.0.0.1:{port}/");
        gateway.Start();
        await Wait.Until(() => gateway.Connected && gateway.Members.Count == 1 && heartbeat, 5000);

        Assert.Equal("tok", identify.GetProperty("d").GetProperty("token").GetString());
        Assert.Equal(129, identify.GetProperty("d").GetProperty("intents").GetInt32());   // GUILDS | GUILD_VOICE_STATES
        Assert.Equal("alice", gateway.Members[0].Username);
        await server;
    }
}

public class GatewayProblemTests
{
    [Fact]
    public async Task A_refused_bot_token_is_reported_for_the_app()
    {
        int port = Random.Shared.Next(20000, 40000);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            var ws = (await (await listener.GetContextAsync()).AcceptWebSocketAsync(null)).WebSocket;
            await ws.SendAsync(Encoding.UTF8.GetBytes("""{"op":10,"d":{"heartbeat_interval":5000}}"""), WebSocketMessageType.Text, true, default);
            await ws.ReceiveAsync(new byte[8192], default);                          // identify
            await ws.CloseOutputAsync((WebSocketCloseStatus)4004, "Authentication failed.", default);
        });

        using var gateway = new VoiceGateway("bad", "g1", NullLog.Instance, $"ws://127.0.0.1:{port}/");
        gateway.Start();
        await Wait.Until(() => gateway.State.Problem != null, 5000);
        Assert.StartsWith("Discord turned down the bot's token", gateway.State.Problem);
        Assert.False(gateway.Connected);
        await server;
    }
}

public class LiveLobbyTests : IDisposable
{
    private const string Webhook = "https://discord.test/api/webhooks/1/abc";
    private readonly TempDir _dir = new();
    private readonly FakeHttp _http = new();
    private readonly FakeVoiceApi _voice = new();
    private readonly FakeClock _clock = new();
    private readonly VoicePresenceState _presence = new("g1") { Connected = true };
    private readonly List<PlayerSnapshot> _lobby = Players.Lobby();
    private TournamentSession? _session;

    public void Dispose()
    {
        _session?.Dispose();
        _dir.Dispose();
    }

    private TournamentSession Session(Action<TrackerSettings>? configure = null)
    {
        var settings = new TrackerSettings { TournamentName = "Cup", StatsWebhookUrl = Webhook, LiveStatus = false, PublicChat = true, ControlPort = -1, PostLeaderboardAfterEachGame = false };
        settings.AutoMute.Enabled = true;
        settings.AutoMute.GuildId = "g1";
        settings.AutoMute.BotTokens.Add("tok");
        configure?.Invoke(settings);
        _session = new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(_http), () => _clock.Now, _voice, _presence);
        return _session;
    }

    private void InVoice(string channel, params (string Id, string Name)[] people)
    {
        var states = string.Join(",", people.Select(p => "{\"user_id\":\"" + p.Id + "\",\"channel_id\":\"" + channel + "\"}"));
        var members = string.Join(",", people.Select(p => "{\"user\":{\"id\":\"" + p.Id + "\",\"username\":\"" + p.Name + "\"}}"));
        _presence.Dispatch("GUILD_CREATE", JsonDocument.Parse($$"""{"id":"g1","voice_states":[{{states}}],"members":[{{members}}]}""").RootElement);
    }

    // ---- Auto-link ----

    [Fact]
    public void Players_are_linked_when_their_name_matches_someone_in_voice()
    {
        var s = Session();
        InVoice("vc", ("100", "alice"), ("102", "Carl"), ("103", "dana_x"), ("104", "dana.x"));
        s.VoiceTick(VoicePhase.Lobby, _lobby);

        Assert.Equal("100", s.Links.Find(_lobby[0].Key)?.DiscordUserId);
        Assert.Equal("102", s.Links.Find(_lobby[2].Key)?.DiscordUserId);
        Assert.Null(s.Links.Find(_lobby[3].Key));             // "Dana" matches neither exactly
        var replies = s.Pump();
        Assert.Contains(replies, r => r.Public && r.Text == "Auto-linked Red (Alice) to @alice. Wrong? Use /unlink in Discord.");
    }

    [Fact]
    public void Ambiguous_or_taken_names_are_not_auto_linked()
    {
        var s = Session();
        s.Links.Link("someone-else#1", "Other", "201", "bob");
        InVoice("vc", ("200", "Alice"), ("201", "bob"), ("202", "a.l.i.c.e"));
        s.VoiceTick(VoicePhase.Lobby, _lobby);
        Assert.Null(s.Links.Find(_lobby[0].Key));             // two people normalise to "alice"
        Assert.Null(s.Links.Find(_lobby[1].Key));             // bob is already someone else's link
    }

    [Fact]
    public void Unlinking_stops_auto_link_from_linking_them_again()
    {
        var s = Session();
        InVoice("vc", ("100", "alice"));
        s.VoiceTick(VoicePhase.Lobby, _lobby);
        Assert.NotNull(s.Links.Find(_lobby[0].Key));

        s.HandleSlashCommand(Slash("unlink", "100", "alice"));
        _clock.Advance(5);
        s.VoiceTick(VoicePhase.Lobby, _lobby);
        Assert.Null(s.Links.Find(_lobby[0].Key));
    }

    [Fact]
    public void Auto_link_can_be_switched_off_and_only_runs_in_the_lobby()
    {
        var s = Session(c => c.AutoMute.AutoLinkByName = false);
        InVoice("vc", ("100", "alice"));
        s.VoiceTick(VoicePhase.Lobby, _lobby);
        Assert.Null(s.Links.Find(_lobby[0].Key));

        var t = Session();
        t.VoiceTick(VoicePhase.Tasks, _lobby);
        Assert.Null(t.Links.Find(_lobby[0].Key));
    }

    [Theory]
    [InlineData("Cool Bean!", "coolbean")]
    [InlineData("ÉLAN_99", "élan99")]
    public void Names_are_compared_ignoring_case_spaces_and_symbols(string raw, string normal) =>
        Assert.Equal(normal, TournamentSession.NormalizeName(raw));

    // ---- /link and /unlink in Discord ----

    private static Interaction Slash(string command, string userId, string name, string? player = null, string? user = null, bool staff = false)
    {
        var i = new Interaction { Id = "i1", Token = "t1", GuildId = "g1", Command = command, UserId = userId, UserName = name, Permissions = staff ? Interaction.MuteMembers : 0 };
        if (player != null) i.Options["player"] = player;
        if (user != null) { i.Options["user"] = user; i.ResolvedNames[user] = "Target"; }
        return i;
    }

    private TournamentSession SlashSession()
    {
        var s = Session(c => { c.AutoMute.AutoLinkByName = false; c.AutoMute.VoiceChannelId = "vc"; });
        InVoice("vc", ("100", "someone"), ("101", "other"));
        s.VoiceTick(VoicePhase.Lobby, _lobby);
        return s;
    }

    [Fact]
    public void Link_by_colour_or_name_from_the_lobby_voice_channel()
    {
        var s = SlashSession();
        Assert.StartsWith("Linked you to Pink (Dana)", s.HandleSlashCommand(Slash("link", "100", "someone", "pink")));
        Assert.Equal("100", s.Links.Find(_lobby[3].Key)?.DiscordUserId);
        Assert.StartsWith("Linked you to Blue (Bob)", s.HandleSlashCommand(Slash("link", "101", "other", "bob")));
        Assert.Contains("(replacing @someone)", s.HandleSlashCommand(Slash("link", "101", "other", "pink")));
        Assert.Equal("101", s.Links.Find(_lobby[3].Key)?.DiscordUserId);
        Assert.StartsWith("Linked you to Pink (Dana)", s.HandleSlashCommand(Slash("link", "100", "someone", "pink")));
        Assert.Contains("No one in", s.HandleSlashCommand(Slash("link", "100", "someone", "Zed")));

        Assert.Equal("Unlinked you from Dana.", s.HandleSlashCommand(Slash("unlink", "100", "someone")));
        Assert.Null(s.Links.Find(_lobby[3].Key));
    }

    [Fact]
    public void Other_lobbies_stay_quiet_unless_the_name_is_theirs()
    {
        var s = SlashSession();
        // Not in this lobby's voice channel: a colour could be anyone's, so another lobby answers.
        Assert.Null(s.HandleSlashCommand(Slash("link", "900", "elsewhere", "red")));
        Assert.Null(s.HandleSlashCommand(Slash("link", "900", "elsewhere", "Zed")));
        Assert.Null(s.HandleSlashCommand(Slash("unlink", "900", "elsewhere")));
        // A name that's in this lobby is enough.
        Assert.StartsWith("Linked you to Green (Carl)", s.HandleSlashCommand(Slash("link", "900", "elsewhere", "Carl")));
    }

    [Fact]
    public void Only_referees_link_someone_else()
    {
        var s = SlashSession();
        Assert.StartsWith("Only referees", s.HandleSlashCommand(Slash("link", "100", "someone", "eve", user: "555")));
        Assert.Null(s.Links.Find(_lobby[4].Key));
        Assert.StartsWith("Linked @Target to Orange (Eve)", s.HandleSlashCommand(Slash("link", "100", "someone", "eve", user: "555", staff: true)));
        Assert.Equal("555", s.Links.Find(_lobby[4].Key)?.DiscordUserId);
        Assert.Equal("Unlinked @Target from Eve.", s.HandleSlashCommand(Slash("unlink", "100", "someone", user: "555", staff: true)));
    }

    [Fact]
    public void Links_are_announced_in_the_lobby_chat_even_with_public_chat_off()
    {
        var s = Session(c => { c.PublicChat = false; c.AutoMute.VoiceChannelId = "vc"; });
        InVoice("vc", ("100", "alice"), ("101", "someone"));
        s.VoiceTick(VoicePhase.Lobby, _lobby);
        Assert.Contains(s.Pump(), r => r.Public && r.Text.StartsWith("Auto-linked Red (Alice) to @alice"));

        s.HandleSlashCommand(Slash("link", "101", "someone", "bob"));
        Assert.Contains(s.Pump(), r => r.Public && r.Text == "Linked Blue (Bob) to @someone.");

        var quiet = Session(c => { c.PublicChat = false; c.AnnounceLinks = false; });
        InVoice("vc", ("100", "alice"));
        quiet.VoiceTick(VoicePhase.Lobby, _lobby);
        Assert.DoesNotContain(quiet.Pump(), r => r.Public);
    }

    [Fact]
    public void Nobody_answering_gets_a_reason_instead_of_silence()
    {
        Assert.Contains("voice channel", TournamentSession.SlashFallback(Slash("link", "1", "x", "red")));
        Assert.Contains("join the lobby first", TournamentSession.SlashFallback(Slash("link", "1", "x", "Zed")));
        Assert.Contains("aren't linked", TournamentSession.SlashFallback(Slash("unlink", "1", "x")));
    }

    [Fact]
    public void Slash_commands_are_read_from_the_gateway()
    {
        var state = new VoicePresenceState("g1");
        Interaction? got = null;
        state.InteractionCreated += i => got = i;
        state.Dispatch("READY", JsonDocument.Parse("""{"user":{"id":"bot"},"application":{"id":"app1"}}""").RootElement);
        state.Dispatch("INTERACTION_CREATE", JsonDocument.Parse("""
            {"id":"5","token":"tok","type":2,"guild_id":"g1",
             "member":{"nick":"Dee","permissions":"4194304","user":{"id":"77","username":"dee"}},
             "data":{"name":"link","options":[{"name":"player","type":3,"value":"Red"},{"name":"user","type":6,"value":"88"}],
                     "resolved":{"users":{"88":{"id":"88","username":"eight"}}}}}
            """).RootElement);
        Assert.Equal("app1", state.ApplicationId);
        Assert.NotNull(got);
        Assert.Equal(("link", "77", "Dee", "Red", "88", "eight", true), (got!.Command, got.UserId, got.UserName, got.Option("player"), got.Option("user"), got.ResolvedNames["88"], got.IsStaff));
    }

    // ---- Spectators ----

    [Fact]
    public async Task Spectators_in_the_game_channel_are_muted_during_games()
    {
        var s = Session(c =>
        {
            c.AutoMute.MuteSpectators = true;
            c.AutoMute.AutoLinkByName = false;
            c.AutoMute.DelayGameStart = 0;
        });
        s.Links.Link(_lobby[0].Key, "Alice", "100", "alice");
        s.Links.Link(_lobby[2].Key, "Carl", "102", "carl");
        InVoice("vc", ("100", "alice"), ("102", "carl"), ("900", "caster"), ("901", "viewer"));
        _presence.Dispatch("VOICE_STATE_UPDATE", JsonDocument.Parse("""{"guild_id":"g1","user_id":"950","channel_id":"other-vc","member":{"user":{"id":"950","username":"elsewhere"}}}""").RootElement);

        s.VoiceTick(VoicePhase.Tasks, _lobby);
        await Wait.Until(() => _voice.Calls.Count(c => c.User == "900" || c.User == "901") == 2);
        Assert.Contains(_voice.Calls, c => c.User == "900" && c.State == new VoiceState(true, false));
        Assert.DoesNotContain(_voice.Calls, c => c.User == "950");   // a different channel
        Assert.Contains(_voice.Calls, c => c.User == "100" && c.State == new VoiceState(true, true));

        Assert.True(s.RunCommand("!spectators off"));
        Assert.Contains("OFF", s.Pump().Single().Text);
        s.VoiceTick(VoicePhase.Tasks, _lobby);
        await Wait.Until(() => _voice.Calls.Any(c => c.User == "900" && c.State == VoiceState.Open));
    }

    // ---- Referee mode ----

    [Fact]
    public async Task Referee_mode_mutes_everyone_in_voice_but_the_referees_until_switched_off()
    {
        var s = Session(c =>
        {
            c.AutoMute.AutoLinkByName = false;
            c.AutoMute.RefereeUserIds.Add("800");                  // a co-referee
        });
        s.Links.Link(_lobby[0].Key, "Alice", "100", "alice");      // Alice is the host
        _lobby[0].IsHost = true;
        s.Links.Link(_lobby[2].Key, "Carl", "102", "carl");
        InVoice("vc", ("100", "alice"), ("102", "carl"), ("800", "coref"), ("900", "viewer"));
        s.VoiceTick(VoicePhase.Lobby, _lobby);
        await Wait.Until(() => _voice.Calls.Any(c => c.User == "102"));

        Assert.True(s.RunCommand("!ref on"));
        var replies = s.Pump();
        Assert.Contains(replies, r => !r.Public && r.Text.StartsWith("Referee mode ON"));
        Assert.Contains(replies, r => r.Public && r.Text.Contains("muted for now"));
        s.VoiceTick(VoicePhase.Lobby, _lobby);
        await Wait.Until(() => _voice.Calls.Any(c => c.User == "900" && c.State.Mute) && _voice.Calls.Any(c => c.User == "102" && c.State.Mute));

        Assert.Contains(_voice.Calls, c => c.User == "102" && c.State == new VoiceState(true, false));   // muted, can still hear
        Assert.DoesNotContain(_voice.Calls, c => c.User == "100" && c.State.Mute);                      // the host talks
        Assert.DoesNotContain(_voice.Calls, c => c.User == "800" && c.State.Mute);                      // so does the co-ref

        s.RunCommand("!ref off");
        s.VoiceTick(VoicePhase.Lobby, _lobby);
        await Wait.Until(() => _voice.Calls.LastOrDefault(c => c.User == "900").State == VoiceState.Open
                            && _voice.Calls.LastOrDefault(c => c.User == "102").State == VoiceState.Open);
    }

    [Fact]
    public void Referee_mode_is_host_only_and_ends_when_the_game_starts()
    {
        var s = Session(c => c.AutoMute.AutoLinkByName = false);
        Assert.False(s.AutoMute!.RefereeMode);

        s.RunCommand("!ref on");
        Assert.True(s.AutoMute.RefereeMode);
        s.Pump();
        s.VoiceTick(VoicePhase.Tasks, _lobby);
        Assert.False(s.AutoMute.RefereeMode);
        Assert.Contains(s.Pump(), r => r.Text == "Referee mode ended because the game started.");
    }

    [Fact]
    public void Unmuteall_also_ends_referee_mode()
    {
        var s = Session(c => c.AutoMute.AutoLinkByName = false);
        s.RunCommand("!ref on");
        s.RunCommand("!unmuteall");
        Assert.False(s.AutoMute!.RefereeMode);
    }

    [Fact]
    public void Status_says_when_the_referee_is_speaking()
    {
        var msg = StatusFormatter.Build(new StatusInfo { Phase = VoicePhase.Lobby, RefereeMode = true });
        Assert.Contains("Referee speaking", msg.Embeds![0].Description);
    }

    // ---- Live status ----

    private static string? Title(Sent r) =>
        JsonDocument.Parse(r.Body).RootElement.GetProperty("embeds")[0].GetProperty("title").GetString();

    private static string Description(Sent r) =>
        JsonDocument.Parse(r.Body).RootElement.GetProperty("embeds")[0].GetProperty("description").GetString()!;

    [Fact]
    public async Task With_a_bot_the_status_has_a_colour_menu_that_links_whoever_picks()
    {
        _http.Default = r =>
            r.Method == HttpMethod.Get && r.RequestUri!.AbsoluteUri == Webhook ? FakeHttp.Json(HttpStatusCode.OK, """{"channel_id":"777"}""")
            : r.Method == HttpMethod.Post ? FakeHttp.Json(HttpStatusCode.OK, """{"id":"888"}""")
            : FakeHttp.Json(HttpStatusCode.OK, "{}");
        var s = Session(c => { c.LiveStatus = true; c.AutoMute.AutoLinkByName = false; });
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;

        var post = _http.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("https://discord.com/api/v10/channels/777/messages", post.Url);
        Assert.Equal("Bot tok", post.Auth);
        var menu = JsonDocument.Parse(post.Body).RootElement.GetProperty("components")[0].GetProperty("components")[0];
        Assert.Equal(s.LinkMenuId, menu.GetProperty("custom_id").GetString());
        var options = menu.GetProperty("options");
        Assert.Equal(6, options.GetArrayLength());                                   // only the colours in the lobby
        Assert.Equal("Red", options[0].GetProperty("label").GetString());
        Assert.Equal("Alice", options[0].GetProperty("description").GetString());

        var pick = new Interaction { Command = "menu", CustomId = s.LinkMenuId, UserId = "300", UserName = "carl.au" };
        pick.Values.Add("2");
        Assert.Equal("Linked you to Green (Carl). Automute will follow you from now on.", s.HandleLinkMenu(pick));
        Assert.Equal("300", s.Links.Find(_lobby[2].Key)?.DiscordUserId);
        Assert.Contains(s.Pump(), r => r.Public && r.Text == "Linked Green (Carl) to @carl.au.");

        var steal = new Interaction { Command = "menu", CustomId = s.LinkMenuId, UserId = "301", UserName = "someone" };
        steal.Values.Add("2");
        Assert.Equal("Linked you to Green (Carl) (replacing @carl.au). Automute will follow you from now on.", s.HandleLinkMenu(steal));
        Assert.Equal("301", s.Links.Find(_lobby[2].Key)?.DiscordUserId);          // the newest pick wins
        Assert.Null(s.Links.FindByDiscordId("300"));
        var gone = new Interaction { Command = "menu", CustomId = s.LinkMenuId, UserId = "301", UserName = "someone" };
        gone.Values.Add("12");
        Assert.StartsWith("Nobody is Maroon in the lobby now", s.HandleLinkMenu(gone));
    }

    [Fact]
    public async Task If_the_bot_cannot_post_the_status_goes_back_to_the_webhook_without_the_menu()
    {
        _http.Default = r =>
            r.Method == HttpMethod.Get ? FakeHttp.Json(HttpStatusCode.OK, """{"channel_id":"777"}""")
            : r.RequestUri!.AbsoluteUri.Contains("/channels/") ? FakeHttp.Json(HttpStatusCode.Forbidden, """{"code":50013}""")
            : FakeHttp.Json(HttpStatusCode.OK, """{"id":"555"}""");
        var s = Session(c => { c.LiveStatus = true; c.AutoMute.AutoLinkByName = false; });
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        _clock.Advance(3);
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        var hook = _http.Requests.Last(r => r.Url.StartsWith(Webhook));
        Assert.DoesNotContain("components", hook.Body);
        Assert.Contains("Type /link", hook.Body);
    }

    private static Interaction NewCmd(string userId, string name, string channel, string? code = null, bool staff = false)
    {
        var i = new Interaction { Id = "n", Token = "t", GuildId = "g1", Command = "new", UserId = userId, UserName = name, ChannelId = channel, Permissions = staff ? Interaction.MuteMembers : 0 };
        if (code != null) i.Options["code"] = code;
        return i;
    }

    [Fact]
    public async Task New_moves_the_hosts_live_message_to_that_channel()
    {
        _http.Default = r => r.Method == HttpMethod.Post ? FakeHttp.Json(HttpStatusCode.OK, """{"id":"555"}""") : FakeHttp.Json(HttpStatusCode.OK, "{}");
        // The status webhook's channel can't be looked up, so it starts as a plain webhook message.
        var s = Session(c => { c.LiveStatus = true; c.AutoMute.AutoLinkByName = false; });
        _lobby[0].IsHost = true;
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        Assert.StartsWith(Webhook, _http.Requests.Last().Url);                       // the usual webhook message first

        // Someone who isn't this lobby's host: another lobby's business.
        Assert.Null(s.HandleNewCommand(NewCmd("999", "stranger", "c1")));
        // The host isn't linked yet: the lobby code proves it's theirs, and links them as the host.
        Assert.StartsWith("Alice's lobby message is in this channel now", s.HandleNewCommand(NewCmd("100", "alice", "c1", code: "abcdef")));
        Assert.Equal("100", s.Links.Find(_lobby[0].Key)?.DiscordUserId);
        await s.PendingPosts;
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        Assert.Contains(_http.Requests, r => r.Method == HttpMethod.Delete && r.Url == Webhook + "/messages/555");   // old one gone
        var moved = _http.Requests.Last(r => r.Method == HttpMethod.Post);
        Assert.Equal("https://discord.com/api/v10/channels/c1/messages", moved.Url);
        Assert.Contains("components", moved.Body);                                      // posted by the bot, with the colour menu

        // Later, /new on its own from the host moves it again; others with the code can't unless they referee.
        Assert.StartsWith("Only this lobby's host (@alice)", s.HandleNewCommand(NewCmd("101", "bob", "c2", code: "ABCDEF")));
        Assert.NotNull(s.HandleNewCommand(NewCmd("100", "alice", "c2")));
        await s.PendingPosts;
        Assert.Contains(_http.Requests, r => r.Method == HttpMethod.Delete && r.Url == "https://discord.com/api/v10/channels/c1/messages/555");
        Assert.Equal("https://discord.com/api/v10/channels/c2/messages", _http.Requests.Last(r => r.Method == HttpMethod.Post).Url);
        Assert.Contains("Open your lobby", TournamentSession.SlashFallback(NewCmd("1", "x", "c")));
    }

    [Fact]
    public void Menu_picks_are_read_from_the_gateway()
    {
        var i = Interaction.Parse(JsonDocument.Parse("""
            {"id":"9","token":"t","type":3,"guild_id":"g1","member":{"user":{"id":"77","username":"dee"}},
             "data":{"component_type":3,"custom_id":"tt-link:abc","values":["4"]}}
            """).RootElement);
        Assert.Equal(("menu", "tt-link:abc", "4", "77"), (i!.Command, i.CustomId, i.Values[0], i.UserId));
    }

    [Fact]
    public async Task Status_is_posted_once_then_edited_in_place()
    {
        _http.Default = r => r.Method == HttpMethod.Post
            ? FakeHttp.Json(HttpStatusCode.OK, """{"id":"555"}""")
            : FakeHttp.Json(HttpStatusCode.OK, "{}");
        var s = Session(c => { c.LiveStatus = true; c.AutoMute.LinkMenu = false; c.AutoMute.AutoLinkByName = false; });
        s.Links.Link(_lobby[0].Key, "Alice", "100", "alice");

        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");    // nothing changed
        await s.PendingPosts;
        var first = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Post, first.Method);
        Assert.Equal("Lobby open", Title(first));
        string text = Description(first);
        Assert.Contains("**Code:** `ABCDEF` · **Map:** Polus", text);
        Assert.Contains("**Red** Alice · <@100>", text);
        Assert.Contains("**Blue** Bob · *not linked*", text);

        _lobby.RemoveAt(5);                                          // Finn leaves
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        var edit = _http.Requests.Last();
        Assert.Equal("PATCH", edit.Method.Method);
        Assert.Equal(Webhook + "/messages/555", edit.Url);
        Assert.DoesNotContain("Finn", Description(edit));
    }

    [Fact]
    public async Task Deaths_only_show_once_the_game_has_revealed_them()
    {
        _http.Default = r => FakeHttp.Json(HttpStatusCode.OK, """{"id":"555"}""");
        var s = Session(c => { c.LiveStatus = true; c.AutoMute.AutoLinkByName = false; });
        s.VoiceTick(VoicePhase.Tasks, _lobby, "ABCDEF", "Polus");
        _lobby[2].IsDead = true;                                     // Carl killed during tasks
        s.VoiceTick(VoicePhase.Tasks, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        Assert.DoesNotContain("💀", Description(_http.Requests.Last()));

        s.VoiceTick(VoicePhase.Meeting, _lobby, "ABCDEF", "Polus");  // body reported
        await s.PendingPosts;
        var meeting = _http.Requests.Last();
        Assert.Equal("Meeting", Title(meeting));
        Assert.Contains("💀 ~~**Green** Carl~~", Description(meeting));
    }

    [Fact]
    public async Task After_a_game_the_status_moves_below_the_report_and_closes_with_the_lobby()
    {
        int next = 500;
        _http.Default = r => r.Method == HttpMethod.Post
            ? FakeHttp.Json(HttpStatusCode.OK, $$"""{"id":"{{next++}}"}""")
            : FakeHttp.Json(HttpStatusCode.OK, "{}");
        var s = Session(c => { c.LiveStatus = true; c.AutoMute.LinkMenu = false; c.AutoMute.AutoLinkByName = false; });
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        s.GameStarted("ABCDEF", "Polus", _lobby);
        s.GameEnded("HumansByTask", _lobby);
        await s.PendingPosts;

        var calls = _http.Requests.Select(r => $"{r.Method.Method} {r.Url.Replace(Webhook, "")}").ToList();
        Assert.Equal(new[] { "POST ?wait=true", "POST ?wait=true", "DELETE /messages/500", "POST ?wait=true" }, calls);

        s.VoiceTick(VoicePhase.Menu, Array.Empty<PlayerSnapshot>());
        await s.PendingPosts;
        var closed = _http.Requests.Last();
        Assert.Equal(Webhook + "/messages/502", closed.Url);
        Assert.EndsWith("Lobby closed", Title(closed));
    }

    [Fact]
    public async Task Status_can_use_its_own_channel()
    {
        const string statusHook = "https://discord.test/api/webhooks/2/status?thread_id=77";
        _http.Default = r => FakeHttp.Json(HttpStatusCode.OK, """{"id":"9"}""");
        var s = Session(c => { c.LiveStatus = true; c.AutoMute.LinkMenu = false; c.StatusWebhookUrl = statusHook; c.AutoMute.AutoLinkByName = false; });
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        _lobby[0].Name = "Alicia";
        s.VoiceTick(VoicePhase.Lobby, _lobby, "ABCDEF", "Polus");
        await s.PendingPosts;
        Assert.Equal("https://discord.test/api/webhooks/2/status?thread_id=77&wait=true", _http.Requests.First().Url);
        Assert.Equal("https://discord.test/api/webhooks/2/status/messages/9?thread_id=77", _http.Requests.Last().Url);
    }
}

public class LeaderboardOptionTests
{
    private static StatsStore Store()
    {
        var store = new StatsStore { Tournament = "Cup" };
        var clock = new FakeClock();
        for (int g = 0; g < 2; g++)
        {
            var t = new GameTracker(new ScoringRules());
            var lobby = g == 0 ? Players.Lobby() : Players.Lobby().Take(5).ToList();   // Finn plays once
            t.Start(g + 1, "Cup", "X", "Polus", lobby, clock.Now);
            store.Apply(t.End("HumansByTask", Outcome.Crewmates, lobby, clock.Now)!);
        }
        return store;
    }

    [Fact]
    public void Players_below_the_minimum_are_left_off()
    {
        var embed = ReportFormatter.Leaderboard(Store(), 15, minGames: 2).Embeds!.Single();
        Assert.DoesNotContain("Finn", embed.Description);
        Assert.Contains("Carl", embed.Description);
        Assert.Contains("1 player under 2 games not shown", embed.Footer!.Text);
    }

    [Fact]
    public void Linked_players_can_be_shown_as_mentions()
    {
        var embed = ReportFormatter.Leaderboard(Store(), 15, 1, key => key.StartsWith("carl") ? "102" : null).Embeds!.Single();
        Assert.DoesNotContain("```", embed.Description);
        Assert.Contains("<@102> — **10** pts", embed.Description);
        Assert.Contains(" Dana — ", embed.Description);
    }
}
