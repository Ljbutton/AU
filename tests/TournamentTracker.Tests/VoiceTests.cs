using TournamentTracker.Discord;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

public class MutePlannerTests
{
    private static readonly AutoMuteSettings Defaults = new();

    [Theory]
    [InlineData(VoicePhase.Lobby, true, false, false)]
    [InlineData(VoicePhase.Lobby, false, false, false)]
    [InlineData(VoicePhase.Tasks, true, true, true)]
    [InlineData(VoicePhase.Tasks, false, false, false)]
    [InlineData(VoicePhase.Meeting, true, false, false)]
    [InlineData(VoicePhase.Meeting, false, true, false)]
    [InlineData(VoicePhase.GameOver, false, false, false)]
    [InlineData(VoicePhase.Menu, true, false, false)]
    public void Default_rules(VoicePhase phase, bool alive, bool mute, bool deaf) =>
        Assert.Equal(new VoiceState(mute, deaf), MutePlanner.Plan(phase, alive, Defaults));

    [Fact]
    public void Options_change_the_rules()
    {
        var s = new AutoMuteSettings { DeafenAliveDuringTasks = false, DeadCanTalkDuringTasks = false, MuteDeadDuringMeetings = false };
        Assert.Equal(new VoiceState(true, false), MutePlanner.Plan(VoicePhase.Tasks, true, s));
        Assert.Equal(new VoiceState(true, false), MutePlanner.Plan(VoicePhase.Tasks, false, s));
        Assert.Equal(VoiceState.Open, MutePlanner.Plan(VoicePhase.Meeting, false, s));
    }
}

public class AutoMuteTests : IDisposable
{
    private readonly FakeVoiceApi _api = new();
    private readonly MuteDispatcher _dispatcher;
    private readonly LinkRegistry _links = new();
    private readonly AutoMuteController _controller;
    private readonly List<PlayerSnapshot> _players = Players.Lobby();
    private readonly FakeClock _clock = new();
    private readonly AutoMuteSettings _settings = new();

    public AutoMuteTests()
    {
        _dispatcher = new MuteDispatcher(_api, new[] { "token-a", "token-b" }, NullLog.Instance);
        _controller = new AutoMuteController(_settings, _links, _dispatcher, () => _clock.Now);
        _links.Link(_players[0].Key, "Alice", "100", "alice");
        _links.Link(_players[2].Key, "Carl", "102", "carl");
        _links.Link(_players[3].Key, "Dana", "103", "dana");
    }

    public void Dispose() => _dispatcher.Dispose();

    /// <summary>Enters a phase and waits out its delay, like a few seconds of game ticks.</summary>
    private void Enter(VoicePhase phase, IReadOnlyList<PlayerSnapshot>? players = null, IReadOnlyCollection<string>? spectators = null)
    {
        _controller.Update(phase, players ?? _players, spectators);
        _clock.Advance(10);
        _controller.Update(phase, players ?? _players, spectators);
    }

