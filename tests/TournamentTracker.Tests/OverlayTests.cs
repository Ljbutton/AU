using System.Net;
using System.Net.Http;
using System.Text.Json;
using TournamentTracker.Discord;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

public class OverlayTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    public void Dispose() => _dir.Dispose();

    private TournamentSession Session(int port = 0) =>
        new(new TrackerSettings { LiveStatus = false, PublicChat = true, ControlPort = -1, OverlayPort = port, TournamentName = "Cup" }, _dir.Path, NullLog.Instance,
            new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));

    private static JsonElement Json(object state) => JsonDocument.Parse(JsonSerializer.Serialize(state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).RootElement;

    [Fact]
    public void The_normal_view_never_gives_away_kills_or_roles()
    {
        using var s = Session();
        var lobby = Players.Lobby();
        s.VoiceTick(VoicePhase.Lobby, lobby);
        s.GameStarted("ABCDEF", "Polus", lobby);
        s.Kill(0, 2);
        lobby[2].IsDead = true;
        s.VoiceTick(VoicePhase.Tasks, lobby);

        var safe = Json(s.OverlayState(VoicePhase.Tasks, lobby, "Polus", full: false));
        Assert.All(safe.GetProperty("players").EnumerateArray(), p =>
        {
            Assert.False(p.GetProperty("dead").GetBoolean());          // Carl's body hasn't been found
            Assert.False(p.GetProperty("impostor").GetBoolean());
        });
        Assert.Empty(safe.GetProperty("feed").EnumerateArray());      // no kill, no "impostors are…"

        var fullView = Json(s.OverlayState(VoicePhase.Tasks, lobby, "Polus", full: true));
        Assert.True(fullView.GetProperty("players")[2].GetProperty("dead").GetBoolean());
        Assert.True(fullView.GetProperty("players")[0].GetProperty("impostor").GetBoolean());
        Assert.Contains(fullView.GetProperty("feed").EnumerateArray(), e => e.GetProperty("text").GetString()!.Contains("killed"));

        s.MeetingCalled(3, 2);
        s.VoiceTick(VoicePhase.Meeting, lobby);                       // the meeting reveals the body
        safe = Json(s.OverlayState(VoicePhase.Meeting, lobby, "Polus", full: false));
        Assert.True(safe.GetProperty("players")[2].GetProperty("dead").GetBoolean());
        Assert.Contains(safe.GetProperty("feed").EnumerateArray(), e => e.GetProperty("text").GetString()!.Contains("reported"));
    }

    [Fact]
    public async Task Overlay_on_serves_the_page_and_state_on_this_computer()
    {
        using var s = Session(port: 0);                                // any free port
        s.RunCommand("!overlay on");
        Assert.NotNull(s.Overlay);
        Assert.Contains(s.Pump(), r => r.Text.StartsWith("Stream overlay: add a Browser source in OBS with http://localhost:"));
        s.VoiceTick(VoicePhase.Lobby, Players.Lobby());

        using var http = new HttpClient();
        string page = await http.GetStringAsync(s.Overlay!.Url);
        var head = await http.GetAsync(s.Overlay.Url + "crew/17.png");
        Assert.Equal("image/png", head.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TimeSpan.FromDays(1), head.Headers.CacheControl?.MaxAge);       // the heads never change
        Assert.Equal(0x89, (await head.Content.ReadAsByteArrayAsync())[0]);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(s.Overlay.Url + "crew/18.png")).StatusCode);
        Assert.Contains("<title>Tournament overlay</title>", page);
        var live = await http.GetAsync(s.Overlay.Url + "state");
        Assert.True(live.Headers.CacheControl?.NoStore);
        var state = JsonDocument.Parse(await live.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Cup", state.GetProperty("tournament").GetString());
        Assert.Equal(6, state.GetProperty("players").GetArrayLength());

        // Only the request line is read: big headers (cookies for localhost) and bare \n line endings still work.
        var url = new Uri(s.Overlay.Url);
        using (var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", url.Port))
        {
            var stream = tcp.GetStream();
            var ask = System.Text.Encoding.ASCII.GetBytes("GET /state HTTP/1.1\nHost: localhost\nCookie: " + new string('c', 20000) + "\n\n");
            await stream.WriteAsync(ask);
            var answer = await new StreamReader(stream).ReadToEndAsync();
            Assert.StartsWith("HTTP/1.1 200", answer);
            Assert.Contains("\"tournament\":\"Cup\"", answer);
        }

        s.RunCommand("!overlay off");
        Assert.Null(s.Overlay);
    }
}
