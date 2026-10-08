using System.Net;
using System.Text;
using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// OBS removing a source but keeping it alive in the background (still listed, refusing to go
/// in a scene): Red Alert moves to a fresh source and carries on, never stuck retrying.
/// </summary>
public class ObsRecoveryTests : IAsyncLifetime
{
    private readonly FakeClock _clock = new();
    private readonly TempDir _dir = new();
    private FakeObs _obs = null!;
    private CasterDesk _desk = null!;
    private ObsDirector _director = null!;
    private readonly List<(string Lobby, string Url)> _feeds = new() { ("LJ", "https://vdo.ninja/?view=a&password=x"), ("MAL", "https://vdo.ninja/?view=b&password=x") };
    private readonly List<(string Lobby, string Url)> _voices = new() { ("LJ", "https://vdo.ninja/?view=av&password=x"), ("MAL", "https://vdo.ninja/?view=bv&password=x") };
    private string SettingsPath => Path.Combine(_dir.Path, ObsSettings.FileName);

    public Task InitializeAsync()
    {
        _obs = new FakeObs();
        _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        _director = new ObsDirector(SettingsPath, _desk, () => _feeds.ToList())
        {
            BroadcastUrl = "http://127.0.0.1:8767/broadcast", TagUrl = "http://127.0.0.1:8767/replaytag", VoiceFeeds = () => _voices.ToList(),
        };
        _desk.Switch = air => _director.ApplyAsync(air).Wait();
        foreach (var l in new[] { "LJ", "MAL" })
            _desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = l, round = 1, phase = "ingame", crewAlive = 7, impAlive = 2, taskPct = 30, t = 0 }));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _director.DisposeAsync();
        await _obs.DisposeAsync();
        _desk.Dispose();
        _dir.Dispose();
    }

    private Task<string> Connect() => _director.ConnectAsync("127.0.0.1", _obs.Port, "secret");

    /// <summary>OBS "removes" an input but keeps it listed (what it did to TT Lobby LJ after Simulation stopped).</summary>
    private void Zombify(string name)
    {
        lock (_obs)
        {
            _obs.Inputs.Remove(name);
            foreach (var sc in _obs.Scenes.Values) sc.RemoveAll(i => i.Source == name);
            _obs.Zombies.Add(name);
        }
    }

    private IEnumerable<string> TtScenes => _director.Settings.Scenes.Values;

    [Fact]
    public async Task A_lobby_whose_source_OBS_removed_but_still_lists_gets_a_fresh_one()
    {
        await Connect();
        Zombify("TT Lobby LJ");
        await _director.BuildAsync();

        Assert.Equal("TT Lobby LJ 2", _director.Settings.Sources["LJ"]);
        Assert.Equal("https://vdo.ninja/?view=a&password=x&videobitrate=8000", _obs.Inputs["TT Lobby LJ 2"]["url"]!.ToString());
        foreach (var scene in TtScenes) Assert.Contains(_obs.Scenes[scene], i => i.Source == "TT Lobby LJ 2");
        Assert.Empty(_director.SourceProblems);
        Assert.Equal("TT Lobby LJ 2", ObsSettings.Load(SettingsPath).Sources["LJ"]);        // remembered

        // On stream it's the fresh one that shows, with its sound.
        _desk.Show("LJ");
        var shown = Assert.Single(_obs.Shown("TT Full"), i => i.Source.StartsWith("TT Lobby "));
        Assert.Equal("TT Lobby LJ 2", shown.Source);
        Assert.False(_obs.Muted["TT Lobby LJ 2"]);
        Assert.Null(_director.SourceIssue("LJ"));

        // It settles: OBS isn't asked again and again, and no "LJ 3".
        int refused = _obs.RefusedSceneItems;
        await _director.BuildAsync();
        await _director.BuildAsync();
        Assert.Equal(refused, _obs.RefusedSceneItems);
        Assert.DoesNotContain("TT Lobby LJ 3", _obs.Inputs.Keys);
    }

    [Fact]
    public async Task The_graphics_swoosh_replay_and_voice_sources_recover_the_same_way()
    {
        await Connect();
        foreach (var n in new[] { "TT Broadcast", "TT Swoosh", "TT Replay Clip", "TT Replay Tag", "TT Voice LJ" }) Zombify(n);
        Assert.Contains("Connected", await Connect());                  // reconnecting builds everything again

        Assert.Equal("TT Broadcast 2", _director.Live("TT Broadcast"));
        Assert.Equal("TT Swoosh 2", _director.Live("TT Swoosh"));
        Assert.Equal("TT Replay Clip 2", _director.Live("TT Replay Clip"));
        Assert.Equal("TT Replay Tag 2", _director.Live("TT Replay Tag"));
        Assert.Equal("TT Voice LJ 2", _director.Live("TT Voice LJ"));
        foreach (var scene in TtScenes)
        {
            Assert.Contains(_obs.Scenes[scene], i => i.Source == "TT Broadcast 2");
            Assert.Contains(_obs.Scenes[scene], i => i.Source == "TT Voice LJ 2");
            Assert.DoesNotContain(_obs.Scenes[scene], i => i.Source == "TT Broadcast");
        }
        Assert.Contains(_obs.Scenes["TT Replay"], i => i.Source == "TT Replay Clip 2");
        Assert.Null(_director.VoiceProblem);
        Assert.Null(_director.SwooshProblem is { } p && !p.StartsWith("No swoosh") ? p : null);

        // The voice follows the picture under its new name.
        _desk.Show("LJ");
        await Task.Delay(100);
        Assert.False(_obs.Muted["TT Voice LJ 2"]);
        Assert.True(_obs.Muted["TT Voice MAL"]);
        var voice = JsonSerializer.SerializeToElement(_director.VoiceStatus());
        Assert.Contains("LJ", voice.GetProperty("Sources").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("LJ 2", voice.GetProperty("Sources").EnumerateArray().Select(x => x.GetString()));

        // Remembered across a restart of Red Alert.
        Assert.Equal("TT Broadcast 2", ObsSettings.Load(SettingsPath).Renamed["TT Broadcast"]);
    }

    [Fact]
    public async Task A_source_OBS_wont_make_at_all_is_reported_and_tried_later_not_in_a_loop()
    {
        await Connect();
        _desk.Show("LJ");
        Zombify("TT Lobby LJ");
        lock (_obs) _obs.Fail.Add("CreateInput");                      // even a fresh one fails
        await _director.BuildAsync();
        Assert.Contains("LJ", _director.SourceProblems.Keys);
        Assert.NotNull(_director.SourceIssue("LJ"));
        var status = JsonSerializer.SerializeToElement(_director.Status());
        Assert.True(status.GetProperty("Missing").TryGetProperty("LJ", out _));   // the Live desk warns, with Fix

        // Switching (and the upkeep tick) leave it alone until its wait is over: no request storm.
        int before; lock (_obs) before = _obs.Requests.Count(r => r == "CreateInput");
        _desk.Show("MAL");
        _desk.Show("LJ");
        lock (_obs) Assert.Equal(before, _obs.Requests.Count(r => r == "CreateInput"));

        // Fix (Rebuild) tries again at once and clears the warning when it works.
        lock (_obs) _obs.Fail.Clear();
        await _director.BuildAsync();
        Assert.Empty(_director.SourceProblems);
        Assert.Equal("TT Lobby LJ 2", _director.Settings.Sources["LJ"]);
        Assert.False(JsonSerializer.SerializeToElement(_director.Status()).GetProperty("Missing").TryGetProperty("LJ", out _));
    }

    [Fact]
    public async Task Stopping_simulation_drops_its_sources_from_the_count_and_old_stand_ins_under_real_names_go()
    {
        _obs.KeepRemoved = true;                                         // OBS keeps what's removed listed, as it did
        _desk.Simulate(true);
        _feeds.Add(("SIM-1", "http://127.0.0.1:8767/sim?lobby=SIM-1"));
        await Connect();
        Assert.Contains("SIM-1", _director.Settings.Sources.Keys);
        // An older version's stand-in under a real lobby's name, still pointing at the stand-in page.
        lock (_obs) _obs.Inputs["TT Lobby LJ"]["url"] = "http://localhost:8766/sim?lobby=LJ";

        _feeds.RemoveAll(f => f.Lobby == "SIM-1");
        _desk.Simulate(false);
        await _director.ForgetSimAsync(CasterDesk.IsSimLobby);
        Assert.DoesNotContain("SIM-1", _director.Settings.Sources.Keys);
        Assert.DoesNotContain("LJ", _director.Settings.Sources.Keys);   // its source was a stand-in: gone
        Assert.Equal(new[] { "MAL" }, _director.Settings.Sources.Keys.OrderBy(k => k));   // the count on the OBS tab is what's really there

        // The real LJ gets a fresh source (OBS still lists the removed one) and nothing is retried in a loop.
        await _director.BuildAsync();
        Assert.Equal("TT Lobby LJ 2", _director.Settings.Sources["LJ"]);
        Assert.Equal(0, _obs.RefusedSceneItems);
        Assert.Equal(2, _director.Settings.Sources.Count);
    }

    [Fact]
    public async Task On_connect_stand_ins_from_older_versions_are_cleaned_up_by_their_address()
    {
        foreach (var old in new[] { "Kai", "LJ", "MAL", "Soggy" })
            _obs.Inputs["TT Lobby " + old] = new() { ["url"] = $"http://127.0.0.1:8766/sim?lobby={old}" };
        _obs.Inputs["TT Voice Kai"] = new() { ["url"] = "http://localhost:8766/simvoice?lobby=Kai" };
        _obs.Inputs["TT Lobby Real"] = new() { ["url"] = "https://vdo.ninja/?view=r" };      // a real lobby: left alone
        await Connect();
        Assert.DoesNotContain(_obs.Inputs.Keys, k => k.EndsWith(" Kai") || k.EndsWith(" Soggy"));
        Assert.Contains("TT Lobby Real", _obs.Inputs.Keys);
        // Real lobbies with those names get real sources.
        Assert.Equal("https://vdo.ninja/?view=a&password=x&videobitrate=8000", _obs.Inputs[_director.Settings.Sources["LJ"]]["url"]!.ToString());
        Assert.Equal("https://vdo.ninja/?view=b&password=x&videobitrate=8000", _obs.Inputs[_director.Settings.Sources["MAL"]]["url"]!.ToString());
    }

    [Fact]
    public void Simulated_lobbies_are_never_named_like_real_ones()
    {
        using var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        desk.Simulate(true);
        _clock.Advance(5);
        for (int i = 0; i < 40; i++) { desk.SimTick(); _clock.Advance(1); }
        var lobbies = desk.Board.Ranking().Select(r => r.Lobby).ToList();
        Assert.NotEmpty(lobbies);
        Assert.All(lobbies, l => Assert.Matches("^SIM-[0-9]+$", l));
        foreach (var real in new[] { "LJ", "Kai", "MAL", "Soggy", "Simon", "SIMBA", "sim", "SIM" })
            Assert.False(CasterDesk.IsSimLobby(real), real);
        Assert.True(CasterDesk.IsSimLobby("SIM-3"));
    }
}

