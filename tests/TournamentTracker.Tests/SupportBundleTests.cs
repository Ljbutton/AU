using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// Support bundles (both apps): nothing secret ever goes in (bot tokens, webhooks, setup and administration
/// codes, passwords in links, JSON secret fields at any depth, Twitch tokens, stream keys, Stream Deck keys),
/// what isn't secret stays readable, big logs keep their newest 5 MB, and The Button's bundle has the files
/// that explain a problem and none that hold secrets.
/// </summary>
public class SupportBundleTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    // Made-up values in the shapes the real ones have.
    private static readonly string BotToken = string.Join(".", "MTE5ODc2NTQzMjEwOTg3NjU0Mw", "GaBcDe", "abcdefghijklmnopqrstuvwxyz0123456789AB");   // built at run time so no token-shaped text sits in the source (secret scanning)
    private static readonly string BotToken2 = string.Join(".", "OTk4ODc3NjY1NTQ0MzMyMjExMA", "Gx1y2z", "ZYXWVUTSRQPONMLKJIHGFEDCBA9876543210_-");   // built at run time so no token-shaped text sits in the source (secret scanning)
    private static readonly string BareHookPrefix = "discord.com/api/" + "webhooks/";
    private static readonly string Webhook = "https://discord.com" + "/api/" + "webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCdEfGhIjKlMnOpQrStUvWxYz012345";
    private const string WebhookPtb = "https://ptb.discordapp.com/api/v10/webhooks/123456789012345678/zzzzSECRETzzzz";

    private static string HostCode() => new SetupCode
    {
        Mode = "tournament", TournamentId = "cup", TournamentName = "Cup", Webhook = Webhook, GuildId = "123456789012345678",
        BotTokens = new List<string> { BotToken },
    }.Encode();

    private static string AdminCode() => new SetupCode { Mode = "admin", TournamentId = "cup", TournamentName = "Cup", Webhook = Webhook, BotTokens = new List<string> { BotToken2 } }.Encode();

    private static void AssertClean(string text)
    {
        foreach (var secret in new[] { BotToken, BotToken2, "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", "zzzzSECRETzzzz", "123456789012345678/", HostCode(), AdminCode(), "hunter2", "s3cretKey", "live_123456789_AbCdEfGhIjKlMnOp", "oauthtoken1234567890", "deckkey12345", "apptoken-abcdef" })
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public void Discord_tokens_webhooks_and_codes_are_taken_out_of_text()
    {
        string log = $"""
            2026-10-12 19:00:01 INFO Bot connected with token {BotToken}
            Authorization: Bot {BotToken2}
            Posting to {Webhook} now
            posting to {BareHookPrefix}123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789 without https
            and {WebhookPtb}
            The host pasted {HostCode()} and the organiser {AdminCode()}
            """;
        string clean = SupportBundle.Redact(log);
        AssertClean(clean);
        Assert.Contains("[removed token]", clean);
        Assert.Contains("[removed webhook]", clean);
        Assert.Contains("[removed code]", clean);
        // What isn't secret stays.
        Assert.Contains("2026-10-12 19:00:01 INFO Bot connected with token", clean);
        Assert.Contains("Posting to", clean);
        // Safe to run twice.
        Assert.Equal(clean, SupportBundle.Redact(clean));
    }

    [Fact]
    public void Passwords_and_keys_in_links_go_but_the_rest_of_the_link_stays()
    {
        string text = "Opened https://vdo.ninja/?push=ttabc123&password=hunter2&screenshare&label=LJ and view https://vdo.ninja/?view=ttabc123&pw=hunter2&noaudio\n"
            + "Deck: http://127.0.0.1:8768/deck/switch/LJ?key=deckkey12345&x=1\n"
            + "Send page: http://127.0.0.1:5000/send?token=apptoken-abcdef&page=abc\n"
            + "Twitch chat oauth:oauthtoken1234567890 stream live_123456789_AbCdEfGhIjKlMnOp to rtmp://live.twitch.tv/app/s3cretKey\n"
            + "OBS password: hunter2\n";
        string clean = SupportBundle.Redact(text);
        AssertClean(clean);
        Assert.Contains("https://vdo.ninja/?push=ttabc123&password=[removed]&screenshare&label=LJ", clean);
        Assert.Contains("view=ttabc123&pw=[removed]&noaudio", clean);
        Assert.Contains("/deck/switch/LJ?key=[removed]&x=1", clean);
        Assert.Contains("token=[removed]&page=abc", clean);
        Assert.Contains("rtmp://live.twitch.tv/app/[removed stream key]", clean);
        Assert.Contains("OBS password: [removed]", clean);
        // Words that only look like the names stay: a hotkey, an exit code, a monkey.
        Assert.Equal("Keys: hotkey=F10 exit code=1 monkey=3", SupportBundle.Redact("Keys: hotkey=F10 exit code=1 monkey=3"));
    }

    [Fact]
    public void Json_secret_fields_are_removed_at_any_depth_and_empty_ones_still_show()
    {
        string json = JsonSerializer.Serialize(new
        {
            GamePath = @"C:\Games\Among Us",
            AdminCode = AdminCode(),
            SetupCode = HostCode(),
            AppToken = "apptoken-abcdef",
            HostDiscord = new { guild = "1234", bots = new[] { BotToken, BotToken2 }, @public = Webhook, status = "" },
            Obs = new { Host = "127.0.0.1", Password = "hunter2", Scenes = new { full = "TT Full" } },
            Twitch = new { AccessToken = "oauthtoken1234567890", RefreshToken = "", ClientId = "public-client" },
            Deck = new { DeckKey = "deckkey12345", Port = 8768 },
            Lists = new object[] { new { streamKey = "s3cretKey" }, new { name = "LJ", note = "see password=hunter2" } },
            Kept = (string?)null,
            Feed = new { PushUrl = "https://vdo.ninja/?push=tt1&password=hunter2" },
            Inner = "{\"token\":\"apptoken-abcdef\",\"ok\":true}",
            UseLobbyVoice = true,
        });
        string clean = SupportBundle.RedactJson(json);
        AssertClean(clean);
        var doc = JsonDocument.Parse(clean).RootElement;
        Assert.Equal("[removed]", doc.GetProperty("AdminCode").GetString());
        Assert.Equal("[removed]", doc.GetProperty("SetupCode").GetString());
        Assert.Equal("[removed]", doc.GetProperty("AppToken").GetString());
        Assert.Equal("[removed]", doc.GetProperty("HostDiscord").GetProperty("bots").GetString());
        Assert.Equal("[removed webhook]", doc.GetProperty("HostDiscord").GetProperty("public").GetString());
        Assert.Equal("", doc.GetProperty("HostDiscord").GetProperty("status").GetString());          // not set: still shows as not set
        Assert.Equal("[removed]", doc.GetProperty("Obs").GetProperty("Password").GetString());
        Assert.Equal("TT Full", doc.GetProperty("Obs").GetProperty("Scenes").GetProperty("full").GetString());
        Assert.Equal("[removed]", doc.GetProperty("Twitch").GetProperty("AccessToken").GetString());
        Assert.Equal("", doc.GetProperty("Twitch").GetProperty("RefreshToken").GetString());
        Assert.Equal("[removed]", doc.GetProperty("Deck").GetProperty("DeckKey").GetString());
        Assert.Equal(8768, doc.GetProperty("Deck").GetProperty("Port").GetInt32());
        Assert.Equal("[removed]", doc.GetProperty("Lists")[0].GetProperty("streamKey").GetString());
        Assert.Equal("LJ", doc.GetProperty("Lists")[1].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("Kept").ValueKind);
        Assert.Equal("https://vdo.ninja/?push=tt1&password=[removed]", doc.GetProperty("Feed").GetProperty("PushUrl").GetString());
        Assert.Contains("\"token\":\"[removed]\"", doc.GetProperty("Inner").GetString());
        Assert.True(doc.GetProperty("UseLobbyVoice").GetBoolean());
        Assert.Equal(@"C:\Games\Among Us", doc.GetProperty("GamePath").GetString());
    }

    [Fact]
    public void Json_inside_a_log_line_and_settings_file_lines_are_redacted_too()
    {
        string text = "19:00 WARN sent {\"botToken\":\"abc.def.ghi\",\"webhookUrl\":\"x\",\"count\":3,\"bots\":[\"t1\",\"t2\"],\"name\":\"LJ\"}\n"
            + "[AutoMute]\n## Bot tokens\n# Setting type: String\nBotTokens = tokenA,tokenB\nGuildId = 1234\n"
            + "[Discord]\nStatsWebhookUrl = https://example.com/hook\nLeaderboardSize = 10\nStatusWebhookUrl = \n";
        string clean = SupportBundle.Redact(text);
        Assert.Contains("\"botToken\": \"[removed]\"".Replace(" ", ""), clean.Replace(" ", ""));
        Assert.Contains("\"bots\":\"[removed]\"", clean);
        Assert.Contains("\"name\":\"LJ\"", clean);
        Assert.Contains("BotTokens = [removed]", clean);
        Assert.Contains("GuildId = 1234", clean);
        Assert.Contains("StatsWebhookUrl = [removed]", clean);
        Assert.Contains("LeaderboardSize = 10", clean);
        Assert.Contains("StatusWebhookUrl = \n", clean);                              // empty: still shows it's not set
        Assert.DoesNotContain("tokenA", clean);
        Assert.DoesNotContain("abc.def.ghi", clean);
    }

    [Fact]
    public void A_big_log_is_redacted_in_good_time_and_a_blob_never_leaks()
    {
        var log = new StringBuilder();
        for (int i = 0; log.Length < 5_000_000; i++)
            log.Append($"[Info   :Tournament Tracker] 19:{i % 60:D2}:00 Player {i} linked, lobby LJ, token={(i % 1000 == 0 ? BotToken : "none")} {{\"key\":\"k{i}\",\"name\":\"P{i}\"}}\n");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string clean = SupportBundle.Redact(log.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.DoesNotContain(BotToken, clean);
        Assert.Contains("\"name\":\"P42\"", clean);

        // One long line of base64-ish text with a secret in it: redacted, or (if it can't be checked in time) left out.
        string blob = string.Concat(Enumerable.Repeat("aB3-xY_9-", 120_000)) + " password=hunter2 " + BotToken;
        string checkedBlob = SupportBundle.Redact(blob);
        Assert.DoesNotContain("hunter2", checkedBlob);
        Assert.DoesNotContain(BotToken, checkedBlob);
    }

    private static Dictionary<string, string> Unzip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        return archive.Entries.ToDictionary(e => e.FullName, e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); });
    }

    [Fact]
    public void A_bundle_has_a_readme_index_keeps_the_newest_5_MB_and_leaves_out_what_isnt_text()
    {
        string big = Path.Combine(_dir.Path, "big.log");
        using (var w = new StreamWriter(big))
        {
            for (int i = 0; w.BaseStream.Length < SupportBundle.MaxFileBytes + 1_000_000; i++) w.WriteLine($"line {i:D8} token={BotToken}");
            w.WriteLine("the very last line");
        }
        string binary = Path.Combine(_dir.Path, "pic.png");
        File.WriteAllBytes(binary, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 1 });
        string settings = Path.Combine(_dir.Path, "settings.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { SetupCode = HostCode(), Name = "LJ" }));

        var b = new SupportBundle("Test 1.0");
        b.AddFile("logs/big.log", big, "A big log");
        b.AddFile("pic.png", binary, "A picture");
        b.AddFile("missing.log", Path.Combine(_dir.Path, "nope.log"), "Not there");
        b.AddFile("settings.json", settings, "Settings");
        b.AddFile("../../escape.json", settings, "A name with dots");
        b.AddText("summary.txt", "Webhook: " + Webhook, "Summary");
        b.AddFolderListing("list.txt", _dir.Path, "The folder");
        var files = Unzip(b.ToZip(new DateTime(2026, 10, 12, 19, 30, 0)));

        Assert.Equal("README.txt", files.Keys.First());
        string readme = files["README.txt"];
        Assert.Contains("Made by Test 1.0 on 2026-10-12 19:30:00", readme);
        Assert.Contains("logs/big.log: A big log (the last 5 MB of", readme);
        Assert.Contains("(left out: not text) pic.png: A picture", readme);
        Assert.Contains("(not found) missing.log: Not there", readme);
        Assert.Contains("summary.txt: Summary", readme);
        Assert.False(files.ContainsKey("pic.png"));
        Assert.True(files.ContainsKey("escape.json"));                                   // no way out of the zip's folder

        string log = files["logs/big.log"];
        Assert.True(Encoding.UTF8.GetByteCount(log) <= SupportBundle.MaxFileBytes + 1000);
        Assert.StartsWith("[… the first", log);
        Assert.EndsWith("the very last line" + Environment.NewLine, log);
        Assert.DoesNotContain(BotToken, log);
        Assert.Matches(@"\]\r?\nline \d{8} token=\[removed", log);                       // starts at a whole line
        foreach (var f in files.Values) AssertClean(f);
        Assert.Contains("\"Name\": \"LJ\"", files["settings.json"]);
        Assert.Contains("big.log", files["list.txt"]);
    }

    [Fact]
    public void Saving_names_the_zip_by_date_and_never_overwrites_one()
    {
        var at = new DateTime(2026, 10, 12, 19, 30, 0);
        Assert.Equal("TournamentTracker-support-2026-10-12-1930.zip", SupportBundle.FileName(at));
        var b = new SupportBundle("Test");
        b.AddText("a.txt", "hello", "A");
        string first = b.Save(_dir.Path, at), second = b.Save(_dir.Path, at);
        Assert.EndsWith("TournamentTracker-support-2026-10-12-1930.zip", first);
        Assert.EndsWith("TournamentTracker-support-2026-10-12-1930-2.zip", second);
        Assert.Equal(_dir.Path, SupportBundle.Folder(Path.Combine(_dir.Path, "no-desktop"), _dir.Path));   // Downloads when there's no Desktop
        Assert.Equal(Path.GetTempPath(), SupportBundle.Folder(null, null));
    }

    [Fact]
    public void The_Buttons_bundle_has_the_logs_and_state_but_never_the_secret_files()
    {
        string appDir = Path.Combine(_dir.Path, "TheButton");
        Directory.CreateDirectory(appDir);
        string settingsFile = Path.Combine(appDir, "app.json");
        File.WriteAllText(settingsFile, JsonSerializer.Serialize(new AppSettings { SetupCode = HostCode(), AdminCode = AdminCode(), AppToken = "apptoken-abcdef",
            HostDiscord = new HostDiscord { GuildId = "123456789012345678", BotTokens = new List<string> { BotToken2 }, PublicWebhook = Webhook } }));
        File.WriteAllText(Path.Combine(appDir, "the-button.log"), $"2026-10-12 19:00:00 INFO Organiser connected {BotToken}\n");

        string game = Path.Combine(_dir.Path, "Among Us");
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "config"));
        File.WriteAllText(Path.Combine(game, "Among Us.exe"), "");
        File.WriteAllText(Path.Combine(game, "BepInEx", "LogOutput.log"), $"[Info   :Tournament Tracker] Started with webhook {Webhook}\n");
        File.WriteAllText(Path.Combine(game, "BepInEx", "config", ButtonSupport.ModConfigName), "[AutoMute]\nBotTokens = " + BotToken + "\n");
        string data = ModInstaller.DataDir(game);
        Directory.CreateDirectory(Path.Combine(data, "games", "cup"));
        File.WriteAllText(Path.Combine(data, "setup-code.txt"), HostCode());
        File.WriteAllText(Path.Combine(data, "host-discord.json"), "{\"bots\":[\"" + BotToken2 + "\"]}");
        File.WriteAllText(Path.Combine(data, "control.json"), "{\"port\":1,\"token\":\"apptoken-abcdef\"}");
        File.WriteAllText(Path.Combine(data, "links.json"), "{\"lj\":\"999\"}");
        File.WriteAllText(Path.Combine(data, "state-cup.json"), "{\"Round\":2,\"FeedKey\":\"hunter2\"}");
        File.WriteAllText(Path.Combine(data, "last-disconnects.json"), "[{\"reason\":\"ClientTimeout\"}]");
        File.WriteAllText(Path.Combine(data, "stats-cup.json"), "{\"players\":" + new string(' ', (int)ButtonSupport.SmallStats) + "[]}");   // too big: only listed
        File.WriteAllText(Path.Combine(data, "games", "cup", "LJ-1.json"), "{}");
        string unity = Path.Combine(_dir.Path, "LocalLow");
        Directory.CreateDirectory(unity);
        File.WriteAllText(Path.Combine(unity, "Player.log"), "Disconnected: ClientTimeout\n");

        string status = JsonSerializer.Serialize(new
        {
            version = "0.1.63", phase = "Lobby", lobby = "LJ",
            safeMode = new { on = true, reason = "Among Us updated", gameVersion = "2026.10.1", builtFor = "2026.9.30", overridden = false },
            lastDisconnect = new { at = "2026-10-12T18:50:00Z", reason = "ClientTimeout", detail = (string?)null, inGame = true },
            hostAuthority = new { on = true, active = false },
            feed = new { pushUrl = "https://vdo.ninja/?push=tt1&password=hunter2" },
        });
        var bundle = ButtonSupport.Build(new ButtonSupport.Inputs
        {
            AppVersion = "0.1.63", SettingsFile = settingsFile, GamePath = game, Mod = ModInstaller.State(game), StatusJson = status,
            UnityLogFolder = unity, Notes = { "Connection check: good." }, UtcNow = new DateTime(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc),
        });
        var files = Unzip(bundle.ToZip(new DateTime(2026, 10, 12, 19, 0, 0)));

        foreach (var name in new[] { "summary.txt", "versions.txt", "the-button/the-button.log", "the-button/settings.json", "mod/LogOutput.log",
            "mod/" + ButtonSupport.ModConfigName, "mod/data/state-cup.json", "mod/data/last-disconnects.json", "mod/data-folder.txt", "mod/games-list.txt", "among-us/Player.log" })
            Assert.True(files.ContainsKey(name), name);
        Assert.DoesNotContain(files.Keys, k => k.Contains("setup-code") || k.Contains("host-discord") || k.Contains("control.json") || k.Contains("links.json") || k.Contains("stats-cup.json") && !k.EndsWith(".txt"));
        Assert.Contains("(not found) mod/BepInEx.cfg", files["README.txt"]);
        foreach (var text in files.Values) AssertClean(text);

        string summary = files["summary.txt"];
        Assert.Contains("Safe mode: ON. Safe mode: Among Us updated.", summary);
        Assert.Contains("Last disconnect: 2026-10-12 18:50:00 UTC (10 min ago), ClientTimeout, during a game.", summary);
        Assert.Contains("Host authority: on, NOT active in this lobby", summary);
        Assert.Contains("Connection check: good.", summary);
        Assert.Contains("password=[removed]", summary);
        Assert.Contains("Among Us: 2026.10.1", files["versions.txt"]);
        Assert.Contains("Mod built for Among Us: 2026.9.30", files["versions.txt"]);
        Assert.Contains("\"FeedKey\": \"[removed]\"", files["mod/data/state-cup.json"]);
        Assert.Contains("\"Round\": 2", files["mod/data/state-cup.json"]);
        Assert.Contains("LJ-1.json", files["mod/games-list.txt"]);
    }
}
