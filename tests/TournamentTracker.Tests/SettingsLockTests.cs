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
