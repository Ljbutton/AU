using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TournamentTracker.App;
using Xunit;
using static TournamentTracker.Tests.AppHttp;

namespace TournamentTracker.Tests;

/// <summary>
/// Every update is checked against the size and SHA-256 its GitHub release lists before it is
/// installed: The Button's own update, the mod (and BepInEx) bundle, and (in Red Alert's tests) Red Alert's.
/// </summary>
public class UpdateCheckTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A new TheButton.exe: starts with "MZ" and is over 1 MB.</summary>
    private static byte[] NewExe()
    {
        var b = new byte[(1024 + 300) * 1024];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        for (int i = 2; i < b.Length; i++) b[i] = (byte)(i * 7);
        return b;
    }

    private static HttpClient Serving(byte[] bytes) =>
        new(new FakeHttp { Default = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } });

    private static ReleaseAsset Exe(long? size, string? sha) => new() { Name = "TheButton.exe", Url = "https://github.com/x/TheButton.exe", Size = size, Sha256 = sha };

    private string OldExe()
    {
        string exe = Path.Combine(_dir.Path, "TheButton.exe");
        File.WriteAllText(exe, "old version");
        return exe;
    }

    private static void LeftAlone(string exe)
    {
        Assert.Equal("old version", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".download"));
        Assert.False(File.Exists(AppUpdater.OldPath(exe)));
    }

    [Fact]
    public void Reads_each_files_size_and_checksum_from_the_release()
    {
        var asset = ReleaseAsset.From(JsonDocument.Parse("""
            { "name": "TheButton.exe", "size": 73071237, "browser_download_url": "https://github.com/x/TheButton.exe",
              "digest": "sha256:D1AC1822B81F6541ECB1403FD1FD65643093732C09972F00974B045275346EBA" }
            """).RootElement)!;
        Assert.Equal("TheButton.exe", asset.Name);
        Assert.Equal("https://github.com/x/TheButton.exe", asset.Url);
        Assert.Equal(73071237, asset.Size);
        Assert.Equal("d1ac1822b81f6541ecb1403fd1fd65643093732c09972f00974b045275346eba", asset.Sha256);

        // Older releases: no digest (or null). Anything but a SHA-256 can't be checked either.
        var old = ReleaseAsset.From(JsonDocument.Parse("""{ "name": "a.zip", "browser_download_url": "https://x/a.zip", "digest": null }""").RootElement)!;
        Assert.Null(old.Sha256);
        Assert.Null(old.Size);
        Assert.Null(ReleaseAsset.Sha256Of("sha512:" + new string('a', 128)));
        Assert.Null(ReleaseAsset.Sha256Of("sha256:not-hex"));
        Assert.Null(ReleaseAsset.Sha256Of("sha256:" + new string('a', 63)));
        Assert.Null(ReleaseAsset.From(JsonDocument.Parse("""{ "name": "no-link.zip" }""").RootElement));
    }

    [Fact]
    public async Task A_download_that_matches_the_release_is_installed()
    {
        string exe = OldExe();
        var bytes = NewExe();
        var log = new ListLog();
        Assert.Equal("", await AppUpdater.InstallAsync(Serving(bytes), Exe(bytes.Length, Sha(bytes)), exe, log: log));
        Assert.Equal(bytes, File.ReadAllBytes(exe));
        Assert.Equal("old version", File.ReadAllText(AppUpdater.OldPath(exe)));
        Assert.Contains(log.All, l => l == $"Downloaded TheButton.exe ({bytes.Length} bytes): the size and SHA-256 match the release.");
    }

    [Fact]
    public async Task A_download_that_doesnt_match_is_refused_and_the_app_left_alone()
    {
        string exe = OldExe();
        var bytes = NewExe();
        var other = (byte[])bytes.Clone();
        other[^1] ^= 1;                                                       // same size, one bit different
        var log = new ListLog();
        Assert.Equal(ReleaseAsset.Mismatch, await AppUpdater.InstallAsync(Serving(other), Exe(bytes.Length, Sha(bytes)), exe, log: log));
        Assert.Equal("The download didn't match the release (it may be incomplete). Nothing was changed; try again later.", ReleaseAsset.Mismatch);
        LeftAlone(exe);
        Assert.Contains(log.All, l => l.StartsWith("Download of TheButton.exe refused: its SHA-256 is " + Sha(other)) && l.Contains("the release lists " + Sha(bytes)));
    }

    [Fact]
    public async Task A_cut_short_or_too_long_download_is_refused()
    {
        string exe = OldExe();
        var bytes = NewExe();
        var log = new ListLog();
        // Cut short: GitHub answered 200 but fewer bytes arrived (the MZ and 1 MB checks would pass).
        Assert.Equal(ReleaseAsset.Mismatch, await AppUpdater.InstallAsync(Serving(bytes[..^1000]), Exe(bytes.Length, Sha(bytes)), exe, log: log));
        LeftAlone(exe);
        Assert.Contains(log.All, l => l.Contains($"{bytes.Length - 1000} bytes arrived, the release lists {bytes.Length}"));
        // The size alone catches it on a release with no checksum.
        Assert.Equal(ReleaseAsset.Mismatch, await AppUpdater.InstallAsync(Serving(bytes[..^1000]), Exe(bytes.Length, null), exe, log: log));
        LeftAlone(exe);
        // More than the release lists: it stops reading and refuses.
        Assert.Equal(ReleaseAsset.Mismatch, await AppUpdater.InstallAsync(Serving(bytes.Concat(new byte[200_000]).ToArray()), Exe(bytes.Length, Sha(bytes)), exe, log: log));
        LeftAlone(exe);
        // The connection dropped part way: the error says so and the part that came is removed.
        var dropped = new HttpClient(new FakeHttp { Default = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Dropping(bytes, 600_000)) } });
        Assert.StartsWith("Couldn't install the new version:", await AppUpdater.InstallAsync(dropped, Exe(bytes.Length, Sha(bytes)), exe, log: log));
        LeftAlone(exe);
    }

    [Fact]
    public async Task A_release_with_no_checksum_still_installs_and_the_log_says_so()
    {
        string exe = OldExe();
        var bytes = NewExe();
        var log = new ListLog();
        Assert.Equal("", await AppUpdater.InstallAsync(Serving(bytes), Exe(bytes.Length, null), exe, log: log));
        Assert.Equal(bytes, File.ReadAllBytes(exe));
        Assert.Contains(log.All, l => l == $"Downloaded TheButton.exe ({bytes.Length} bytes): the release has no checksum to check, so only its size was checked.");

        File.WriteAllText(exe, "old version");
        Assert.Equal("", await AppUpdater.InstallAsync(Serving(bytes), Exe(null, null), exe, log: log));
        Assert.Contains(log.All, l => l == $"Downloaded TheButton.exe ({bytes.Length} bytes): the release has no checksum to check.");
        // The old checks stay: a matching file that isn't a program is still refused.
        var page = Encoding.UTF8.GetBytes("<html>not a program</html>");
        File.WriteAllText(exe, "old version");
        File.Delete(AppUpdater.OldPath(exe));
        Assert.StartsWith("The download wasn't a complete copy of The Button", await AppUpdater.InstallAsync(Serving(page), Exe(page.Length, Sha(page)), exe, log: log));
        LeftAlone(exe);
    }

    // ---- The mod's bundle ----------------------------------------------------------------------

    private static byte[] Pe(ushort machine)
    {
        var b = new byte[0x200];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(b, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(b, 0x80);
        BitConverter.GetBytes(machine).CopyTo(b, 0x84);
        return b;
    }

    private static byte[] Bundle()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] bytes) { using var s = archive.CreateEntry(name).Open(); s.Write(bytes); }
            Add("winhttp.dll", Pe(0x8664));
            Add("BepInEx/plugins/TournamentTracker.dll", new byte[] { 1 });
        }
        return zip.ToArray();
    }

    /// <summary>GitHub answering with a release whose files have <paramref name="listed"/>'s size and SHA-256, and serving <paramref name="served"/>.</summary>
    private static FakeHttp Release(byte[] listed, byte[] served, bool digest = true, byte[]? exe = null, byte[]? exeListed = null) => new()
    {
        Default = r => r.RequestUri!.Host == "api.github.com"
            ? FakeHttp.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                tag_name = "v1.2.0",
                assets = new[]
                {
                    new { name = ModInstaller.BundleName("x64"), browser_download_url = "https://github.com/x/bundle-x64.zip", size = (long)listed.Length, digest = digest ? "sha256:" + Sha(listed) : null },
                    new { name = ModInstaller.AppName, browser_download_url = "https://github.com/x/TheButton.exe", size = (long)(exeListed ?? exe ?? listed).Length, digest = digest ? "sha256:" + Sha(exeListed ?? exe ?? listed) : null },
                },
            }))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(r.RequestUri.AbsoluteUri.EndsWith(".exe") && exe != null ? exe : served) },
    };

    private string Game()
    {
        string game = Path.Combine(_dir.Path, "AU");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "Among Us.exe"), Pe(0x8664));
        return game;
    }

    [Fact]
    public async Task The_mod_bundle_is_only_unpacked_when_it_matches_the_release()
    {
        string game = Game();
        var bundle = Bundle();
        var other = Bundle().Concat(new byte[] { 0 }).ToArray();               // still a zip that would unpack
        var log = new ListLog();

        var installer = new ModInstaller(new HttpClient(Release(bundle, other)), log);
        var release = (await installer.LatestAsync())!;
        Assert.Equal(bundle.Length, release.BundleFor("x64")!.Size);
        Assert.Equal(Sha(bundle), release.BundleFor("x64")!.Sha256);
        Assert.Equal(ReleaseAsset.Mismatch, await installer.InstallAsync(game, release, _ => { }));
        Assert.False(File.Exists(Path.Combine(game, "winhttp.dll")));
        Assert.False(ModInstaller.State(game).Installed);
        Assert.False(Directory.Exists(ModInstaller.DataDir(game)));             // no "installed" marker either
        Assert.Contains(log.All, l => l.StartsWith($"Download of {ModInstaller.BundleName("x64")} refused:"));

        // Cut short.
        installer = new ModInstaller(new HttpClient(Release(bundle, bundle[..^10])), log);
        Assert.Equal(ReleaseAsset.Mismatch, await installer.InstallAsync(game, (await installer.LatestAsync())!, _ => { }));
        Assert.False(ModInstaller.State(game).Installed);

        installer = new ModInstaller(new HttpClient(Release(bundle, bundle)), log);
        Assert.Equal("", await installer.InstallAsync(game, (await installer.LatestAsync())!, _ => { }));
        Assert.True(ModInstaller.State(game).Installed);
        Assert.Contains(log.All, l => l.EndsWith("the size and SHA-256 match the release."));
    }

    [Fact]
    public async Task A_mod_release_with_no_checksum_still_installs()
    {
        string game = Game();
        var bundle = Bundle();
        var log = new ListLog();
        var installer = new ModInstaller(new HttpClient(Release(bundle, bundle, digest: false)), log);
        var release = (await installer.LatestAsync())!;
        Assert.Null(release.BundleFor("x64")!.Sha256);
        Assert.Equal("", await installer.InstallAsync(game, release, _ => { }));
        Assert.True(ModInstaller.State(game).Installed);
        Assert.Contains(log.All, l => l.Contains("the release has no checksum to check"));
    }

    // ---- The Button's own update, end to end ----------------------------------------------------

    [Fact]
    public async Task The_Button_shows_why_its_update_was_refused_and_logs_it()
    {
        var env = ButtonApp.Env(_dir.Path);
        string exe = OldExe();
        env.ExePath = exe;
        var bytes = NewExe();
        var other = (byte[])bytes.Clone();
        other[100] ^= 1;
        using var app = new AppServer(env, new HttpClient(Release(Bundle(), Bundle(), exe: other, exeListed: bytes)));
        using var http = ButtonApp.Client(app);

        // Automatic updates are on by default: the first look finds v1.2.0 and tries to install it.
        await Wait.Until(async () => (await Get(http, "app/state")).GetProperty("appUpdate").GetProperty("error").ValueKind == JsonValueKind.String, 10000);
        var update = (await Get(http, "app/state")).GetProperty("appUpdate");
        Assert.Equal(ReleaseAsset.Mismatch, update.GetProperty("error").GetString());
        Assert.Equal("v1.2.0", update.GetProperty("available").GetString());   // still offered: "Try again"
        Assert.Equal(JsonValueKind.Null, update.GetProperty("ready").ValueKind);
        LeftAlone(exe);
        string log = File.ReadAllText(Path.Combine(_dir.Path, "app", "the-button.log"));
        Assert.Contains("Download of TheButton.exe refused: its SHA-256 is", log);
    }

    /// <summary>Gives the first <c>upTo</c> bytes, then fails as a dropped connection does.</summary>
    private sealed class Dropping : MemoryStream
    {
        private readonly int _upTo;
        public Dropping(byte[] bytes, int upTo) : base(bytes) => _upTo = upTo;
        public override int Read(byte[] buffer, int offset, int count) =>
            Position >= _upTo ? throw new IOException("The connection was reset.") : base.Read(buffer, offset, Math.Min(count, _upTo - (int)Position));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer, offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var tmp = new byte[buffer.Length];
            int n = Read(tmp, 0, tmp.Length);
            tmp.AsSpan(0, n).CopyTo(buffer.Span);
            return ValueTask.FromResult(n);
        }
    }
}
