using System.IO.Compression;
using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Broadcast;
using TournamentTracker.Discord;
using TournamentTracker.Stats;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// Red Alert keeps its own copy of every game's replay, sent by the host's mod as the game goes,
/// so a game can be watched even when the host's PC drops out before it's posted.
/// </summary>
public class GameReplayTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly TournamentSession _host;
    private readonly CasterDesk _desk;
    private readonly List<PlayerSnapshot> _lobby = Players.Lobby();
    private long _read;
    private string RaFolder => Path.Combine(_dir.Path, "ra");

    public GameReplayTests()
    {
        var settings = new TrackerSettings { TournamentName = "Fall Cup", LiveStatus = false, ControlPort = -1, Mode = TrackerMode.Tournament, LobbyLabel = "LJ" };
        _host = new TournamentSession(settings, Path.Combine(_dir.Path, "host"), NullLog.Instance, new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));
        _host.RunCommand("r2");
        _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, RaFolder);
    }

    public void Dispose() { _desk.Dispose(); _host.Dispose(); _dir.Dispose(); }

    private static FeedFrame Frame() => new()
    {
        Phase = VoicePhase.Tasks,
        Players = Enumerable.Range(0, 6).Select(i => new FeedPlayer { Id = (byte)i, X = i * 10, Y = 0, Room = "Cafeteria" }).ToList(),
        Camera = new FeedCamera { X = 0, Y = 0, HalfWidth = 5.33f, HalfHeight = 3 },
    };

    /// <summary>What reaches Red Alert: the mod's messages since last time, through the data link.</summary>
    private int Deliver()
    {
        var doc = JsonDocument.Parse(_host.FeedSince(_read)).RootElement;
        _read = doc.GetProperty("last").GetInt64();
        int replays = 0;
        foreach (var e in doc.GetProperty("items").EnumerateArray())
        {
            if (e.GetProperty("type").GetString() == "replay") replays++;
            _desk.Apply(e.GetRawText());
        }
        return replays;
    }

    /// <summary>Plays <paramref name="seconds"/> of the game: positions ten times a second, the feed ticking.</summary>
    private void Play(double seconds, float drift = 0.5f)
    {
        for (int i = 0; i < seconds * 10; i++)
        {
            _clock.Advance(0.1);
            float s = (float)(_clock.Now - new DateTime(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            _host.RecordPositions(_lobby.Select(p => new ReplayPosition(p.PlayerId, p.PlayerId * 3 + s * drift, -s * drift, p.IsDead, false, false)));
            _host.FeedTick(Frame());
        }
    }

    private static JsonElement Open(byte[] gz)
    {
        using var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        return JsonDocument.Parse(z).RootElement.Clone();
    }

    [Fact]
    public void A_finished_game_kept_by_Red_Alert_matches_the_hosts_own_replay()
    {
        _host.GameStarted("QWERTY", "Polus", _lobby);
        _host.ReplayMapLoaded(new ReplayMap { Walls = new() { new float[] { 0, 0, 10, 0 } }, Background = new ReplayBackground { Image = "data:image/jpeg;base64,AAAA" } });
        Play(5);
        _host.Kill(0, 2); _lobby[2].IsDead = true;
        Play(5);
        Assert.True(Deliver() >= 4);                                  // a message every couple of seconds while it's played
        _host.GameEnded("HumansByVote", _lobby);
        Deliver();

        var game = Assert.Single(_desk.Replays.List());
        var row = JsonSerializer.SerializeToElement(game);
        Assert.Equal("LJ", row.GetProperty("Lobby").GetString());
        Assert.True(row.GetProperty("Ended").GetBoolean());
        string id = row.GetProperty("Id").GetString()!;

        var ours = Open(_desk.Replays.File(id)!);
        string hostFile = Directory.GetFiles(Path.Combine(_dir.Path, "host"), "tt-replay-*.json.gz", SearchOption.AllDirectories).Single();
        var theirs = Open(File.ReadAllBytes(hostFile));
        Assert.Equal(theirs.GetProperty("frames").GetRawText(), ours.GetProperty("frames").GetRawText());
        Assert.Equal(theirs.GetProperty("events").GetArrayLength(), ours.GetProperty("events").GetArrayLength());
        Assert.Equal(theirs.GetProperty("winner").GetString(), ours.GetProperty("winner").GetString());
        Assert.Equal(theirs.GetProperty("name").GetString(), ours.GetProperty("name").GetString());
        Assert.Equal(2, ours.GetProperty("round").GetInt32());
        // Every player, in the frames' order, with their points.
        Assert.Equal(theirs.GetProperty("players").EnumerateArray().Select(p => (p.GetProperty("id").GetInt32(), p.GetProperty("points").GetDouble())),
                     ours.GetProperty("players").EnumerateArray().Select(p => (p.GetProperty("id").GetInt32(), p.GetProperty("points").GetDouble())));
        // The walls came, the map picture (too big to send) didn't.
        Assert.Equal(1, ours.GetProperty("geometry").GetProperty("walls").GetArrayLength());
        Assert.False(ours.GetProperty("geometry").TryGetProperty("background", out _));
        // Saved next to Red Alert's settings, and found by the game's name for the Live desk.
        Assert.True(File.Exists(Path.Combine(RaFolder, "game-replays", GameReplays.FileNameFor(id))));
        Assert.Equal(id, _desk.Replays.Find("LJ", ours.GetProperty("name").GetString()!));
    }

    [Fact]
    public void A_game_whose_host_drops_out_can_still_be_watched_up_to_where_it_stopped()
    {
        _host.GameStarted("QWERTY", "Polus", _lobby);
        Play(6);
        Deliver();
        // The host's PC drops: nothing more ever comes (no end, no posted replay).
        _clock.Advance(30);
        var row = JsonSerializer.SerializeToElement(_desk.Replays.List().Single());
        Assert.False(row.GetProperty("Ended").GetBoolean());
        Assert.True(row.GetProperty("Cut").GetBoolean());
        var replay = Open(_desk.Replays.File(row.GetProperty("Id").GetString()!)!);
        Assert.InRange(replay.GetProperty("frames").GetArrayLength(), 38, 60);     // all but the last couple of seconds (sent every 2 s)
        Assert.Equal("Interrupted", replay.GetProperty("endReason").GetString());
        Assert.Contains(replay.GetProperty("events").EnumerateArray(), e => e.GetProperty("text").GetString()!.Contains("stopped sending"));
        Assert.Equal(6, replay.GetProperty("players").GetArrayLength());

        // Red Alert restarts: the copy on disk still opens.
        using var again = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, RaFolder);
        Assert.NotNull(again.Replays.File(row.GetProperty("Id").GetString()!));
    }

    [Fact]
    public void Messages_sent_again_after_a_drop_dont_double_the_replay()
    {
        _host.GameStarted("QWERTY", "Polus", _lobby);
        Play(4);
        var doc = JsonDocument.Parse(_host.FeedSince(0)).RootElement;
        var items = doc.GetProperty("items").EnumerateArray().Select(e => e.GetRawText()).ToList();
        foreach (var json in items) _desk.Apply(json);
        foreach (var json in items) _desk.Apply(json.Replace("\"type\":\"replay\"", "\"type\":\"replay\",\"re\":true"));   // the page sends them again
        var replay = Open(_desk.Replays.File(_desk.Replays.Find("LJ", "LJ-1")!)!);
        var times = replay.GetProperty("frames").EnumerateArray().Select(f => f[0].GetDouble()).ToList();
        Assert.Equal(times.Distinct().Count(), times.Count);
        Assert.Equal(times.OrderBy(x => x), times);
    }

    [Fact]
    public void Stopping_simulation_forgets_its_games()
    {
        var replays = new GameReplays(Path.Combine(_dir.Path, "sim"), () => _clock.Now);
        var msg = JsonDocument.Parse("{\"id\":\"SIM-1-1-x\",\"name\":\"SIM-1-1\",\"players\":[],\"from\":0,\"frames\":[[0.1]],\"events\":[]}").RootElement;
        replays.Add("SIM-1", msg, sim: true);
        replays.Add("LJ", JsonDocument.Parse("{\"id\":\"LJ-1-x\",\"name\":\"LJ-1\",\"players\":[],\"from\":0,\"frames\":[[0.1]],\"events\":[]}").RootElement);
        replays.Forget(CasterDesk.IsSimLobby);
        Assert.Equal(new[] { "LJ" }, replays.List().Select(g => JsonSerializer.SerializeToElement(g).GetProperty("Lobby").GetString()));
        Assert.Null(replays.File("SIM-1-1-x"));
    }
}