    private async Task Settle()
    {
        Assert.True(await _dispatcher.WaitIdleAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Follows_the_game_through_its_phases()
    {
        Enter(VoicePhase.Lobby, _players);
        await Settle();
        Assert.Equal(VoiceState.Open, _dispatcher.Applied("100"));

        Enter(VoicePhase.Tasks, _players);
        await Settle();
        Assert.Equal(new VoiceState(true, true), _dispatcher.Applied("100"));
        Assert.Equal(new VoiceState(true, true), _dispatcher.Applied("102"));

        _players[2].IsDead = true;
        Enter(VoicePhase.Tasks, _players);
        await Settle();
        Assert.Equal(VoiceState.Open, _dispatcher.Applied("102"));   // dead talk freely

        Enter(VoicePhase.Meeting, _players);
        await Settle();
        Assert.Equal(VoiceState.Open, _dispatcher.Applied("100"));
        Assert.Equal(new VoiceState(true, false), _dispatcher.Applied("102"));

        Enter(VoicePhase.GameOver, _players);
        await Settle();
        Assert.All(new[] { "100", "102", "103" }, id => Assert.Equal(VoiceState.Open, _dispatcher.Applied(id)));

        // Unlinked players are never touched.
        Assert.DoesNotContain(_api.Calls, c => c.User != "100" && c.User != "102" && c.User != "103");
    }

    [Fact]
    public async Task Repeated_ticks_do_not_resend()
    {
        Enter(VoicePhase.Tasks, _players);
        await Settle();
        int before = _api.Calls.Count;
        for (int i = 0; i < 20; i++) _controller.Update(VoicePhase.Tasks, _players);
        await Settle();
        Assert.Equal(before, _api.Calls.Count);
        Assert.Equal(3, _api.Calls.Count(c => c.State == new VoiceState(true, true)));
    }

    [Fact]
    public async Task Work_is_spread_over_every_bot_token()
    {
        _api.Latency = TimeSpan.FromMilliseconds(50);
        Enter(VoicePhase.Tasks, _players);
        await Settle();
        Assert.Equal(2, _api.Calls.Select(c => c.Token).Distinct().Count());
    }

    [Fact]
    public async Task A_player_leaving_the_lobby_gets_their_voice_back()
    {
        Enter(VoicePhase.Tasks, _players);
        await Settle();
        Enter(VoicePhase.Tasks, _players.Where(p => p.PlayerId != 3).ToList());
        await Settle();
        Assert.Equal(VoiceState.Open, _dispatcher.Applied("103"));
        Assert.Equal(new VoiceState(true, true), _dispatcher.Applied("100"));
    }

    [Fact]
    public async Task Disabling_releases_everyone_and_stops_updates()
    {
        Enter(VoicePhase.Tasks, _players);
        await Settle();
        _controller.Enabled = false;
        await Settle();
        _controller.Update(VoicePhase.Tasks, _players);
        await Settle();
        Assert.All(new[] { "100", "102", "103" }, id => Assert.Equal(VoiceState.Open, _dispatcher.Applied(id)));
    }

    [Fact]
    public async Task Phase_changes_wait_for_the_delay()
    {
        Enter(VoicePhase.Lobby);
        await Settle();

        _controller.Update(VoicePhase.Tasks, _players);            // game starts
        _clock.Advance(2.9);
        _controller.Update(VoicePhase.Tasks, _players);
        await Settle();
        Assert.Equal(VoicePhase.Lobby, _controller.Phase);
        Assert.Equal(VoiceState.Open, _dispatcher.Applied("100"));

        _clock.Advance(0.2);                                       // 3 seconds in
        _controller.Update(VoicePhase.Tasks, _players);
        await Settle();
        Assert.Equal(new VoiceState(true, true), _dispatcher.Applied("100"));

        _controller.Update(VoicePhase.Meeting, _players);          // meetings open at once by default
        await Settle();
        Assert.Equal(VoiceState.Open, _dispatcher.Applied("100"));
    }

    [Fact]
    public async Task Deaths_apply_without_waiting()
    {
        Enter(VoicePhase.Tasks);
        _players[2].IsDead = true;
        _controller.Update(VoicePhase.Tasks, _players);
        await Settle();
        Assert.Equal(VoiceState.Open, _dispatcher.Applied("102"));
    }

    [Fact]
    public void Delays_follow_the_settings()
    {
        _settings.DelayGameStart = 7;
        _settings.DelayMeetingEnd = 5;
        _settings.DelayGameEnd = 4;
        _settings.DelayMeetingStart = 1;
        Assert.Equal(7, _controller.DelayFor(VoicePhase.Lobby, VoicePhase.Tasks));
        Assert.Equal(5, _controller.DelayFor(VoicePhase.Meeting, VoicePhase.Tasks));
        Assert.Equal(1, _controller.DelayFor(VoicePhase.Tasks, VoicePhase.Meeting));
        Assert.Equal(4, _controller.DelayFor(VoicePhase.Meeting, VoicePhase.GameOver));
        Assert.Equal(4, _controller.DelayFor(VoicePhase.Tasks, VoicePhase.Lobby));
        Assert.Equal(0, _controller.DelayFor(VoicePhase.GameOver, VoicePhase.Lobby));
    }

    [Fact]
    public async Task Spectators_are_muted_during_games_only_when_switched_on()
    {
        var spectators = new[] { "900", "901" };
        _settings.SpectatorExemptUserIds.Add("901");

        Enter(VoicePhase.Tasks, _players, spectators);
        await Settle();
        Assert.Null(_dispatcher.Applied("900"));                    // off by default: never touched

        _controller.MuteSpectators = true;
        _controller.Update(VoicePhase.Tasks, _players, spectators);
        await Settle();
        Assert.Equal(new VoiceState(true, false), _dispatcher.Applied("900"));
        Assert.Null(_dispatcher.Applied("901"));                    // exempt (a caster)

        Enter(VoicePhase.Lobby, _players, spectators);
        await Settle();
        Assert.Equal(VoiceState.Open, _dispatcher.Applied("900"));

        Enter(VoicePhase.Tasks, _players, spectators);
        await Settle();
        Enter(VoicePhase.Tasks, _players, Array.Empty<string>());   // they left the channel
        await Settle();
        Assert.Equal(VoiceState.Open, _dispatcher.Applied("900"));
    }
}

public class MuteDispatcherTests
{
    [Fact]
    public async Task Superseded_states_are_never_sent()
    {
        var api = new FakeVoiceApi { Latency = TimeSpan.FromMilliseconds(100) };
        using var d = new MuteDispatcher(api, new[] { "t" }, NullLog.Instance);

        d.SetDesired("1", new VoiceState(true, true));
        d.SetDesired("2", new VoiceState(true, true));   // queued behind "1"
        d.SetDesired("2", VoiceState.Open);               // replaced before it was sent
        Assert.True(await d.WaitIdleAsync(TimeSpan.FromSeconds(3)));

        Assert.DoesNotContain(api.Calls, c => c.User == "2" && c.State.Mute);
    }

    [Fact]
    public async Task Change_while_in_flight_is_sent_afterwards()
    {
        var api = new FakeVoiceApi { Latency = TimeSpan.FromMilliseconds(100) };
        using var d = new MuteDispatcher(api, new[] { "t" }, NullLog.Instance);

        d.SetDesired("1", new VoiceState(true, true));
        await Wait.Until(() => d.Pending == 1 && api.Calls.IsEmpty);
        await Task.Delay(20);
        d.SetDesired("1", VoiceState.Open);
        Assert.True(await d.WaitIdleAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(VoiceState.Open, d.Applied("1"));
    }

    [Fact]
    public async Task Member_not_in_voice_is_retried_later()
    {
        var clock = new FakeClock();
        bool inVoice = false;
        var api = new FakeVoiceApi
        {
            Respond = (_, _) => inVoice
                ? new DiscordResult { Ok = true, Status = 204 }
                : new DiscordResult { Ok = false, Status = 400, ErrorCode = DiscordResult.NotInVoice },
        };
        using var d = new MuteDispatcher(api, new[] { "t" }, NullLog.Instance, () => clock.Now);

        d.SetDesired("1", new VoiceState(true, false));
        await Wait.Until(() => api.Calls.Count == 1);
        await d.WaitIdleAsync(TimeSpan.FromSeconds(1));
        Assert.Null(d.Applied("1"));

        // Nothing happens before the retry delay...
        await Task.Delay(1200);
        Assert.Single(api.Calls);

        // ...then they join voice and the retry lands.
        inVoice = true;
        clock.Advance(11);
        await Wait.Until(() => d.Applied("1") == new VoiceState(true, false), 4000);
    }
}

public class LinkRegistryTests
{
    [Fact]
    public void A_discord_account_belongs_to_one_player()
    {
        var links = new LinkRegistry();
        links.Link("a#1", "Alice", "100", "alice");
        links.Link("b#2", "Bob", "100", "alice");
        Assert.Null(links.Find("a#1"));
        Assert.Equal("100", links.Find("b#2")!.DiscordUserId);
    }

    [Fact]
    public void Persists_and_accepts_hand_written_files()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "links.json");
        File.WriteAllText(path, """[{ "PlayerKey": "Alice#1234 ", "DiscordUserId": "100" }]""");
        var links = LinkRegistry.Load(path, NullLog.Instance);
        Assert.Equal("100", links.Find("alice#1234")!.DiscordUserId);

        links.Link("bob#1", "Bob", "200", "bob");
        var reloaded = LinkRegistry.Load(path, NullLog.Instance);
        Assert.Equal(2, reloaded.All.Count);
        Assert.True(reloaded.Unlink("bob#1"));
    }
}
