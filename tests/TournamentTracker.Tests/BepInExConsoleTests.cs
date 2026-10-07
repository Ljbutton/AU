using System.IO;
using TournamentTracker.App;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>BepInEx's console window: hidden for hosts by default, switched in BepInEx.cfg without touching the rest of it.</summary>
public class BepInExConsoleTests
{
    [Fact]
    public void Before_BepInExs_first_run_the_file_is_made_with_just_the_console_switch()
    {
        using var dir = new TempDir();
        Assert.True(ModInstaller.ConsoleShown(dir.Path));                 // BepInEx's own default
        ModInstaller.SetConsole(dir.Path, false);
        Assert.False(ModInstaller.ConsoleShown(dir.Path));
        Assert.Contains("[Logging.Console]", File.ReadAllText(ModInstaller.BepInExConfig(dir.Path)));
    }

    [Fact]
    public void An_existing_config_keeps_everything_else_and_only_the_consoles_Enabled_changes()
    {
        using var dir = new TempDir();
        string file = ModInstaller.BepInExConfig(dir.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "[Logging.Disk]\n\nEnabled = true\n\n[Logging.Console]\n\n## Enables showing a console for log output.\n# Setting type: Boolean\nEnabled = true\n\n[Preloader]\nEnabled = true\n");
        ModInstaller.SetConsole(dir.Path, false);
        string text = File.ReadAllText(file);
        Assert.False(ModInstaller.ConsoleShown(dir.Path));
        Assert.Contains("[Logging.Disk]\n\nEnabled = true", text.Replace("\r\n", "\n"));   // the log file stays on
        Assert.Contains("[Preloader]\nEnabled = true", text.Replace("\r\n", "\n"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"\[Logging\.Console\]"));
        ModInstaller.SetConsole(dir.Path, true);
        Assert.True(ModInstaller.ConsoleShown(dir.Path));
    }
}
