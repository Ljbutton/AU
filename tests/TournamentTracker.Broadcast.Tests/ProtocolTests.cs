using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Broadcast;
using TournamentTracker.Discord;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>The broadcast feed from the mod, through the shared protocol, into TT Broadcast (docs/broadcast-protocol.md).</summary>
public class ProtocolTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly TournamentSession _host;
    private readonly CasterDesk _desk;
    private long _read;

    public ProtocolTests()
    {
        var settings = new TrackerSettings { TournamentName = "Fall Cup", LiveStatus = false, ControlPort = -1, Mode = TrackerMode.Tournament, LobbyLabel = "LJ" };
        _host = new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));
        _host.RunCommand("r2");
        _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
    }

    public void Dispose() { _desk.Dispose(); _host.Dispose(); _dir.Dispose(); }

    private static FeedFrame Frame(VoicePhase phase = VoicePhase.Tasks) => new()
    {
        Phase = phase,
        Players = Enumerable.Range(0, 6).Select(i => new FeedPlayer { Id = (byte)i, X = i * 10, Y = 0, Room = "Cafeteria" }).ToList(),
        Camera = new FeedCamera { X = 0, Y = 0, HalfWidth = 5.33f, HalfHeight = 3 },
    };

    /// <summary>What the host's page sends: the mod's messages since last time, as JSON text over the data channel.</summary>
    private List<string> Wire()
    {
        var doc = JsonDocument.Parse(_host.FeedSince(_read)).RootElement;
        _read = doc.GetProperty("last").GetInt64();
        return doc.GetProperty("items").EnumerateArray().Select(e => e.GetRawText()).ToList();
    }

    [Fact]
    public void A_message_from_the_mod_round_trips_through_the_protocol_into_the_lobby_board()
    {
        var lobby = Players.Lobby();
        _host.GameStarted("QWERTY", "Polus", lobby);
        _host.FeedTick(Frame());
        _clock.Advance(1.2);
        _host.Kill(0, 2);
        lobby[2].IsDead = true;
        _host.FeedTick(Frame());

        var wire = Wire();
        Assert.NotEmpty(wire);
        foreach (var json in wire)
        {
            var m = JsonDocument.Parse(json).RootElement;
            // Every message: the version, its type, the lobby and the time; the mod's are numbered.
            Assert.Equal(FeedProtocol.Version, FeedProtocol.VersionOf(m));
            Assert.Equal("LJ", m.GetProperty(FeedProtocol.Lobby).GetString());
            Assert.True(FeedProtocol.FromMod(m.GetProperty(FeedProtocol.Type).GetString()));
            Assert.True(m.TryGetProperty(FeedProtocol.Src, out _) && m.TryGetProperty(FeedProtocol.Seq, out _) && m.TryGetProperty(FeedProtocol.T, out _));
            if (m.GetProperty(FeedProtocol.Type).GetString() == FeedProtocol.Types.Event)
                Assert.Contains(m.GetProperty(FeedProtocol.Kind).GetString(), FeedProtocol.Kinds.All);
            _desk.Apply(json);
        }

        var board = _desk.Board.Lobby("LJ")!;
        Assert.Equal("ingame", board.Phase);
        Assert.Equal(2, board.Round);
        Assert.Equal(3, board.Crew);                                   // Carl is dead
        Assert.Equal(2, board.Imps);
        Assert.True(board.People.Values.Single(p => p.Name == "Alice").Imp);
        Assert.True(board.People.Values.Single(p => p.Name == "Carl").Dead);
        Assert.Null(_desk.HostNeedsUpdate("LJ"));
        Assert.Contains(_desk.Archive.Live("LJ")!.Kills, k => k.Victim.Length > 0);   // the kill reached the stats too
        // A repeat (the page sending again after a drop) changes nothing.
        foreach (var json in wire) _desk.Apply(json);
        Assert.Single(_desk.Archive.Live("LJ")!.Kills);
    }

    [Fact]
    public void Unknown_fields_and_kinds_are_ignored_and_an_old_host_is_marked()
    {
        _host.GameStarted("QWERTY", "Polus", Players.Lobby());
        _host.FeedTick(Frame());
        foreach (var json in Wire())
        {
            // A newer host adds a field and a kind this app doesn't know.
            var m = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
            m["somethingNew"] = JsonSerializer.SerializeToElement(new { x = 1 });
            _desk.Apply(JsonSerializer.Serialize(m));
        }
        _desk.Apply(JsonSerializer.Serialize(new { v = FeedProtocol.Version, type = "event", kind = "hatWorn", lobby = "LJ", t = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds(), src = "x", seq = 1 }));
        Assert.Equal("ingame", _desk.Board.Lobby("LJ")!.Phase);
        Assert.Null(_desk.HostNeedsUpdate("LJ"));

        // A host whose mod predates versions: still shown, marked.
        _desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = "OLD", phase = "lobby", t = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds() }));
        Assert.Equal("lobby", _desk.Board.Lobby("OLD")!.Phase);
        Assert.Equal(FeedProtocol.NeedsUpdate(0), _desk.HostNeedsUpdate("OLD"));
    }
}
