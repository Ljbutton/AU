using System.Net;
using System.Text;
using System.Text.Json;
using TournamentTracker.Control;
using TournamentTracker.Discord;
using Xunit;

namespace TournamentTracker.Tests;

public class ControlApiTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    public void Dispose() => _dir.Dispose();

    private TournamentSession Session(int port = 0) =>
        new(new TrackerSettings { LiveStatus = false, TournamentName = "Cup", ControlPort = port }, _dir.Path, NullLog.Instance,
            new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));

    [Fact]
    public void Commands_from_the_app_never_go_to_the_lobby_chat()
    {
        using var s = Session(port: -1);
        s.VoiceTick(Voice.VoicePhase.Lobby, Players.Lobby());
        Assert.True(s.RunCommand("r2"));                          // from the app, no "!" needed
        Assert.Equal(2, s.Round);
        var replies = s.Pump();
        Assert.NotEmpty(replies);
        Assert.All(replies, r => Assert.False(r.Public));           // "Round 2 starts now!" stays with the host
    }

    [Fact]
    public async Task The_app_gets_point_totals_for_the_host_and_referees()
    {
        var settings = new TrackerSettings { LiveStatus = false, TournamentName = "Cup", ControlPort = 0, Mode = TrackerMode.Tournament, AdvanceCount = 3, GamesPerRound = 3 };
        using var s = new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));
        var lobby = Players.Lobby();
        lobby[3].IsHost = true;
        s.RunCommand("r1");
        s.GameStarted("X", "Polus", lobby);
        s.Kill(0, 2);
        s.GameEnded("ImpostorByKill", lobby);
        _clock.Advance(5);
        s.VoiceTick(Voice.VoicePhase.Lobby, lobby);

        var control = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir.Path, ControlServer.FileName))).RootElement;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{control.GetProperty("port").GetInt32()}/") };
        http.DefaultRequestHeaders.Add("X-TT-Token", control.GetProperty("token").GetString());
        var points = JsonDocument.Parse(await http.GetStringAsync("api/status")).RootElement.GetProperty("points");
        var sections = points.GetProperty("sections");
        Assert.Equal("Round 1 · Dana's lobby", sections[0].GetProperty("title").GetString());
        Assert.Equal(3, sections[0].GetProperty("cut").GetInt32());
        var top = sections[0].GetProperty("rows")[0];
        Assert.Equal("Alice", top.GetProperty("name").GetString());
        Assert.Equal(6, top.GetProperty("points").GetDouble());
        Assert.True(top.GetProperty("advancing").GetBoolean());
        Assert.Equal(1, sections.GetArrayLength());                 // in round 1 the running total would only repeat it

        s.RunCommand("r2");
        s.GameStarted("X", "Polus", lobby);
        s.GameEnded("HumansByTask", lobby);
        _clock.Advance(5);
        s.VoiceTick(Voice.VoicePhase.Lobby, lobby);
        sections = JsonDocument.Parse(await http.GetStringAsync("api/status")).RootElement.GetProperty("points").GetProperty("sections");
        Assert.Equal("Running total", sections[1].GetProperty("title").GetString());
        Assert.Equal(6, sections[1].GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task The_app_reads_the_status_and_runs_commands_with_the_token_from_control_json()
    {
        using var s = Session();
        s.VoiceTick(Voice.VoicePhase.Lobby, Players.Lobby(), "ABCDEF", "Polus");
        var control = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir.Path, ControlServer.FileName))).RootElement;
        int port = control.GetProperty("port").GetInt32();
        string token = control.GetProperty("token").GetString()!;
        Assert.Equal(s.Control!.Port, port);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var denied = await http.GetAsync("api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        http.DefaultRequestHeaders.Add("X-TT-Token", token);
        var status = JsonDocument.Parse(await http.GetStringAsync("api/status")).RootElement;
        Assert.Equal("Cup", status.GetProperty("tournament").GetString());
        Assert.Equal("ABCDEF", status.GetProperty("lobbyCode").GetString());
        Assert.Equal(6, status.GetProperty("players").GetArrayLength());

        // The command runs on the game thread: keep "the game" pumping while the app waits.
        var post = http.PostAsync("api/command", new StringContent("{\"command\":\"r3\"}", Encoding.UTF8, "application/json"));
        while (!post.IsCompleted) { s.Pump(); await Task.Delay(10); }
        var answer = JsonDocument.Parse(await (await post).Content.ReadAsStringAsync()).RootElement;
        Assert.True(answer.GetProperty("ok").GetBoolean());
        Assert.Contains(answer.GetProperty("replies").EnumerateArray(), r => r.GetString()!.StartsWith("Round 3 started"));
        Assert.Equal(3, s.Round);
        status = JsonDocument.Parse(await http.GetStringAsync("api/status")).RootElement;
        Assert.Equal(3, status.GetProperty("round").GetInt32());

        var activity = JsonDocument.Parse(await http.GetStringAsync("api/activity?since=0")).RootElement;
        Assert.True(activity.GetProperty("last").GetInt64() > 0);
        Assert.Contains(activity.GetProperty("lines").EnumerateArray(), l => l.GetProperty("text").GetString()!.StartsWith("Round 3"));
    }

    [Fact]
    public async Task A_frozen_game_gets_a_clear_answer_instead_of_hanging()
    {
        using var s = Session();
        var control = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir.Path, ControlServer.FileName))).RootElement;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{control.GetProperty("port").GetInt32()}/") };
        http.DefaultRequestHeaders.Add("X-TT-Token", control.GetProperty("token").GetString());
        var answer = JsonDocument.Parse(await (await http.PostAsync("api/command", new StringContent("{\"command\":\"r3\"}"))).Content.ReadAsStringAsync()).RootElement;
        Assert.False(answer.GetProperty("ok").GetBoolean());                // nobody pumped
    }
}
