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
    public void Chat_commands_are_off_by_default_and_nothing_goes_to_the_lobby_chat()
    {
        using var s = Session(port: -1);
        var host = Players.Lobby()[0];
        host.IsHost = true;
        Assert.False(s.HandleChat(host, true, "!r2"));
        Assert.Equal(0, s.Round);

        s.VoiceTick(Voice.VoicePhase.Lobby, Players.Lobby());
        Assert.True(s.RunCommand("r2"));                          // from the app, no "!" needed
        Assert.Equal(2, s.Round);
        var replies = s.Pump();
        Assert.NotEmpty(replies);
        Assert.All(replies, r => Assert.False(r.Public));           // "Round 2 starts now!" stays with the host
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
