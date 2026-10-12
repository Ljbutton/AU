using System.Text.Json;
using TournamentTracker.Control;
using TournamentTracker.Discord;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// Why Among Us disconnected the host: the plain-English reasons, the stale words the game passes
/// along, what the mod sent just before, the last 20 kept on disk, and what The Button reads.
/// </summary>
public class DisconnectTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    public void Dispose() => _dir.Dispose();

    private sealed class Lines : ILog
    {
        public List<string> All { get; } = new();
        public void Info(string message) => All.Add(message);
        public void Warn(string message) => All.Add(message);
        public void Error(string message) => All.Add(message);
    }

    private DisconnectHistory History(ILog? log = null) => new(_dir.Path, log ?? NullLog.Instance, clock: () => _clock.Now);

    /// <summary>Every name in the game's DisconnectReasons (2026 builds).</summary>
    public static IEnumerable<object[]> GameReasons() => new[]
    {
        "ExitGame", "GameFull", "GameStarted", "GameNotFound", "IncorrectVersion", "Banned", "Kicked", "Custom", "InvalidName",
        "Hacking", "NotAuthorized", "ConnectionLimit", "Destroy", "Error", "IncorrectGame", "ServerRequest", "ServerFull",
        "MismatchedVersion", "InternalPlayerMissing", "InternalNonceFailure", "InternalConnectionToken", "PlatformLock",
        "LobbyInactivity", "MatchmakerInactivity", "InvalidGameOptions", "NoServersAvailable", "QuickmatchDisabled",
        "TooManyGames", "QuickchatLock", "MatchmakerFull", "Sanctions", "ServerError", "SelfPlatformLock",
        "DuplicateConnectionDetected", "TooManyRequests", "IntentionalLeaving", "FocusLostBackground", "FocusLost",
        "NewConnection", "PlatformParentalControlsBlock", "PlatformUserBlock", "PlatformFailedToGetUserBlock",
        "ServerNotFound", "ClientTimeout", "ErrorAuthNonceFailure", "Unknown",
    }.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(GameReasons))]
    public void Every_reason_the_game_has_is_explained(string reason)
    {
        var (label, explanation) = DisconnectText.Explain(reason);
        Assert.NotEqual("unrecognised reason", label);
        Assert.EndsWith(".", explanation);
    }

    [Fact]
    public void The_common_reasons_say_what_happened()
    {
        Assert.Equal("server anti-cheat", DisconnectText.Explain("Hacking").Label);
        Assert.Contains("only expect from a cheat", DisconnectText.Explain("Hacking").Explanation);
        Assert.Equal("kicked", DisconnectText.Explain("Kicked").Label);
        Assert.Equal("banned", DisconnectText.Explain("Banned").Label);
        Assert.Contains("internet", DisconnectText.Explain("Error").Explanation);
        Assert.Contains("host authority", DisconnectText.Explain("IncorrectVersion").Explanation);
        Assert.Equal("unrecognised reason", DisconnectText.Explain("250").Label);   // a number from a newer game
    }

    [Fact]
    public void Words_are_kept_only_for_reasons_that_really_carry_them()
    {
        // The game hands a Hacking or Kicked disconnect whatever it said last time: stale, dropped.
        Assert.Null(DisconnectText.Detail("Hacking", "You disconnected from the server."));
        Assert.Null(DisconnectText.Detail("Kicked", "Couldn't connect"));
        Assert.Equal("You disconnected from the server. / Reliable packet 12 was not ack'd",
            DisconnectText.Detail("Custom", "You disconnected from the server.\r\n\r\nReliable packet 12   was not ack'd"));
        Assert.Equal("Failed to send message: Disconnected", DisconnectText.Detail("Error", "Failed to send message: Disconnected"));
        Assert.Null(DisconnectText.Detail("Custom", "   "));
        Assert.Equal(300, DisconnectText.Detail("Custom", new string('x', 1000))!.Length);
    }

    [Fact]
    public void Only_what_the_mod_sent_in_the_10_seconds_before_is_listed_oldest_first()
    {
        var sends = new RecentSends(() => _clock.Now);
        sends.Add("chat: Round 2 starts now!");
        _clock.Advance(20);
        sends.Add("role for player 2: Impostor");
        _clock.Advance(4.5);
        sends.Add("referee moved out of sight (RpcSnapTo 1.0, -20.0)");
        _clock.Advance(0.5);
        var list = sends.Before(_clock.Now, DisconnectHistory.SentWindow);
        Assert.Equal(new[] { "5.0 s before: role for player 2: Impostor", "0.5 s before: referee moved out of sight (RpcSnapTo 1.0, -20.0)" }, list);
    }

    [Fact]
    public void The_ring_keeps_only_the_latest_sends()
    {
        var sends = new RecentSends(() => _clock.Now);
        for (int i = 0; i < RecentSends.Capacity + 10; i++) sends.Add("send " + i);
        var list = sends.Before(_clock.Now, TimeSpan.FromSeconds(10));
        Assert.Equal(RecentSends.Capacity, list.Count);
        Assert.EndsWith("send 10", list[0]);
        Assert.EndsWith("send " + (RecentSends.Capacity + 9), list[^1]);
    }

    [Fact]
    public void A_kick_is_logged_in_one_clear_line_with_what_the_mod_sent_before()
    {
        var log = new Lines();
        var history = History(log);
        history.Sends.Add("role for player 4: Crewmate");
        _clock.Advance(2);
        var r = history.Record("Hacking", 10, "stale words", inGame: true, lobby: "ABCDEF", host: true, moddedFlag: true, safeMode: false);

        Assert.Null(r.Detail);
        Assert.Equal(new[] { "2.0 s before: role for player 4: Crewmate" }, r.SentBefore);
        Assert.Equal("Disconnected: Hacking (server anti-cheat) — Among Us's servers saw a message from this game that they only expect from a cheat. " +
                     "For a host mod that's usually something sent twice or at the wrong time: a role, an exile, a kill, a teleport, a settings change. " +
                     "(during a game ABCDEF, hosting, host authority on).", log.All[0]);
        Assert.Equal("The mod sent in the 10 s before the disconnect: 2.0 s before: role for player 4: Crewmate.", log.All[1]);
    }

    [Fact]
    public void A_connection_that_never_got_in_says_so()
    {
        var log = new Lines();
        var r = History(log).Record("Custom", 8, "Couldn't connect", inGame: false, lobby: null, host: false, moddedFlag: false, safeMode: true);
        Assert.Equal("Couldn't connect", r.Detail);
        Assert.Contains("It said: \"Couldn't connect\".", log.All[0]);
        Assert.EndsWith("(not in a lobby, safe mode on).", log.All[0]);
        Assert.Equal("The mod sent nothing to the network in the 10 s before the disconnect.", log.All[1]);
    }

    [Fact]
    public void The_last_20_are_kept_on_disk_for_the_support_bundle()
    {
        var history = History();
        for (int i = 0; i < 25; i++)
        {
            _clock.Advance(60);
            history.Record(i % 2 == 0 ? "Error" : "Kicked", i % 2 == 0 ? 17 : 7, "lost " + i, inGame: false, lobby: "ABCDEF", host: true, moddedFlag: false, safeMode: false);
        }
        Assert.Equal(DisconnectHistory.Keep, history.All.Count);

        var path = Path.Combine(_dir.Path, DisconnectHistory.FileName);
        var saved = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal(DisconnectHistory.Keep, saved.GetArrayLength());
        var last = saved[DisconnectHistory.Keep - 1];
        Assert.Equal("Error", last.GetProperty("reason").GetString());     // camelCase, like the rest of the data folder's JSON
        Assert.Equal("lost 24", last.GetProperty("detail").GetString());
        Assert.False(File.Exists(path + ".tmp"));

        // After a restart of the game: still there, newest last.
        var again = History();
        Assert.Equal(DisconnectHistory.Keep, again.All.Count);
        Assert.Equal("lost 24", again.Latest!.Detail);
        Assert.Equal("Kicked", again.All[0].Reason);                     // the 6th of 25: the first 5 went
        Assert.Equal(new DateTime(2026, 9, 1, 18, 6, 0, DateTimeKind.Utc), again.All[0].At.ToUniversalTime());
    }

    [Fact]
    public void A_broken_file_starts_a_new_list()
    {
        File.WriteAllText(Path.Combine(_dir.Path, DisconnectHistory.FileName), "[{oops");
        var log = new Lines();
        var history = History(log);
        Assert.Empty(history.All);
        Assert.Null(history.Status());
        Assert.Contains(log.All, l => l.Contains("starting a new one"));
        history.Record("Error", 17, null, false, null, false, false, false);
        Assert.Single(History().All);
    }

    [Fact]
    public void The_status_has_the_latest_in_UTC()
    {
        var history = History();
        history.Record("Kicked", 7, null, inGame: true, lobby: "ABCDEF", host: true, moddedFlag: true, safeMode: false);
        var status = history.Status()!;
        Assert.Equal("2026-09-01T18:00:00.0000000Z", status.At);
        Assert.Equal("Kicked", status.Reason);
        Assert.Null(status.Detail);
        Assert.True(status.InGame);
        // Read back from disk, the time is still UTC.
        Assert.Equal("2026-09-01T18:00:00.0000000Z", History().Status()!.At);
    }

    [Fact]
    public async Task The_app_sees_the_latest_disconnect()
    {
        using var s = new TournamentSession(new TrackerSettings { TournamentName = "Cup", LiveStatus = false, ControlPort = 0 }, _dir.Path, NullLog.Instance,
            new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));
        s.Disconnects = History();
        var record = s.Disconnects.Record("Custom", 8, "The server closed the room", inGame: false, lobby: "ABCDEF", host: true, moddedFlag: false, safeMode: false);
        s.DisconnectSeen(record);

        var control = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir.Path, ControlServer.FileName))).RootElement;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{control.GetProperty("port").GetInt32()}/") };
        http.DefaultRequestHeaders.Add("X-TT-Token", control.GetProperty("token").GetString());
        var status = JsonDocument.Parse(await http.GetStringAsync("api/status")).RootElement;
        var last = status.GetProperty("lastDisconnect");
        Assert.Equal("2026-09-01T18:00:00.0000000Z", last.GetProperty("at").GetString());
        Assert.Equal("Custom", last.GetProperty("reason").GetString());
        Assert.Equal("The server closed the room", last.GetProperty("detail").GetString());
        Assert.False(last.GetProperty("inGame").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("safeMode").ValueKind);   // no version check in this test
    }
}