/// <summary>Red Alert's buttons never fail silently: a failure comes back as { ok: false, message }.</summary>
public class SilentFailureTests : IAsyncDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeObs _obs = new();

    public async ValueTask DisposeAsync() { await _obs.DisposeAsync(); _dir.Dispose(); }

    private static async Task<JsonElement> Call(HttpClient http, string path, object body)
    {
        var r = await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Rebuild_TT_scenes_says_what_went_wrong()
    {
        using var server = new BroadcastServer(new BroadcastEnvironment { SettingsFile = Path.Combine(_dir.Path, "RedAlert", "settings.json"), Port = 0, CasterPort = 0, Version = "0.1.0" },
            new HttpClient(new FakeHttp()));
        using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
        http.DefaultRequestHeaders.Add("X-App-Token", server.Token);
        var admin = new SetupCode { Mode = "admin", TournamentId = "c", TournamentName = "Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results" };
        Assert.True((await Call(http, "app/admin/code", new { code = admin.Encode() })).GetProperty("ok").GetBoolean());
        var connect = await Call(http, "app/admin/obs", new { host = "127.0.0.1", port = _obs.Port.ToString(), password = "secret" });
        Assert.True(connect.GetProperty("ok").GetBoolean(), connect.GetProperty("message").GetString());

        Assert.True((await Call(http, "app/admin/obs", new { action = "build" })).GetProperty("ok").GetBoolean());
        lock (_obs) _obs.Fail.Add("GetSceneList");
        var failed = await Call(http, "app/admin/obs", new { action = "build" });
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.Contains("OBS is busy", failed.GetProperty("message").GetString());
    }
}
