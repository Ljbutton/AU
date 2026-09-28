using System.Net;
using System.Text.Json;
using TournamentTracker.Discord;
using TournamentTracker.Setup;
using TournamentTracker.Stats;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>A small fake of Discord: channels with messages and files, webhooks, reactions and the CDN.</summary>
public sealed class FakeDiscord
{
    public sealed record Msg(string Id, string Channel, string Content, string? File, string? Json, bool Bot, JsonElement? Embeds);
    public readonly List<Msg> Messages = new();                    // oldest first
    public readonly List<(string Url, JsonElement Payload, string? File)> Webhooks = new();
    public readonly List<(string Channel, string Message, string Emoji)> Reactions = new();
    private long _next = 1_300_000_000_000_000_000;
    public int Edits;

    private string NextId(DateTime? at = null)
    {
        if (at.HasValue)
        {
            long ms = new DateTimeOffset(at.Value).ToUnixTimeMilliseconds() - 1420070400000L;
            return ((ms << 22) + Interlocked.Increment(ref _next) % 1000).ToString();
        }
        return Interlocked.Increment(ref _next).ToString();
    }

    public void Say(string channel, string text, DateTime at) => Messages.Add(new Msg(NextId(at), channel, text, null, null, false, null));
    public void AddGame(string channel, GameRecord g) =>
        Messages.Add(new Msg(NextId(g.EndedUtc), channel, $"Game {g.Name}", SharedResults.FileNameFor(g), JsonSerializer.Serialize(g), true, null));

    public HttpResponseMessage Handle(HttpRequestMessage r)
    {
        string url = r.RequestUri!.AbsoluteUri;
        if (url.StartsWith("https://cdn.test/"))
            return FakeHttp.Json(HttpStatusCode.OK, Messages.Single(m => m.Id == url.Split('/')[3]).Json!);
        if (url.Contains("/reactions/"))
        {
            var parts = r.RequestUri.AbsolutePath.Split('/');
            Reactions.Add((parts[^6], parts[^4], Uri.UnescapeDataString(parts[^2])));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (url.Contains("/webhooks/"))
        {
            JsonElement payload;
            string? file = null;
            if (r.Content is MultipartFormDataContent form)
            {
                var parts = form.ToList();
                payload = JsonDocument.Parse(parts[0].ReadAsStringAsync().Result).RootElement.Clone();
                file = parts[1].ReadAsStringAsync().Result;
            }
            else payload = JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result).RootElement.Clone();
            Webhooks.Add((url, payload, file));
            return FakeHttp.Json(HttpStatusCode.OK, """{"id":"42"}""");
        }
        var m = System.Text.RegularExpressions.Regex.Match(url, @"/channels/(\w+)/messages(\?|$)");
        if (m.Success && r.Method == HttpMethod.Get)
        {
            string channel = m.Groups[1].Value;
            var items = Messages.Where(x => x.Channel == channel).Reverse().Select(x => new Dictionary<string, object?>
            {
                ["id"] = x.Id,
                ["content"] = x.Content,
                ["author"] = new { id = x.Bot ? "bot" : "ref", bot = x.Bot },
                ["embeds"] = x.Embeds.HasValue ? x.Embeds.Value : (object)Array.Empty<object>(),
                ["attachments"] = x.File == null ? Array.Empty<object>() : new object[] { new { filename = x.File, url = $"https://cdn.test/{x.Id}/{x.File}" } },
            });
            return FakeHttp.Json(HttpStatusCode.OK, JsonSerializer.Serialize(items));
        }
        if (m.Success && r.Method == HttpMethod.Post)
        {
            string channel = m.Groups[1].Value;
            if (r.Content is MultipartFormDataContent form)
            {
                var parts = form.ToList();
                var payload = JsonDocument.Parse(parts[0].ReadAsStringAsync().Result).RootElement;
                Messages.Add(new Msg(NextId(), channel, payload.GetProperty("content").GetString()!,
                    payload.GetProperty("attachments")[0].GetProperty("filename").GetString(), parts[1].ReadAsStringAsync().Result, true, null));
            }
            else
            {
                var body = JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result).RootElement.Clone();
                Messages.Add(new Msg(NextId(), channel, body.TryGetProperty("content", out var c) ? c.GetString()! : "", null, null, true,
                    body.TryGetProperty("embeds", out var e) ? e : null));
            }
            return FakeHttp.Json(HttpStatusCode.OK, """{"id":"1"}""");
        }
        var edit = System.Text.RegularExpressions.Regex.Match(url, @"/channels/(\w+)/messages/(\d+)$");
        if (edit.Success && r.Method.Method == "PATCH")
        {
            int i = Messages.FindIndex(x => x.Id == edit.Groups[2].Value);
            var body = JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result).RootElement.Clone();
            Messages[i] = Messages[i] with { Embeds = body.GetProperty("embeds") };
            Edits++;
            return FakeHttp.Json(HttpStatusCode.OK, "{}");
        }
        return FakeHttp.Json(HttpStatusCode.OK, "{}");
    }

    public static string Title(JsonElement payload) => payload.GetProperty("embeds")[0].GetProperty("title").GetString()!;
    public static string Description(JsonElement payload) => payload.GetProperty("embeds")[0].GetProperty("description").GetString()!;
}

