using System.Net;
using System.Text.Json;
using TournamentTracker.Sheets;
using TournamentTracker.Stats;
using Xunit;

namespace TournamentTracker.Tests;

public class SheetsTests : IDisposable
{
    private const string Url = "https://script.google.test/macros/s/abc/exec";
    private readonly TempDir _dir = new();
    private readonly FakeHttp _http = new();
    private readonly FakeClock _clock = new();
    private readonly List<TournamentSession> _sessions = new();

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        _dir.Dispose();
    }

    private TournamentSession Session()
    {
        var settings = new TrackerSettings { TournamentName = "Fall Cup", GoogleSheetsUrl = Url, GoogleSheetsSecret = "hunter2" };
        var s = new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(_http), () => _clock.Now);
        _sessions.Add(s);
        return s;
    }

    private static HttpResponseMessage Ok() => FakeHttp.Json(HttpStatusCode.OK, """{"ok":true}""");

    private GameRecord PlayGame(TournamentSession s)
    {
        var lobby = Players.Lobby();
        s.GameStarted("ABCDEF", "Polus", lobby);
        _clock.Advance(60);
        s.Kill(0, 2);
        s.MeetingCalled(3, 2);
        s.VotingComplete(new[] { new VoteCast(3, 0), new VoteCast(4, 0) }, 0, false);
        _clock.Advance(60);
        return s.GameEnded("HumansByVote", lobby)!;
    }

    [Fact]
    public async Task A_finished_game_is_sent_with_the_secret_and_every_player()
    {
        _http.Default = _ => Ok();
        var s = Session();
        var game = PlayGame(s);
        await s.Sheets!.Pending;

        var sent = Assert.Single(_http.Requests, r => r.Url == Url);
        using var doc = JsonDocument.Parse(sent.Body);
        var root = doc.RootElement;
        Assert.Equal("hunter2", root.GetProperty("secret").GetString());
        Assert.Equal(SheetsSync.GameId(game), root.GetProperty("game").GetProperty("id").GetString());
        Assert.StartsWith("fall-cup-0001-", SheetsSync.GameId(game));
        Assert.Equal("Crewmates", root.GetProperty("game").GetProperty("winner").GetString());
        Assert.True(root.GetProperty("game").GetProperty("counted").GetBoolean());

        var players = root.GetProperty("players");
        Assert.Equal(6, players.GetArrayLength());
        var dana = players.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Dana");
        Assert.Equal("Crewmate", dana.GetProperty("team").GetString());
        Assert.Equal(1, dana.GetProperty("correctVoteOuts").GetInt32());
        Assert.Equal(game.ById(3)!.Points, dana.GetProperty("points").GetDouble());
        Assert.Equal(game.ById(3)!.PointBreakdown.Count, dana.GetProperty("breakdown").GetArrayLength());
    }

    [Fact]
    public async Task Undelivered_games_wait_in_the_outbox_and_go_out_with_the_next_one()
    {
        _http.Responses.Enqueue(_ => FakeHttp.Json(HttpStatusCode.OK, """{"ok":false,"error":"Wrong secret"}"""));
        var s = Session();
        PlayGame(s);
        await s.Sheets!.Pending;
        Assert.Equal(1, s.Sheets.OutboxCount);

        _http.Default = _ => Ok();
        PlayGame(s);
        await s.Sheets.Pending;
        Assert.Equal(0, s.Sheets.OutboxCount);

        var ids = _http.Requests.Where(r => r.Url == Url)
            .Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("game").GetProperty("number").GetInt32()).ToList();
        Assert.Equal(new[] { 1, 2, 1 }, ids);   // game 1 failed, game 2 sent, then game 1 retried
    }

    [Fact]
    public async Task A_web_page_instead_of_json_counts_as_a_failure()
    {
        _http.Default = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Sign in</html>") };
        var s = Session();
        PlayGame(s);
        await s.Sheets!.Pending;
        Assert.Equal(1, s.Sheets.OutboxCount);
    }

    [Fact]
    public async Task The_outbox_is_retried_when_the_game_starts_up_again()
    {
        _http.Default = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") };
        var first = Session();
        PlayGame(first);
        await first.Sheets!.Pending;
        Assert.Equal(1, first.Sheets.OutboxCount);

        _http.Default = _ => Ok();
        var next = Session();                   // e.g. the host restarted Among Us
        await next.Sheets!.Pending;
        Assert.Equal(0, next.Sheets.OutboxCount);
    }

    [Fact]
    public async Task Sheetsync_resends_every_saved_game()
    {
        _http.Default = _ => Ok();
        var s = Session();
        PlayGame(s);
        PlayGame(s);
        await s.Sheets!.Pending;
        _http.Requests.Clear();

        var lobby = Players.Lobby();
        Assert.True(s.HandleChat(lobby[0], fromHost: true, "!sheetsync"));
        Assert.Equal("Sending 2 games to Google Sheets.", s.Pump().Single().Text);
        await s.Sheets.Pending;
        Assert.Equal(2, _http.Requests.Count(r => r.Url == Url));
        Assert.False(s.HandleChat(lobby[2], fromHost: false, "!sheetsync"));
    }

    [Fact]
    public void Payload_fixture_for_the_apps_script_test_matches_the_current_format()
    {
        // sheets/test/fixtures holds payloads the Apps Script test runs against; keep them in step.
        string fixture = Path.Combine(AppContext.BaseDirectory, "../../../../../sheets/test/fixtures/payload-1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(fixture));
        var fromFixture = doc.RootElement.GetProperty("players")[0].EnumerateObject().Select(p => p.Name).ToList();

        var game = new GameTracker(new ScoringRules());
        game.Start(1, "Cup", "X", "Polus", Players.Lobby(), _clock.Now);
        var record = game.End("HumansByTask", Outcome.Crewmates, Players.Lobby(), _clock.Now)!;
        var json = JsonSerializer.Serialize(SheetsSync.Build(record), SheetsSync.Json);
        using var current = JsonDocument.Parse(json);
        var fromCode = current.RootElement.GetProperty("players")[0].EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(fromCode, fromFixture);
    }
}
