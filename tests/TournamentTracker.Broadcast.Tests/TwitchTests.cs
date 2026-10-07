using System.Net;
using System.Text.Json;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Part 23: Twitch predictions, polls, !sus, Chat Detective and channel points.</summary>
public class TwitchTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly CasterDesk _desk;
    private TwitchDirector _tw = null!;
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public TwitchTests()
    {
        _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
    }

    public void Dispose() { _tw?.Dispose(); _desk.Dispose(); _dir.Dispose(); }

    private async Task<FakeTwitchApi> Start(int seed = 1, Action<TwitchSettings>? set = null)
    {
        _tw = new TwitchDirector(_desk, _dir.Path, new HttpClient(new FakeHttp()), () => _clock.Now, new Random(seed)) { FakeAudience = false };
        _tw.Settings.TestMode = true;
        set?.Invoke(_tw.Settings);
        await _tw.ConnectAsync();
        return (FakeTwitchApi)_tw.Api!;
    }

    private static readonly string[] Names = { "Jake", "Maria", "Sam", "Priya", "Leo", "Nina", "Omar", "Chloe" };

    /// <summary>Players 0 and 1 are the impostors; <paramref name="dead"/> are dead.</summary>
    private static object[] People(int count, params int[] dead) => Enumerable.Range(0, count)
        .Select(i => (object)new { id = i, name = Names[i], color = i + 2, key = Names[i].ToLower() + "#1", imp = i < 2, dead = dead.Contains(i) }).ToArray();

    private void Send(string lobby, string type, string? kind, object data, string game = "LJ-1")
    {
        var msg = JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        msg["type"] = type; msg["lobby"] = lobby; msg["round"] = 1; msg["game"] = game;
        msg["t"] = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds();
        if (kind != null) msg["kind"] = kind;
        _desk.Apply(JsonSerializer.Serialize(msg));
    }

    private void Snap(string lobby, string phase, object[] players, string game = "LJ-1") =>
        Send(lobby, "snap", null, new { phase, crewAlive = 6, impAlive = 2, alive = 8, taskPct = 20, players }, game);

    private static object Who(int i) => new { id = i, name = Names[i], color = i + 2, imp = i < 2 };

    private JsonElement Overlay() => JsonSerializer.SerializeToElement(_tw.Overlay(), Camel);

    [Fact]
    public async Task A_game_prediction_opens_at_its_start_on_stream_and_resolves_at_its_end()
    {
        var api = await Start();
        Snap("LJ", "lobby", People(8)); Snap("MAL", "lobby", People(8), "MAL-1");
        _desk.Show("LJ");
        Send("MAL", "event", "gameStart", new { map = "Polus" }, "MAL-1");      // not on stream: nothing
        Send("LJ", "event", "gameStart", new { map = "Polus" });
        Snap("LJ", "ingame", People(8));
        await _tw.TickAsync();
        var p = _tw.Prediction!;
        Assert.Equal("LJ: Impostors or Crewmates?", p.Title);
        Assert.Single(api.Calls, c => c.StartsWith("prediction "));
        Assert.Equal(new[] { "Crewmates", "Impostors" }, p.Options.Select(o => o.Title).OrderBy(x => x));
        Assert.EndsWith(" 90", api.Calls.Single(c => c.StartsWith("prediction ")));         // locks after 90 s

        // The window closes, and the game ends.
        _clock.Advance(91);
        await _tw.TickAsync();
        Assert.Equal("locked", p.Status);
        Assert.Equal("LOCKED", Overlay().GetProperty("active").GetProperty("status").GetString()!.ToUpperInvariant());
        Send("LJ", "event", "gameEnd", new { winner = "Crewmates", abandoned = false, impostors = new[] { Who(0), Who(1) } });
        await _tw.TickAsync();
        string crew = p.Options.Single(o => o.Title == "Crewmates").Id;
        Assert.Contains($"resolved {p.Id} {crew}", api.Calls);
        Assert.Equal("prediction", Overlay().GetProperty("result").GetProperty("type").GetString());
        Assert.Equal("Crewmates!", Overlay().GetProperty("result").GetProperty("text").GetString());
    }

    [Fact]
    public async Task An_interrupted_game_cancels_its_prediction_so_everyone_gets_their_points_back()
    {
        var api = await Start();
        Snap("LJ", "lobby", People(8));
        _desk.Show("LJ");
        Send("LJ", "event", "gameStart", new { map = "Polus" });
        await _tw.TickAsync();
        // The referee's game restarts mid-game (a new run of the mod, back in the lobby).
        _desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = "LJ", round = 1, phase = "lobby", t = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds(), src = "run2", seq = 1 }));
        await _tw.TickAsync();
        Assert.Contains($"canceled {_tw.Prediction!.Id}", api.Calls);
        Assert.Equal("canceled", _tw.Prediction.Status);
    }

    [Fact]
    public async Task Everything_waits_for_the_stream_delay_and_only_one_prediction_runs_at_a_time()
    {
        var api = await Start(set: s => s.DelaySeconds = 6);
        Snap("LJ", "lobby", People(8));
        _desk.Show("LJ");
        Send("LJ", "event", "gameStart", new { map = "Polus" });
        await _tw.TickAsync();
        Assert.Null(_tw.Prediction);
        _clock.Advance(6);
        await _tw.TickAsync();
        Assert.NotNull(_tw.Prediction);
        Snap("MAL", "ingame", People(8), "MAL-1"); Snap("LJ", "ingame", People(8));
        Assert.Equal("A prediction is already running.", await _tw.StartRoundPredictionAsync("first"));
        Assert.Single(api.Calls, c => c.StartsWith("prediction "));
    }

    [Fact]
    public async Task A_meeting_on_stream_gets_a_poll_of_the_living_players_and_chat_hears_the_truth_only_when_the_game_shows_it()
    {
        var api = await Start();
        Snap("LJ", "ingame", People(8, 2, 3, 4, 5));      // four alive
        _desk.Show("LJ");
        Send("LJ", "event", "meeting", new { caller = Who(6) });
        await _tw.TickAsync();
        var poll = _tw.Poll!;
        Assert.Equal("Who's the impostor? (LJ)", poll.Title);
        Assert.Equal(new[] { "Chloe", "Jake", "Maria", "Omar" }, poll.Options.Select(o => o.Title).OrderBy(x => x));
        Assert.Null(_tw.Vote);                                          // up to five alive: the poll, no !sus

        // Votes come in; the ejection ends the poll and shows what chat thought, not whether it was right.
        _tw.Handle("channel.poll.progress", JsonSerializer.SerializeToElement(new { id = poll.Id, choices = poll.Options.Select(o => new { id = o.Id, votes = o.Title == "Omar" ? 7 : 1 }) }));
        Assert.Equal(70, Overlay().GetProperty("active").GetProperty("options").EnumerateArray().Single(o => o.GetProperty("title").GetString() == "Omar").GetProperty("pct").GetInt32());
        Send("LJ", "event", "eject", new { ejected = Who(6), wasImpostor = false, skipped = false });
        await _tw.TickAsync();
        Assert.Contains($"endpoll {poll.Id}", api.Calls);
        var r = Overlay().GetProperty("result");
        Assert.Equal("CHAT THINKS", r.GetProperty("title").GetString());
        Assert.Equal("Omar was ejected", r.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("right").ValueKind);

        _clock.Advance(20);
        Send("LJ", "event", "gameEnd", new { winner = "Impostors", abandoned = false, impostors = new[] { Who(0), Who(1) } });
        await _tw.TickAsync();
        r = Overlay().GetProperty("result");
        Assert.Equal("LJ: WAS CHAT RIGHT?", r.GetProperty("title").GetString());
        Assert.False(r.GetProperty("lines")[0].GetProperty("right").GetBoolean());
    }

    [Fact]
    public async Task With_more_than_five_alive_chat_votes_with_sus_one_vote_each_and_detectives_score_at_the_end()
    {
        var api = await Start();
        Snap("LJ", "ingame", People(8));
        _desk.Show("LJ");
        Send("LJ", "event", "meeting", new { caller = Who(6) });
        await _tw.TickAsync();
        Assert.Null(_tw.Poll);
        var vote = _tw.Vote!;
        Assert.Equal(8, vote.Options.Count);
        Assert.Contains(api.ChatSent, m => m.StartsWith("Who's the impostor in LJ? Type !sus and a name:"));

        _tw.Chat("1", "alice", "!sus jake");
        _tw.Chat("2", "bob", "!SUS Mar");                               // a start of a name
        _tw.Chat("3", "cat", "!sus green");                            // a colour (Jake is colour 2: green)
        _tw.Chat("1", "alice", "!sus sam");                            // changes her mind: still one vote
        _tw.Chat("4", "dan", "!sus nobody");                           // not a player: ignored
        _tw.Chat("5", "eve", "jake is sus");                           // not a vote
        Assert.Equal(3, vote.Voters.Count);
        Assert.Equal(new[] { ("alice", "sam#1"), ("bob", "maria#1"), ("cat", "jake#1") }, vote.Voters.Values.OrderBy(v => v.Login).Select(v => (v.Login, v.Option)));
        var active = Overlay().GetProperty("active");
        Assert.Equal("chat", active.GetProperty("type").GetString());
        Assert.Equal(3, active.GetProperty("voters").GetInt32());

        Send("LJ", "event", "eject", new { ejected = Who(1), wasImpostor = true });
        await _tw.TickAsync();
        Send("LJ", "event", "gameEnd", new { winner = "Crewmates", abandoned = false, impostors = new[] { Who(0), Who(1) } });
        await _tw.TickAsync();
        var det = _tw.Detectives();
        Assert.Equal(new[] { "bob", "cat" }, det.Select(d => d.Login).OrderBy(x => x));       // Maria and Jake were the impostors
        Assert.All(det, d => Assert.Equal(1, d.Correct));
        Assert.Equal(1, _tw.Detectives().Count(d => d.Login == "cat"));
        // Kept for the tournament.
        Assert.True(File.Exists(Path.Combine(_dir.Path, "twitch-detectives.json")));
    }

    [Fact]
    public async Task Option_order_never_follows_who_the_impostors_are()
    {
        int first = 0, notFirst = 0;
        for (int seed = 0; seed < 40; seed++)
        {
            _tw?.Dispose();
            await Start(seed, s => s.Features["chatVote"] = false);
            Snap("LJ", "ingame", People(8, 2, 3, 4, 5));
            _desk.Show("LJ");
            Send("LJ", "event", "meeting", new { caller = Who(6) }, "LJ-" + seed);
            await _tw.TickAsync();
            var poll = _tw.Poll!;
            if (poll.Options[0].Value is "jake#1" or "maria#1") first++; else notFirst++;
            Assert.DoesNotContain(poll.Options, o => o.Title.Contains("mpostor"));
            await _tw.EndPollAsync();
        }
        Assert.InRange(first, 8, 32);           // about half the time, like anyone else
        Assert.InRange(notFirst, 8, 32);
    }

    [Fact]
    public async Task Which_lobby_next_becomes_a_card_the_caster_approves()
    {
        var api = await Start();
        foreach (var l in new[] { "LJ", "MAL", "Soggy" }) Snap(l, "ingame", People(8), l + "-1");
        _desk.Show("LJ");
        Assert.StartsWith("Poll open", await _tw.StartLobbyPollAsync());
        var poll = _tw.Poll!;
        Assert.Equal(new[] { "MAL", "Soggy" }, poll.Options.Select(o => o.Title).OrderBy(x => x));       // not the one on already
        _tw.Handle("channel.poll.end", JsonSerializer.SerializeToElement(new { id = poll.Id, status = "completed", choices = poll.Options.Select(o => new { id = o.Id, votes = o.Title == "Soggy" ? 12 : 4 }) }));
        await Task.Delay(50);
        var card = JsonSerializer.SerializeToElement(_desk.State(), Camel).GetProperty("cards").EnumerateArray().Single(c => c.GetProperty("rule").GetString() == "twitchLobby");
        Assert.Equal("show:Soggy", card.GetProperty("action").GetString());
        Assert.Equal("Chat picked Soggy next (75% of 16 votes).", card.GetProperty("text").GetString());
        Assert.Equal("LJ", _desk.OnAir.Slots[0]);                      // nothing switches until the caster says
    }

    [Fact]
    public async Task Channel_point_shoutouts_are_filtered_matched_to_the_roster_and_approved_and_replays_play()
    {
        var api = await Start();
        _desk.Roster.Extra.Add(new RosterEntry { Name = "Jake Rivera", InGameNames = new() { "Jakey" } });
        string shout = api.Calls.Count(c => c.StartsWith("reward ")) == 2 ? "" : "missing";
        Assert.Equal("", shout);
        var state = JsonSerializer.SerializeToElement(_tw.State(), Camel);
        // Find the rewards' ids from what test mode made.
        var rewardIds = typeof(TwitchDirector).GetField("_rewards", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_tw) as Dictionary<string, string>;
        string replayId = rewardIds!.Single(x => x.Value == "replay").Key, shoutId = rewardIds.Single(x => x.Value == "shoutout").Key;
        void Redeem(string id, string reward, string input) => _tw.Handle("channel.channel_points_custom_reward_redemption.add",
            JsonSerializer.SerializeToElement(new { id, user_login = "viewer", user_name = "Viewer", user_input = input, reward = new { id = reward } }));

        Redeem("r1", shoutId, "sh1t player");
        Redeem("r2", shoutId, "jakey");
        Redeem("r3", shoutId, "Zed Unknown");
        Redeem("r4", replayId, "that kill");
        await _tw.TickAsync();
        Assert.Contains("refunded r1", api.Calls);                    // the filter turns it away, points back
        var reds = JsonSerializer.SerializeToElement(_tw.State(), Camel).GetProperty("redemptions").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);
        Assert.Equal("denied", reds["r1"].GetProperty("status").GetString());
        Assert.Equal("Jake Rivera", reds["r2"].GetProperty("playerName").GetString());
        Assert.Equal("not on the tournament list", reds["r3"].GetProperty("flag").GetString());

        Assert.Equal("Player card on stream.", await _tw.DecideAsync("r2", true));
        Assert.Contains("fulfilled r2", api.Calls);
        Assert.Contains("Shoutout to Jake Rivera, requested by @Viewer!", api.ChatSent);
        Assert.NotNull(_desk.CardNow());
        await _tw.DecideAsync("r3", false);
        Assert.Contains("refunded r3", api.Calls);

        string? played = null;
        _tw.Clips = () => new List<(string, string, string)> { ("clip9", "Jake killed Maria", "LJ") };
        _tw.PlayReplay = id => { played = id; return Task.FromResult("Playing."); };
        Assert.Equal("Playing.", await _tw.DecideAsync("r4", true));
        Assert.Equal("clip9", played);
        Assert.Contains("fulfilled r4", api.Calls);
    }

    [Fact]
    public async Task Twitch_off_does_nothing_and_the_round_brings_the_mvp_poll_and_detective_shoutouts()
    {
        var api = await Start(set: s => s.Off = true);
        Snap("LJ", "lobby", People(8));
        _desk.Show("LJ");
        Send("LJ", "event", "gameStart", new { map = "Polus" });
        await _tw.TickAsync();
        Assert.Null(_tw.Prediction);
        Assert.Null(_tw.Overlay());

        _tw.Settings.Off = false;
        var games = new List<TournamentTracker.Stats.GameRecord>
        {
            new() { Host = "LJ", GameNumber = 1, Round = 1, Winner = "Crewmates", Players = { new() { Key = "jake#1", Name = "Jake", Points = 9 }, new() { Key = "maria#1", Name = "Maria", Points = 4 }, new() { Key = "sam#1", Name = "Sam", Points = 2 } } },
        };
        _desk.ExternalGames = () => games;
        _desk.Tick();
        _desk.ShowIntermission();
        await _tw.TickAsync();
        Assert.Equal("Round 1 MVP?", _tw.Poll?.Title);
    }

    [Fact]
    public async Task Helix_gets_what_Twitch_allows_and_signs_in_again_when_the_token_runs_out()
    {
        var http = new FakeHttp();
        new TwitchToken { AccessToken = "old", RefreshToken = "refresh1", UserId = "42", Login = "chan", Scopes = TwitchAuth.Scopes.ToList() }.Save(Path.Combine(_dir.Path, "twitch-token.json"));
        var auth = new TwitchAuth(new HttpClient(http), () => "client1", Path.Combine(_dir.Path, "twitch-token.json"), () => _clock.Now);
        var api = new HelixApi(new HttpClient(http), auth, () => "client1");

        http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.Unauthorized, "{\"message\":\"Invalid OAuth token\"}"));
        http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.OK, "{\"access_token\":\"new\",\"refresh_token\":\"refresh2\",\"expires_in\":14000,\"scope\":[]}"));
        http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"p1\",\"choices\":[{\"id\":\"a\",\"title\":\"x\"},{\"id\":\"b\",\"title\":\"y\"}]}]}"));
        var (id, choices) = await api.CreatePollAsync(new string('Q', 80), new[] { "A very long player name that goes on", "B", "C", "D", "E", "F" }, 5);
        Assert.Equal("p1", id);
        var reqs = http.Requests.ToList();
        Assert.Equal("Bearer old", reqs[0].Auth);
        Assert.Contains("grant_type=refresh_token", reqs[1].Body);
        Assert.Contains("refresh_token=refresh1", reqs[1].Body);
        Assert.Equal("Bearer new", reqs[2].Auth);
        Assert.Equal("https://api.twitch.tv/helix/polls", reqs[2].Url);
        var body = JsonDocument.Parse(reqs[2].Body).RootElement;
        Assert.Equal("42", body.GetProperty("broadcaster_id").GetString());
        Assert.Equal(60, body.GetProperty("title").GetString()!.Length);                    // cut to Twitch's 60
        Assert.Equal(5, body.GetProperty("choices").GetArrayLength());                      // at most 5
        Assert.InRange(body.GetProperty("choices")[0].GetProperty("title").GetString()!.Length, 20, 25);
        Assert.Equal(15, body.GetProperty("duration").GetInt32());                          // at least 15 s
        Assert.Equal("refresh2", TwitchToken.Load(Path.Combine(_dir.Path, "twitch-token.json"))!.RefreshToken);

        http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"pr\",\"outcomes\":[{\"id\":\"o1\",\"title\":\"x\"},{\"id\":\"o2\",\"title\":\"y\"}]}]}"));
        await api.CreatePredictionAsync("LJ: Impostors or Crewmates? And a lot more words", new[] { "Impostors", "Crewmates" }, 5000);
        body = JsonDocument.Parse(http.Requests.Last().Body).RootElement;
        Assert.Equal(45, body.GetProperty("title").GetString()!.Length);
        Assert.Equal(1800, body.GetProperty("prediction_window").GetInt32());
        await api.EndPredictionAsync("pr", "RESOLVED", "o2");
        body = JsonDocument.Parse(http.Requests.Last().Body).RootElement;
        Assert.Equal("o2", body.GetProperty("winning_outcome_id").GetString());
        Assert.Equal(HttpMethod.Patch, http.Requests.Last().Method);
        await api.UpdateRedemptionAsync("rw", "rd", false);
        Assert.Equal("https://api.twitch.tv/helix/channel_points/custom_rewards/redemptions?broadcaster_id=42&reward_id=rw&id=rd", http.Requests.Last().Url);
        Assert.Equal("CANCELED", JsonDocument.Parse(http.Requests.Last().Body).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Without_Affiliate_or_Partner_it_says_what_wont_work_and_the_chat_vote_still_does()
    {
        var http = new FakeHttp();
        new TwitchToken { AccessToken = "t", RefreshToken = "r", UserId = "42", Login = "chan", Scopes = TwitchAuth.Scopes.ToList() }.Save(Path.Combine(_dir.Path, "twitch-token.json"));
        http.Default = req => req.RequestUri!.AbsolutePath.EndsWith("/users")
            ? FakeHttp.Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"42\",\"login\":\"chan\",\"display_name\":\"Chan\",\"broadcaster_type\":\"\"}]}")
            : FakeHttp.Json(HttpStatusCode.OK, "{\"user_id\":\"42\",\"login\":\"chan\",\"scopes\":[" + string.Join(",", TwitchAuth.Scopes.Select(s => $"\"{s}\"")) + "]}");
        _tw = new TwitchDirector(_desk, _dir.Path, new HttpClient(http), () => _clock.Now, new Random(3)) { StartEvents = false };
        _tw.Settings.ClientId = "client1";
        string msg = await _tw.ConnectAsync();
        Assert.Contains("isn't Affiliate or Partner", msg);
        Assert.False(_tw.CanPoll);
        Snap("LJ", "ingame", People(5));
        _desk.Show("LJ");
        Send("LJ", "event", "meeting", new { caller = Who(3) });
        await _tw.TickAsync();
        Assert.Null(_tw.Poll);
        Assert.NotNull(_tw.Vote);                                       // auto mode falls back to !sus
    }

    [Fact]
    public async Task Test_mode_fakes_an_audience_votes_chat_and_redemptions()
    {
        await Start();
        _tw.FakeAudience = true;
        foreach (var l in new[] { "LJ", "MAL", "Soggy" }) Snap(l, "ingame", People(8), l + "-1");
        _desk.Show("LJ");
        await _tw.StartLobbyPollAsync();
        Send("LJ", "event", "meeting", new { caller = Who(6) });
        for (int i = 0; i < 25; i++) { _clock.Advance(1); await _tw.TickAsync(); }
        Assert.True(_tw.Vote!.Voters.Count > 0);                                     // fake viewers typed !sus
        Assert.Contains(JsonSerializer.SerializeToElement(_tw.State(), Camel).GetProperty("redemptions").EnumerateArray(), r => r.GetProperty("user").GetString()!.Length > 0);
        Assert.Contains(JsonSerializer.SerializeToElement(_tw.State(), Camel).GetProperty("log").EnumerateArray(), l => l.GetProperty("text").GetString()!.StartsWith("Poll: Which lobby"));
    }

    [Fact]
    public async Task EventSub_subscribes_to_what_it_needs_and_follows_Twitch_to_a_new_connection()
    {
        var api = new FakeTwitchApi();
        using var es = new TwitchEventSub(api, () => "42");
        var got = new List<string>();
        es.Event += (t, e) => got.Add(t + ":" + e.GetProperty("id").GetString());
        await es.HandleAsync("{\"metadata\":{\"message_type\":\"session_welcome\"},\"payload\":{\"session\":{\"id\":\"s1\"}}}");
        Assert.Equal(TwitchEventSub.Types.Length, api.Calls.Count(c => c.StartsWith("subscribe ")));
        Assert.Contains("subscribe channel.chat.message", api.Calls);
        Assert.Equal("connected", es.State);
        await es.HandleAsync("{\"metadata\":{\"message_type\":\"notification\"},\"payload\":{\"subscription\":{\"type\":\"channel.poll.progress\"},\"event\":{\"id\":\"p1\"}}}");
        Assert.Equal(new[] { "channel.poll.progress:p1" }, got);
        Assert.Equal("wss://elsewhere", await es.HandleAsync("{\"metadata\":{\"message_type\":\"session_reconnect\"},\"payload\":{\"session\":{\"id\":\"s1\",\"reconnect_url\":\"wss://elsewhere\"}}}"));
    }
}
