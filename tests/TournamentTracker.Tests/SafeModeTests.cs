using System.Text.Json;
using TournamentTracker.Control;
using TournamentTracker.Discord;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// Safe mode on Among Us update day: the version dates, the decision, the host's "run normally anyway"
/// (saved for one game version), the safemode command and what The Button reads.
/// </summary>
public class SafeModeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    public void Dispose() => _dir.Dispose();

    private TournamentSession Session(int port = -1) =>
        new(new TrackerSettings { TournamentName = "Cup", LiveStatus = false, ControlPort = port }, _dir.Path, NullLog.Instance,
            new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));

    private static GameDate D(int y, int m, int d) => new(y, m, d);

    // ---- Version dates ----------------------------------------------------------------------

    [Theory]
    [InlineData("2026.9.29", 2026, 9, 29)]
    [InlineData("2026.10.20", 2026, 10, 20)]
    [InlineData("2026.09.29", 2026, 9, 29)]
    [InlineData("v2026.9.29s", 2026, 9, 29)]          // how the game shows it
    [InlineData("2026.9.29.1", 2026, 9, 29)]          // with a revision
    [InlineData("2026.9.29-patch1", 2026, 9, 29)]     // a prerelease game-libs package
    [InlineData("2026_8_17_1125", 2026, 8, 17)]
    public void Version_texts_read_as_dates(string text, int year, int month, int day)
    {
        Assert.Equal(D(year, month, day), GameDate.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*-*")]
    [InlineData("1.0.0")]
    [InlineData("2026.13.1")]
    [InlineData("2026.0.5")]
    [InlineData("2026.9.32")]
    [InlineData("12026.9.29")]
    public void Texts_without_a_real_date_are_unknown(string? text)
    {
        Assert.Null(GameDate.Parse(text));
    }

    [Fact]
    public void The_network_version_number_reads_as_its_date_whatever_the_revision()
    {
        Assert.Equal(D(2026, 7, 20), GameDate.FromBroadcastVersion(50663600));                  // 2026 × 25000 + 7 × 1800 + 20 × 50
        Assert.Equal(D(2026, 7, 20), GameDate.FromBroadcastVersion(50663600 + 25));             // the host authority flag
        Assert.Equal(D(2026, 10, 20), GameDate.FromBroadcastVersion(2026 * 25000 + 10 * 1800 + 20 * 50 + 3));
        Assert.Null(GameDate.FromBroadcastVersion(0));
        Assert.Null(GameDate.FromBroadcastVersion(-1));
    }

    [Fact]
    public void The_running_game_is_dated_by_Application_version_like_the_game_libraries()
    {
        // A real game: the network version still said 2026.7.20 (Constants not moved) while the release
        // was 2026.8.18 or later; its game libraries are named after Application.version.
        Assert.Equal(D(2026, 10, 20), GameDate.OfRunningGame("2026.10.20", 50663600));
        Assert.Equal(D(2026, 8, 18), GameDate.OfRunningGame("2026.8.18s", 50663600 + 25));
        // No date in Application.version: the network version's.
        Assert.Equal(D(2026, 7, 20), GameDate.OfRunningGame(null, 50663600));
        Assert.Equal(D(2026, 7, 20), GameDate.OfRunningGame("1.0", 50663600));
        Assert.Null(GameDate.OfRunningGame("", 0));
    }

    [Fact]
    public void An_update_is_seen_even_when_the_network_version_keeps_an_older_date()
    {
        // Built against GameLibs 2026.9.29; the next Among Us is 2026.10.20 by Application.version, and its
        // network version is still dated 2026.7.20 (older than the libraries).
        string? running = GameDate.OfRunningGame("2026.10.20", 50663600)?.ToString();
        var safe = new SafeMode(_dir.Path, NullLog.Instance, running, "2026.9.29");
        Assert.True(safe.On);
        Assert.StartsWith("Among Us 2026.10.20 is newer than this mod (built for 2026.9.29)", safe.Reason);
        // Read by the network version alone it would have looked older, and run normally.
        Assert.False(SafeModeRules.Decide(GameDate.FromBroadcastVersion(50663600), D(2026, 9, 29), null, false).On);

        // "Run normally anyway" is kept under the Application.version date: the next update, with the same
        // (unmoved) network version, turns safe mode back on by itself.
        safe.RunNormally(true);
        Assert.False(new SafeMode(_dir.Path, NullLog.Instance, running, "2026.9.29").On);
        string? next = GameDate.OfRunningGame("2026.11.3", 50663600)?.ToString();
        var update = new SafeMode(_dir.Path, NullLog.Instance, next, "2026.9.29");
        Assert.True(update.On);
        Assert.False(update.Overridden);
    }

    [Fact]
    public void Dates_compare_as_dates_not_as_text()
    {
        Assert.True(D(2026, 10, 20).CompareTo(D(2026, 9, 29)) > 0);       // "2026.10.20" < "2026.9.29" as text
        Assert.True(D(2027, 1, 1).CompareTo(D(2026, 12, 31)) > 0);
        Assert.True(D(2026, 9, 28).CompareTo(D(2026, 9, 29)) < 0);
        Assert.Equal(0, D(2026, 9, 29).CompareTo(D(2026, 9, 29)));
        Assert.Equal("2026.9.29", D(2026, 9, 29).ToString());
    }

    // ---- The decision -----------------------------------------------------------------------

    [Fact]
    public void A_newer_game_turns_safe_mode_on_with_a_plain_reason()
    {
        var d = SafeModeRules.Decide(D(2026, 10, 20), D(2026, 9, 29), null, runNormally: false);
        Assert.True(d.On);
        Assert.True(d.Automatic);
        Assert.False(d.Overridden);
        Assert.Equal("Among Us 2026.10.20 is newer than this mod (built for 2026.9.29): risky features are off until an update", d.Reason);
    }

    [Fact]
    public void A_newer_game_by_one_day_counts()
    {
        Assert.True(SafeModeRules.Decide(D(2026, 9, 30), D(2026, 9, 29), null, false).On);
    }

    [Theory]
    [InlineData(2026, 9, 29)]        // what it was built for
    [InlineData(2026, 7, 20)]        // older: the hooks' own self-check catches what changed
    public void The_same_or_an_older_game_runs_normally(int y, int m, int day)
    {
        var d = SafeModeRules.Decide(D(y, m, day), D(2026, 9, 29), Array.Empty<string>(), false);
        Assert.False(d.On);
        Assert.False(d.Automatic);
        Assert.Null(d.Reason);
    }

    [Fact]
    public void Unknown_versions_without_failed_hooks_run_normally()
    {
        Assert.False(SafeModeRules.Decide(null, D(2026, 9, 29), null, false).On);
        Assert.False(SafeModeRules.Decide(D(2026, 10, 20), null, null, false).On);
        Assert.False(SafeModeRules.Decide(null, null, null, false).On);
    }

    [Fact]
    public void Failed_risky_hooks_turn_it_on_whatever_the_versions()
    {
        var same = SafeModeRules.Decide(D(2026, 9, 29), D(2026, 9, 29), new[] { "RoleOncePatch" }, false);
        Assert.True(same.On);
        Assert.Equal("hooks failed: RoleOncePatch isn't in place, so risky features are off until an update", same.Reason);

        var unknown = SafeModeRules.Decide(null, null, new[] { "RoleOncePatch", "HostAuthorityVersionPatch" }, false);
        Assert.True(unknown.On);
        Assert.StartsWith("hooks failed: RoleOncePatch, HostAuthorityVersionPatch aren't in place", unknown.Reason);

        var many = SafeModeRules.Decide(null, null, new[] { "A", "B", "C", "D", "E", "F" }, false);
        Assert.Contains("A, B, C, D and 2 more", many.Reason);
    }

    [Fact]
    public void A_newer_game_with_failed_hooks_says_both()
    {
        var d = SafeModeRules.Decide(D(2026, 10, 20), D(2026, 9, 29), new[] { "RoleOncePatch" }, false);
        Assert.True(d.On);
        Assert.StartsWith("Among Us 2026.10.20 is newer than this mod (built for 2026.9.29)", d.Reason);
        Assert.Contains("hooks failed too: RoleOncePatch", d.Reason);
    }

    [Fact]
    public void Running_normally_anyway_turns_it_off_and_keeps_the_reason()
    {
        var d = SafeModeRules.Decide(D(2026, 10, 20), D(2026, 9, 29), null, runNormally: true);
        Assert.False(d.On);
        Assert.True(d.Automatic);
        Assert.True(d.Overridden);
        Assert.Contains("is newer than this mod", d.Reason);

        // Nothing to override: not "overridden".
        var plain = SafeModeRules.Decide(D(2026, 9, 29), D(2026, 9, 29), null, runNormally: true);
        Assert.False(plain.On);
        Assert.False(plain.Overridden);
    }

    // ---- The host's choice, saved for one game version --------------------------------------

    [Fact]
    public void Running_normally_is_remembered_for_this_game_version_only()
    {
        var safe = new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29");
        Assert.True(safe.On);
        safe.RunNormally(true);
        Assert.False(safe.On);
        Assert.True(safe.Overridden);
        Assert.True(File.Exists(Path.Combine(_dir.Path, SafeMode.FileName)));

        // Next start of the same game version: still running normally.
        var again = new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29");
        Assert.False(again.On);
        Assert.True(again.Overridden);

        // Among Us updates again: safe mode is back by itself.
        var update = new SafeMode(_dir.Path, NullLog.Instance, "2026.11.3", "2026.9.29");
        Assert.True(update.On);
        Assert.False(update.Overridden);
    }

    [Fact]
    public void Safe_mode_on_goes_back_to_automatic()
    {
        var safe = new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29");
        safe.RunNormally(true);
        safe.RunNormally(false);
        Assert.True(safe.On);
        Assert.False(File.Exists(Path.Combine(_dir.Path, SafeMode.FileName)));
        Assert.True(new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29").On);
    }

    [Fact]
    public void A_broken_saved_choice_is_ignored()
    {
        File.WriteAllText(Path.Combine(_dir.Path, SafeMode.FileName), "{not json");
        var log = new ListLog();
        var safe = new SafeMode(_dir.Path, log, "2026.10.20", "2026.9.29");
        Assert.True(safe.On);
        Assert.Contains(log.All, l => l.Contains("couldn't read the saved choice"));
    }

    [Fact]
    public void The_status_has_the_versions_and_the_choice()
    {
        var safe = new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29-pre");
        var status = safe.Status();
        Assert.True(status.On);
        Assert.Equal("2026.10.20", status.GameVersion);
        Assert.Equal("2026.9.29", status.BuiltFor);
        Assert.False(status.Overridden);
        Assert.Contains("newer", status.Reason);

        var unknown = new SafeMode(_dir.Path, NullLog.Instance, null, null).Status();
        Assert.False(unknown.On);
        Assert.Null(unknown.GameVersion);
        Assert.Null(unknown.BuiltFor);
        Assert.Null(unknown.Reason);
    }

    [Fact]
    public void The_log_line_says_what_is_off_and_what_keeps_working()
    {
        var on = new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29").Describe();
        Assert.StartsWith("Safe mode ON (Among Us 2026.10.20, mod built for 2026.9.29)", on);
        Assert.Contains("host authority", on);
        Assert.Contains("referee ghost slot", on);
        Assert.Contains("Tracking, stats, overlays and Discord keep working", on);
        Assert.Contains("can't be compared", new SafeMode(_dir.Path, NullLog.Instance, null, "2026.9.29").Describe());
    }

    // ---- The command and The Button ---------------------------------------------------------

    [Fact]
    public void Safemode_off_runs_normally_on_this_version_and_on_goes_back()
    {
        using var s = Session();
        s.SafeMode = new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29");
        Assert.True(s.RunCommand("safemode off"));
        Assert.False(s.SafeMode.On);
        var reply = Assert.Single(s.Pump());
        Assert.Contains("Safe mode OFF for Among Us 2026.10.20", reply.Text);
        Assert.Contains("next lobby you host", reply.Text);           // host authority goes with the connection
        Assert.False(reply.Public);

        Assert.True(s.RunCommand("safemode on"));
        Assert.True(s.SafeMode.On);
        Assert.StartsWith("Safe mode is ON: Among Us 2026.10.20 is newer", s.Pump().Single().Text);
    }

    [Fact]
    public void Safemode_off_when_it_is_not_on_changes_nothing()
    {
        using var s = Session();
        s.SafeMode = new SafeMode(_dir.Path, NullLog.Instance, "2026.9.29", "2026.9.29");
        s.RunCommand("safemode off");
        Assert.Contains("already off", s.Pump().Single().Text);
        Assert.False(s.SafeMode.Overridden);
        Assert.False(File.Exists(Path.Combine(_dir.Path, SafeMode.FileName)));
    }

    // ---- Held for a whole game ---------------------------------------------------------------

    [Fact]
    public void A_change_mid_game_applies_from_the_next_game()
    {
        var safe = new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29");
        Assert.True(safe.OnThisGame);                                 // between games: as it is now
        safe.HoldForGame();
        safe.RunNormally(true);
        Assert.False(safe.On);
        Assert.True(safe.OnThisGame);                                 // the game being played carries on in safe mode
        Assert.True(safe.HeldForGame);
        safe.ReleaseGame();
        Assert.False(safe.OnThisGame);                                // the next game runs normally
        safe.HoldForGame();
        safe.RunNormally(false);
        Assert.True(safe.On);
        Assert.False(safe.OnThisGame);
    }

    private TournamentSession RefSession(SafeMode safe, out List<PlayerSnapshot> lobby, bool rotation = false)
    {
        var s = new TournamentSession(new TrackerSettings { TournamentName = "Cup", LiveStatus = false, ControlPort = -1, Mode = TrackerMode.Tournament, ImpostorRotation = rotation },
            _dir.Path, NullLog.Instance, new HttpClient(new FakeHttp()), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"));
        s.SafeMode = safe;
        lobby = Players.Lobby();
        var host = Players.Make(10, "LJ", 10);
        host.IsHost = true;
        lobby.Add(host);
        s.VoiceTick(Voice.VoicePhase.Lobby, lobby);
        return s;
    }

    [Fact]
    public void In_safe_mode_the_referee_plays_and_counts_like_everyone_else()
    {
        using var s = RefSession(new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29"), out var lobby);
        s.RunCommand("!refslot on");
        s.Pump();
        Assert.NotNull(s.RefSlotKey);                                 // the host's choice is kept for when safe mode ends
        Assert.Null(s.ActiveRefSlotKey);
        Assert.Null(s.RefSlotPlayerId(lobby));
        Assert.Equal(7, s.WithoutReferee(lobby).Count);
        Assert.False(s.IsSpectator);                                  // the caster feed doesn't say the host is spectating

        s.GameStarted("ABCDEF", "Polus", lobby);
        var game = s.GameEnded("HumansByTask", lobby)!;
        Assert.Contains(game.Players, p => p.Name == "LJ");           // in the record, the standings and the points
        Assert.Equal(7, game.Players.Count);
    }

    [Fact]
    public void Turning_safe_mode_off_mid_game_leaves_that_game_as_it_started()
    {
        using var s = RefSession(new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29"), out var lobby);
        s.RunCommand("!refslot on");
        s.GameStarted("ABCDEF", "Polus", lobby);
        s.Pump();

        // "Run normally anyway" pressed mid-game: the host who's playing doesn't become the referee.
        s.RunCommand("safemode off");
        Assert.False(s.SafeMode!.On);
        Assert.Contains("This game carries on in safe mode", s.Pump().Single().Text);
        s.VoiceTick(Voice.VoicePhase.Tasks, lobby);
        Assert.Null(s.ActiveRefSlotKey);
        Assert.False(s.IsSpectator);
        Assert.Equal(7, s.WithoutReferee(lobby).Count);
        s.RunCommand("safemode");
        Assert.Contains("OFF from the next game", s.Pump().Single().Text);
        Assert.Contains(s.GameEnded("HumansByTask", lobby)!.Players, p => p.Name == "LJ");

        // Back in the lobby: the next game has the referee ghost.
        s.VoiceTick(Voice.VoicePhase.Lobby, lobby);
        Assert.NotNull(s.ActiveRefSlotKey);
        Assert.Equal((byte)10, s.RefSlotPlayerId(lobby));
        Assert.True(s.IsSpectator);
        s.GameStarted("ABCDEF", "Polus", lobby);
        Assert.DoesNotContain(s.GameEnded("HumansByTask", lobby)!.Players, p => p.Name == "LJ");
    }

    [Fact]
    public void Turning_safe_mode_on_mid_game_keeps_the_referee_a_referee_until_the_lobby()
    {
        var safe = new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29");
        safe.RunNormally(true);
        using var s = RefSession(safe, out var lobby);
        s.RunCommand("!refslot on");
        s.GameStarted("ABCDEF", "Polus", lobby);
        s.Pump();
        Assert.True(s.IsSpectator);

        // "Back to automatic" mid-game: the referee stays out of the game (and its vote fence stays up).
        s.RunCommand("safemode on");
        Assert.True(s.SafeMode!.On);
        Assert.Contains("safe mode applies from the next game", s.Pump().Single().Text);
        Assert.Equal((byte)10, s.RefSlotPlayerId(lobby));
        Assert.True(s.IsSpectator);
        Assert.DoesNotContain(s.GameEnded("HumansByTask", lobby)!.Players, p => p.Name == "LJ");

        s.VoiceTick(Voice.VoicePhase.Lobby, lobby);
        Assert.Null(s.ActiveRefSlotKey);
        Assert.False(s.IsSpectator);
    }

    [Fact]
    public void The_rotation_isnt_announced_in_safe_mode()
    {
        using (var normal = RefSession(new SafeMode(_dir.Path, NullLog.Instance, "2026.9.29", "2026.9.29"), out _, rotation: true))
        {
            normal.RunCommand("r2");
            Assert.Contains(normal.Pump(), r => r.Text.StartsWith("Impostor rotation is on"));
        }
        using var safe = RefSession(new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29"), out _, rotation: true);
        safe.RunCommand("r3");
        var said = safe.Pump();
        Assert.Contains(said, r => r.Text.StartsWith("Round 3 starts now"));
        Assert.DoesNotContain(said, r => r.Text.Contains("Impostor rotation"));
    }

    [Fact]
    public void Without_a_version_check_the_command_says_so()
    {
        using var s = Session();
        Assert.True(s.RunCommand("safemode off"));
        Assert.Contains("isn't set up", s.Pump().Single().Text);
    }

    [Fact]
    public void Each_held_back_feature_is_on_Home_once()
    {
        using var s = Session();
        s.SafeMode = new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29");
        s.SafeModeHeldBack("the settings lock");
        s.SafeModeHeldBack("the settings lock");
        s.SafeModeHeldBack("the referee ghost slot");
        var notices = s.NoticeTexts();
        Assert.Equal(2, notices.Count);
        Assert.Contains("the settings lock is off", notices[0]);
        Assert.Contains("is newer than this mod", notices[0]);
        Assert.Empty(s.Pump());                                       // never in the chat
    }

    [Fact]
    public async Task The_app_sees_safe_mode_and_no_disconnect_yet()
    {
        using var s = Session(port: 0);
        s.SafeMode = new SafeMode(_dir.Path, NullLog.Instance, "2026.10.20", "2026.9.29");
        s.Disconnects = new DisconnectHistory(_dir.Path, NullLog.Instance, clock: () => _clock.Now);
        s.VoiceTick(Voice.VoicePhase.Lobby, Players.Lobby(), "ABCDEF", "Polus");
        var control = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir.Path, ControlServer.FileName))).RootElement;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{control.GetProperty("port").GetInt32()}/") };
        http.DefaultRequestHeaders.Add("X-TT-Token", control.GetProperty("token").GetString());

        var status = JsonDocument.Parse(await http.GetStringAsync("api/status")).RootElement;
        var safe = status.GetProperty("safeMode");
        Assert.True(safe.GetProperty("on").GetBoolean());
        Assert.Equal("2026.10.20", safe.GetProperty("gameVersion").GetString());
        Assert.Equal("2026.9.29", safe.GetProperty("builtFor").GetString());
        Assert.False(safe.GetProperty("overridden").GetBoolean());
        Assert.Contains("risky features are off", safe.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastDisconnect").ValueKind);

        s.RunCommand("safemode off");
        _clock.Advance(1);
        s.VoiceTick(Voice.VoicePhase.Lobby, Players.Lobby(), "ABCDEF", "Polus");
        safe = JsonDocument.Parse(await http.GetStringAsync("api/status")).RootElement.GetProperty("safeMode");
        Assert.False(safe.GetProperty("on").GetBoolean());
        Assert.True(safe.GetProperty("overridden").GetBoolean());
    }
}