public class SetupCodeTests
{
    [Fact]
    public void Codes_round_trip_and_survive_copy_paste_whitespace()
    {
        var code = new SetupCode { Mode = "tournament", TournamentId = "fall-cup-2026", TournamentName = "Fall Cup", Webhook = "https://discord.com/api/webhooks/1/x",
            GuildId = "g1", BotTokens = new() { "tok" }, ResultsChannelId = "results", PrelimChannelIds = new() { "p1" }, AdvanceCount = 5,
            Scoring = new ScoringRules { Kill = 2 } };
        string text = code.Encode();
        Assert.StartsWith("TT1-", text);
        Assert.True(SetupCode.TryParse("  " + text.Insert(20, "\n  ") + "\r\n", out var back, out _));
        Assert.Equal("Fall Cup", back.TournamentName);
        Assert.Equal(2, back.Scoring!.Kill);
        Assert.Equal("Fall Cup (tournament host, automute on)", back.Describe());
    }

    [Theory]
    [InlineData("hello", "start with TT1-")]
    [InlineData("TT1-!!!notbase64", "damaged")]
    [InlineData("TT1-e30", "no tournament")]
    public void Bad_codes_say_what_is_wrong(string text, string message)
    {
        Assert.False(SetupCode.TryParse(text, out _, out var error));
        Assert.Contains(message, error);
    }

    [Fact]
    public void A_code_from_the_generator_page_is_read_by_the_mod()
    {
        // Produced by the page's own encoder (node), including non-ASCII in the name.
        Assert.True(SetupCode.TryParse("TT1-eyJtIjoidG91cm5hbWVudCIsImlkIjoiZmFsbC1jdXAtMjAyNiIsIm4iOiJGYWxsIEN1cCAyMDI2IOKAlCDDiXTDqSIsIndoIjoiaHR0cHM6Ly9kaXNjb3JkLmNvbS9hcGkvd2ViaG9va3MvMS94IiwiZyI6IjExODAwMDAwMDAwMDAwMDAwMDAiLCJidCI6WyJhLmIuYyJdLCJyYyI6IjEyOTAwMDAwMDAwMDAwMDAwMDAiLCJwYyI6WyIxMjkwMDAwMDAwMDAwMDAwMDAxIl0sImFkdiI6NSwic2MiOnsia2lsbCI6MS41LCJnb3RLaWxsZWQiOjAuNX19", out var code, out var error), error);
        Assert.Equal("Fall Cup 2026 — Été", code.TournamentName);
        Assert.True(code.IsTournament);
        Assert.Equal(1.5, code.Scoring!.Kill);
        var settings = new TrackerSettings();
        code.ApplyTo(settings);
        Assert.Equal(TrackerMode.Tournament, settings.Mode);
        Assert.Equal(new[] { "a.b.c" }, settings.AutoMute.BotTokens);
        Assert.True(settings.AutoMute.Enabled);
        Assert.Equal(5, settings.AdvanceCount);
    }

    [Fact]
    public void A_preliminary_code_switches_to_preliminary_mode()
    {
        var settings = new TrackerSettings { LiveStatus = true, ResultsChannelId = "x", PostLeaderboardAfterEachGame = true };
        new SetupCode { TournamentId = "prelim-oct", TournamentName = "October prelims", Server = "Soggy's Server", Webhook = "https://discord.com/api/webhooks/2/y" }.ApplyTo(settings);
        Assert.Equal(TrackerMode.Preliminary, settings.Mode);
        Assert.Equal("Soggy's Server", settings.ServerName);
        Assert.Equal("prelim-oct", settings.EffectiveTournamentId);
        Assert.False(settings.LiveStatus);
        Assert.False(settings.PostLeaderboardAfterEachGame);
        Assert.Equal("", settings.ResultsChannelId);
    }

