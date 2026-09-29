using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.Discord;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Real HTTP for this computer (the mod), made-up answers for GitHub.</summary>
public sealed class LocalOrFake : DelegatingHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Internet { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    public LocalOrFake() : base(new HttpClientHandler()) { }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        request.RequestUri!.Host == "127.0.0.1" ? base.SendAsync(request, ct) : Task.FromResult(Internet(request));
}

public class AppTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string Game(string at)
    {
        Directory.CreateDirectory(at);
        File.WriteAllText(Path.Combine(at, "Among Us.exe"), "");
        return at;
    }

    private static byte[] Pe(ushort machine)
    {
        var b = new byte[0x200];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(b, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(b, 0x80);
        BitConverter.GetBytes(machine).CopyTo(b, 0x84);
        return b;
    }

    [Fact]
    public void Finds_the_game_in_any_steam_library_and_in_epic()
    {
        string steam = Path.Combine(_dir.Path, "Steam");
        string otherLibrary = Path.Combine(_dir.Path, "D", "SteamLibrary");
        Game(Path.Combine(otherLibrary, "steamapps", "common", "Among Us"));
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n \"1\"\n {{\n  \"path\"  \"{otherLibrary.Replace("\\", "\\\\")}\"\n }}\n}}");
        string manifests = Path.Combine(_dir.Path, "Manifests");
        string epicGame = Game(Path.Combine(_dir.Path, "Epic", "AmongUs"));
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, "x.item"), JsonSerializer.Serialize(new { DisplayName = "Among Us", InstallLocation = epicGame }));

        var found = GameLocator.Find(steam, manifests, new[] { Path.Combine(_dir.Path, "missing") });
        Assert.Equal(new[] { "Steam", "Epic Games" }, found.Select(f => f.Store));
        Assert.Equal(epicGame, found[1].Path);
    }

    [Fact]
    public void Tells_a_32_bit_loader_from_a_64_bit_one()
    {
        string game = Game(Path.Combine(_dir.Path, "AU"));
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(game, "BepInEx", "plugins", "TournamentTracker.dll"), "");
        File.WriteAllBytes(Path.Combine(game, "winhttp.dll"), Pe(0x8664));
        var state = ModInstaller.State(game);
        Assert.True(state.Installed);
        Assert.False(state.LoaderIs32Bit);
        Assert.True(state.NeedsRepair);
        File.WriteAllBytes(Path.Combine(game, "winhttp.dll"), Pe(0x14C));
        Assert.False(ModInstaller.State(game).NeedsRepair);
    }

    [Fact]
    public async Task Installs_the_latest_release_over_the_game_keeping_settings()
    {
        string game = Game(Path.Combine(_dir.Path, "AU"));
        string data = ModInstaller.DataDir(game);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "links.json"), "keep me");
        File.WriteAllBytes(Path.Combine(game, "winhttp.dll"), Pe(0x8664));      // the old 64-bit loader

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] bytes) { using var s = archive.CreateEntry(name).Open(); s.Write(bytes); }
            Add("winhttp.dll", Pe(0x14C));
            Add("BepInEx/plugins/TournamentTracker.dll", new byte[] { 1 });
            Add("BepInEx/core/BepInEx.Core.dll", new byte[] { 2 });
        }
        var web = new LocalOrFake
        {
            Internet = r => r.RequestUri!.AbsoluteUri.Contains("api.github.com")
                ? FakeHttp.Json(HttpStatusCode.OK, """{"tag_name":"v1.2.0","assets":[{"name":"TournamentTracker-Full.zip","browser_download_url":"https://github.com/x/TournamentTracker-Full.zip"}]}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip.ToArray()) },
        };
        var installer = new ModInstaller(new HttpClient(web));
        var release = await installer.LatestAsync();
        Assert.Equal("v1.2.0", release!.Tag);
        var progress = new List<string>();
        Assert.Equal("", await installer.InstallAsync(game, release, progress.Add));

        var state = ModInstaller.State(game);
        Assert.True(state.Installed);
        Assert.True(state.LoaderIs32Bit);
        Assert.Equal("v1.2.0", state.InstalledVersion);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(data, "links.json")));
        Assert.Contains(progress, p => p.StartsWith("Downloading v1.2.0"));
    }

    private (AppServer App, HttpClient Http, string Game) App(string? steamGame = null)
    {
        string game = steamGame ?? Game(Path.Combine(_dir.Path, "Steam", "steamapps", "common", "Among Us"));
        var env = new AppEnvironment
        {
            SettingsFile = Path.Combine(_dir.Path, "app", "app.json"), SteamRoot = Path.Combine(_dir.Path, "Steam"),
            EpicManifests = Path.Combine(_dir.Path, "none"), Fallbacks = Array.Empty<string>(), Downloads = Path.Combine(_dir.Path, "Downloads"),
        };
        var app = new AppServer(env, new HttpClient(new LocalOrFake()));
        var http = new HttpClient { BaseAddress = new Uri(app.Url) };
        http.DefaultRequestHeaders.Add("X-App-Token", app.Token);
        return (app, http, game);
    }

    private static async Task<JsonElement> Get(HttpClient http, string path) => JsonDocument.Parse(await http.GetStringAsync(path)).RootElement;
    private static async Task<JsonElement> Post(HttpClient http, string path, object? body = null) =>
        JsonDocument.Parse(await (await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task The_app_page_is_served_with_its_token_and_everything_else_needs_it()
    {
        var (app, http, _) = App();
        using var _ = app;
        using var anon = new HttpClient { BaseAddress = new Uri(app.Url) };
        string page = await anon.GetStringAsync("/");
        Assert.Contains($"const TOKEN = '{app.Token}'", page);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("app/state")).StatusCode);
        var rebinding = new HttpRequestMessage(HttpMethod.Get, "app/state");
        rebinding.Headers.Host = "evil.example";
        rebinding.Headers.Add("X-App-Token", app.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.SendAsync(rebinding)).StatusCode);
        Assert.Contains("REPLAY", await anon.GetStringAsync("/viewer"));
        var head = await anon.GetByteArrayAsync("/crew/17.png");            // every colour has a head, up to coral
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, head.Take(4).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/crew/18.png")).StatusCode);
    }

    [Fact]
    public async Task Finds_the_game_takes_a_setup_code_and_shows_what_is_in_it()
    {
        var (app, http, game) = App();
        using var _ = app;
        var state = await Get(http, "app/state");
        Assert.True(state.GetProperty("game").GetProperty("found").GetBoolean());
        Assert.False(state.GetProperty("mod").GetProperty("installed").GetBoolean());
        Assert.False(state.GetProperty("connected").GetBoolean());

        var bad = await Post(http, "app/setup", new { code = "hello" });
        Assert.False(bad.GetProperty("ok").GetBoolean());
        var code = new SetupCode { TournamentId = "oct", TournamentName = "October prelims", Server = "Sus Squad", Webhook = "https://discord.com/api/webhooks/1/a", Lobby = LobbySettings.TournamentDefaults() };
        var good = await Post(http, "app/setup", new { code = "  " + code.Encode() + "\n" });
        Assert.True(good.GetProperty("ok").GetBoolean());
        Assert.StartsWith("Setup saved: October prelims (preliminary in Sus Squad). It's used next time Among Us starts.", good.GetProperty("message").GetString());
        Assert.True(File.Exists(Path.Combine(ModInstaller.DataDir(game), SetupCode.FileName)));

        var setup = (await Get(http, "app/state")).GetProperty("setup");
        Assert.Equal("Preliminary", setup.GetProperty("kind").GetString());
        Assert.Equal(1.25, setup.GetProperty("settings").GetProperty("playerSpeed").GetDouble(), 3);
        Assert.False(setup.TryGetProperty("scoring", out var scoring));   // point values stay with the organiser

        var offline = await Post(http, "app/command", new { command = "r2" });
        Assert.False(offline.GetProperty("ok").GetBoolean());
        Assert.StartsWith("Among Us isn't running", offline.GetProperty("replies")[0].GetString());
    }

    [Fact]
    public async Task Commands_from_the_app_reach_the_mod_in_the_game()
    {
        var (app, http, game) = App();
        using var _ = app;
        // "Among Us" with the mod running: its data folder is inside the game folder.
        using var mod = new TournamentSession(new TrackerSettings { LiveStatus = false, TournamentName = "Cup", ControlPort = 0 }, ModInstaller.DataDir(game),
            NullLog.Instance, new HttpClient(new FakeHttp()), null, new FakeVoiceApi(), new VoicePresenceState("g1"));
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(game, "BepInEx", "plugins", "TournamentTracker.dll"), "");
        mod.VoiceTick(Voice.VoicePhase.Lobby, Players.Lobby(), "ABCDEF", "Polus");

        var state = await Get(http, "app/state");
        Assert.True(state.GetProperty("connected").GetBoolean());
        Assert.Equal("ABCDEF", state.GetProperty("status").GetProperty("lobbyCode").GetString());

        var command = Post(http, "app/command", new { command = "r4" });
        while (!command.IsCompleted) { mod.Pump(); await Task.Delay(10); }
        Assert.True((await command).GetProperty("ok").GetBoolean());
        Assert.Equal(4, mod.Round);
        var activity = await Get(http, "app/activity?since=0");
        Assert.Contains(activity.GetProperty("lines").EnumerateArray(), l => l.GetProperty("text").GetString()!.StartsWith("Round 4"));
    }

    [Fact]
    public async Task Lists_replays_from_the_games_folder_and_downloads()
    {
        var (app, http, game) = App();
        using var _ = app;
        string games = Path.Combine(ModInstaller.DataDir(game), "games", "cup");
        Directory.CreateDirectory(games);
        File.WriteAllBytes(Path.Combine(games, "tt-replay-LJ-3-20261003-192144.json.gz"), new byte[] { 1, 2, 3 });
        Directory.CreateDirectory(Path.Combine(_dir.Path, "Downloads"));
        File.WriteAllBytes(Path.Combine(_dir.Path, "Downloads", "tt-replay-MAL-1-20261003-190000.json.gz"), new byte[] { 4 });

        var list = await Get(http, "app/replays");
        Assert.Equal(2, list.GetArrayLength());
        Assert.Contains(list.EnumerateArray(), r => r.GetProperty("label").GetString() == "MAL-1" && r.GetProperty("downloaded").GetBoolean());
        var bytes = await http.GetByteArrayAsync("app/replay?name=tt-replay-LJ-3-20261003-192144.json.gz");
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("app/replay?name=..%2F..%2Fsecret.txt")).StatusCode);
    }
}

public class AppVersionTests
{
    [Theory]
    [InlineData("v1.2.0", "v1.1.0", true)]
    [InlineData("v1.1.0", "v1.2.0", false)]
    [InlineData("v1.10.0", "v1.9.0", true)]
    [InlineData("v1.2.0", "v1.2.0", false)]
    [InlineData("v1.2.0", null, true)]
    public void Offers_an_update_only_for_a_newer_release(string latest, string? installed, bool newer) =>
        Assert.Equal(newer, AppServer.Newer(latest, installed));
}
public class AppUpdaterTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_new_version_replaces_the_running_one_and_the_old_one_is_cleaned_up_later()
    {
        string exe = Path.Combine(_dir.Path, "TheButton.exe");
        File.WriteAllText(exe, "old version");
        var newExe = new byte[2 * 1024 * 1024]; newExe[0] = (byte)'M'; newExe[1] = (byte)'Z';
        var http = new HttpClient(new FakeHttp { Default = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(newExe) } });

        Assert.Equal("", await AppUpdater.InstallAsync(http, "https://github.com/x/TheButton.exe", exe));
        Assert.Equal(newExe.Length, new FileInfo(exe).Length);
        Assert.Equal("old version", File.ReadAllText(AppUpdater.OldPath(exe)));   // kept until the next start
        AppUpdater.CleanUp(exe);
        Assert.False(File.Exists(AppUpdater.OldPath(exe)));
    }

    [Fact]
    public async Task A_bad_download_leaves_the_app_alone()
    {
        string exe = Path.Combine(_dir.Path, "TheButton.exe");
        File.WriteAllText(exe, "old version");
        var http = new HttpClient(new FakeHttp { Default = _ => FakeHttp.Json(HttpStatusCode.OK, "<html>not a program</html>") });
        Assert.StartsWith("The download wasn't a complete copy", await AppUpdater.InstallAsync(http, "https://github.com/x/TheButton.exe", exe));
        Assert.Equal("old version", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".download"));
        var down = new HttpClient(new FakeHttp { Default = _ => FakeHttp.Json(HttpStatusCode.NotFound, "{}") });
        Assert.Contains("404", await AppUpdater.InstallAsync(down, "https://github.com/x/TheButton.exe", exe));
    }
}
