using System.Text.Json;
using TournamentTracker.Control;
using TournamentTracker.Discord;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Host authority (the "+25" host-only mod flag): the setting, its command, the app's status and the turned-down messages.</summary>
public class HostAuthorityTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    public void Dispose() => _dir.Dispose();

    private TournamentSession Session(int port = -1) =>
        new(new TrackerSettings { TournamentName = "Cup", LiveStatus = false, ControlPort = port }, _dir.Path, NullLog.Instance,
            new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));

    [Fact]
    public void It_is_on_by_default_and_the_host_can_turn_it_off_for_good()
    {
        using (var s = Session())
        {
            Assert.True(s.HostAuthority);
            Assert.True(s.RunCommand("hostauth off"));
            Assert.False(s.HostAuthority);
            var reply = Assert.Single(s.Pump());
            Assert.Contains("OFF", reply.Text);
            Assert.Contains("main menu", reply.Text);           // it's sent as Among Us connects
            Assert.False(reply.Public);
        }
        using (var s = Session())
        {
            Assert.False(s.HostAuthority);                      // remembered
            s.RunCommand("hostauth on");
            Assert.True(s.HostAuthority);
            Assert.Contains("main menu", s.Pump().Single().Text);
        }
        using (var s = Session()) Assert.True(s.HostAuthority);
    }

    [Fact]
    public void An_older_saved_state_keeps_it_on()
    {
        string id = new TrackerSettings { TournamentName = "Cup" }.EffectiveTournamentId;
        File.WriteAllText(Path.Combine(_dir.Path, $"state-{id}.json"), "{\"Round\":2}");
        using var s = Session();
        Assert.Equal(2, s.Round);
        Assert.True(s.HostAuthority);
    }

    [Fact]
    public void Changing_it_while_hosting_says_the_lobby_keeps_what_it_had()
    {
        using var s = Session();
        s.HostAuthorityActive = true;                           // hosting with it on
        s.RunCommand("hostauth off");
        Assert.Contains("keeps it until you leave to the main menu", s.Pump().Single().Text);
        s.HostAuthorityActive = false;
        s.RunCommand("hostauth on");
        Assert.Contains("doesn't have it yet", s.Pump().Single().Text);
        s.HostAuthorityActive = null;                           // in the menus
        s.RunCommand("hostauth");
        Assert.DoesNotContain("this lobby", s.Pump().Single().Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_app_sees_the_setting_and_whether_this_lobby_has_it()
    {
        using var s = Session(port: 0);
        s.VoiceTick(VoicePhase.Lobby, Players.Lobby(), "ABCDEF", "Polus");
        var control = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir.Path, ControlServer.FileName))).RootElement;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{control.GetProperty("port").GetInt32()}/") };
        http.DefaultRequestHeaders.Add("X-TT-Token", control.GetProperty("token").GetString());

        var auth = JsonDocument.Parse(await http.GetStringAsync("api/status")).RootElement.GetProperty("hostAuthority");
        Assert.True(auth.GetProperty("on").GetBoolean());
        Assert.Equal(JsonValueKind.Null, auth.GetProperty("active").ValueKind);   // not told yet: not hosting

        s.HostAuthorityActive = true;
        _clock.Advance(1);
        s.VoiceTick(VoicePhase.Lobby, Players.Lobby(), "ABCDEF", "Polus");
        auth = JsonDocument.Parse(await http.GetStringAsync("api/status")).RootElement.GetProperty("hostAuthority");
        Assert.True(auth.GetProperty("active").GetBoolean());
    }

    [Fact]
    public void A_turned_down_message_is_on_Home_once_a_minute_per_player_and_kind()
    {
        using var s = Session();
        s.HostOnlyMessageBlocked("Bob", "MurderPlayer");
        s.HostOnlyMessageBlocked("Bob", "MurderPlayer");
        s.HostOnlyMessageBlocked("Bob", "MurderPlayer");
        var notice = Assert.Single(s.NoticeTexts());
        Assert.Contains("Bob", notice);
        Assert.Contains("MurderPlayer", notice);
        s.HostOnlyMessageBlocked("Carl", "SetName");
        Assert.Equal(2, s.NoticeTexts().Count);
        _clock.Advance(61);
        s.HostOnlyMessageBlocked("Bob", "MurderPlayer");
        Assert.Equal(3, s.NoticeTexts().Count);
        Assert.Empty(s.Pump());                                 // never in the chat (the referee's chat is on stream)
    }

    [Fact]
    public void A_referee_without_host_authority_is_said_on_Home_once()
    {
        using var s = Session();
        s.RefereeWithoutHostAuthority();
        s.RefereeWithoutHostAuthority();
        Assert.Contains("main menu", Assert.Single(s.NoticeTexts()));
        Assert.Empty(s.Pump());
    }
}
