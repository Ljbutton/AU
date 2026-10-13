using System.IO.Compression;
using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.Control;
using Xunit;
using static TournamentTracker.Tests.AppHttp;
using static TournamentTracker.Tests.ButtonApp;

namespace TournamentTracker.Tests;

/// <summary>
/// The Button's side of the mod's safe mode and disconnect reasons (Home), sharing just the Among Us window
/// (the send page's link and instructions), Check my connection and the support bundle through The Button's
/// own server, and older mods without the new fields.
/// </summary>
public class ButtonHealthTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static readonly DateTime Now = new(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc);

    private static JsonElement Status(object status) => JsonDocument.Parse(JsonSerializer.Serialize(status)).RootElement;

    [Fact]
    public void Safe_mode_is_read_from_the_mods_status_and_an_older_mod_has_none()
    {
        Assert.Null(ModHealth.SafeMode(null));
        Assert.Null(ModHealth.SafeMode(Status(new { version = "0.1.62", phase = "Lobby" })));
        Assert.Null(ModHealth.LastDisconnect(Status(new { version = "0.1.62", lastDisconnect = (object?)null }), Now));

        var on = ModHealth.SafeMode(Status(new { safeMode = new { on = true, reason = "Among Us 2026.10.1 is newer than this mod", gameVersion = "2026.10.1", builtFor = "2026.9.30", overridden = false } }))!;
        Assert.True(on.On);
        Assert.Equal("Safe mode: Among Us 2026.10.1 is newer than this mod", on.Title);
        Assert.Contains("This Among Us is 2026.10.1; the mod was built for 2026.9.30.", on.Say);

        var forced = ModHealth.SafeMode(Status(new { safeMode = new { on = false, reason = (string?)null, gameVersion = "2026.10.1", builtFor = "2026.9.30", overridden = true } }))!;
        Assert.False(forced.On);
        Assert.True(forced.Overridden);
        Assert.StartsWith("Running normally", forced.Title);

        var off = ModHealth.SafeMode(Status(new { safeMode = new { on = false, overridden = false } }))!;
        Assert.Equal("", off.Title);
    }

    [Fact]
    public void A_disconnect_in_the_last_hour_is_explained_and_leaving_on_purpose_is_not_shown()
    {
        JsonElement Gone(string at, string reason, string? detail = null, bool inGame = false) => Status(new { lastDisconnect = new { at, reason, detail, inGame } });

        var timeout = ModHealth.LastDisconnect(Gone("2026-10-12T18:50:00Z", "ClientTimeout", inGame: true), Now)!;
        Assert.True(timeout.Show);
        Assert.Equal(10, timeout.MinutesAgo);
        Assert.Equal("You were disconnected: the connection to the Among Us server timed out", timeout.Title);
        Assert.Contains("wired connection", timeout.Explain);
        Assert.Contains("Referee tab", timeout.Explain);                                          // during a game: check its points

        Assert.False(ModHealth.LastDisconnect(Gone("2026-10-12T17:30:00Z", "ClientTimeout"), Now)!.Show);   // over an hour ago
        Assert.False(ModHealth.LastDisconnect(Gone("2026-10-12T18:59:00Z", "ExitGame"), Now)!.Show);        // the host left
        Assert.False(ModHealth.LastDisconnect(Gone("2026-10-12T18:59:00Z", "IntentionalLeaving"), Now)!.Show);

        var custom = ModHealth.LastDisconnect(Gone("2026-10-12T18:59:00Z", "Custom", "The server is restarting"), Now)!;
        Assert.Equal("You were disconnected: The server is restarting", custom.Title);
        var kicked = ModHealth.LastDisconnect(Gone("2026-10-12T18:59:00Z", "Kicked", "by the host"), Now)!;
        Assert.Equal("You were disconnected: the host kicked you", kicked.Title);
        Assert.StartsWith("by the host", kicked.Explain);
        var newer = ModHealth.LastDisconnect(Gone("2026-10-12T18:59:00Z", "SomethingNew"), Now)!;
        Assert.Equal("You were disconnected: Among Us didn't say why (SomethingNew)", newer.Title);
        Assert.Null(ModHealth.LastDisconnect(Gone("yesterday", "ClientTimeout"), Now));
        // Every reason Among Us has today gets plain words of its own.
        foreach (var name in new[] { "GameFull", "GameNotFound", "IncorrectVersion", "MismatchedVersion", "Banned", "Hacking", "NotAuthorized", "ServerFull", "LobbyInactivity", "InvalidGameOptions", "FocusLost", "DuplicateConnectionDetected", "TooManyRequests", "ServerNotFound", "InternalNonceFailure", "Sanctions" })
            Assert.DoesNotContain("didn't say why", ModHealth.Explain(name).What);
    }

    [Fact]
    public void The_share_link_asks_for_the_Among_Us_window_or_the_screen_and_lower_quality_caps_the_bitrate()
    {
        const string push = "https://vdo.ninja/?push=ttabc&password=pw1&screenshare&quality=0&maxframerate=60&label=LJ";
        string window = SendPage.ShareUrl(push, SendPage.ShareWindow, lowQuality: false);
        Assert.Equal(push + "&displaysurface=window&selfbrowsersurface=exclude&surfaceswitching=include&systemaudio=exclude", window);
        string screen = SendPage.ShareUrl(push, SendPage.ShareScreen, lowQuality: false);
        Assert.Contains("&displaysurface=monitor&selfbrowsersurface=exclude", screen);
        Assert.DoesNotContain("systemaudio", screen);                                               // the box to tick for game sound stays
        string low = SendPage.ShareUrl(push, SendPage.ShareWindow, lowQuality: true);
        Assert.DoesNotContain("quality=0", low);
        Assert.DoesNotContain("maxframerate=60", low);
        Assert.EndsWith("&quality=1&maxframerate=30&maxvideobitrate=2500", low);
        Assert.Contains("push=ttabc&password=pw1&screenshare", low);
        // Built again from its own result, it doesn't pile options up.
        Assert.Equal(window, SendPage.ShareUrl(window, SendPage.ShareWindow, false));

        Assert.Contains("pick Among Us", SendPage.Instructions(SendPage.ShareWindow, voiceOn: true));
        Assert.Contains("sends the game sound", SendPage.Instructions(SendPage.ShareWindow, voiceOn: true));
        Assert.Contains("A window share has no sound", SendPage.Instructions(SendPage.ShareWindow, voiceOn: false));
        Assert.Contains("tick Share system audio", SendPage.Instructions(SendPage.ShareScreen, voiceOn: false));
        Assert.Contains("leave Share system audio unticked", SendPage.Instructions(SendPage.ShareScreen, voiceOn: true));
    }

    [Fact]
    public void The_send_page_and_Home_say_to_share_the_Among_Us_window()
    {
        string send = SendPage.Html;
        Assert.Contains("open the <b>Window</b> tab and pick <b>Among Us</b>", send);
        Assert.Contains("name=\"sm\" value=\"window\"> The Among Us window (recommended)", send);
        Assert.Contains("name=\"sm\" value=\"screen\"> Entire screen", send);
        Assert.Contains("id=\"again\"", send);                                                      // Pick again
        Assert.Contains("id=\"low\"", send);                                                        // Lower quality
        Assert.Contains("const want=r.shareUrl||r.pushUrl;", send);
        Assert.Contains("quality_limitation_reason==='bandwidth'", send);
        Assert.DoesNotContain("press the share button and pick <b>Entire screen</b>. Then", send);  // the old instruction

        using var stream = typeof(AppServer).Assembly.GetManifestResourceStream("ui/index.html")!;
        string home = new StreamReader(stream).ReadToEnd();
        Assert.Contains("pick the <b>Among Us window</b> (Window tab)", home);
        Assert.DoesNotContain("choose <b>Entire screen</b>, tick <b>Share system audio</b> for the game sound, and leave the tab open.", home);
        Assert.Contains("id=\"health\"", home);
        Assert.Contains("data-safemode=\"off\"", home);
        Assert.Contains("data-cmd=\"safemode on\"", home);
        Assert.Contains("cmd('safemode off')", home);
        Assert.Contains("id=\"net-go\">Check my connection<", home);
        Assert.Contains("id=\"support-go\">Save support bundle<", home);
    }

    // ---- Through The Button's own server ----------------------------------------------------------

    private sealed class Mod : IDisposable
    {
        public string Game = "";
        public volatile string StatusJson = "{}";
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Commands = new();
        public ControlServer? Server;
        public void Dispose() => Server?.Dispose();
    }

    /// <summary>Among Us with the mod "running": a game folder, the mod installed, and its control connection answering with <paramref name="status"/>.</summary>
    private Mod StartMod(object status)
    {
        var m = new Mod { Game = Path.Combine(_dir.Path, "Steam", "steamapps", "common", "Among Us"), StatusJson = JsonSerializer.Serialize(status) };
        Directory.CreateDirectory(Path.Combine(m.Game, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(m.Game, "Among Us.exe"), "");
        File.WriteAllText(Path.Combine(m.Game, "BepInEx", "plugins", "TournamentTracker.dll"), "");
        m.Server = new ControlServer(0, ModInstaller.DataDir(m.Game), () => m.StatusJson,
            c => { m.Commands.Enqueue(c); return Task.FromResult("{\"ok\":true,\"replies\":[\"Safe mode is off for Among Us 2026.10.1.\"]}"); }, NullLog.Instance);
        return m;
    }

    private readonly List<string> _opened = new();

    private AppEnvironment Env(FakeNet? net = null) => new()
    {
        SettingsFile = Path.Combine(_dir.Path, "app", "app.json"), SteamRoot = Path.Combine(_dir.Path, "Steam"),
        EpicManifests = Path.Combine(_dir.Path, "none"), Fallbacks = Array.Empty<string>(),
        Downloads = Path.Combine(_dir.Path, "Downloads"), Desktop = Path.Combine(_dir.Path, "Desktop"), Version = "0.1.63",
        Open = target => { lock (_opened) _opened.Add(target); },
        CheckOptions = new ConnectionCheck.Options
        {
            UploadUrl = "https://up.example/__up", DiscordUrl = "https://discord.example/api/v10/gateway", VdoUrl = "https://vdo.example/",
            Duration = TimeSpan.FromSeconds(1.5), Warmup = TimeSpan.FromSeconds(0.3), Sample = TimeSpan.FromMilliseconds(100), RequestBytes = 256 * 1024, Pings = 2,
        },
    };

    private static object ModStatus(bool safe) => new
    {
        version = "0.1.63", phase = "Lobby", lobby = "LJ",
        safeMode = new { on = safe, reason = safe ? "Among Us updated" : null, gameVersion = "2026.10.1", builtFor = "2026.9.30", overridden = !safe },
        lastDisconnect = new { at = DateTime.UtcNow.AddMinutes(-5).ToString("o"), reason = "ClientTimeout", detail = (string?)null, inGame = false },
        hostAuthority = new { on = true, active = true },
        feed = new { available = true, on = true, pushUrl = "https://vdo.ninja/?push=ttabc&password=pw1&screenshare&quality=0&maxframerate=60&label=LJ", cam = false },
    };

    [Fact]
    public async Task Home_gets_safe_mode_and_the_disconnect_and_the_buttons_reach_the_mod()
    {
        using var mod = StartMod(ModStatus(safe: true));
        using var app = new AppServer(Env(), new HttpClient(new FakeNet()));
        using var http = Client(app);
        var state = await Get(http, "app/state");
        var health = state.GetProperty("health");
        Assert.True(health.GetProperty("safeMode").GetProperty("on").GetBoolean());
        Assert.Equal("Safe mode: Among Us updated", health.GetProperty("safeMode").GetProperty("title").GetString());
        Assert.True(health.GetProperty("lastDisconnect").GetProperty("show").GetBoolean());
        Assert.StartsWith("You were disconnected: the connection", health.GetProperty("lastDisconnect").GetProperty("title").GetString());
        // Run normally anyway sends "safemode off"; Back to automatic "safemode on".
        Assert.True((await Post(http, "app/command", new { command = "safemode off" })).GetProperty("ok").GetBoolean());
        await Post(http, "app/command", new { command = "safemode on" });
        Assert.Equal(new[] { "safemode off", "safemode on" }, mod.Commands.ToArray());

        // An older mod: no safe mode, no disconnect, nothing breaks.
        mod.StatusJson = "{\"version\":\"0.1.62\",\"phase\":\"Lobby\"}";
        state = await Get(http, "app/state");
        Assert.Equal(JsonValueKind.Null, state.GetProperty("health").GetProperty("safeMode").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("health").GetProperty("lastDisconnect").ValueKind);
        Assert.True(state.GetProperty("connected").GetBoolean());
        // The disconnect and safe mode went in The Button's log for the support bundle.
        string log = File.ReadAllText(Path.Combine(_dir.Path, "app", "the-button.log"));
        Assert.Contains("The mod reports a disconnect at", log);
        Assert.Contains("The mod is in safe mode: Among Us updated", log);
    }

    [Fact]
    public async Task The_send_page_gets_the_window_share_link_and_the_choice_is_kept()
    {
        using var mod = StartMod(ModStatus(safe: false));
        using (var app = new AppServer(Env(), new HttpClient(new FakeNet())))
        {
            using var http = Client(app);
            var info = await Get(http, "app/sendinfo?page=p1");
            Assert.Equal("https://vdo.ninja/?push=ttabc&password=pw1&screenshare&quality=0&maxframerate=60&label=LJ", info.GetProperty("pushUrl").GetString());
            Assert.EndsWith("&displaysurface=window&selfbrowsersurface=exclude&surfaceswitching=include&systemaudio=exclude", info.GetProperty("shareUrl").GetString());
            Assert.Equal("window", info.GetProperty("share").GetProperty("mode").GetString());
            Assert.Equal("window", (await Get(http, "app/state")).GetProperty("share").GetProperty("mode").GetString());

            var r = await Post(http, "app/share?page=p1", new { mode = "screen", low = "true" });
            Assert.True(r.GetProperty("ok").GetBoolean());
            Assert.Contains("Sharing the entire screen. Lower quality", r.GetProperty("message").GetString());
            // An older tab (replaced by a newer one) can't change it.
            Assert.Equal(System.Net.HttpStatusCode.Conflict, (await http.PostAsync("app/share?page=old", new StringContent("{\"mode\":\"window\"}"))).StatusCode);
        }
        using var again = new AppServer(Env(), new HttpClient(new FakeNet()));
        using var http2 = Client(again);
        var shareUrl = (await Get(http2, "app/sendinfo")).GetProperty("shareUrl").GetString()!;
        Assert.Contains("displaysurface=monitor", shareUrl);
        Assert.Contains("maxvideobitrate=2500", shareUrl);
        Assert.DoesNotContain("quality=0", shareUrl);
        var share = (await Get(http2, "app/state")).GetProperty("share");
        Assert.True(share.GetProperty("low").GetBoolean());
        Assert.Contains("Entire screen", share.GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task Check_my_connection_runs_in_the_background_and_its_result_shows_and_goes_in_the_bundle()
    {
        using var mod = StartMod(ModStatus(safe: true));
        var net = new FakeNet { BytesPerSecond = 2_000_000 };                           // 16 Mbps
        using var app = new AppServer(Env(net), new HttpClient(net));
        using var http = Client(app);

        var started = await Post(http, "app/conncheck", new { action = "start" });
        Assert.True(started.GetProperty("ok").GetBoolean());
        Assert.False((await Post(http, "app/conncheck", new { action = "start" })).GetProperty("ok").GetBoolean());   // one at a time
        Assert.True((await Get(http, "app/state")).GetProperty("connCheck").GetProperty("running").GetBoolean());
        JsonElement check = default;
        for (int i = 0; i < 100; i++)
        {
            check = (await Get(http, "app/state")).GetProperty("connCheck");
            if (!check.GetProperty("running").GetBoolean()) break;
            await Task.Delay(100);
        }
        var result = check.GetProperty("result");
        Assert.Equal("good", result.GetProperty("level").GetString());
        Assert.InRange(result.GetProperty("upload").GetDouble(), 10, 20);
        Assert.Contains(result.GetProperty("facts").EnumerateArray(), f => f.GetString()!.StartsWith("Discord: "));
        Assert.Contains("10 Mbps upload is recommended", check.GetProperty("needs").GetString());

        // Started and stopped: says so, and keeps the last result.
        await Post(http, "app/conncheck", new { action = "start" });
        Assert.True((await Post(http, "app/conncheck", new { action = "cancel" })).GetProperty("ok").GetBoolean());
        for (int i = 0; i < 50 && (await Get(http, "app/state")).GetProperty("connCheck").GetProperty("running").GetBoolean(); i++) await Task.Delay(100);
        check = (await Get(http, "app/state")).GetProperty("connCheck");
        Assert.Equal("Check stopped.", check.GetProperty("error").GetString());
        Assert.Equal("good", check.GetProperty("result").GetProperty("level").GetString());

        // Save support bundle: a zip on the Desktop, its folder opened, the check and the mod's health in the summary.
        Directory.CreateDirectory(Path.Combine(_dir.Path, "Desktop"));
        var saved = await Post(http, "app/support");
        Assert.True(saved.GetProperty("ok").GetBoolean(), saved.ToString());
        string zip = saved.GetProperty("path").GetString()!;
        Assert.StartsWith(Path.Combine(_dir.Path, "Desktop", "TournamentTracker-support-"), zip);
        Assert.Contains("your Desktop", saved.GetProperty("message").GetString());
        lock (_opened) Assert.Contains(Path.Combine(_dir.Path, "Desktop"), _opened);
        using var archive = ZipFile.OpenRead(zip);
        string summary = new StreamReader(archive.GetEntry("summary.txt")!.Open()).ReadToEnd();
        Assert.Contains("Safe mode: ON.", summary);
        Assert.Contains("Last disconnect: ", summary);
        Assert.Contains("Connection check (", summary);
        Assert.Contains("Host authority: on, active in this lobby.", summary);
        Assert.Contains("password=[removed]", summary);
        Assert.DoesNotContain("pw1", summary);
        string settings = new StreamReader(archive.GetEntry("the-button/settings.json")!.Open()).ReadToEnd();
        Assert.Contains("\"AppToken\": \"[removed]\"", settings);
        Assert.DoesNotContain(app.Token, settings);
        Assert.NotNull(archive.GetEntry("README.txt"));
    }
}