    [Fact]
    public void The_same_code_comes_out_of_the_generator_page_format()
    {
        // The generator page builds codes in the browser: base64url of this JSON, no padding.
        string json = """{"m":"prelim","id":"p","n":"Prelims","srv":"S","wh":"https://discord.com/api/webhooks/1/x","sc":{"kill":3}}""";
        string code = "TT1-" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(SetupCode.TryParse(code, out var parsed, out _));
        Assert.Equal(3, parsed.Scoring!.Kill);
        Assert.Equal(1, parsed.Scoring.FirstBlood);    // unspecified rules keep their defaults
    }
}

public class TournamentModeTests : IDisposable
{
    private const string Webhook = "https://discord.test/api/webhooks/1/abc";
    private readonly TempDir _dir = new();
    private readonly FakeDiscord _discord = new();
    private readonly FakeHttp _http = new();
    private readonly FakeClock _clock = new();
    private readonly List<TournamentSession> _sessions = new();

    public TournamentModeTests() => _http.Default = _discord.Handle;

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        _dir.Dispose();
    }

    private TournamentSession Session(SetupCode code)
    {
        var s = new TournamentSession(new TrackerSettings { LiveStatus = false }, _dir.Path, NullLog.Instance, new HttpClient(_http), () => _clock.Now,
            new FakeVoiceApi(), new VoicePresenceState("g1"), code);
        _sessions.Add(s);
        return s;
    }

    private static SetupCode TournamentCode() => new()
    {
        Mode = "tournament", TournamentId = "fall-cup", TournamentName = "Fall Cup", Webhook = Webhook,
        GuildId = "g1", BotTokens = new() { "tok" }, ResultsChannelId = "results", PrelimChannelIds = new() { "prelims" }, AdvanceCount = 2,
    };

    private static List<PlayerSnapshot> Lobby(string host = "LJ")
    {
        var lobby = Players.Lobby();
        lobby[3].Name = host;
        lobby[3].Key = host.ToLower() + "#1";
        lobby[3].IsHost = true;
        return lobby;
    }

    private GameRecord Play(TournamentSession s, List<PlayerSnapshot> lobby, string reason = "HumansByTask")
    {
        s.GameStarted("ABCDEF", "Polus", lobby);
        _clock.Advance(300);
        var game = s.GameEnded(reason, lobby)!;
        _clock.Advance(60);
        return game;
    }

    [Fact]
    public async Task Rounds_are_set_from_the_lobby_and_remembered()
    {
        var s = Session(TournamentCode());
        var lobby = Lobby();
        Assert.True(s.HandleChat(lobby[3], fromHost: true, "!r2"));
        Assert.Equal(2, s.Round);
        Assert.Contains(s.Pump(), r => r.Public && r.Text == "Round 2 starts now!" || r.Text.StartsWith("Round 2 started"));
        Assert.False(s.HandleChat(lobby[0], fromHost: false, "!r3"));
        Assert.Equal(2, s.Round);
        s.HandleChat(lobby[3], true, "!round 3");
        Assert.Equal(3, s.Round);
        await s.PendingPosts;

        var again = Session(TournamentCode());                     // Among Us restarted
        Assert.Equal(3, again.Round);
        Assert.Equal(3, Play(again, lobby).Round);
    }

    [Fact]
    public async Task After_each_game_the_lobby_standings_show_the_round_the_cut_line_and_the_running_total()
    {
        var s = Session(TournamentCode());
        var lobby = Lobby();
        s.HandleChat(lobby[3], true, "!r1");
        Play(s, lobby, "ImpostorByKill");                          // Alice and Bob win round 1
        s.HandleChat(lobby[3], true, "!r2");
        Play(s, lobby);                                            // crew wins round 2
        await s.PendingPosts;

        var standings = _discord.Webhooks.Last(w => FakeDiscord.Title(w.Payload).Contains("standings"));
        Assert.Equal("LJ — Round 2 standings", FakeDiscord.Title(standings.Payload));
        string table = FakeDiscord.Description(standings.Payload);
        Assert.Contains("── top 2 move on ──", table);
        var carl = table.Split('\n').Single(l => l.Contains("Carl"));
        Assert.Matches(@"Carl\s+5\s+4 ", carl);                  // round 2: +5 task win; total: 0 − 1 + 5
        Assert.Equal(2, _discord.Messages.Count(m => m.Channel == "results" && m.File != null));
    }

    [Fact]
    public async Task Referees_adjust_points_from_discord_and_see_a_tick()
    {
        var s = Session(TournamentCode());
        var lobby = Lobby();
        s.HandleChat(lobby[3], true, "!r1");
        s.GameStarted("ABCDEF", "Polus", lobby);
        _clock.Advance(120);
        _discord.Say("results", "!adjust LJ red -2 meta call in meeting", _clock.Now);      // typed during the game
        _discord.Say("results", "!adjust LJ nobody +1 typo", _clock.Now);
        _clock.Advance(180);
        s.GameEnded("ImpostorByKill", lobby);
        await s.PendingPosts;

        var alice = s.Combined!.GameRecords.Single().Players.Single(p => p.Name == "Alice");
        Assert.Contains(alice.PointBreakdown, l => l.Rule == "Referee: meta call in meeting" && l.Points == -2);
        Assert.Equal(4 - 2, alice.Points);                                                // kill win, referee −2
        string red = _discord.Messages.Single(m => m.Content.Contains(" red ")).Id;
        string nobody = _discord.Messages.Single(m => m.Content.Contains("nobody")).Id;
        Assert.Contains(("results", red, "✅"), _discord.Reactions);
        Assert.Contains(("results", nobody, "❓"), _discord.Reactions);
        Assert.Equal(2, _discord.Reactions.Count);
    }

    [Fact]
    public void Adjustment_messages_are_read_carefully()
    {
        var a = RefereeAdjustment.TryParse("!adjust LJ-3 Soggy Dingus +1,5 great call", "1", DateTime.UtcNow)!;
        Assert.Equal("LJ-3", a.Game);
        Assert.Equal("Soggy Dingus", a.Player);
        Assert.Equal(1.5, a.Points);
        Assert.Equal("great call", a.Reason);
        Assert.Null(RefereeAdjustment.TryParse("adjust LJ red 2", "1", DateTime.UtcNow));
        Assert.Equal(new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc), RefereeAdjustment.TimeOfSnowflake("0"));
    }

    [Fact]
    public async Task Server_standings_rank_servers_by_points_and_name_their_furthest_player()
    {
        // Preliminary history: Alice and Bob mostly played in "Sus Squad", everyone else in "Crewmates HQ".
        GameRecord Prelim(string server, IEnumerable<int> ids, DateTime at)
        {
            var t = new GameTracker(new ScoringRules());
            var g = t.Start(1, "Oct prelims", "X", "Skeld", Players.Lobby().Where(p => ids.Contains(p.PlayerId)), at);
            g.Server = server; g.Mode = "Preliminary"; g.Id = Guid.NewGuid().ToString("N");
            return t.End("HumansByTask", Outcome.Crewmates, Players.Lobby(), at.AddMinutes(5))!;
        }
        _discord.AddGame("prelims", Prelim("Sus Squad", new[] { 0, 1 }, _clock.Now.AddDays(-3)));
        _discord.AddGame("prelims", Prelim("Sus Squad", new[] { 0, 1 }, _clock.Now.AddDays(-2)));
        _discord.AddGame("prelims", Prelim("Crewmates HQ", new[] { 0, 2, 3, 4, 5 }, _clock.Now.AddDays(-1)));

        var s = Session(TournamentCode());
        var lobby = Players.Lobby();
        s.HandleChat(lobby[0], true, "!r1");
        Play(s, lobby);
        s.HandleChat(lobby[0], true, "!r2");
        Play(s, lobby.Where(p => p.PlayerId != 1 && p.PlayerId != 4).ToList());   // Bob and Eve knocked out
        s.HandleChat(lobby[0], true, "!servers");
        await s.PendingPosts;

        var post = _discord.Webhooks.Last(w => FakeDiscord.Title(w.Payload).Contains("Server standings"));
        string text = FakeDiscord.Description(post.Payload);
        Assert.StartsWith("` 1.` **Crewmates HQ**", text);
        Assert.Contains("**Sus Squad**", text);
        Assert.Contains("furthest: Alice (still in the running)", text);        // Alice and Bob: Alice reached round 2
        Assert.Contains("furthest: Carl (still in the running)", text);          // several still in: first alphabetically
    }

    [Fact]
    public async Task Preliminary_games_post_the_report_with_its_data_and_summarise_in_chat()
    {
        var s = Session(new SetupCode { TournamentId = "oct-prelim-sus", TournamentName = "October prelims", Server = "Sus Squad", Webhook = Webhook });
        var game = Play(s, Lobby());
        await s.PendingPosts;

        var report = Assert.Single(_discord.Webhooks);
        Assert.Contains("Game LJ-1", FakeDiscord.Title(report.Payload));
        Assert.Equal(3, report.Payload.GetProperty("embeds").GetArrayLength());
        Assert.Equal(SharedResults.FileNameFor(game), report.Payload.GetProperty("attachments")[0].GetProperty("filename").GetString());
        var sent = JsonSerializer.Deserialize<GameRecord>(report.File!)!;
        Assert.Equal("Sus Squad", sent.Server);
        Assert.Equal("Preliminary", sent.Mode);

        var chat = s.Pump();
        Assert.Contains(chat, r => !r.Public && r.Text.StartsWith("Game LJ-1: Crewmates win"));
        Assert.Contains(chat, r => r.Text.StartsWith("Points: "));
    }

    [Fact]
    public async Task Setup_reads_the_code_from_the_clipboard_and_asks_for_a_restart()
    {
        var s = Session(TournamentCode());
        bool restarted = false;
        s.RestartRequested += () => restarted = true;
        var host = Lobby()[3];

        s.Clipboard = () => "nothing useful";
        s.HandleChat(host, true, "!setup");
        Assert.Contains(s.Pump(), r => r.Text == "Current setup: Fall Cup (tournament host, automute on).");
        Assert.False(restarted);

        var prelim = new SetupCode { TournamentId = "p", TournamentName = "Prelims", Server = "S", Webhook = Webhook };
        s.Clipboard = () => prelim.Encode();
        s.HandleChat(host, true, "!setup");
        Assert.True(restarted);
        Assert.True(SetupCode.TryParse(File.ReadAllText(Path.Combine(_dir.Path, SetupCode.FileName)), out var saved, out _));
        Assert.Equal("Prelims", saved.TournamentName);
        await s.PendingPosts;
    }
}

