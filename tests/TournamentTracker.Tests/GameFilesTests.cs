using System.IO;
using System.Net.Http;
using TournamentTracker.Discord;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

public class GameFilesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _dir.Dispose();

    private TournamentSession Session() =>
        new(new TrackerSettings { LiveStatus = false, PublicChat = true, ControlPort = -1, TournamentName = "Cup" }, _dir.Path, NullLog.Instance,
            new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));

    [Fact]
    public void A_broken_game_file_is_skipped_when_looking_a_game_up()
    {
        var games = Path.Combine(_dir.Path, "games", "cup");
        Directory.CreateDirectory(games);
        File.WriteAllText(Path.Combine(games, "game-1.json"), "{\"Host\":null,\"GameNumber\":1}");   // hand-edited: no host
        File.WriteAllText(Path.Combine(games, "game-2.json"), "{oops");
        using var s = Session();
        s.RunCommand("voidgame LJ-9 restarted");
        Assert.Contains(s.Pump(), r => r.Text == "No game LJ-9 on this PC.");
    }
}
