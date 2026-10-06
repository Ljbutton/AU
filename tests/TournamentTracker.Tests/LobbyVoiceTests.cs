using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.App.Broadcast;
using TournamentTracker.App.Voice;
using TournamentTracker.Discord;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Part 11: lobby voice from the referee's PC to the stream.</summary>
public class LobbyVoiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Dispose() => _dir.Dispose();

    private static short[] Tone(short value, int frames) => Enumerable.Repeat(value, frames * 2).ToArray();

    private static short[] Samples(byte[] b) => Enumerable.Range(0, b.Length / 2).Select(i => (short)(b[i * 2] | b[i * 2 + 1] << 8)).ToArray();

    [Fact]
    public void Discord_and_game_sound_are_mixed_with_their_levels_and_old_sound_is_dropped()
    {
        using var mix = new VoiceCapture(() => _clock.Now) { VoiceLevel = 1.0, GameLevel = 0.5 };
        mix.Read();                                             // the send page is reading
        mix.AddVoice(Tone(1000, 480), 480);
        mix.AddGame(Tone(2000, 480), 480);
        var out1 = Samples(mix.Read());
        Assert.Equal(960, out1.Length);
        Assert.All(out1, s => Assert.Equal(2000, s));           // 1000 × 1.0 + 2000 × 0.5

        // Too loud together: clipped, not wrapped round.
        mix.VoiceLevel = 1.5; mix.GameLevel = 1.5;
        mix.AddVoice(Tone(30000, 10), 10);
        mix.AddGame(Tone(30000, 10), 10);
        Assert.All(Samples(mix.Read()), s => Assert.Equal(short.MaxValue, s));

        // Only one program sending (the other closed): it isn't held up.
        mix.AddVoice(Tone(100, 48), 48);
        Assert.Equal(96, Samples(mix.Read()).Length);

        // More than half a second waiting: only the last half second is kept.
        mix.AddVoice(Tone(5, 48000), 48000);
        Assert.Equal(24000 * 2, Samples(mix.Read()).Length);

        // Nobody reading for a while (the page closed): nothing piles up; only fresh sound comes through.
        mix.AddVoice(Tone(7, 4800), 4800);
        _clock.Advance(5);
        mix.AddVoice(Tone(5, 480), 480);
        Assert.Equal(960, Samples(mix.Read()).Length);

        var state = JsonSerializer.SerializeToElement(mix.State(), Camel);
        Assert.Equal("off", state.GetProperty("voice").GetProperty("state").GetString());
    }

    [Fact]
    public void The_meter_shows_each_sources_recent_peak()
    {
        using var mix = new VoiceCapture(() => _clock.Now);
        mix.Read();
        mix.AddVoice(Tone(16384, 100), 100);                    // half scale: −6 dB
        var s = JsonSerializer.SerializeToElement(mix.State(), Camel);
        Assert.Equal(-6.0, s.GetProperty("voice").GetProperty("level").GetDouble(), 0);
        Assert.Equal(-60, s.GetProperty("game").GetProperty("level").GetDouble());
        _clock.Advance(2);
        Assert.Equal(-60, JsonSerializer.SerializeToElement(mix.State(), Camel).GetProperty("voice").GetProperty("level").GetDouble());
    }

    [Fact]
    public void The_voice_stream_is_the_video_stream_plus_v_with_no_picture()
    {
        var data = JsonSerializer.SerializeToElement(new { vdo = "ttabc123:secretkey" });
        Assert.Equal("https://vdo.ninja/?view=ttabc123v&password=secretkey&novideo&cleanoutput", Organizer.VoiceUrl(data));
        Assert.Null(Organizer.VoiceUrl(JsonSerializer.SerializeToElement(new { vdo = "bad id:x" })));
    }

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

    [Fact]
    public async Task The_referee_can_post_a_voice_may_be_recorded_notice_in_the_lobby_voice_channel()
    {
        var http = new FakeHttp();
        var settings = new TrackerSettings { TournamentName = "Cup", LiveStatus = false, ControlPort = -1, PostLeaderboardAfterEachGame = false };
        settings.AutoMute.Enabled = true;
        settings.AutoMute.GuildId = "g1";
        settings.AutoMute.BotTokens.Add("tok");
        settings.AutoMute.VoiceChannelId = "vc";
        using var s = new TournamentSession(settings, _dir.Path, NullLog.Instance, new HttpClient(http), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1") { Connected = true });
        s.VoiceTick(Voice.VoicePhase.Lobby, Players.Lobby());
        Assert.True(s.RunCommand("voicenotice"));
        await Wait.Until(() => http.Requests.Any(r => r.Url.EndsWith("/channels/vc/messages")));
        var post = http.Requests.First(r => r.Url.EndsWith("/channels/vc/messages"));
        Assert.Contains("may be recorded", post.Body);
        Assert.Equal("Bot tok", post.Auth);
        // Not again for a while.
        int count = http.Requests.Count(r => r.Url.EndsWith("/channels/vc/messages"));
        s.RunCommand("voicenotice");
        await Task.Delay(200);
        Assert.Equal(count, http.Requests.Count(r => r.Url.EndsWith("/channels/vc/messages")));
        Assert.Contains(s.Pump(), r => r.Text.Contains("already up"));
    }
}
