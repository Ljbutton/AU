using System.Net;
using System.Text.Json;
using TournamentTracker.Discord;
using TournamentTracker.Stats;
using Xunit;

namespace TournamentTracker.Tests;

public class DiscordRestTests
{
    private readonly FakeHttp _http = new();
    private readonly List<TimeSpan> _delays = new();
    private readonly DiscordRest _rest;

    public DiscordRestTests()
    {
        _rest = new DiscordRest(new HttpClient(_http), NullLog.Instance, "https://discord.test/api",
            (t, _) => { _delays.Add(t); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Mute_is_a_patch_on_the_member()
    {
        _http.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var result = await _rest.SetVoiceStateAsync("tok", "g1", "u1", mute: true, deaf: false);

        Assert.True(result.Ok);
        var sent = Assert.Single(_http.Requests);
        Assert.Equal("PATCH", sent.Method.Method);
        Assert.Equal("https://discord.test/api/guilds/g1/members/u1", sent.Url);
        Assert.Equal("Bot tok", sent.Auth);
        Assert.Equal("""{"mute":true,"deaf":false}""", sent.Body);
    }

    [Fact]
    public async Task Rate_limit_is_waited_out_and_retried()
    {
        _http.Responses.Enqueue(_ => FakeHttp.Json((HttpStatusCode)429, """{"message":"You are being rate limited.","retry_after":1.5,"global":false}"""));
        _http.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await _rest.SetVoiceStateAsync("tok", "g1", "u1", true, true);

        Assert.True(result.Ok);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Contains(TimeSpan.FromSeconds(1.5), _delays);
    }

    [Fact]
    public async Task Exhausted_bucket_delays_the_next_request()
    {
        _http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.OK, "{}", ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset-After", "2")));
        await _rest.SetVoiceStateAsync("tok", "g1", "u1", true, true);
        await _rest.SetVoiceStateAsync("tok", "g1", "u2", true, true);
        Assert.Single(_delays);
        Assert.InRange(_delays[0].TotalSeconds, 1.5, 2.01);
    }

    [Fact]
    public async Task Discord_error_codes_are_surfaced()
    {
        _http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.BadRequest, """{"message":"Target user is not connected to voice.","code":40032}"""));
        var result = await _rest.SetVoiceStateAsync("tok", "g1", "u1", true, true);
        Assert.False(result.Ok);
        Assert.True(result.IsNotInVoice);
    }

    [Fact]
    public async Task Member_search_parses_names()
    {
        _http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.OK,
            """[{"user":{"id":"42","username":"bob","global_name":"Bobby"},"nick":null}]"""));
        var found = await _rest.SearchMembersAsync("tok", "g1", "bo b");
        var m = Assert.Single(found);
        Assert.Equal("42", m.Id);
        Assert.Equal("Bobby", m.DisplayName);
        Assert.EndsWith("/guilds/g1/members/search?limit=10&query=bo%20b", _http.Requests.Single().Url);
    }

    [Fact]
    public async Task Webhook_waits_for_the_message_and_blocks_mentions()
    {
        await _rest.ExecuteWebhookAsync("https://discord.test/api/webhooks/1/abc", new WebhookMessage { Content = "@everyone hi" });
        var sent = Assert.Single(_http.Requests);
        Assert.Equal("https://discord.test/api/webhooks/1/abc?wait=true", sent.Url);
        using var doc = JsonDocument.Parse(sent.Body);
        Assert.Equal(0, doc.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("embeds", out _));
    }
}

public class ReportFormatterTests
{
    private static GameRecord PlayedGame(int extraEvents = 0)
    {
        var clock = new FakeClock();
        var t = new GameTracker(new ScoringRules());
        t.Start(7, "Cup", "ABCDEF", "The Skeld", Players.Lobby(), clock.Now);
        t.Kill(0, 2, clock.Now.AddSeconds(40));
        t.MeetingCalled(3, 2, clock.Now.AddSeconds(50));
        t.VotingComplete(new[] { new VoteCast(3, 0), new VoteCast(4, 0) }, 0, false, clock.Now.AddSeconds(90));
        for (int i = 0; i < extraEvents; i++)
            t.Sabotage(1, "System" + i, clock.Now.AddSeconds(100 + i));
        return t.End("HumansByVote", Outcome.Crewmates, Players.Lobby(), clock.Now.AddSeconds(200 + extraEvents))!;
    }

    [Fact]
    public void Game_report_has_summary_and_timeline()
    {
        var msg = ReportFormatter.GameReport(PlayedGame());
        Assert.Equal(3, msg.Embeds!.Count);
        var summary = msg.Embeds[0];
        Assert.Equal("Game 7 — Crewmates win", summary.Title);
        Assert.Contains("Red Alice", summary.Description);
        Assert.Contains(summary.Fields!, f => f.Name == "Result" && f.Value == "Impostors voted out");
        Assert.Contains(summary.Fields!, f => f.Name == "Length" && f.Value == "03:20");
        Assert.Equal("Points", msg.Embeds[1].Title);
        Assert.Contains("Dana", msg.Embeds[1].Description);
        Assert.Contains("Correct vote out +2", msg.Embeds[1].Description);
        Assert.Contains("00:40  Red (Alice) killed Green (Carl) (first blood)", msg.Embeds[2].Description);
    }

    [Fact]
    public void Long_games_stay_inside_discord_limits()
    {
        var msg = ReportFormatter.GameReport(PlayedGame(extraEvents: 400));
        var timeline = msg.Embeds![2].Description!;
        Assert.True(timeline.Length <= Embed.DescriptionLimit);
        Assert.StartsWith("```", timeline);
        Assert.EndsWith("```", timeline);
        Assert.Contains("more events", timeline);
        Assert.Contains("Crewmates win", timeline);   // the ending survives the cut
        int total = msg.Embeds.Sum(e => (e.Title?.Length ?? 0) + (e.Description?.Length ?? 0)
            + (e.Fields?.Sum(f => f.Name.Length + f.Value.Length) ?? 0) + (e.Footer?.Text.Length ?? 0));
        Assert.True(total <= 6000, $"embeds total {total} chars");
    }

    [Fact]
    public void Leaderboard_lists_players_in_order()
    {
        var store = new StatsStore { Tournament = "Cup" };
        store.Apply(PlayedGame());
        var embed = ReportFormatter.Leaderboard(store, 3).Embeds!.Single();
        Assert.Equal("Cup — Leaderboard", embed.Title);
        var lines = embed.Description!.Split('\n');
        Assert.Equal(3 + 3, lines.Length);           // fence, header, 3 rows, fence
        Assert.Contains("1 game", embed.Footer!.Text);
    }

    [Fact]
    public void Clock_formats_long_games_with_hours() =>
        Assert.Equal("1:02:03", ReportFormatter.Clock(3723));
}
