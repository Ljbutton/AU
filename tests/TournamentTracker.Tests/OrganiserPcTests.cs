using System.IO.Compression;
using System.Net;
using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.Setup;
using Xunit;
using static TournamentTracker.Tests.AppHttp;
using static TournamentTracker.Tests.ButtonApp;

namespace TournamentTracker.Tests;

/// <summary>
/// The organiser's PC (or a referee's) without Among Us: an administration code works there, a host's code still
/// needs Among Us, and nothing on Home, in updates or in the support bundle asks for the game or the mod.
/// </summary>
public class OrganiserPcTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static SetupCode Admin() => new() { Mode = "admin", TournamentId = "oct", TournamentName = "October", BotTokens = new() { "a.b.c" }, ResultsChannelId = "1" };

    private static SetupCode Host() => new() { TournamentId = "oct", TournamentName = "October prelims", Server = "Sus Squad", Webhook = "https://discord.com/api/webhooks/1/a" };

    /// <summary>The Button on a PC where it finds no Among Us.</summary>
    private AppEnvironment NoGame()
    {
        var env = Env(_dir.Path);
        env.Desktop = Path.Combine(_dir.Path, "Desktop");
        return env;
    }

    [Fact]
    public async Task An_administration_code_works_without_Among_Us_and_is_kept_in_The_Buttons_settings()
    {
        var env = NoGame();
        var discord = new FakeHttp();
        using (var app = new AppServer(env, new HttpClient(discord)))
        {
            using var http = Client(app);
            var before = await Get(http, "app/state");
            Assert.False(before.GetProperty("game").GetProperty("found").GetBoolean());
            Assert.Equal(JsonValueKind.Null, before.GetProperty("admin").ValueKind);
            Assert.False(before.GetProperty("organiserPc").GetBoolean());

            var unlocked = await Post(http, "app/setup", new { code = Admin().Encode() });
            Assert.True(unlocked.GetProperty("ok").GetBoolean(), unlocked.ToString());
            Assert.True(unlocked.GetProperty("admin").GetBoolean());                        // the page opens the Organiser tab
            Assert.Equal("Organiser view unlocked for October.", unlocked.GetProperty("message").GetString());

            var state = await Get(http, "app/state");
            Assert.Equal("October", state.GetProperty("admin").GetProperty("tournament").GetString());
            Assert.True(state.GetProperty("organiserPc").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("app/admin")).StatusCode);   // the Organiser tab's lobbies and standings

            // The Referee tab's results-channel tools post as the administration bot, no mod needed.
            var posted = await Post(http, "app/admin/command", new { kind = "adjust", game = "LJ", player = "red", points = "-2", reason = "meta call" });
            Assert.Equal("Posted: !adjust LJ red -2 meta call", posted.GetProperty("message").GetString());
            Assert.Contains(discord.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("/channels/1/messages") && r.Body.Contains("!adjust LJ red -2 meta call"));
        }

        // Kept in The Button's own settings (%LOCALAPPDATA%\TheButton\app.json), never in an Among Us folder.
        var saved = JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement;
        Assert.True(SetupCode.TryParse(saved.GetProperty("AdminCode").GetString(), out var kept, out _) && kept.IsAdmin);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("SetupCode").ValueKind);
        Assert.Empty(Directory.GetDirectories(_dir.Path, "BepInEx", SearchOption.AllDirectories));

        // Still unlocked after The Button restarts; Lock takes it away again.
        using var again = new AppServer(env, new HttpClient(new FakeHttp()));
        using var http2 = Client(again);
        Assert.Equal("October", (await Get(http2, "app/state")).GetProperty("admin").GetProperty("tournament").GetString());
        Assert.Equal("Administration is off.", (await Post(http2, "app/admin/code", new { code = "" })).GetProperty("message").GetString());
        var locked = await Get(http2, "app/state");
        Assert.Equal(JsonValueKind.Null, locked.GetProperty("admin").ValueKind);
        Assert.False(locked.GetProperty("organiserPc").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await http2.GetAsync("app/admin")).StatusCode);
    }

    [Fact]
    public async Task A_host_code_still_needs_Among_Us()
    {
        var env = NoGame();
        using (var app = new AppServer(env, new HttpClient(new FakeHttp())))
        {
            using var http = Client(app);
            var refused = await Post(http, "app/setup", new { code = Host().Encode() });
            Assert.False(refused.GetProperty("ok").GetBoolean());
            Assert.Equal("Find Among Us first.", refused.GetProperty("message").GetString());

            // With the administration code in, still the same: a host's code goes in the mod's folder.
            Assert.True((await Post(http, "app/setup", new { code = Admin().Encode() })).GetProperty("ok").GetBoolean());
            Assert.Equal("Find Among Us first.", (await Post(http, "app/setup", new { code = Host().Encode() })).GetProperty("message").GetString());
            Assert.Equal("Find Among Us first.", (await Post(http, "app/setup/clear")).GetProperty("message").GetString());
            // Something that isn't a code says so (it may be a damaged administration code).
            Assert.False((await Post(http, "app/setup", new { code = "hello" })).GetProperty("ok").GetBoolean());
            Assert.NotEqual("Find Among Us first.", (await Post(http, "app/setup", new { code = "hello" })).GetProperty("message").GetString());

            var state = await Get(http, "app/state");
            Assert.Equal(JsonValueKind.Null, state.GetProperty("setup").ValueKind);
            Assert.Equal("October", state.GetProperty("admin").GetProperty("tournament").GetString());
        }
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(File.ReadAllText(env.SettingsFile)).RootElement.GetProperty("SetupCode").ValueKind);

        // An Among Us folder that was found once and is gone now (uninstalled) counts as none: nothing is written
        // there, not even the host code saved from back then.
        string gone = Path.Combine(_dir.Path, "Old", "Among Us");
        var settings = AppSettings.Load(env.SettingsFile);
        settings.GamePath = gone;
        settings.SetupCode = Host().Encode();
        settings.Save(env.SettingsFile);
        using var later = new AppServer(env, new HttpClient(new FakeHttp()));
        using var http2 = Client(later);
        Assert.Equal("Find Among Us first.", (await Post(http2, "app/setup", new { code = Host().Encode() })).GetProperty("message").GetString());
        Assert.Equal("Find Among Us first.", (await Post(http2, "app/install")).GetProperty("message").GetString());
        Assert.True((await Post(http2, "app/modconsole", new { on = "true" })).GetProperty("ok").GetBoolean());
        var stale = await Get(http2, "app/state");
        Assert.True(stale.GetProperty("organiserPc").GetBoolean());
        Assert.Equal(JsonValueKind.Null, stale.GetProperty("setup").ValueKind);
        Assert.False(Directory.Exists(gone));
    }

    [Fact]
    public async Task An_organiser_PC_never_asks_for_the_mod_and_its_updates_and_support_bundle_work()
    {
        var env = NoGame();
        string adminCode = Admin().Encode();
        using var app = new AppServer(env, new HttpClient(new FakeHttp()));
        using var http = Client(app);
        await Post(http, "app/setup", new { code = adminCode });

        // Home's state: no game, no mod, no setup code, and nothing broken.
        var state = await Get(http, "app/state");
        Assert.True(state.GetProperty("organiserPc").GetBoolean());
        Assert.False(state.GetProperty("connected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("setup").ValueKind);
        Assert.False(state.GetProperty("mod").GetProperty("installed").GetBoolean());
        Assert.False(state.GetProperty("mod").GetProperty("updateAvailable").GetBoolean());
        Assert.Equal("", state.GetProperty("mod").GetProperty("installing").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("health").GetProperty("safeMode").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("health").GetProperty("lastDisconnect").ValueKind);

        // Updates and the mod's switches answer without a game folder.
        Assert.Equal("Everything is up to date.", (await Post(http, "app/updateall")).GetProperty("message").GetString());
        Assert.Equal("Find Among Us first.", (await Post(http, "app/install")).GetProperty("message").GetString());
        Assert.True((await Post(http, "app/modconsole", new { on = "true" })).GetProperty("ok").GetBoolean());
        Assert.False((await Post(http, "app/open", new { what = "data" })).GetProperty("ok").GetBoolean());
        Assert.False((await Post(http, "app/command", new { command = "r2" })).GetProperty("ok").GetBoolean());
        Assert.False((await Post(http, "app/feed", new { on = "true" })).GetProperty("ok").GetBoolean());
        Assert.Empty(Directory.GetDirectories(_dir.Path, "BepInEx", SearchOption.AllDirectories));

        // The support bundle: says it's an organiser's PC, has no mod files, and never the administration code.
        Directory.CreateDirectory(env.Desktop);
        var saved = await Post(http, "app/support");
        Assert.True(saved.GetProperty("ok").GetBoolean(), saved.ToString());
        using var archive = ZipFile.OpenRead(saved.GetProperty("path").GetString()!);
        string Entry(string name) => new StreamReader(archive.GetEntry(name)!.Open()).ReadToEnd();
        string summary = Entry("summary.txt");
        Assert.Contains("Setup code: none; administration code saved", summary);
        Assert.Contains("Organiser view: October", summary);
        Assert.Contains("organiser PC (no Among Us here, and none needed)", summary);
        Assert.NotNull(archive.GetEntry("mod/not-found.txt"));
        Assert.Contains("Among Us folder: not found", Entry("versions.txt"));
        foreach (var e in archive.Entries)
            Assert.DoesNotContain(adminCode, new StreamReader(e.Open()).ReadToEnd());

        // Home's page: one short line and the way to the Organiser tab instead of "Find Among Us" or "Install the mod".
        using var anon = new HttpClient { BaseAddress = new Uri(app.Url) };
        string page = await anon.GetStringAsync("/");
        Assert.Contains("const desk = !!S.organiserPc;", page);
        Assert.Contains("['Organiser PC', 'Among Us isn\\'t needed here: the live lobbies and standings are on the Organiser tab, and the results channel is on the Referee tab.', 'organiser', 'Open the Organiser tab']", page);
        Assert.Contains("const mod = S.organiserPc ? 'Organiser PC: Among Us isn\\'t needed here.'", page);
        Assert.Contains("const orgOnly = !!S.organiserPc;", page);
        Assert.Contains("if (!S.setup) { if (!orgOnly) list.push(['', 'No setup code installed']); }", page);
    }
}
