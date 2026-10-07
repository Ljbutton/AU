using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Audio safety: the Mute all key, and the mic lobby voice ducks under.</summary>
public class AudioSafetyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+M", Hotkey.Ctrl | Hotkey.Shift, 'M', "Ctrl+Shift+M")]
    [InlineData("shift + ctrl + m", Hotkey.Ctrl | Hotkey.Shift, 'M', "Ctrl+Shift+M")]
    [InlineData("Alt+F9", Hotkey.Alt, 0x78, "Alt+F9")]
    [InlineData("F13", 0, 0x7C, "F13")]
    [InlineData("Pause", 0, 0x13, "Pause")]
    [InlineData("Ctrl+Alt+7", Hotkey.Ctrl | Hotkey.Alt, '7', "Ctrl+Alt+7")]
    public void Mute_all_keys_parse(string text, int mods, int key, string normal)
    {
        Assert.True(Hotkey.TryParse(text, out int m, out int k, out string n));
        Assert.Equal((mods, key, normal), (m, k, n));
    }

    [Theory]
    [InlineData("M")]            // a bare letter would fire while typing anywhere
    [InlineData("Ctrl+")]
    [InlineData("Hyper+M")]
    [InlineData("")]
    public void Unsafe_or_unknown_keys_are_refused(string text) => Assert.False(Hotkey.TryParse(text, out _, out _, out _));

    [Fact]
    public void Ducking_picks_the_obvious_mic()
    {
        Assert.Equal("Mic/Aux", ObsDirector.PickMic(new[] { ("Desktop Audio", "wasapi_output_capture"), ("Mic/Aux", "wasapi_input_capture"), ("TT Voice A", "browser_source") }));
        Assert.Equal("Shure MV7 mic", ObsDirector.PickMic(new[] { ("Line in", "wasapi_input_capture"), ("Shure MV7 mic", "wasapi_input_capture") }));
        Assert.Null(ObsDirector.PickMic(new[] { ("Desktop Audio", "wasapi_output_capture") }));
    }
}
