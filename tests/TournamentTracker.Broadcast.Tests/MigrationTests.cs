using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Red Alert's first start: the caster's setup comes over from The Button's folder.</summary>
public class MigrationTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void The_casters_setup_comes_over_once_and_nothing_is_overwritten_or_taken_away()
    {
        string button = Path.Combine(_dir.Path, "TheButton"), settings = Path.Combine(_dir.Path, "RedAlert", "settings.json");
        Directory.CreateDirectory(Path.Combine(button, "broadcast-games"));
        Directory.CreateDirectory(Path.Combine(button, "tools"));
        var admin = new SetupCode { Mode = "admin", TournamentId = "c", TournamentName = "Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results" }.Encode();
        File.WriteAllText(Path.Combine(button, "app.json"), JsonSerializer.Serialize(new { GamePath = "C:\\Games\\Among Us", AdminCode = admin }));
        // OBS, with the replay keys and folder, as The Button saved it.
        File.WriteAllText(Path.Combine(button, "obs.json"), "{\"host\":\"127.0.0.1\",\"port\":4455,\"replay\":{\"folder\":\"D:\\\\Clips\",\"hotkeys\":{\"playPause\":\"KeyP\"}}}");
        File.WriteAllText(Path.Combine(button, PriorityConfig.FileName), "{\"tiers\":{\"must\":90}}");
        File.WriteAllText(Path.Combine(button, Roster.FileName), "Name,Discord\nJake Rivera,1\n");
        File.WriteAllText(Path.Combine(button, "twitch.json"), "{\"clientId\":\"abc\"}");
        File.WriteAllText(Path.Combine(button, "broadcast-games", "LJ-1.json"), "{}");
        File.WriteAllText(Path.Combine(button, "tools", "ffmpeg.exe"), "x");
        File.WriteAllText(Path.Combine(button, "tt-swoosh-1a2b3c4d.webm"), "w");
        File.WriteAllText(Path.Combine(button, "settings-the-button-only.json"), "{}");

        var copied = Migration.Run(button, settings);
        string to = Path.GetDirectoryName(settings)!;
        Assert.Contains("obs.json", copied);
        Assert.Contains("administration code", copied);
        Assert.Equal(File.ReadAllText(Path.Combine(button, "obs.json")), File.ReadAllText(Path.Combine(to, "obs.json")));
        Assert.Equal("KeyP", ObsSettings.Load(Path.Combine(to, "obs.json")).Replay.Hotkeys["playPause"]);
        Assert.Equal("D:\\Clips", ObsSettings.Load(Path.Combine(to, "obs.json")).Replay.Folder);
        Assert.True(File.Exists(Path.Combine(to, PriorityConfig.FileName)));
        Assert.True(File.Exists(Path.Combine(to, Roster.FileName)));
        Assert.True(File.Exists(Path.Combine(to, "twitch.json")));
        Assert.True(File.Exists(Path.Combine(to, "broadcast-games", "LJ-1.json")));
        Assert.True(File.Exists(Path.Combine(to, "tools", "ffmpeg.exe")));
        Assert.True(File.Exists(Path.Combine(to, "tt-swoosh-1a2b3c4d.webm")));
        Assert.False(File.Exists(Path.Combine(to, "app.json")));                        // The Button's own settings stay its own
        Assert.False(File.Exists(Path.Combine(to, "settings-the-button-only.json")));
        Assert.Equal(admin, BroadcastAppSettings.Load(settings).AdminCode);
        Assert.True(File.Exists(Path.Combine(button, "obs.json")));                    // copied, not moved

        // Only once: a change made in Red Alert later is never overwritten.
        File.WriteAllText(Path.Combine(to, "obs.json"), "{\"port\":4456}");
        Assert.Empty(Migration.Run(button, settings));
        Assert.Equal("{\"port\":4456}", File.ReadAllText(Path.Combine(to, "obs.json")));
    }

    [Fact]
    public void The_folder_from_before_the_rename_comes_over_once_and_skips_the_browser_cache()
    {
        string old = Path.Combine(_dir.Path, "TTBroadcast"), now = Path.Combine(_dir.Path, "RedAlert");
        Directory.CreateDirectory(Path.Combine(old, "broadcast-games"));
        Directory.CreateDirectory(Path.Combine(old, "WebView2", "Default"));
        File.WriteAllText(Path.Combine(old, "settings.json"), "{\"AdminCode\":\"x\"}");
        File.WriteAllText(Path.Combine(old, Migration.Marker), "done");
        File.WriteAllText(Path.Combine(old, "broadcast-games", "LJ-1.json"), "{}");
        File.WriteAllText(Path.Combine(old, "WebView2", "Default", "cache"), "big");

        Assert.True(Migration.FromOldName(old, now));
        Assert.Equal("{\"AdminCode\":\"x\"}", File.ReadAllText(Path.Combine(now, "settings.json")));
        Assert.True(File.Exists(Path.Combine(now, Migration.Marker)));             // so The Button's isn't copied over it again
        Assert.True(File.Exists(Path.Combine(now, "broadcast-games", "LJ-1.json")));
        Assert.False(Directory.Exists(Path.Combine(now, "WebView2")));

        File.WriteAllText(Path.Combine(now, "settings.json"), "{}");
        Assert.False(Migration.FromOldName(old, now));                             // only once
        Assert.Equal("{}", File.ReadAllText(Path.Combine(now, "settings.json")));
        Assert.False(Migration.FromOldName(Path.Combine(_dir.Path, "none"), Path.Combine(_dir.Path, "fresh")));
    }
}
