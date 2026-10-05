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
        new(new TrackerSettings { LiveStatus = false, PublicChat = true, ControlPort = -1 }, _dir.Path, NullLog.Instance, new HttpClient(new FakeHttp()), () => _clock.Now,
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
        s.RunCommand("!lock off");
        Assert.Null(s.LockedSettings);
        Assert.Contains(s.Pump(), r => r.Text.StartsWith("Settings unlocked"));

        s.GameStarted("ABCDEF", "Polus", Players.Lobby());
        var wrong = LobbySettings.TournamentDefaults();
        wrong.KillCooldown = 20;
        s.CheckSettings(wrong);
        Assert.Contains(s.Pump(), r => r.Text.StartsWith("This game started with the wrong settings: Kill cooldown 20s (should be 25s)"));
        var game = s.GameEnded("HumansByTask", Players.Lobby())!;
        Assert.Equal(new[] { "Kill cooldown 20s (should be 25s)" }, game.SettingsIssues);

        s.RunCommand("!lock on");
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
        TournamentSession Make(bool rot) => new(new TrackerSettings { LiveStatus = false, PublicChat = true, ControlPort = -1 }, _dir.Path, NullLog.Instance, new HttpClient(new FakeHttp()), () => clock.Now,
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

public class GeneratorOptionsTests
{
    // Made by docs/setup-codes.html (tournament host, rotation, lead lobby, 4 games a round, kill cooldown 22.5).
    private const string FromGenerator = "TT1-eyJtIjoidG91cm5hbWVudCIsImlkIjoiZmFsbC1jdXAiLCJuIjoiRmFsbCBDdXAiLCJ3aCI6Imh0dHBzOi8vZGlzY29yZC5jb20vYXBpL3dlYmhvb2tzLzEyMy9hYmMiLCJnIjoiMTE4MDAwMDAwMDAwMDAwMDAwIiwiYnQiOlsiYWFhLmJiYi5jY2MiXSwicmMiOiIxMjkwMDAwMDAwMDAwMDAwMDAiLCJhZHYiOjUsImdwciI6NCwibGVhZCI6dHJ1ZSwicm90Ijp0cnVlLCJzZXQiOnsiaW1wIjoyLCJlbSI6MSwiZWMiOjIwLCJkdCI6MTUsInZ0IjoxNTAsImN0IjoyLCJsdCI6Mywic3QiOjUsImtkIjowLCJrYyI6MjIuNSwicHMiOjEsImN2IjowLjI1LCJpdiI6MSwiY2UiOmZhbHNlLCJhdiI6ZmFsc2UsInZpcyI6ZmFsc2UsInJvIjp0cnVlfSwic2MiOnsia2lsbCI6MSwiZmlyc3RCbG9vZCI6MSwidm90ZWRDcmV3bWF0ZU91dCI6MiwiaW1wb3N0b3JTYWJvdGFnZVdpbiI6NSwiaW1wb3N0b3JWb3RlV2luIjo0LCJpbXBvc3RvcktpbGxXaW4iOjQsImltcG9zdG9yVGFza0xvc3MiOi0zLCJpbXBvc3RvclZvdGVMb3NzIjotMiwidm90ZWRPdXRGaXJzdCI6LTIsInZvdGVkT3V0TGFzdCI6LTEsImNvbXBsZXRlZFRhc2tzIjowLCJ0YXNrUGVyY2VudEJvbnVzIjozLCJsb25nVGFza1dlaWdodCI6MiwiY29ycmVjdFZvdGVPdXQiOjIsInJlYWRWb3RlUG9pbnRzIjoxLCJyZWFkVm90ZUJvbnVzIjo0LCJjYXVnaHRLaWxsZXIiOjEsImRpZWRGaXJzdFNoYXJlT2ZDcmV3QXZlcmFnZSI6MC45LCJnb3RLaWxsZWQiOjAsImluY29ycmVjdFZvdGVPdXQiOi0yLCJjcmV3VGFza1dpbiI6NSwiY3Jld1ZvdGVXaW4iOjMsImNyZXdTYWJvdGFnZUxvc3NBbGl2ZSI6LTUsImNyZXdPdGhlckxvc3MiOi0xLCJib251c1JvdW5kaW5nIjowLjUsImRpc2Nvbm5lY3RXaW4iOjB9fQ";

    [Fact]
    public void The_generator_s_new_options_reach_the_settings()
    {
        Assert.True(TournamentTracker.Setup.SetupCode.TryParse(FromGenerator, out var code, out var error), error);
        var s = new TrackerSettings();
        code.ApplyTo(s);
        Assert.True(s.ImpostorRotation);
        Assert.True(s.Lead);
        Assert.Equal(4, s.GamesPerRound);
        Assert.Equal(22.5f, s.LobbySettings!.KillCooldown);
        Assert.Equal(0, s.LobbySettings.KillDistance);
        Assert.True(s.LobbySettings.RolesOff);
        Assert.False(s.LobbySettings.ConfirmEjects);
        Assert.Equal(4, s.Scoring.ReadVoteBonus);
        Assert.Equal(0.9, s.Scoring.DiedFirstShareOfCrewAverage);
    }
}
