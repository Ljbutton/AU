using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Settings → Updates in Red Alert: the new RedAlert.exe is only put in place when it matches its release.</summary>
public class RedAlertUpdateTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] NewExe()
    {
        var b = new byte[(1024 + 100) * 1024];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        for (int i = 2; i < b.Length; i++) b[i] = (byte)(i * 13);
        return b;
    }

    /// <summary>Red Alert with a RedAlert.exe of its own, and GitHub listing <paramref name="listed"/> but serving <paramref name="served"/>.</summary>
    private BroadcastServer Server(string exe, byte[] listed, byte[] served, bool digest = true) => new(
        new BroadcastEnvironment { SettingsFile = Path.Combine(_dir.Path, "RedAlert", "settings.json"), Port = 0, CasterPort = 0, Version = "0.1.0", ExePath = exe },
        new HttpClient(new FakeHttp
        {
            Default = r => r.RequestUri!.Host == "api.github.com"
                ? FakeHttp.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        tag_name = "broadcast-v0.2.0", draft = false,
                        assets = new[] { new { name = BroadcastServer.ExeName, browser_download_url = "https://github.com/x/RedAlert.exe", size = (long)listed.Length, digest = digest ? "sha256:" + Sha(listed) : null } },
                    },
                }))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) },
        }));

    private static async Task<JsonElement> Update(HttpClient http) =>
        JsonDocument.Parse(await http.GetStringAsync("app/state")).RootElement.GetProperty("update").Clone();

    /// <summary>Waits for the check to find broadcast-v0.2.0, presses Update, and waits for the result.</summary>
    private static async Task<JsonElement> PressUpdate(HttpClient http)
    {
        await Wait.Until(async () => (await Update(http)).GetProperty("available").GetString() == "v0.2.0", 10000);
        var pressed = await http.PostAsync("app/update", new StringContent("{}"));
        Assert.True(JsonDocument.Parse(await pressed.Content.ReadAsStringAsync()).RootElement.GetProperty("ok").GetBoolean());
        await Wait.Until(async () => { var u = await Update(http); return !u.GetProperty("busy").GetBoolean() && (u.GetProperty("error").ValueKind == JsonValueKind.String || u.GetProperty("ready").ValueKind == JsonValueKind.String); }, 10000);
        return await Update(http);
    }

    [Fact]
    public async Task A_download_that_doesnt_match_is_refused_and_Settings_says_why()
    {
        string exe = Path.Combine(_dir.Path, "RedAlert.exe");
        File.WriteAllText(exe, "old version");
        var bytes = NewExe();
        var other = (byte[])bytes.Clone();
        other[5000] ^= 1;
        using var server = Server(exe, bytes, other);
        using var http = AppHttp.Client(server.Url, server.Token);

        var update = await PressUpdate(http);
        Assert.Equal(ReleaseAsset.Mismatch, update.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, update.GetProperty("ready").ValueKind);
        Assert.Equal("old version", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".download"));
        Assert.False(File.Exists(AppUpdater.OldPath(exe)));
        Assert.Contains("Download of RedAlert.exe refused: its SHA-256 is", File.ReadAllText(server.Log.FilePath!));
    }

    [Fact]
    public async Task A_download_that_matches_is_put_in_place_for_the_next_start()
    {
        string exe = Path.Combine(_dir.Path, "RedAlert.exe");
        File.WriteAllText(exe, "old version");
        var bytes = NewExe();
        using var server = Server(exe, bytes, bytes);
        using var http = AppHttp.Client(server.Url, server.Token);

        var update = await PressUpdate(http);
        Assert.Equal("v0.2.0", update.GetProperty("ready").GetString());
        Assert.Equal(JsonValueKind.Null, update.GetProperty("error").ValueKind);
        Assert.Equal(bytes, File.ReadAllBytes(exe));
        Assert.Contains("Downloaded RedAlert.exe", File.ReadAllText(server.Log.FilePath!));
    }

    [Fact]
    public async Task A_release_with_no_checksum_still_updates_but_a_cut_short_one_does_not()
    {
        string exe = Path.Combine(_dir.Path, "RedAlert.exe");
        File.WriteAllText(exe, "old version");
        var bytes = NewExe();
        using (var cut = Server(exe, bytes, bytes[..^500], digest: false))
        using (var http = AppHttp.Client(cut.Url, cut.Token))
        {
            Assert.Equal(ReleaseAsset.Mismatch, (await PressUpdate(http)).GetProperty("error").GetString());
            Assert.Equal("old version", File.ReadAllText(exe));
        }
        using var server = Server(exe, bytes, bytes, digest: false);
        using var http2 = AppHttp.Client(server.Url, server.Token);
        Assert.Equal("v0.2.0", (await PressUpdate(http2)).GetProperty("ready").GetString());
        Assert.Equal(bytes, File.ReadAllBytes(exe));
        Assert.Contains("the release has no checksum to check", File.ReadAllText(server.Log.FilePath!));
    }
}
