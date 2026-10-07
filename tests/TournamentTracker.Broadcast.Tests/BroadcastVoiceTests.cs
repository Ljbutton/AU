using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Part 11 on the caster's side: each lobby's voice status, and OBS hearing the lobby on screen.</summary>
public class BroadcastVoiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Each_lobbys_voice_status_reaches_the_caster_and_the_simulation_sends_it_too()
    {
        var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        try
        {
            desk.Apply(JsonSerializer.Serialize(new { type = "voice", lobby = "LJ", t = 1, on = true, sending = true, discord = "capturing", game = "not running", voiceDb = -12.5, gameDb = -60, mic = true }));
            var lj = JsonSerializer.SerializeToElement(desk.VoiceState(), Camel).EnumerateArray().Single(x => x.GetProperty("lobby").GetString() == "LJ");
            Assert.True(lj.GetProperty("online").GetBoolean());
            Assert.True(lj.GetProperty("sending").GetBoolean());
            Assert.Equal("not running", lj.GetProperty("game").GetString());
            Assert.Equal(-12.5, lj.GetProperty("voiceDb").GetDouble());
            Assert.True(lj.GetProperty("mic").GetBoolean());
            _clock.Advance(10);
            Assert.False(JsonSerializer.SerializeToElement(desk.VoiceState(), Camel)[0].GetProperty("online").GetBoolean());

            desk.Simulate(true);
            _clock.Advance(3); desk.SimTick();
            var sim = JsonSerializer.SerializeToElement(desk.VoiceState(), Camel).EnumerateArray().Where(x => x.GetProperty("reported").GetBoolean() && x.GetProperty("online").GetBoolean()).ToList();
            Assert.True(sim.Count >= 4);
        }
        finally { desk.Dispose(); }
    }

    [Fact]
    public async Task OBS_hears_only_the_lobby_on_screen_and_the_caster_can_pin_mute_set_levels_and_duck()
    {
        await using var obs = new FakeObs();
        var desk = new CasterDesk(null, null, new PriorityConfig());
        var feeds = new List<(string, string)> { ("LJ", "https://vdo.ninja/?view=a&password=x&cleanoutput"), ("MAL", "https://vdo.ninja/?view=b&password=x&cleanoutput") };
        var voices = feeds.Select(f => (f.Item1, f.Item2.Replace("view=a", "view=av").Replace("view=b", "view=bv") + "&novideo")).ToList();
        await using var director = new ObsDirector(Path.Combine(_dir.Path, ObsSettings.FileName), desk, () => feeds) { VoiceFeeds = () => voices };
        director.Settings.Swoosh.On = false;
        try
        {
            await director.ConnectAsync("127.0.0.1", obs.Port, "secret");
            Assert.Contains("TT Voice LJ", obs.Inputs.Keys);
            Assert.Equal("https://vdo.ninja/?view=av&password=x&cleanoutput&novideo", obs.Inputs["TT Voice LJ"]["url"]!.ToString());
            // In every TT scene, at the bottom, always on (muting decides who's heard).
            foreach (var scene in new[] { "TT Full", "TT Quad", "TT Replay" })
            {
                Assert.Equal("TT Voice", obs.Scenes[scene][0].Source[..8]);
                Assert.All(obs.Scenes[scene].Where(i => i.Source.StartsWith("TT Voice")), i => Assert.True(i.Enabled));
            }

            OnAir Air(string layout, params string?[] slots) => new() { Layout = layout, Slots = slots.ToList() };
            await director.ApplyAsync(Air("full", "MAL"));
            Assert.False(obs.Muted["TT Voice MAL"]);
            Assert.True(obs.Muted["TT Voice LJ"]);
            await director.ApplyAsync(Air("2up", "LJ", "MAL"));          // slot 1 is heard
            Assert.False(obs.Muted["TT Voice LJ"]);
            Assert.True(obs.Muted["TT Voice MAL"]);
            await director.ShowReplaySceneAsync();                         // replays: no live voice
            Assert.True(obs.Muted["TT Voice LJ"] && obs.Muted["TT Voice MAL"]);
            await director.ApplyAsync(Air("full", "LJ"));
            Assert.False(obs.Muted["TT Voice LJ"]);

            // Listen: MAL stays up whatever is on screen.
            director.Settings.Voice.Pin = "MAL";
            await director.VoiceChangedAsync();
            Assert.False(obs.Muted["TT Voice MAL"]);
            Assert.True(obs.Muted["TT Voice LJ"]);
            director.Settings.Voice.Pin = "";
            // Mute all.
            director.Settings.Voice.MuteAll = true;
            await director.VoiceChangedAsync();
            Assert.True(obs.Muted["TT Voice LJ"] && obs.Muted["TT Voice MAL"]);
            director.Settings.Voice.MuteAll = false;

            // Volume and delay per lobby.
            director.Settings.Voice.Volume["LJ"] = -6;
            director.Settings.Voice.Offset["LJ"] = 350;
            await director.VoiceChangedAsync();
            Assert.Equal(-6, obs.Volume["TT Voice LJ"]);
            Assert.Equal(350, obs.SyncOffset["TT Voice LJ"]);
            Assert.Equal(0, obs.SyncOffset["TT Voice MAL"]);

            // Ducking under the caster's mic: a compressor with it as the sidechain, gone when switched off.
            director.Settings.Voice.DuckUnder = "Mic/Aux";
            await director.VoiceChangedAsync(duck: true);
            Assert.Equal("Mic/Aux", obs.Filters["TT Voice LJ|TT Duck"]["sidechain_source"].GetString());
            director.Settings.Voice.DuckUnder = "";
            await director.VoiceChangedAsync(duck: true);
            Assert.False(obs.Filters.ContainsKey("TT Voice LJ|TT Duck"));

            // Kept in obs.json.
            var saved = ObsSettings.Load(Path.Combine(_dir.Path, ObsSettings.FileName));
            Assert.Equal(350, saved.Voice.Offset["lj"]);
        }
        finally { desk.Dispose(); }
    }
}
