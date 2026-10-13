using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TournamentTracker.App.Broadcast;
using TournamentTracker.App;
using TournamentTracker.Setup;
using Xunit;
using static TournamentTracker.Tests.AppHttp;

namespace TournamentTracker.Tests;

/// <summary>
/// Red Alert's support bundle (Settings → Save support bundle): which files go in (Red Alert's log and
/// settings, OBS's newest log and crash report, never the Twitch sign-in), logs read while another
/// program writes them and cut to their last 5 MB, and nothing secret in the zip.
/// </summary>
public class SupportBundleTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    // Made-up secrets, shaped like the real thing.
    private static readonly string BotToken = string.Join(".", "MTA5ODc2NTQzMjEwOTg3NjU0", "GaBcDe", "abcdefghijklmnopqrstuvwxyz01234");   // built at run time so no token-shaped text sits in the source (secret scanning)
    private static readonly string Webhook = "https://discord.com" + "/api/" + "webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz-ABCDEFG";
    private const string DeckKey = "Zx9_deckKEY-0123456789";
    private const string ObsPassword = "obs-pass-4455-secret";
    private const string TwitchAccess = "twitchaccess0123456789abcdef";
    private const string TwitchRefresh = "twitchrefresh0123456789abcdef";
    private const string StreamKey = "live_123456789_AbCdEfGhIjKlMnOpQrStUv";
    private const string VdoPassword = "vdoPw0987654321";

    private static string AdminCode() => new SetupCode { Mode = "admin", TournamentId = "c", TournamentName = "Cup", BotTokens = new() { BotToken }, Webhook = Webhook, ResultsChannelId = "results" }.Encode();

    private string App => Path.Combine(_dir.Path, "RedAlert");
    private string Obs => Path.Combine(_dir.Path, "obs-studio");

    private static void Write(string path, string text, DateTime? at = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (at is { } t) File.SetLastWriteTimeUtc(path, t);
    }

    /// <summary>Red Alert's folder and OBS's, as on a caster's PC, with secrets planted where they live (and a few where they shouldn't).</summary>
    private void Plant()
    {
        new BroadcastAppSettings { AdminCode = AdminCode(), DeckKey = DeckKey, MuteHotkey = "Ctrl+Shift+M" }.Save(Path.Combine(App, "settings.json"));
        new ObsSettings { Host = "127.0.0.1", Port = 4455, Password = ObsPassword }.Save(Path.Combine(App, ObsSettings.FileName));
        Write(Path.Combine(App, PriorityConfig.FileName), "{ \"halfLifeSeconds\": 6 }");
        Write(Path.Combine(App, BroadcastSettings.FileName), new BroadcastSettings().ToJson());
        Write(Path.Combine(App, CasterDesk.NumbersFile), "{}");
        Write(Path.Combine(App, HealthSettings.FileName), "{ \"dataRedSeconds\": 10 }");
        Write(Path.Combine(App, TwitchSettings.FileName), "{ \"clientId\": \"public-client\" }");
        new TwitchToken { AccessToken = TwitchAccess, RefreshToken = TwitchRefresh, Login = "caster" }.Save(Path.Combine(App, "twitch-token.json"));
        Write(Path.Combine(App, "roster.csv"), "name,discord\nSoggy,123");
        Write(Path.Combine(App, "red-alert.log"),
            "2026-10-12 19:00:00 INFO Red Alert 0.3.15 started on port 8768.\n" +
            $"2026-10-12 19:01:00 WARN Stream Deck press deck/lobby/1?key={DeckKey} refused\n" +
            $"2026-10-12 19:02:00 ERROR OBS refused the password {ObsPassword}\n" +
            $"2026-10-12 19:03:00 INFO view https://vdo.ninja/?view=abc&password={VdoPassword}&noaudio\n");
        Write(Path.Combine(App, "red-alert.log.1"), "2026-10-12 18:00:00 WARN earlier problem\n");

        var old = DateTime.UtcNow.AddDays(-2);
        Write(Path.Combine(Obs, "logs", "2026-10-10 20-00-00.txt"), "old session", old);
        Write(Path.Combine(Obs, "logs", "2026-10-12 19-00-00.txt"), $"19:00:00.000: OBS 31.0.0\n19:00:01.000: stream key {StreamKey} (shouldn't be here)\n", DateTime.UtcNow);
        Write(Path.Combine(Obs, "crashes", "Crash 2026-10-01 10-00-00.txt"), "older crash", old.AddDays(-1));
        Write(Path.Combine(Obs, "crashes", "Crash 2026-10-05 10-00-00.txt"), "newer crash", old);
        Write(Path.Combine(Obs, "basic", "profiles", "Tournament", "service.json"), $"{{ \"type\": \"rtmp_common\", \"settings\": {{ \"service\": \"Twitch\", \"key\": \"{StreamKey}\" }} }}");
    }

    private static readonly string[] Secrets = { BotToken, Webhook, DeckKey, ObsPassword, TwitchAccess, TwitchRefresh, StreamKey };

    [Fact]
    public void It_takes_Red_Alerts_log_and_settings_and_OBSs_newest_log_and_crash_report_and_never_the_Twitch_sign_in()
    {
        Plant();
        var bundle = RedAlertSupport.FromFolders(App, Obs);
        var names = bundle.Entries.Select(e => e.Name).ToList();
        foreach (var want in new[] { "red-alert.log", "red-alert.log.1", "settings.json", "obs.json", "caster-priority.json", "broadcast.json", "lobby-numbers.json", "health.json", "twitch.json",
                     "obs/logs/2026-10-12 19-00-00.txt", "obs/crashes/Crash 2026-10-05 10-00-00.txt" })
            Assert.Contains(want, names);
        // The newest only, and nothing that isn't needed (the sign-in, the players' roster).
        Assert.DoesNotContain("obs/logs/2026-10-10 20-00-00.txt", names);
        Assert.DoesNotContain("obs/crashes/Crash 2026-10-01 10-00-00.txt", names);
        Assert.DoesNotContain(names, n => n.Contains("twitch-token") || n.Contains("roster") || n.Contains("service.json"));
        Assert.All(bundle.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.About), e.Name));

        // Taken out of their own settings by name: the administration code and the Stream Deck key; the
        // OBS password. What isn't secret stays.
        string settings = bundle.Entries.Single(e => e.Name == "settings.json").Text;
        var s = JsonDocument.Parse(settings).RootElement;
        Assert.Equal(RedAlertSupport.Removed, s.GetProperty("AdminCode").GetString());
        Assert.Equal(RedAlertSupport.Removed, s.GetProperty("DeckKey").GetString());
        Assert.Equal("Ctrl+Shift+M", s.GetProperty("MuteHotkey").GetString());
        var obs = JsonDocument.Parse(bundle.Entries.Single(e => e.Name == "obs.json").Text).RootElement;
        Assert.Equal(RedAlertSupport.Removed, obs.GetProperty("password").GetString());
        Assert.Equal(4455, obs.GetProperty("port").GetInt32());

        // And every known secret, wherever it turns up (a log line, OBS's log), before the shared redactor even runs.
        bundle.AddSecretsFromLink($"https://vdo.ninja/?view=abc&password={VdoPassword}");
        foreach (var (name, text, _) in bundle.Entries)
            foreach (var secret in Secrets.Append(VdoPassword))
                Assert.DoesNotContain(secret, bundle.Mask(text));
        Assert.Contains("refused", bundle.Mask(bundle.Entries.Single(e => e.Name == "red-alert.log").Text));

        string readme = bundle.Readme("0.3.15", new DateTime(2026, 10, 12, 19, 30, 0));
        Assert.Contains("Red Alert 0.3.15", readme);
        Assert.Contains("obs/logs/2026-10-12 19-00-00.txt", readme);
        Assert.Contains("twitch-token.json", readme);       // said to be left out
    }

    [Fact]
    public void Nothing_there_is_listed_in_the_readme_not_an_error()
    {
        var bundle = RedAlertSupport.FromFolders(Path.Combine(_dir.Path, "nope"), Path.Combine(_dir.Path, "no-obs"));
        Assert.Empty(bundle.Entries);
        Assert.NotEmpty(bundle.Notes);
        Directory.CreateDirectory(App);
        Write(Path.Combine(App, "settings.json"), "{ \"AdminCode\": \"TT1-abc\", broken");
        var half = RedAlertSupport.FromFolders(App, null);
        Assert.DoesNotContain(half.Entries, e => e.Name == "settings.json");         // not JSON: left out, not risked
        Assert.Contains(half.Notes, n => n.StartsWith("settings.json isn't valid JSON"));
        Assert.Contains(half.Notes, n => n.StartsWith("red-alert.log: not there"));
        Assert.Contains("Left out", half.Readme("0.3.15", DateTime.Now));
    }

    [Fact]
    public void A_log_another_program_is_writing_is_read_and_a_big_one_keeps_its_last_5_MB()
    {
        string path = Path.Combine(_dir.Path, "obs.txt");
        var line = new string('x', 99) + "\n";
        using (var w = new StreamWriter(path))
            for (int i = 0; i < 60_000; i++) w.Write(line);          // 6 MB
        File.AppendAllText(path, "the last line\n");
        // OBS keeps its log open for writing all session.
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        string text = RedAlertSupport.ReadTail(path);
        Assert.StartsWith("[Only the end of this file is here", text);
        Assert.EndsWith("the last line\n", text);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= RedAlertSupport.MaxBytes + 200);
        Assert.Equal(line, text.Split('\n')[1] + "\n");                                 // whole lines only
        Assert.Equal("small\n", ReadSmall());

        string ReadSmall()
        {
            string p = Path.Combine(_dir.Path, "small.txt");
            File.WriteAllText(p, "small\n");
            return RedAlertSupport.ReadTail(p);
        }
    }

    [Fact]
    public void Scrubbing_JSON_takes_the_named_values_out_anywhere_and_keeps_empty_ones_empty()
    {
        string json = "{ \"Password\": \"p4ssw0rd!\", \"nested\": { \"password\": \"two\", \"other\": 1 }, \"list\": [ { \"PASSWORD\": \"three\" } ], \"empty\": { \"password\": \"\" } }";
        string clean = RedAlertSupport.ScrubJson(json, "password")!;
        Assert.DoesNotContain("p4ssw0rd!", clean);
        Assert.DoesNotContain("\"two\"", clean);
        Assert.DoesNotContain("three", clean);
        var e = JsonDocument.Parse(clean).RootElement;
        Assert.Equal("", e.GetProperty("empty").GetProperty("password").GetString());
        Assert.Equal(1, e.GetProperty("nested").GetProperty("other").GetInt32());
        Assert.Null(RedAlertSupport.ScrubJson("not json", "password"));
    }

    [Fact]
    public void The_newest_file_is_picked_by_when_it_was_written()
    {
        string folder = Path.Combine(_dir.Path, "logs");
        Write(Path.Combine(folder, "b.txt"), "b", DateTime.UtcNow.AddHours(-1));
        Write(Path.Combine(folder, "a.txt"), "a", DateTime.UtcNow);
        Write(Path.Combine(folder, "c.log"), "c", DateTime.UtcNow.AddHours(1));
        Assert.Equal(Path.Combine(folder, "a.txt"), RedAlertSupport.Newest(folder, "*.txt"));
        Assert.Null(RedAlertSupport.Newest(Path.Combine(_dir.Path, "missing"), "*.txt"));
        var at = new DateTime(2026, 10, 12, 19, 30, 0);
        string first = RedAlertSupport.FreePath(_dir.Path, at);
        Assert.Equal("RedAlert-support-2026-10-12-1930.zip", Path.GetFileName(first));
        File.WriteAllText(first, "");
        Assert.Equal("RedAlert-support-2026-10-12-1930-2.zip", Path.GetFileName(RedAlertSupport.FreePath(_dir.Path, at)));
    }

    [Fact]
    public void The_shared_redactor_takes_out_what_Red_Alerts_files_can_hold()
    {
        string text = string.Join("\n",
            $"bot {BotToken}",
            $"webhook {Webhook}",
            $"code {AdminCode()}",
            $"deck http://127.0.0.1:8768/deck/lobby/1?key={DeckKey}",
            $"view https://vdo.ninja/?view=abc&password={VdoPassword}&noaudio",
            $"{{ \"password\": \"{ObsPassword}\", \"deckKey\": \"{DeckKey}\", \"accessToken\": \"{TwitchAccess}\" }}");
        string clean = SupportBundle.Redact(text);
        foreach (var secret in new[] { BotToken, Webhook, AdminCode(), DeckKey, VdoPassword, ObsPassword, TwitchAccess })
            Assert.DoesNotContain(secret, clean);
    }

    [Fact]
    public async Task Save_support_bundle_writes_one_zip_with_a_readme_and_nothing_secret_even_while_locked()
    {
        Plant();
        string desktop = Path.Combine(_dir.Path, "Desktop");
        var opened = new List<string>();
        var env = new BroadcastEnvironment { SettingsFile = Path.Combine(App, "settings.json"), Port = 0, CasterPort = 0, Version = "0.3.15", SupportFolder = desktop, ObsFolder = Obs, Open = opened.Add };
        using var server = new BroadcastServer(env, new HttpClient(new FakeHttp()));
        using var http = AppHttp.Client(server.Url, server.Token);
        Assert.NotNull(server.Desk);                                                       // unlocked with the planted code
        await Post(http, "app/admin/sim", new { on = "true" });
        await Wait.Until(() => server.Desk!.Board.Ranking().Count > 0, 10000);

        var r = await Post(http, "app/support", new { warnings = new[] { "OBS not connected: Red Alert lost OBS" }, window = "Mozilla/5.0 Edg/130.0" });
        Assert.True(r.GetProperty("ok").GetBoolean(), r.GetProperty("message").GetString());
        string zip = Path.Combine(desktop, r.GetProperty("file").GetString()!);
        Assert.StartsWith("RedAlert-support-", Path.GetFileName(zip));
        Assert.Contains(desktop, opened);                                                  // the folder opens, to find it

        var files = Read(zip);
        foreach (var want in new[] { "README.txt", "red-alert.log", "settings.json", "obs.json", "lobby-numbers.json", "versions.txt", "summary.txt", "state.json", "stream-deck.txt", "obs/logs/2026-10-12 19-00-00.txt" })
            Assert.True(files.ContainsKey(want), want + " missing: " + string.Join(", ", files.Keys));
        foreach (var (name, text) in files)
            foreach (var secret in Secrets.Append(AdminCode()))
                Assert.False(text.Contains(secret), $"{name} still has a secret");
        Assert.Contains("OBS not connected: Red Alert lost OBS", files["summary.txt"]);
        Assert.Contains("== Lobby health ==", files["summary.txt"]);
        Assert.Contains("ERROR OBS refused the password", files["summary.txt"]);            // the log's latest warnings and errors
        Assert.Contains("Red Alert 0.3.15", files["versions.txt"]);
        Assert.Contains("Edg/130.0", files["versions.txt"]);
        Assert.Contains("Stream Deck key: set (left out of this bundle)", files["stream-deck.txt"]);
        Assert.Contains("summary.txt", files["README.txt"]);
        // Nothing that gives the games away: no cards, nobody's role.
        var desk = JsonDocument.Parse(files["state.json"]).RootElement.GetProperty("desk");
        Assert.False(desk.TryGetProperty("cards", out _));
        Assert.False(desk.TryGetProperty("history", out _));
        Assert.NotEmpty(desk.GetProperty("lobbies").EnumerateArray());
        Assert.All(desk.GetProperty("lobbies").EnumerateArray(), l => Assert.False(l.TryGetProperty("players", out _)));
        Assert.Contains("SIM-1", files["summary.txt"]);

        // Locked (no administration code yet): still a bundle, with the log and settings.
        await Post(http, "app/admin/code", new { code = "" });
        var locked = await Post(http, "app/support", new { });
        Assert.True(locked.GetProperty("ok").GetBoolean(), locked.GetProperty("message").GetString());
        var again = Read(Path.Combine(desktop, locked.GetProperty("file").GetString()!));
        Assert.Contains("LOCKED", again["summary.txt"]);
        Assert.True(again.ContainsKey("red-alert.log"));
    }

    private static Dictionary<string, string> Read(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.ToDictionary(e => e.FullName, e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); });
    }

    [Fact]
    public void Settings_has_the_dry_run_checklist_and_the_support_bundle()
    {
        using var s = typeof(BroadcastServer).Assembly.GetManifestResourceStream("ui/caster.html")!;
        string page = new StreamReader(s).ReadToEnd();
        Assert.Contains("href=\"https://github.com/Ljbutton/AU/blob/HEAD/docs/dry-run.md\"", page);
        Assert.Contains("api('POST', '/app/support', { warnings: said, window: navigator.userAgent })", page);
        // On Settings and on the lock screen (it works before the code is in).
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, "<button class=\"b[a-z ]* sup-save\"").Count);
    }
}
