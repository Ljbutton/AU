using System.Net;
using System.Text.Json;
using TournamentTracker.Discord;
using TournamentTracker.Setup;
using TournamentTracker.Stats;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;


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
    public void Automute_is_optional_in_preliminaries()
    {
        // Without a bot: exactly as before, automute stays whatever the host's config says (off).
        var plain = new TrackerSettings();
        new SetupCode { TournamentId = "p", TournamentName = "Prelims", Server = "S", Webhook = "https://discord.com/api/webhooks/2/y" }.ApplyTo(plain);
        Assert.False(plain.AutoMute.IsConfigured);
        Assert.False(plain.LiveStatus);

        // With the organiser's bot (and a channel for the live lobby message): automute and the colour menu.
        var code = new SetupCode { TournamentId = "p", TournamentName = "Prelims", Server = "S", Webhook = "https://discord.com/api/webhooks/2/y",
            GuildId = "118", BotTokens = new() { "a.b.c" }, StatusWebhook = "https://discord.com/api/webhooks/3/z" };
        Assert.True(SetupCode.TryParse(code.Encode(), out var read, out _));
        var withBot = new TrackerSettings();
        read.ApplyTo(withBot);
        Assert.Equal(TrackerMode.Preliminary, withBot.Mode);
        Assert.True(withBot.AutoMute.IsConfigured);
        Assert.True(withBot.LiveStatus);
        Assert.Equal("https://discord.com/api/webhooks/3/z", withBot.StatusWebhookUrl);
        Assert.Equal("https://discord.com/api/webhooks/2/y", withBot.StatsWebhookUrl);   // reports still go to the preliminary channel
        Assert.Equal("", withBot.ResultsChannelId);
        Assert.Equal("Prelims (preliminary in S, automute on)", read.Describe());
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

    private TournamentSession Session(SetupCode code, bool verify = false)
    {
        var s = new TournamentSession(new TrackerSettings { LiveStatus = false, PublicChat = true, ControlPort = -1, VerifyResults = verify }, _dir.Path, NullLog.Instance, new HttpClient(_http), () => _clock.Now,
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
        Assert.True(s.RunCommand("!r2"));
        Assert.Equal(2, s.Round);
        Assert.Contains(s.Pump(), r => r.Public && r.Text == "Round 2 starts now!" || r.Text.StartsWith("Round 2 started"));
        Assert.Equal(2, s.Round);
        s.RunCommand("!round 3");
        Assert.Equal(3, s.Round);
        await s.PendingPosts;

        var again = Session(TournamentCode());                     // Among Us restarted
        Assert.Equal(3, again.Round);
        Assert.Equal(3, Play(again, lobby).Round);
    }

    [Fact]
    public async Task When_a_lobby_finishes_its_round_its_scores_go_to_the_staff_channel()
    {
        var s = Session(TournamentCode());
        var lobby = Lobby();
        s.RunCommand("!r1");
        Play(s, lobby, "ImpostorByKill");
        Play(s, lobby);
        await s.PendingPosts;
        bool Summary(FakeDiscord.Msg m) => m.Embeds.HasValue && m.Embeds.Value.GetArrayLength() > 0
            && (m.Embeds.Value[0].TryGetProperty("title", out var t) ? t.GetString() ?? "" : "").Contains("round 1 scores");
        Assert.DoesNotContain(_discord.Messages.ToList(), Summary);   // not until game 3
        Play(s, lobby);
        await s.PendingPosts;

        var summary = Assert.Single(_discord.Messages.ToList(), Summary);
        Assert.True(summary.Bot);                                                    // the bot, in the private results channel
        Assert.DoesNotContain(_discord.Webhooks, w => FakeDiscord.Title(w.Payload).Contains("scores"));   // never the players' channel
        var embed = summary.Embeds!.Value[0];
        Assert.Equal("LJ's lobby — round 1 scores", embed.GetProperty("title").GetString());
        string text = embed.GetProperty("description").GetString()!;
        Assert.DoesNotContain("Moving on", text);
        Assert.DoesNotContain("move on", text);
        foreach (var p in lobby) Assert.Contains($". {p.Name}** · ", text);          // everyone's score
        Assert.Contains("↳ ", text);
        Assert.Contains("Kill win +", text);                                         // where the points came from
        Assert.Contains(s.Pump(), r => r.Text.Contains("round 1 is done for this lobby"));
    }

    [Fact]
    public void Point_sources_add_up_each_rule_over_the_round()
    {
        Assert.Equal("Kill", StandingsFormatter.SourceOf("Kill x2"));
        Assert.Equal("Tasks", StandingsFormatter.SourceOf("Tasks 80%"));
        Assert.Equal("Reads", StandingsFormatter.SourceOf("Reads 3/4 on impostors"));
        Assert.Equal("Died first", StandingsFormatter.SourceOf("Died first: 50% of crew average 4.2"));
        Assert.Equal("Lost to tasks", StandingsFormatter.SourceOf("Lost to tasks (left the game)"));
        var game = new GameRecord { Winner = "Crewmates", Players = { new GamePlayer { Key = "a", PointBreakdown = { new PointLine("Kill x2", 4), new PointLine("Kill win", 3) } } } };
        var again = new GameRecord { Winner = "Impostors", Players = { new GamePlayer { Key = "a", PointBreakdown = { new PointLine("Kill", 2) } } } };
        var sources = StandingsFormatter.PointSources(new[] { game, again })["a"];
        Assert.Equal(("Kill", 6.0), sources[0]);
        Assert.Equal(("Kill win", 3.0), sources[1]);
    }

    [Fact]
    public async Task After_each_game_the_lobby_standings_show_the_round_the_cut_line_and_the_running_total()
    {
        var s = Session(TournamentCode());
        var lobby = Lobby();
        s.RunCommand("!r1");
        Play(s, lobby, "ImpostorByKill");                          // Alice and Bob win round 1
        s.RunCommand("!r2");
        Play(s, lobby);                                            // crew wins round 2
        await s.PendingPosts;

        var standings = _discord.Webhooks.Last(w => FakeDiscord.Title(w.Payload).Contains("standings"));
        Assert.Equal("LJ — Round 2 standings · after game 1 of 3", FakeDiscord.Title(standings.Payload));
        string table = FakeDiscord.Description(standings.Payload);
        Assert.Contains("── top 2 move on ──", table);
        var carl = table.Split('\n').Single(l => l.Contains("Carl"));
        Assert.Matches(@"Carl\s+5\s+3 ", carl);                  // round 2: +5 task win; total: 0 − 2 + 5
        Assert.Equal(2, _discord.Messages.Count(m => m.Channel == "results" && m.File != null));
    }

    [Fact]
    public async Task Referees_adjust_points_from_discord_and_see_a_tick()
    {
        var s = Session(TournamentCode());
        var lobby = Lobby();
        s.RunCommand("!r1");
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
    public async Task Host_voids_a_restarted_game_mid_game_and_it_counts_for_nothing()
    {
        var s = Session(TournamentCode());
        var lobby = Lobby();
        s.RunCommand("!r1");
        s.GameStarted("ABCDEF", "Polus", lobby);
        Assert.True(s.RunCommand("!void lights bug"));
        Assert.Contains(s.Pump(), r => r.Public && r.Text.StartsWith("Game LJ-1 is void"));
        s.GameAbandoned(lobby);
        var replay = Play(s, lobby);                               // the restart counts
        await s.PendingPosts;

        Assert.Equal("LJ-2", replay.Name);
        Assert.Equal(1, s.Store.GamesRecorded);
        Assert.Contains(_discord.Webhooks, w => FakeDiscord.Title(w.Payload).StartsWith("VOID · Game LJ-1"));
        Assert.Equal(2, s.Combined!.GameRecords.Count);             // both on record…
        Assert.Equal(1, s.Combined.Store.GamesRecorded);           // …one counted
        Assert.Contains(_discord.Messages, m => m.Channel == "results" && m.Content.Contains("VOID: lights bug"));
    }

    [Fact]
    public async Task A_game_waits_for_the_host_to_verify_it_before_discord_sees_it()
    {
        var s = Session(TournamentCode(), verify: true);
        var lobby = Lobby();
        s.RunCommand("!r1");
        var game = Play(s, lobby);
        await s.PendingPosts;
        Assert.DoesNotContain(_discord.Messages, m => m.Content?.Contains("LJ-1") == true || m.File?.Contains(game.Id) == true);
        Assert.Contains(s.Pump(), r => !r.Public && r.Text.StartsWith("Game LJ-1 is waiting for you"));
        Assert.Equal(new[] { "LJ-1" }, s.WaitingGames.Select(g => g.Name));
        Assert.Equal(1, s.Store.GamesRecorded);   // counted on this PC already

        // A referee's change before it goes: Red loses 2 points.
        var red = game.Players.First(p => p.ColorId == 0);
        double before = red.Points;
        s.RunCommand($"!adjust LJ-1 {red.Key} -2 left the call");
        Assert.Equal(before - 2, s.WaitingGames[0].ByKey(red.Key)!.Points);
        Assert.Contains(s.WaitingGames[0].ByKey(red.Key)!.PointBreakdown, l => l.Rule == "Referee: left the call" && l.Points == -2);
        await s.PendingPosts;
        Assert.Equal(0, s.Combined?.Store.GamesRecorded ?? 0);   // still nothing shared

        s.RunCommand("!verify all");
        await s.PendingPosts;
        Assert.Empty(s.WaitingGames);
        Assert.Equal(1, s.Combined!.Store.GamesRecorded);
        Assert.Equal(before - 2, s.Combined.GameRecords.Single().ByKey(red.Key)!.Points);
    }

    [Fact]
    public async Task A_game_voided_before_it_was_verified_never_reaches_discord()
    {
        var s = Session(TournamentCode(), verify: true);
        var lobby = Lobby();
        s.RunCommand("!r1");
        Play(s, lobby);
        s.RunCommand("!void lobby restarted");
        Assert.Equal(0, s.Store.GamesRecorded);
        Assert.True(s.WaitingGames.Single().Voided);
        s.RunCommand("!verify LJ-1");
        await s.PendingPosts;
        Assert.Empty(s.WaitingGames);
        Assert.DoesNotContain(_discord.Messages, m => m.Content?.Contains("LJ-1") == true);
        Assert.Contains(s.Pump(), r => r.Text.StartsWith("Verified: the voided game stays off Discord"));
    }

    [Fact]
    public async Task Waiting_games_survive_a_restart_and_reset_puts_every_game_away()
    {
        var s = Session(TournamentCode(), verify: true);
        var lobby = Lobby();
        s.RunCommand("!r1");
        Play(s, lobby);
        Play(s, lobby);
        await s.PendingPosts;
        _sessions.Remove(s);
        s.Dispose();
        var again = Session(TournamentCode(), verify: true);
        Assert.Equal(new[] { "LJ-1", "LJ-2" }, again.WaitingGames.Select(g => g.Name));
        Assert.Equal(2, again.Store.GamesRecorded);

        int posted = _discord.Messages.Count;
        again.RunCommand("!resetpoints");
        await again.PendingPosts;
        Assert.Empty(again.WaitingGames);
        Assert.Equal(0, again.Store.GamesRecorded);
        Assert.Equal(0, again.GamesThisRound);
        Assert.Equal(posted, _discord.Messages.Count);   // Discord untouched
        Assert.Empty(Directory.GetFiles(_dir.Path, "game-*.json", SearchOption.TopDirectoryOnly));
        Assert.Equal(2, Directory.GetFiles(_dir.Path, "game-*.json", SearchOption.AllDirectories).Count(f => f.Contains("reset ")));
        Assert.Contains(again.Pump(), r => r.Text.StartsWith("All points reset: 2 games put away"));
    }

    [Fact]
    public async Task Host_can_void_the_last_game_afterwards_and_unvoid_it()
    {
        var s = Session(TournamentCode());
        var lobby = Lobby();
        s.RunCommand("!r1");
        Play(s, lobby);
        await s.PendingPosts;
        Assert.Equal(1, s.Store.GamesRecorded);

        s.RunCommand("!void wrong settings");
        await s.PendingPosts;
        Assert.Equal(0, s.Store.GamesRecorded);
        Assert.Empty(s.Store.Players);
        Assert.Equal(0, s.Combined!.Store.GamesRecorded);

        s.RunCommand("!unvoid");
        await s.PendingPosts;
        Assert.Equal(1, s.Store.GamesRecorded);
        Assert.Equal(1, s.Combined!.Store.GamesRecorded);
    }

    [Fact]
    public async Task Referee_void_sticks_even_if_the_message_is_deleted_and_only_unvoid_undoes_it()
    {
        var s = Session(TournamentCode());
        var lobby = Lobby();
        s.RunCommand("!r1");
        Play(s, lobby);
        await s.PendingPosts;

        _discord.Say("results", "!void LJ-1 restarted after a crash", _clock.Now);
        s.RunCommand("!lb");
        await s.PendingPosts;
        Assert.Equal(0, s.Combined!.Store.GamesRecorded);
        var command = _discord.Messages.Single(m => m.Content.StartsWith("!void"));
        Assert.Contains(("results", command.Id, "✅"), _discord.Reactions);
        Assert.Contains(_discord.Messages, m => m.Content.StartsWith("Game LJ-1 is void") && m.File != null);
        s.Pump();
        Assert.Equal(0, s.Store.GamesRecorded);                    // the local totals follow

        _discord.Messages.Remove(command);                         // deleting it undoes nothing
        s.RunCommand("!lb");
        await s.PendingPosts;
        Assert.Equal(0, s.Combined!.Store.GamesRecorded);

        _clock.Advance(60);
        _discord.Say("results", "!unvoid LJ-1", _clock.Now);
        s.RunCommand("!lb");
        await s.PendingPosts;
        Assert.Equal(1, s.Combined!.Store.GamesRecorded);
        Assert.Equal(1, _discord.Messages.Count(m => m.Content.StartsWith("Game LJ-1 is void")));   // reposted once
        s.Pump();
        Assert.Equal(1, s.Store.GamesRecorded);
    }

    [Fact]
    public async Task A_disconnect_prompts_the_host_and_notes_it_for_referees()
    {
        var s = Session(TournamentCode());
        var lobby = Lobby();
        s.RunCommand("!r1");
        s.GameStarted("ABCDEF", "Polus", lobby);
        _clock.Advance(72);
        s.PlayerLeft(4);
        Assert.Contains(s.Pump(), r => !r.Public && r.Text.StartsWith("Eve left at 1:12, before the first meeting. To restart, press Void in The Button"));
        s.MeetingCalled(0, null);
        s.PlayerLeft(5);
        Assert.Contains(s.Pump(), r => r.Text.StartsWith("Finn left at 1:12. The game plays on"));
        s.PlayerLeft(5);                                            // reported twice by the game: once is enough
        Assert.Empty(s.Pump());
        await s.PendingPosts;
        Assert.Contains(_discord.Messages, m => m.Channel == "results" && m.Content == "Disconnect · LJ-1: Eve (crewmate) left at 1:12, before the first meeting.");
    }

    private static ChannelMessage Typed(string text, string channel = "results") =>
        new() { Id = "9" + Math.Abs(text.GetHashCode()), ChannelId = channel, Content = text };

    [Fact]
    public async Task The_organiser_builds_next_round_lobbies_and_starts_the_round_from_the_results_channel()
    {
        foreach (var g in LobbyPlanTests.RoundOne(3)) { g.TournamentId = "fall-cup"; g.Tournament = "Fall Cup"; g.StartedUtc = _clock.Now.AddHours(-1); _discord.AddGame("results", g); }
        var code = TournamentCode();
        code.Lead = true;
        var lead = Session(code);
        var other = Session(TournamentCode());
        var delays = new List<TimeSpan>();
        lead.Delay = t => { delays.Add(t); return Task.CompletedTask; };

        foreach (var s in new[] { lead, other }) s.HandleChannelMessage(Typed("!lobbies 2"));
        await lead.PendingPosts;
        lead.Pump();
        Assert.Null(other.Plan);                                   // only the lead answers
        var plan = lead.Plan!;
        Assert.Equal(6, plan.Lobbies[0].Players.Count);            // top 2 of 3 lobbies: one final lobby
        var posted = _discord.Messages.Last(m => m.Content.StartsWith("Round 2 lobbies"));
        Assert.Contains("**Final**", posted.Content);

        lead.HandleChannelMessage(Typed("!host final LJ"));
        await lead.PendingPosts;
        Assert.Equal("LJ", lead.Plan!.Lobbies[0].Host);

        foreach (var s in new[] { lead, other }) s.HandleChannelMessage(Typed("!start 2 in 10"));
        await lead.PendingPosts;
        await Task.Delay(50);
        await lead.PendingPosts;
        Assert.Equal(2, lead.Round);
        Assert.Equal(2, other.Round);                              // every lobby switches round
        var pings = _discord.Webhooks.Where(w => w.Payload.TryGetProperty("content", out var c) && c.GetString()!.Contains("Round 2")).ToList();
        Assert.Equal(2, pings.Count);                              // the call, then "starting now" after the wait
        Assert.StartsWith("**Round 2 starts in 10 minutes!**", pings[0].Payload.GetProperty("content").GetString());
        Assert.Contains("<@100>", pings[0].Payload.GetProperty("content").GetString());
        Assert.Contains("100", pings[0].Payload.GetProperty("allowed_mentions").GetProperty("users").EnumerateArray().Select(u => u.GetString()));
        Assert.StartsWith("**Round 2 is starting now!**", pings[1].Payload.GetProperty("content").GetString());
        Assert.Equal(new[] { TimeSpan.FromMinutes(10) }, delays);
    }

    [Fact]
    public async Task At_a_round_start_each_lobby_says_where_to_go()
    {
        var settings = new TrackerSettings { LiveStatus = false, ControlPort = -1 };
        settings.AutoMute.VoiceChannelId = "vc55";
        var presence = new VoicePresenceState("g1") { Connected = true };
        var s = new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(_http), () => _clock.Now, new FakeVoiceApi(), presence, TournamentCode());
        _sessions.Add(s);
        s.VoiceTick(VoicePhase.Lobby, Lobby("LJ"), "QWERTY", "Polus");
        s.HandleChannelMessage(Typed("!start 3"));
        await s.PendingPosts;
        var join = _discord.Webhooks.Select(w => w.Payload.TryGetProperty("content", out var c) ? c.GetString() : null).Last(c => c != null && c.Contains("join"));
        Assert.Equal("**Round 3 · LJ's lobby:** join voice <#vc55> · lobby code `QWERTY` · Polus", join);
    }

    [Fact]
    public async Task Lead_passes_to_whoever_types_lead_in_game()
    {
        var code = TournamentCode();
        code.Lead = true;
        var first = Session(code);
        var second = Session(TournamentCode());
        Assert.True(first.IsLead);
        second.VoiceTick(VoicePhase.Lobby, Lobby("Sam"), "ABCDEF", "Polus");
        second.RunCommand("!lead");
        await second.PendingPosts;
        var note = _discord.Messages.ToList().Last(m => m.Content?.StartsWith("Lead · ") == true);   // the live data can be posted around it
        Assert.StartsWith("Lead · Sam answers channel commands", note.Content);
        first.HandleChannelMessage(new ChannelMessage { Id = note.Id, ChannelId = "results", Content = note.Content, AuthorIsBot = true });
        Assert.False(first.IsLead);
        Assert.True(second.IsLead);
    }

    [Fact]
    public async Task Games_are_recorded_for_the_replay_viewer_on_this_pc_only()
    {
        var s = Session(TournamentCode());
        var lobby = Lobby();
        s.RunCommand("!r1");
        s.GameStarted("ABCDEF", "Polus", lobby);
        s.ReplayMapLoaded(new ReplayMap { Walls = { new[] { 0f, 0f, 5f, 0f } }, Rooms = { new ReplayRoom { Name = "Office", Area = new[] { 0f, 0f, 1f, 0f, 1f, 1f } } } });
        for (int i = 0; i < 30; i++)
        {
            _clock.Advance(0.1);
            s.RecordPositions(lobby.Select(p => new ReplayPosition(p.PlayerId, p.PlayerId + i * 0.1f, 2f, false, p.PlayerId == 0 && i > 20, false)));
        }
        s.Kill(0, 2);
        s.GameEnded("ImpostorByKill", lobby);
        await s.PendingPosts;

        Assert.DoesNotContain(_discord.Messages, m => m.File != null && m.File.StartsWith("tt-replay-"));   // never to Discord
        string saved = Directory.GetFiles(_dir.Path, "tt-replay-*.json.gz", SearchOption.AllDirectories).Single();
        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(saved), System.IO.Compression.CompressionMode.Decompress);
        var replay = JsonDocument.Parse(gz).RootElement;
        Assert.Equal("LJ-1", replay.GetProperty("name").GetString());
        Assert.Equal(30, replay.GetProperty("frames").GetArrayLength());
        var last = replay.GetProperty("frames")[29];
        Assert.Equal(1 + 3 * 6, last.GetArrayLength());
        Assert.Equal(2, last[3].GetDouble());                      // Alice in a vent
        Assert.Equal("Office", replay.GetProperty("geometry").GetProperty("rooms")[0].GetProperty("name").GetString());
        Assert.Contains(replay.GetProperty("events").EnumerateArray(), e => e.GetProperty("kind").GetString() == "kill");
    }

    [Fact]
    public void Void_messages_are_read_carefully()
    {
        var v = RefereeVoid.TryParse("!void LJ-3 lobby restarted", "1", DateTime.UtcNow)!;
        Assert.True(v.Void);
        Assert.Equal("LJ-3", v.Game);
        Assert.Equal("lobby restarted", v.Reason);
        Assert.False(RefereeVoid.TryParse("!UNVOID LJ-3", "1", DateTime.UtcNow)!.Void);
        Assert.Null(RefereeVoid.TryParse("!void", "1", DateTime.UtcNow));
        Assert.Null(RefereeVoid.TryParse("!voidx LJ-3", "1", DateTime.UtcNow));
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
        s.RunCommand("!r1");
        Play(s, lobby);
        s.RunCommand("!r2");
        Play(s, lobby.Where(p => p.PlayerId != 1 && p.PlayerId != 4).ToList());   // Bob and Eve knocked out
        s.RunCommand("!servers");
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
    public async Task Setup_reloads_the_code_The_Button_saved_and_asks_for_a_restart()
    {
        var s = Session(TournamentCode());
        bool restarted = false;
        s.RestartRequested += () => restarted = true;

        s.RunCommand("!setup");
        Assert.Contains(s.Pump(), r => r.Text.StartsWith("Current setup: Fall Cup (tournament host, automute on)."));
        Assert.False(restarted);

        var prelim = new SetupCode { TournamentId = "p", TournamentName = "Prelims", Server = "S", Webhook = Webhook };
        SetupCode.Save(_dir.Path, prelim.Encode());
        s.RunCommand("!setup reload");
        Assert.True(restarted);
        Assert.Contains(s.Pump(), r => r.Text.StartsWith("Setup applied: Prelims"));
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

    [Fact]
    public async Task Referee_voids_in_a_preliminary_channel_stick_after_the_message_is_deleted()
    {
        _discord.AddGame("prelims", Prelim("oct-sus", "October: Sus Squad", "Sus Squad", "Soggy", 1, "HumansByTask"));
        _discord.AddGame("prelims", Prelim("oct-sus", "October: Sus Squad", "Sus Squad", "Soggy", 2, "HumansByTask"));
        _discord.Say("prelims", "!void Soggy-1 restarted", _clock.Now);

        await Job().UpdateAsync(new[] { "prelims" });
        string Footer() => _discord.Messages.Single(m => m.Embeds.HasValue).Embeds!.Value[0].GetProperty("footer").GetProperty("text").GetString()!;
        Assert.StartsWith("1 game ·", Footer());
        var command = _discord.Messages.Single(m => m.Content.StartsWith("!void"));
        Assert.Contains(("prelims", command.Id, "✅"), _discord.Reactions);

        _discord.Messages.Remove(command);
        await Job().UpdateAsync(new[] { "prelims" });
        Assert.StartsWith("1 game ·", Footer());
    }

    private static JsonElement Payload(FakeDiscord.Msg m) => JsonDocument.Parse("{\"embeds\":" + m.Embeds!.Value.GetRawText() + "}").RootElement;
}
