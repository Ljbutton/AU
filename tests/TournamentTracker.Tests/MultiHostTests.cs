using System.Net;
using System.Text.Json;
using TournamentTracker.Discord;
using TournamentTracker.Stats;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

public class MultiHostTests : IDisposable
{
    private const string Webhook = "https://discord.test/api/webhooks/1/abc";
    private readonly TempDir _dir = new();
    private readonly FakeHttp _http = new();
    private readonly FakeClock _clock = new();
    private readonly VoicePresenceState _presence = new("g1") { Connected = true };
    private readonly List<TournamentSession> _sessions = new();

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        _dir.Dispose();
    }

    private static List<PlayerSnapshot> Lobby(string hostName = "LJ")
    {
        var lobby = Players.Lobby();
        lobby[3].Name = hostName;
        lobby[3].Key = PlayerSnapshot.MakeKey(hostName.ToLower() + "#1", hostName);
        lobby[3].IsHost = true;
        return lobby;
    }

    private TournamentSession Session(Action<TrackerSettings>? configure = null, string? dir = null)
    {
        var settings = new TrackerSettings { TournamentName = "Fall Cup", StatsWebhookUrl = Webhook, LiveStatus = false };
        settings.AutoMute.Enabled = true;
        settings.AutoMute.GuildId = "g1";
        settings.AutoMute.BotTokens.Add("tok");
        settings.AutoMute.AutoLinkByName = false;
        configure?.Invoke(settings);
        var s = new TournamentSession(settings, dir ?? _dir.Path, NullLog.Instance, new HttpClient(_http), () => _clock.Now, new FakeVoiceApi(), _presence);
        _sessions.Add(s);
        return s;
    }

    private GameRecord Play(TournamentSession s, List<PlayerSnapshot> lobby, string reason = "HumansByTask")
    {
        s.GameStarted("ABCDEF", "Polus", lobby);
        _clock.Advance(300);
        var game = s.GameEnded(reason, lobby)!;
        _clock.Advance(60);
        return game;
    }

    // ---- Labels and game names ----

    [Fact]
    public void Games_are_named_after_the_host_and_numbered_per_host()
    {
        var s = Session();
        var first = Play(s, Lobby());
        var second = Play(s, Lobby());
        Assert.Equal("LJ-1", first.Name);
        Assert.Equal("LJ-2", second.Name);
        Assert.Equal("LJ-1-20260901-180000", first.Id);
        Assert.Equal("LJ", s.LobbyLabel());
        Assert.Single(Directory.GetFiles(Path.Combine(_dir.Path, "games", "fall-cup"), "game-LJ-2-*.json"));
        Assert.Equal("Game LJ-1 — Crewmates win", ReportFormatter.GameReport(first).Embeds![0].Title);
        Assert.Contains("LJ-1-20260901-180000", ReportFormatter.GameReport(first).Embeds![0].Footer!.Text);
    }

    [Fact]
    public void A_configured_label_wins_over_the_host_name()
    {
        var s = Session(c => c.LobbyLabel = "Bracket 2");
        var game = Play(s, Lobby("MAL"));
        Assert.Equal("Bracket 2-1", game.Name);
        Assert.StartsWith("Bracket-2-1-", game.Id);
    }

    [Fact]
    public void Status_title_names_the_lobby() =>
        Assert.Equal("LJ — Lobby open", StatusFormatter.Build(new StatusInfo { Label = "LJ", Phase = VoicePhase.Lobby }).Embeds![0].Title);

    // ---- Host's voice channel ----

    [Fact]
    public async Task The_game_channel_is_the_one_the_host_is_in()
    {
        var voice = new FakeVoiceApi();
        var settings = new TrackerSettings { TournamentName = "Cup", LiveStatus = false };
        settings.AutoMute.Enabled = true;
        settings.AutoMute.GuildId = "g1";
        settings.AutoMute.BotTokens.Add("tok");
        settings.AutoMute.AutoLinkByName = false;
        settings.AutoMute.MuteSpectators = true;
        settings.AutoMute.DelayGameStart = 0;
        using var s = new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(_http), () => _clock.Now, voice, _presence);
        var lobby = Lobby();
        s.Links.Link(lobby[3].Key, "LJ", "300", "lj");        // the host
        s.Links.Link(lobby[0].Key, "Alice", "100", "alice");
        s.Links.Link(lobby[1].Key, "Bob", "101", "bob");
        // Two linked players sit in another lobby's channel; the host is in "bracket-1".
        _presence.Dispatch("GUILD_CREATE", JsonDocument.Parse("""
            {"id":"g1","voice_states":[
              {"user_id":"300","channel_id":"bracket-1"},{"user_id":"900","channel_id":"bracket-1"},
              {"user_id":"100","channel_id":"bracket-2"},{"user_id":"101","channel_id":"bracket-2"},{"user_id":"901","channel_id":"bracket-2"}],
             "members":[]}
            """).RootElement);

        s.VoiceTick(VoicePhase.Tasks, lobby);
        await Wait.Until(() => voice.Calls.Any(c => c.User == "900"));
        Assert.DoesNotContain(voice.Calls, c => c.User == "901");     // the other lobby's spectator is left alone
    }

    // ---- Combined leaderboard ----

    /// <summary>A fake results channel: messages newest first, attachments served from a fake CDN.</summary>
    private sealed class Channel
    {
        public readonly List<(string Id, string Content, string? File, string? Json, bool Bot)> Messages = new();
        private int _next = 1000;

        public void AddGame(GameRecord g) => Messages.Insert(0, ((_next++).ToString(), $"Game {g.Name}", $"tt-game-{g.Id}.json", JsonSerializer.Serialize(g), true));
        public void AddText(string text, bool bot = false) => Messages.Insert(0, ((_next++).ToString(), text, null, null, bot));

        public HttpResponseMessage Handle(HttpRequestMessage r)
        {
            string url = r.RequestUri!.AbsoluteUri;
            if (url.StartsWith("https://cdn.test/"))
            {
                var m = Messages.Single(x => x.Id == url.Split('/')[3]);
                return FakeHttp.Json(HttpStatusCode.OK, m.Json!);
            }
            if (url.Contains("/channels/results/messages") && r.Method == HttpMethod.Get)
            {
                var items = Messages.Select(m => new Dictionary<string, object?>
                {
                    ["id"] = m.Id,
                    ["content"] = m.Content,
                    ["author"] = new { id = m.Bot ? "bot" : "human", bot = m.Bot },
                    ["embeds"] = Array.Empty<object>(),
                    ["attachments"] = m.File == null ? Array.Empty<object>() : new object[] { new { filename = m.File, url = $"https://cdn.test/{m.Id}/{m.File}" } },
                });
                return FakeHttp.Json(HttpStatusCode.OK, JsonSerializer.Serialize(items));
            }
            if (url.Contains("/channels/results/messages") && r.Method == HttpMethod.Post)
            {
                var form = r.Content as MultipartFormDataContent;
                if (form == null)
                {
                    string body = r.Content!.ReadAsStringAsync().Result;
                    AddText(JsonDocument.Parse(body).RootElement.GetProperty("content").GetString()!, bot: true);
                }
                else
                {
                    var parts = form.ToList();
                    var payload = JsonDocument.Parse(parts[0].ReadAsStringAsync().Result).RootElement;
                    string fileName = payload.GetProperty("attachments")[0].GetProperty("filename").GetString()!;
                    Messages.Insert(0, ((_next++).ToString(), payload.GetProperty("content").GetString()!, fileName, parts[1].ReadAsStringAsync().Result, true));
                }
                return FakeHttp.Json(HttpStatusCode.OK, """{"id":"1"}""");
            }
            return FakeHttp.Json(HttpStatusCode.OK, "{}");
        }
    }

    private static GameRecord OtherHostGame(string host, int number, string winnerKey, DateTime at)
    {
        var t = new GameTracker(new ScoringRules());
        var lobby = Players.Lobby();
        var g = t.Start(number, "Fall Cup", "QWERTY", "Skeld", lobby, at);
        g.Host = host;
        g.Id = $"{host}-{number}-{at:yyyyMMdd-HHmmss}";
        return t.End("HumansByTask", Outcome.Crewmates, lobby, at.AddMinutes(8))!;
    }

    [Fact]
    public async Task Every_hosts_games_add_up_to_one_leaderboard()
    {
        var channel = new Channel();
        _http.Default = channel.Handle;
        channel.AddGame(OtherHostGame("MAL", 1, "x", _clock.Now.AddHours(-1)));
        channel.AddGame(OtherHostGame("MAL", 1, "x", _clock.Now.AddHours(-1)));   // resent: counts once
        var otherCup = OtherHostGame("MAL", 2, "x", _clock.Now);
        otherCup.Tournament = "Spring Cup";
        channel.AddGame(otherCup);

        var s = Session(c => c.ResultsChannelId = "results");
        await s.PendingPosts;
        Assert.Equal(1, s.Combined!.Games);

        Play(s, Lobby());                                      // LJ's game
        await s.PendingPosts;

        Assert.Equal(2, s.Combined!.Games);
        Assert.Equal(2, s.Combined.Hosts);
        var carl = s.Standings.Players.Values.Single(p => p.Name == "Carl");
        Assert.Equal(2, carl.Games);                           // one game in each lobby
        Assert.Contains(channel.Messages, m => m.File != null && m.File.StartsWith("tt-game-LJ-1-"));

        var leaderboard = _http.Requests.Last(r => r.Url.StartsWith(Webhook));
        Assert.Contains("All lobbies (2 hosts)", leaderboard.Body);
    }

    [Fact]
    public async Task Resetting_from_discord_starts_the_leaderboard_over_and_deleting_it_undoes_that()
    {
        var channel = new Channel();
        _http.Default = channel.Handle;
        channel.AddGame(OtherHostGame("MAL", 1, "x", _clock.Now.AddHours(-2)));
        channel.AddText("!resetleaderboard");                  // the organiser, typed in Discord
        channel.AddGame(OtherHostGame("MAL", 2, "x", _clock.Now.AddHours(-1)));

        var s = Session(c => c.ResultsChannelId = "results");
        await s.PendingPosts;
        Assert.Equal(1, s.Combined!.Games);
        Assert.True(s.Combined.SinceReset);

        channel.Messages.RemoveAll(m => m.Content == "!resetleaderboard");
        Assert.True(s.HandleChat(Lobby()[3], fromHost: true, "!leaderboard"));
        await s.PendingPosts;
        Assert.Equal(2, s.Combined!.Games);
    }

    [Fact]
    public async Task The_host_can_reset_the_combined_leaderboard_from_the_game()
    {
        var channel = new Channel();
        _http.Default = channel.Handle;
        channel.AddGame(OtherHostGame("MAL", 1, "x", _clock.Now.AddHours(-1)));
        var s = Session(c => c.ResultsChannelId = "results");
        s.VoiceTick(VoicePhase.Lobby, Lobby());

        Assert.True(s.HandleChat(Lobby()[3], fromHost: true, "!resetleaderboard"));
        await s.PendingPosts;
        Assert.StartsWith("!resetleaderboard\n(Leaderboard reset by LJ.", channel.Messages[0].Content);
        Assert.Equal(0, s.Combined!.Games);
        Assert.False(s.HandleChat(Lobby()[0], fromHost: false, "!resetleaderboard"));
    }

    [Fact]
    public async Task Empty_messages_from_other_bots_warn_about_the_content_intent()
    {
        var channel = new Channel();
        _http.Default = channel.Handle;
        channel.AddText("", bot: true);                        // what Discord returns without the intent
        var s = Session(c => c.ResultsChannelId = "results");
        await s.PendingPosts;
        Assert.True(s.Combined!.MissingContentIntent);
    }

    [Theory]
    [InlineData("!resetleaderboard", true)]
    [InlineData("  !ResetLeaderboard \n(reset by LJ)", true)]
    [InlineData("please !resetleaderboard", false)]
    public void Reset_marker_is_the_first_line(string content, bool reset) =>
        Assert.Equal(reset, SharedResults.IsReset(content));
}
