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

/// <summary>Storyline notes: the same note about several players becomes one.</summary>
public class StoryMergeTests
{
    private static Note N(string kind, string text, string key, double w) => new() { Id = kind + key, Kind = kind, Text = text, Players = new() { key }, Weight = w };

    [Fact]
    public void Same_notes_about_different_players_merge_into_one()
    {
        var notes = new List<Note>
        {
            N("record", "[[1|Ann]] is 3–0 as crewmate today", "a", 1.5),
            N("record", "[[2|Bo]] is 3–0 as crewmate today", "b", 1.5),
            N("record", "[[3|Cy]] is 3–0 as crewmate today", "c", 1.5),
            N("record", "[[4|Di]] is 2–1 as crewmate today", "d", 1.5),
            N("streak", "[[1|Ann]] has won 3 in a row", "a", 5),
            N("streak", "[[2|Bo]] has won 3 in a row", "b", 5),
            new() { Id = "r", Kind = "rivalry", Text = "[[1|Ann]] has killed [[2|Bo]] 2 times today", Players = new() { "a", "b" }, Weight = 7 },
        };
        var merged = Storylines.Merge(notes, "2026-10-07");
        Assert.Equal(4, merged.Count);
        var rec = merged.Single(n => n.Kind == "record" && n.Players.Count == 3);
        Assert.Equal("3 players are 3–0 as crewmate today: [[1|Ann]], [[2|Bo]] and [[3|Cy]]", rec.Text);
        Assert.Equal("2 players have won 3 in a row: [[1|Ann]] and [[2|Bo]]", merged.Single(n => n.Kind == "streak").Text);
        Assert.Contains(merged, n => n.Id == "r");
        // The id stays the same when another player joins the group.
        notes.Add(N("record", "[[5|Ed]] is 3–0 as crewmate today", "e", 1.5));
        Assert.Equal(rec.Id, Storylines.Merge(notes, "2026-10-07").Single(n => n.Kind == "record" && n.Players.Count == 4).Id);
    }
}
