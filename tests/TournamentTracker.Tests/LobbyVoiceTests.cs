using System.Text.Json;
using TournamentTracker.App;
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
