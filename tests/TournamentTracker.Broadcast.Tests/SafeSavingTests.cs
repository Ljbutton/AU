using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// Red Alert's own files are saved whole (never half a file) and a file that can't be read is kept as
/// name.corrupt-… before the defaults or the last good settings are saved over it.
/// </summary>
public class SafeSavingTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_broken_settings_file_is_kept_before_the_defaults_are_saved_over_it()
    {
        string file = Path.Combine(_dir.Path, "settings.json");
        File.WriteAllText(file, "{\"AdminCode\":\"TT1-");                      // cut off by a crash in an older version
        var settings = BroadcastAppSettings.Load(file);
        Assert.Null(settings.AdminCode);
        settings.CamOff = true;
        settings.Save(file);
        string kept = Assert.Single(Directory.GetFiles(_dir.Path, "settings.json.corrupt-*"));
        Assert.Equal("{\"AdminCode\":\"TT1-", File.ReadAllText(kept));
        Assert.True(BroadcastAppSettings.Load(file).CamOff);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
    }

    [Fact]
    public void A_hand_edited_file_with_a_mistake_is_kept_before_a_switch_saves_over_it()
    {
        string file = Path.Combine(_dir.Path, BroadcastSettings.FileName);
        var settings = new BroadcastSettingsFile(file);                          // written with the defaults
        Assert.Null(settings.Problem);
        File.WriteAllText(file, "{ \"elements\": { \"standings\": tru }");      // edited by hand, wrongly
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        settings.Refresh(force: true);
        Assert.NotNull(settings.Problem);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.corrupt-*"));               // nothing kept while nothing is written over it

        settings.Set("standings", false);                                        // a switch saves the last good settings
        string kept = Assert.Single(Directory.GetFiles(_dir.Path, BroadcastSettings.FileName + ".corrupt-*"));
        Assert.Contains("tru }", File.ReadAllText(kept));
        Assert.False(BroadcastSettings.Parse(File.ReadAllText(file)).Elements["standings"]);
        settings.Set("standings", true);                                         // fine again: no second copy
        Assert.Single(Directory.GetFiles(_dir.Path, "*.corrupt-*"));
    }
}
