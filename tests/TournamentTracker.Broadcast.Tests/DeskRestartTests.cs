using System.IO.Compression;
using System.Net;
using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.App.Broadcast;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// Red Alert across a restart: Hold and the desk's automation kept in settings.json, and the lobbies kept
/// for the next start (back at once, marked cached; never in a support bundle).
/// </summary>
public class DeskRestartTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string App => Path.Combine(_dir.Path, "RedAlert");
    private string SettingsFile => Path.Combine(App, "settings.json");

    private static SetupCode Admin() => new() { Mode = "admin", TournamentId = "c", TournamentName = "Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results" };

    private BroadcastServer Server(FakeHttp? http = null, string? obsFolder = null) =>
        new(new BroadcastEnvironment { SettingsFile = SettingsFile, Port = 0, CasterPort = 0, Version = "0.3.18", SupportFolder = Path.Combine(_dir.Path, "Desktop"), ObsFolder = obsFolder ?? Path.Combine(_dir.Path, "no-obs") },
            new HttpClient(http ?? new FakeHttp()) { Timeout = TimeSpan.FromSeconds(20) });

    /// <summary>A request as the desk page sends it (straight to the server's router: no sockets).</summary>
    private static async Task<JsonElement> Call(BroadcastServer server, string method, string path, object? body = null)
    {
        var headers = new Dictionary<string, string> { ["x-app-token"] = server.Token };
        var (_, _, bytes) = await server.Route(method, path, headers, body == null ? "" : JsonSerializer.Serialize(body)).WaitAsync(TimeSpan.FromSeconds(20));
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    private static Task<JsonElement> Post(BroadcastServer server, string path, object body) => Call(server, "POST", path, body);

    // ---- Hold and every automation toggle --------------------------------------------------------

    [Fact]
    public async Task Hold_and_every_automation_toggle_are_back_after_a_restart()
    {
        DateTime countdown;
        using (var server = Server())
        {
            Assert.True((await Post(server, "/app/admin/code", new { code = Admin().Encode() })).GetProperty("ok").GetBoolean());
            await Post(server, "/app/admin/autogrid", new { on = "true" });
            // Auto intermission was removed: an old desk page's toggle is told so, and nothing is kept.
            var removed = await Post(server, "/app/admin/intermission", new { action = "auto", on = "true" });
            Assert.False(removed.GetProperty("ok").GetBoolean());
            Assert.Equal("Automatic switching was removed: people switch the stream.", removed.GetProperty("message").GetString());
            await Post(server, "/app/admin/intermission", new { action = "next", value = "30" });
            await Post(server, "/app/admin/wins", new { scope = "round" });
            await Post(server, "/app/admin/standings", new { scope = "overall" });
            await Post(server, "/app/admin/health", new { what = "autoSwitch", on = "false" });
            await Post(server, "/app/admin/health", new { what = "slate", on = "false" });
            await Post(server, "/app/admin/graphics", new { action = "afterGameAuto", on = "true" });
            await Post(server, "/app/admin/graphics", new { action = "hold", value = "12" });
            await Post(server, "/app/admin/broadcast", new { action = "alerts", on = "false" });
            Assert.True((await Post(server, "/app/admin/do", new { path = "hold/on" })).GetProperty("ok").GetBoolean());
            Assert.True(server.Desk!.Held);
            countdown = server.Desk.NextRoundAt!.Value;
        }

        using var again = Server();
        var desk = again.Desk!;
        Assert.True(desk.AutoGrid);
        Assert.Equal(countdown, desk.NextRoundAt);
        Assert.Equal("round", desk.WinScope);
        Assert.Equal("overall", desk.StandingsScope);
        Assert.True(desk.Held);
        Assert.Equal("Held from the Stream Deck", desk.HoldWhy);
        Assert.False(desk.HealthConfig.AutoSwitch);
        Assert.False(desk.HealthConfig.Slate);
        Assert.True(desk.Alerts.Settings.Paused);
        var full = await Call(again, "GET", "/app/admin/desk");
        Assert.True(full.GetProperty("broadcast").GetProperty("afterGameAuto").GetBoolean());
        Assert.Equal(12, full.GetProperty("broadcast").GetProperty("hold").GetDouble());

        // Released and turned off again: kept that way too.
        await Post(again, "/app/admin/do", new { path = "hold/off" });
        await Post(again, "/app/admin/autogrid", new { on = "false" });
        await Post(again, "/app/admin/intermission", new { action = "next", value = "" });
        again.Dispose();
        using var third = Server();
        Assert.False(third.Desk!.Held);
        Assert.False(third.Desk.AutoGrid);
        Assert.Null(third.Desk.NextRoundAt);
    }

    [Fact]
    public void A_settings_file_from_before_still_reads_and_keeps_its_names()
    {
        Directory.CreateDirectory(App);
        File.WriteAllText(SettingsFile, "{\"AdminCode\":null,\"MuteHotkey\":\"Ctrl+Shift+F9\",\"DeckKey\":\"abcdef123456\",\"CamOff\":true}");
        var old = BroadcastAppSettings.Load(SettingsFile);
        Assert.Equal(("Ctrl+Shift+F9", "abcdef123456", true), (old.MuteHotkey, old.DeckKey, old.CamOff));
        Assert.Null(old.Desk);                                            // never saved: the desk's defaults

        old.Desk = new DeskAutomation { AutoGrid = true, Hold = true, HoldWhy = "Panic" };
        old.Save(SettingsFile);
        var e = JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement;
        foreach (var name in new[] { "AdminCode", "MuteHotkey", "DeckKey", "CamOff", "Desk" }) Assert.True(e.TryGetProperty(name, out _), name);
        Assert.Equal(new DeskAutomation { AutoGrid = true, Hold = true, HoldWhy = "Panic" }, BroadcastAppSettings.Load(SettingsFile).Desk);
    }

    [Fact]
    public void An_older_settings_file_with_auto_intermission_on_still_reads_and_it_stays_off()
    {
        Directory.CreateDirectory(App);
        File.WriteAllText(SettingsFile, "{\"DeckKey\":\"abcdef1234567890\",\"Desk\":{\"AutoGrid\":true,\"AutoIntermission\":true}}");
        var old = BroadcastAppSettings.Load(SettingsFile);
        Assert.True(old.Desk!.AutoGrid);
        var clock = new FakeClock();
        using var desk = new CasterDesk(null, () => clock.Now, new PriorityConfig());
        old.Desk.ApplyTo(desk, clock.Now);
        Assert.True(desk.AutoGrid);
        Assert.False(DeskAutomation.From(desk).AutoIntermission);         // written as off from now on
        // Every lobby quiet for minutes: the offer only, nothing switches.
        desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = "LJ", phase = "lobby", t = new DateTimeOffset(clock.Now).ToUnixTimeMilliseconds() }));
        desk.Show("LJ");
        for (int i = 0; i < 120; i++) { clock.Advance(1); if (i % 5 == 0) desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = "LJ", phase = "lobby", t = new DateTimeOffset(clock.Now).ToUnixTimeMilliseconds() })); desk.Tick(); }
        Assert.Equal(("full", "LJ"), (desk.OnAir.Layout, desk.OnAir.Slots[0]));
        Assert.True(desk.IntermissionOffer);
    }

    [Fact]
    public void Starting_soon_keeps_its_countdown_and_line_across_a_restart()
    {
        var clock = new FakeClock();
        using var before = new CasterDesk(null, () => clock.Now, new PriorityConfig());
        before.SetSoonAt(clock.Now.AddMinutes(10));
        before.SetSoonText("Grand final tonight");
        var kept = DeskAutomation.From(before);
        using var after = new CasterDesk(null, () => clock.Now, new PriorityConfig());
        kept.ApplyTo(after, clock.Now);
        Assert.Equal((clock.Now.AddMinutes(10), "Grand final tonight"), (after.SoonAt!.Value, after.SoonText));
        // A start time that went by while Red Alert was closed isn't brought back (the line is).
        using var late = new CasterDesk(null, () => clock.Now, new PriorityConfig());
        kept.ApplyTo(late, clock.Now.AddMinutes(11));
        Assert.Equal(((DateTime?)null, "Grand final tonight"), (late.SoonAt, late.SoonText));
    }

    [Fact]
    public void A_countdown_or_timed_hold_that_ran_out_while_Red_Alert_was_closed_isnt_brought_back()
    {
        var clock = new FakeClock();
        using var desk = new CasterDesk(null, () => clock.Now, new PriorityConfig());
        new DeskAutomation { NextRoundAt = clock.Now.AddMinutes(-1), HoldUntil = clock.Now.AddMinutes(-1), HoldWhy = "Held for 5 minutes", WinScope = "nonsense" }.ApplyTo(desk, clock.Now);
        Assert.Null(desk.NextRoundAt);
        Assert.False(desk.Held);
        Assert.Equal("today", desk.WinScope);

        new DeskAutomation { NextRoundAt = clock.Now.AddMinutes(10), HoldUntil = clock.Now.AddMinutes(3), HoldWhy = "Held for 5 minutes" }.ApplyTo(desk, clock.Now);
        Assert.Equal(clock.Now.AddMinutes(10), desk.NextRoundAt);
        Assert.True(desk.Held);
        Assert.Equal((false, (DateTime?)clock.Now.AddMinutes(3), "Held for 5 minutes"), desk.HoldNow());
        clock.Advance(181);
        Assert.False(desk.Held);                                          // until the same time as before
    }

    // ---- The lobbies kept for a restart, and the support bundle ----------------------------------

    private const string VdoPassword = "vdopw0987654321";             // as the mod makes them: lower case and digits

    private static HttpResponseMessage Channel(HttpRequestMessage r)
    {
        string path = r.RequestUri!.AbsolutePath;
        if (!path.EndsWith("/channels/results/messages")) return FakeHttp.Json(HttpStatusCode.OK, "{}");
        var live = new
        {
            id = "42", content = "", author = new { id = "host-bot", bot = true }, attachments = Array.Empty<object>(),
            embeds = new[] { new { title = TournamentSession.LiveTitlePrefix + "LJ", description = "```json\n" + JsonSerializer.Serialize(new { v = 1, lobby = "LJ", phase = "Tasks", t = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), vdo = "ttabc:" + VdoPassword, p = Array.Empty<object>(), f = Array.Empty<object>(), st = Array.Empty<object>() }) + "\n```" } },
        };
        return FakeHttp.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new[] { live }));
    }

    [Fact]
    public async Task The_kept_lobbies_are_never_in_a_support_bundle_and_their_passwords_are_taken_out_everywhere()
    {
        string cache = Path.Combine(App, Organizer.CacheFileName);
        using (var server = Server(new FakeHttp { Default = Channel }))
        {
            await Post(server, "/app/admin/code", new { code = Admin().Encode() });
            await Wait.Until(() => File.Exists(cache), 10000);
        }
        Assert.Contains(VdoPassword, File.ReadAllText(cache));
        // The password where neither its link nor its name gives it away.
        File.AppendAllText(Path.Combine(App, "red-alert.log"), $"2026-10-20 19:00:00 INFO lobby LJ sends ttabc:{VdoPassword}\n");

        // Restarted, but the channel can't be read yet: LJ is there, cached, with its link.
        using var again = Server();
        var org = await Call(again, "GET", "/app/admin");
        var lj = org.GetProperty("lobbies").EnumerateArray().Single();
        Assert.True(lj.GetProperty("cached").GetBoolean());
        // The desk page's data link for it is back too (before any data or the channel).
        var deskNow = await Call(again, "GET", "/app/admin/desk");
        Assert.Contains(deskNow.GetProperty("receivers").EnumerateArray(), r => r.GetProperty("lobby").GetString() == "LJ");
        // … and the desk's tiles can say it's cached.
        Assert.Equal(new[] { "LJ" }, deskNow.GetProperty("cached").EnumerateArray().Select(x => x.GetString()).ToArray());

        // The bundle: no kept-lobbies file, and the password nowhere.
        var r = await Post(again, "/app/support", new { });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.GetProperty("message").GetString());
        using (var zip = ZipFile.OpenRead(Path.Combine(_dir.Path, "Desktop", r.GetProperty("file").GetString()!)))
        {
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("lobbies-cache"));
            foreach (var e in zip.Entries)
            {
                using var reader = new StreamReader(e.Open());
                Assert.False(reader.ReadToEnd().Contains(VdoPassword), e.FullName + " has the VDO.Ninja password");
            }
        }
        Assert.Contains(Organizer.CacheFileName, RedAlertSupport.NeverIncluded);
        // Even planted next to the settings, it isn't picked up.
        Assert.DoesNotContain(RedAlertSupport.FromFolders(App, null).Entries, e => e.Name.Contains("lobbies-cache"));

        // Locking forgets them.
        await Post(again, "/app/admin/code", new { code = "" });
        Assert.False(File.Exists(cache));
    }
}