public class PrelimLeaderboardTests
{
    private readonly FakeDiscord _discord = new();
    private readonly FakeClock _clock = new();

    private GameRecord Prelim(string id, string name, string server, string host, int n, string reason)
    {
        var t = new GameTracker(new ScoringRules());
        var g = t.Start(n, name, "X", "Skeld", Players.Lobby(), _clock.Now);
        g.TournamentId = id; g.Server = server; g.Host = host; g.Mode = "Preliminary"; g.Id = $"{host}-{n}-{_clock.Now:HHmmss}";
        _clock.Advance(600);
        return t.End(reason, Outcome.WinnerFromReason(reason), Players.Lobby(), _clock.Now)!;
    }

    private PrelimLeaderboards Job() => new(new DiscordRest(new HttpClient(new FakeHttp { Default = _discord.Handle }), NullLog.Instance), "tok", NullLog.Instance);

    [Fact]
    public async Task One_leaderboard_per_preliminary_combining_every_lobby_and_edited_in_place()
    {
        _discord.AddGame("prelims", Prelim("oct-sus", "October: Sus Squad", "Sus Squad", "Soggy", 1, "HumansByTask"));
        _discord.AddGame("prelims", Prelim("oct-sus", "October: Sus Squad", "Sus Squad", "Fred", 1, "HumansByTask"));   // a second lobby at once
        _discord.AddGame("prelims", Prelim("oct-hq", "October: Crew HQ", "Crew HQ", "Millie", 1, "ImpostorByKill"));

        Assert.Equal(2, await Job().UpdateAsync(new[] { "prelims" }));
        var boards = _discord.Messages.Where(m => m.Embeds.HasValue).ToList();
        Assert.Equal(2, boards.Count);
        var sus = boards.Single(b => FakeDiscord.Title(Payload(b)) == "October: Sus Squad — Preliminary leaderboard");
        Assert.Contains("2 games · 2 lobbies · Sus Squad", sus.Embeds!.Value[0].GetProperty("footer").GetProperty("text").GetString());

        Assert.Equal(0, await Job().UpdateAsync(new[] { "prelims" }));        // nothing new: no edits

        _discord.AddGame("prelims", Prelim("oct-sus", "October: Sus Squad", "Sus Squad", "Soggy", 2, "HumansByTask"));
        _discord.Say("prelims", "!adjust Soggy-2 red -1 meta", _clock.Now);
        Assert.Equal(1, await Job().UpdateAsync(new[] { "prelims" }));
        Assert.Equal(1, _discord.Edits);
        Assert.Equal(2, _discord.Messages.Count(m => m.Embeds.HasValue));   // still two messages
        Assert.Contains("3 games", _discord.Messages.Where(m => m.Embeds.HasValue).Select(m => m.Embeds!.Value[0].GetProperty("footer").GetProperty("text").GetString()).First(t => t!.Contains("Sus")));
    }

    private static JsonElement Payload(FakeDiscord.Msg m) => JsonDocument.Parse("{\"embeds\":" + m.Embeds!.Value.GetRawText() + "}").RootElement;
}
