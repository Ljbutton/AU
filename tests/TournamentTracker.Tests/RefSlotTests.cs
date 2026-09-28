using TournamentTracker.Discord;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

public class RefSlotTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeVoiceApi _voice = new();
    private readonly FakeClock _clock = new();
    private readonly VoicePresenceState _presence = new("g1") { Connected = true };
    private readonly List<TournamentSession> _sessions = new();

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        _dir.Dispose();
    }

    private TournamentSession Session()
    {
        var settings = new TrackerSettings { TournamentName = "Cup", LiveStatus = false };
        settings.AutoMute.Enabled = true;
        settings.AutoMute.GuildId = "g1";
        settings.AutoMute.BotTokens.Add("tok");
        settings.AutoMute.AutoLinkByName = false;
        settings.AutoMute.MuteSpectators = true;
        settings.AutoMute.DelayGameStart = 0;
        var s = new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(new FakeHttp()), () => _clock.Now, _voice, _presence);
        _sessions.Add(s);
        return s;
    }

    private static List<PlayerSnapshot> ElevenPlayers()
    {
        var lobby = Players.Lobby();
        lobby.AddRange(new[] { Players.Make(6, "Gus", 6), Players.Make(7, "Hana", 7), Players.Make(8, "Ivan", 8), Players.Make(9, "Jade", 9) });
        var host = Players.Make(10, "LJ", 10);
        host.IsHost = true;
        lobby.Add(host);
        return lobby;
    }

    [Fact]
    public void The_host_can_become_the_referee_ghost_or_pick_someone()
    {
        var s = Session();
        var lobby = ElevenPlayers();
        s.VoiceTick(VoicePhase.Lobby, lobby);
        Assert.True(s.HandleChat(lobby[10], fromHost: true, "!refslot on"));
        Assert.Equal((byte)10, s.RefSlotPlayerId(lobby));
        Assert.Contains("Referee ghost slot ON", s.Pump().First().Text);

        s.HandleChat(lobby[10], true, "!refslot green");
        Assert.Equal((byte)2, s.RefSlotPlayerId(lobby));
        Assert.Contains(s.Pump(), r => r.Public && r.Text == "Carl is the referee ghost for the next games.");

        Assert.False(s.HandleChat(lobby[0], false, "!refslot on"));
        s.HandleChat(lobby[10], true, "!refslot off");
        Assert.Null(s.RefSlotPlayerId(lobby));
    }

    [Fact]
    public void The_referee_is_left_out_of_the_game_record_and_points()
    {
        var s = Session();
        var lobby = ElevenPlayers();
        s.HandleChat(lobby[10], true, "!refslot on");
        s.GameStarted("ABCDEF", "Polus", lobby);
        lobby[10].IsDead = true;                                   // the ghost
        var game = s.GameEnded("HumansByTask", lobby)!;
        Assert.Equal(10, game.Players.Count);
        Assert.DoesNotContain(game.Players, p => p.Name == "LJ");
        Assert.DoesNotContain(s.Store.Players.Values, p => p.Name == "LJ");
    }

    [Fact]
    public async Task The_referee_is_never_muted_even_as_a_ghost_or_a_spectator()
    {
        var s = Session();
        var lobby = ElevenPlayers();
        s.Links.Link(lobby[10].Key, "LJ", "300", "lj");
        s.Links.Link(lobby[0].Key, "Alice", "100", "alice");
        _presence.Dispatch("GUILD_CREATE", System.Text.Json.JsonDocument.Parse(
            """{"id":"g1","voice_states":[{"user_id":"300","channel_id":"vc"},{"user_id":"100","channel_id":"vc"},{"user_id":"900","channel_id":"vc"}],"members":[]}""").RootElement);
        s.HandleChat(lobby[10], true, "!refslot on");
        lobby[10].IsDead = true;
        s.VoiceTick(VoicePhase.Meeting, lobby);                   // dead players are muted in meetings…
        await Wait.Until(() => _voice.Calls.Any(c => c.User == "900") && _voice.Calls.Any(c => c.User == "100"));
        Assert.DoesNotContain(_voice.Calls, c => c.User == "300" && c.State.Mute);   // …but not the referee
    }

    [Fact]
    public void The_status_shows_the_referee_apart_from_the_players()
    {
        var referee = Players.Make(10, "LJ", 10);
        var msg = StatusFormatter.Build(new StatusInfo { Phase = VoicePhase.Tasks, Referee = referee });
        Assert.Contains("🎥 Referee (ghost): **Cyan** LJ", msg.Embeds![0].Description);
    }
}
