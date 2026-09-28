using TournamentTracker.Discord;
using TournamentTracker.Setup;
using TournamentTracker.Stats;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

public class SettingsLockTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private const string Webhook = "https://discord.com/api/webhooks/1/abc";

    public void Dispose() => _dir.Dispose();

    private TournamentSession Session(SetupCode? code) =>
        new(new TrackerSettings { LiveStatus = false }, _dir.Path, NullLog.Instance, new HttpClient(new FakeHttp()), () => _clock.Now,
            new FakeVoiceApi(), new VoicePresenceState("g1"), code);

    private static SetupCode Prelim(LobbySettings? set) =>
        new() { TournamentId = "oct", TournamentName = "October prelims", Server = "Sus", Webhook = Webhook, Lobby = set };

    [Fact]
    public void Differences_name_each_wrong_setting()
    {
        var want = LobbySettings.TournamentDefaults();
        var got = LobbySettings.TournamentDefaults();
        got.KillCooldown = 20; got.ConfirmEjects = true; got.KillDistance = 1; got.RolesOff = false;
        Assert.Equal(new[]
        {
            "Confirm ejects on (should be off)", "Kill cooldown 20s (should be 25s)", "Kill distance Medium (should be Short)", "Special roles on (should all be off)",
        }, want.Differences(got));
        Assert.Empty(want.Differences(LobbySettings.TournamentDefaults()));
        Assert.Empty(want.Differences(new LobbySettings()));          // nothing read: nothing to complain about
    }

    [Fact]
    public void The_code_carries_the_settings_and_locks_only_outside_standard_mode()
    {
        var code = Prelim(LobbySettings.TournamentDefaults());
        Assert.True(SetupCode.TryParse(code.Encode(), out var back, out _));
        Assert.Equal(25f, back.Lobby!.KillCooldown);

        using var s = Session(back);
        Assert.NotNull(s.LockedSettings);
        using var casual = Session(null);                            // no code: the host's own games are never touched
        Assert.Null(casual.LockedSettings);
        using var noSettings = Session(Prelim(null));
        Assert.Null(noSettings.LockedSettings);
    }

    [Fact]
    public void Lock_off_frees_the_lobby_and_games_are_still_checked()
    {
        using var s = Session(Prelim(LobbySettings.TournamentDefaults()));
        var host = Players.Lobby()[0];
        s.HandleChat(host, true, "!lock off");
        Assert.Null(s.LockedSettings);
        Assert.Contains(s.Pump(), r => r.Text.StartsWith("Settings unlocked"));

        s.GameStarted("ABCDEF", "Polus", Players.Lobby());
        var wrong = LobbySettings.TournamentDefaults();
        wrong.KillCooldown = 20;
        s.CheckSettings(wrong);
        Assert.Contains(s.Pump(), r => r.Text.StartsWith("This game started with the wrong settings: Kill cooldown 20s (should be 25s)"));
        var game = s.GameEnded("HumansByTask", Players.Lobby())!;
        Assert.Equal(new[] { "Kill cooldown 20s (should be 25s)" }, game.SettingsIssues);

        s.HandleChat(host, true, "!lock on");
        Assert.NotNull(s.LockedSettings);
    }

    [Fact]
    public void Putting_settings_back_tells_the_host_without_spamming()
    {
        using var s = Session(Prelim(LobbySettings.TournamentDefaults()));
        s.SettingsRestored(new[] { "Kill cooldown 20s (should be 25s)" });
        s.SettingsRestored(new[] { "Kill cooldown 20s (should be 25s)" });
        Assert.Single(s.Pump(), r => r.Text.StartsWith("Settings are locked for October prelims: put back Kill cooldown 20s"));
        _clock.Advance(6);
        s.SettingsRestored(new[] { "Impostors 3 (should be 2)" });
        Assert.Single(s.Pump());
    }
}

public class RotationTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Everyone_is_impostor_once_before_anyone_is_twice()
    {
        var counts = new Dictionary<string, int>();
        var players = Enumerable.Range(0, 10).Select(i => ((byte)i, "p" + i)).ToList();
        var random = new Random(1);
        for (int game = 0; game < 5; game++)
        {
            foreach (var id in Rotation.Pick(players, counts, 2, random))
                counts["p" + id] = counts.TryGetValue("p" + id, out var n) ? n + 1 : 1;
        }
        Assert.Equal(10, counts.Count);
        Assert.All(counts.Values, n => Assert.Equal(1, n));
    }

    [Fact]
    public void Rotation_is_off_unless_the_code_turns_it_on_and_counts_per_round()
    {
        var clock = new FakeClock();
        TournamentSession Make(bool rot) => new(new TrackerSettings { LiveStatus = false }, _dir.Path, NullLog.Instance, new HttpClient(new FakeHttp()), () => clock.Now,
            new FakeVoiceApi(), new TournamentTracker.Discord.VoicePresenceState("g1"),
            new TournamentTracker.Setup.SetupCode { TournamentId = "t", TournamentName = "T", Webhook = "https://discord.com/api/webhooks/1/a", ImpostorRotation = rot });

        using (var off = Make(false)) Assert.Null(off.PickImpostors(Players.Lobby(), 2));

        using var s = Make(true);
        var lobby = Players.Lobby();                                 // 6 players, Alice and Bob impostors
        s.GameStarted("X", "Polus", lobby);
        s.GameEnded("HumansByTask", lobby);
        for (int i = 0; i < 20; i++)
        {
            var pick = s.PickImpostors(lobby, 2)!;
            Assert.DoesNotContain((byte)0, pick);                       // Alice and Bob have had their turn
            Assert.DoesNotContain((byte)1, pick);
        }
    }
}
