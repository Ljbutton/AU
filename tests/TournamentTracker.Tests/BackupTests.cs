using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using TournamentTracker.App;
using TournamentTracker.Stats;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// The Button's backup of the mod's data (%LOCALAPPDATA%\TheButton\backup): it copies the games, links, waiting
/// list, round state and totals and nothing secret, newer always wins (both ways), a game saved under a new name
/// leaves its old name behind, games gone from the mod's folder stay in the backup (and the backup then keeps its
/// own links and round), and Restore puts back what's missing or older.
/// </summary>
public class BackupTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _game, _data, _backupDir;
    private readonly ModBackup _backup;
    private static readonly string Webhook = "https://discord.com" + "/api/" + "webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";

    public BackupTests()
    {
        // Where The Button in a test finds Among Us (its Steam folder), and keeps its settings (app/app.json) and backup.
        _game = Path.Combine(_dir.Path, "Steam", "steamapps", "common", "Among Us");
        _data = ModInstaller.DataDir(_game);
        _backupDir = Path.Combine(_dir.Path, "app", ModBackup.FolderName);
        _backup = new ModBackup(_backupDir);
    }

    public void Dispose() => _dir.Dispose();

    private string Mod(string rel) => Path.Combine(_data, rel);
    private string Bak(string rel) => Path.Combine(_backupDir, rel);
    private static string G(string name) => Path.Combine("games", "cup", name);

    // Well in the past, so files the code under test saves now are always newer.
    private static readonly DateTime T0 = new(2025, 1, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A file in the mod's folder, written at <paramref name="at"/>.</summary>
    private void Write(string rel, string text, DateTime at)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Mod(rel))!);
        File.WriteAllText(Mod(rel), text);
        File.SetLastWriteTimeUtc(Mod(rel), at);
    }

    /// <summary>A finished game as the mod saves it, with the points it scored.</summary>
    private static GameRecord Game(int number, bool impostorsWin, DateTime started)
    {
        var tracker = new GameTracker(SheetRules.Fixed());
        tracker.Start(number, "Cup", "ABCDEF", "Polus", Players.Lobby(), started);
        var game = tracker.End(impostorsWin ? "ImpostorByKill" : "HumansByTask", impostorsWin ? Outcome.Impostors : Outcome.Crewmates, Players.Lobby(), started.AddMinutes(10))!;
        game.Id = $"LJ-{number}-{started:yyyyMMdd-HHmmss}";
        game.Host = "LJ";
        return game;
    }

    private void SaveGame(GameRecord g, DateTime at, string suffix = "") =>
        Write(G($"game-{g.Id}{suffix}.json"), JsonSerializer.Serialize(g, new JsonSerializerOptions { WriteIndented = true }), at);

    /// <summary>A mod folder mid-tournament: two games, the waiting list, links, round, totals, a replay, and the files that must never be copied.</summary>
    private (GameRecord A, GameRecord B) Tournament()
    {
        var a = Game(1, true, T0);
        var b = Game(2, false, T0.AddMinutes(20));
        SaveGame(a, T0.AddMinutes(10));
        SaveGame(b, T0.AddMinutes(30));
        Write(G("waiting.json"), JsonSerializer.Serialize(new[] { new { Id = b.Id, RoundDone = false } }), T0.AddMinutes(30));
        Write(G($"tt-replay-{a.Id}.json.gz"), "replay", T0.AddMinutes(11));
        var links = new LinkRegistry(Mod("links.json"));
        links.Link("alice#1", "Alice", "111", "alice");
        links.Link("bob#2", "Bob", "222", "bob");
        File.SetLastWriteTimeUtc(Mod("links.json"), T0.AddMinutes(5));
        Write("state-cup.json", "{\"Round\":2,\"RoundGames\":{\"2\":2}}", T0.AddMinutes(30));
        var store = new StatsStore { Tournament = "Cup" };
        foreach (var g in new[] { a, b }) { store.Apply(g); store.NoteFinished(g); }
        store.Save(Mod("stats-cup.json"));
        File.SetLastWriteTimeUtc(Mod("stats-cup.json"), T0.AddMinutes(30));

        // Never copied: codes, tokens, webhooks, connection files, side files of saves, and everything else.
        Write("setup-code.txt", "TT1-secret", T0);
        Write("host-discord.json", "{\"BotTokens\":[\"tok\"]}", T0);
        Write("control.json", "{\"token\":\"t\"}", T0);
        Write("button.json", "{\"token\":\"t\"}", T0);
        Write("installed.json", "{}", T0);
        Write("last-disconnects.json", "[]", T0);
        Write(G("prelim-count.json"), "{\"Webhook\":\"" + Webhook + "\"}", T0);
        Write("links.json.tmp", "[", T0);
        Write("links.json.corrupt-20261019120000", "[", T0);
        return (a, b);
    }

    private static string[] Files(string root) =>
        Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(f => f).ToArray() : Array.Empty<string>();

    [Fact]
    public void The_backup_copies_the_games_links_and_rounds_and_never_a_secret()
    {
        var (a, b) = Tournament();
        _backup.Mirror(_data, T0.AddHours(1));

        Assert.Equal(new[]
        {
            $"games/cup/game-{a.Id}.json", $"games/cup/game-{b.Id}.json", $"games/cup/tt-replay-{a.Id}.json.gz", "games/cup/waiting.json",
            "links.json", "state-cup.json", "stats-cup.json",
        }.OrderBy(f => f), Files(_backupDir));
        foreach (var f in Files(_backupDir))
        {
            Assert.Equal(File.ReadAllText(Mod(f)), File.ReadAllText(Bak(f)));
            Assert.Equal(File.GetLastWriteTimeUtc(Mod(f)), File.GetLastWriteTimeUtc(Bak(f)));    // each copy keeps its file's time
            Assert.DoesNotContain("TT1-", File.ReadAllText(Bak(f)));
            Assert.DoesNotContain("webhooks", File.ReadAllText(Bak(f)));
        }
        Assert.Equal(7, _backup.State.Files);
        Assert.Equal(0, _backup.State.Missing);
        Assert.Equal(T0.AddHours(1), _backup.State.CopiedAt);
        Assert.False(ModBackup.Wanted("setup-code.txt"));
        Assert.False(ModBackup.Wanted("host-discord.json"));
        Assert.False(ModBackup.Wanted("games/cup/prelim-count.json"));
        Assert.False(ModBackup.Wanted("games/cup/game-x.json.tmp"));
        Assert.True(ModBackup.Wanted("games/cup/reset 2026-10-20 12-00-00/game-x.json"));
    }

    [Fact]
    public void Newer_always_wins_and_unchanged_files_are_not_copied_again()
    {
        var (a, _) = Tournament();
        _backup.Mirror(_data, T0.AddHours(1));

        // The mod saves the game again (points changed): the backup takes the newer copy.
        a.Players[0].Points += 5;
        SaveGame(a, T0.AddHours(2));
        _backup.Mirror(_data, T0.AddHours(2));
        Assert.Equal(File.ReadAllText(Mod(G($"game-{a.Id}.json"))), File.ReadAllText(Bak(G($"game-{a.Id}.json"))));
        Assert.Equal(T0.AddHours(2), _backup.State.CopiedAt);

        // Nothing changed: nothing is copied (the time of the last copy stays).
        _backup.Mirror(_data, T0.AddHours(3));
        Assert.Equal(T0.AddHours(2), _backup.State.CopiedAt);

        // An older copy in the mod's folder (put there by hand) never replaces the backup's newer one…
        Write("state-cup.json", "{\"Round\":1}", T0.AddMinutes(1));
        Write("links.json", "[]", T0.AddMinutes(1));
        _backup.Mirror(_data, T0.AddHours(4));
        Assert.Contains("\"Round\":2", File.ReadAllText(Bak("state-cup.json")));
        // …and Restore puts the newer ones back.
        var r = _backup.Restore(_data);
        Assert.True(r.Ok, r.Message);
        Assert.True(r.Round);
        Assert.Equal("Put back from the backup: 2 players' links and the round.", r.Message);
        Assert.Equal(2, LinkRegistry.Load(Mod("links.json"), NullLog.Instance).All.Count);
        Assert.Contains("\"Round\":2", File.ReadAllText(Mod("state-cup.json")));
        Assert.Equal(T0.AddMinutes(30), File.GetLastWriteTimeUtc(Mod("state-cup.json")));

        // Restore with nothing older or missing: the mod's newer files stay as they are.
        Write("state-cup.json", "{\"Round\":3}", T0.AddHours(5));
        r = _backup.Restore(_data);
        Assert.Equal("Nothing to put back: the mod's folder already has everything in the backup, as new or newer.", r.Message);
        Assert.Contains("\"Round\":3", File.ReadAllText(Mod("state-cup.json")));
    }

    [Fact]
    public void A_game_saved_under_a_new_name_leaves_its_old_name_in_the_backup_too()
    {
        var (a, b) = Tournament();
        _backup.Mirror(_data, T0.AddHours(1));

        // Voided: game-….json becomes game-…-void.json.
        a.Voided = true;
        File.Delete(Mod(G($"game-{a.Id}.json")));
        SaveGame(a, T0.AddHours(2), "-void");
        _backup.Mirror(_data, T0.AddHours(2));
        Assert.False(File.Exists(Bak(G($"game-{a.Id}.json"))));
        Assert.True(File.Exists(Bak(G($"game-{a.Id}-void.json"))));

        // Reset all points: the games are put away in a dated folder (kept, not deleted).
        string put = Path.Combine("games", "cup", "reset 2026-10-20 15-00-00");
        Directory.CreateDirectory(Mod(put));
        foreach (var f in Directory.GetFiles(Mod(Path.Combine("games", "cup")), "game-*.json")) File.Move(f, Path.Combine(Mod(put), Path.GetFileName(f)));
        _backup.Mirror(_data, T0.AddHours(3));
        Assert.Empty(Directory.GetFiles(Bak(Path.Combine("games", "cup")), "game-*.json"));
        Assert.Equal(2, Directory.GetFiles(Bak(put), "game-*.json").Length);
        Assert.Equal(0, _backup.State.Missing);

        // So restoring never brings put-away games back into play.
        Assert.Equal(0, _backup.Restore(_data).Games);
        Assert.Empty(Directory.GetFiles(Mod(Path.Combine("games", "cup")), "game-*.json"));
        Assert.True(File.Exists(Path.Combine(Mod(put), $"game-{b.Id}.json")));
    }

    [Fact]
    public void After_reinstalling_Among_Us_the_backup_keeps_its_own_and_Restore_puts_it_all_back()
    {
        var (a, b) = Tournament();
        _backup.Mirror(_data, T0.AddHours(1));

        // Among Us reinstalled: the mod's folder is gone. The mod starts again, a new player links and a new game is played.
        Directory.Delete(_game, true);
        var c = Game(1, true, T0.AddDays(1));
        c.Id = "LJ-1-new";
        SaveGame(c, T0.AddDays(1).AddMinutes(10));
        Write(G("waiting.json"), JsonSerializer.Serialize(new[] { new { Id = c.Id, RoundDone = false } }), T0.AddDays(1).AddMinutes(10));
        var fresh = new LinkRegistry(Mod("links.json"));
        fresh.Link("carl#3", "Carl", "333", "carl");
        File.SetLastWriteTimeUtc(Mod("links.json"), T0.AddDays(1));
        Write("state-cup.json", "{\"Round\":0}", T0.AddDays(1));
        var store = new StatsStore { Tournament = "Cup" };
        store.Apply(c); store.NoteFinished(c);
        store.Save(Mod("stats-cup.json"));
        File.SetLastWriteTimeUtc(Mod("stats-cup.json"), T0.AddDays(1));

        _backup.Mirror(_data, T0.AddDays(1).AddHours(1));
        Assert.Equal(2, _backup.State.Missing);
        // The new game is backed up; the backup's links, waiting list, round and totals aren't replaced by the fresh ones.
        Assert.True(File.Exists(Bak(G($"game-{c.Id}.json"))));
        Assert.Equal(2, LinkRegistry.Load(Bak("links.json"), NullLog.Instance).All.Count);
        Assert.Contains(b.Id, File.ReadAllText(Bak(G("waiting.json"))));
        Assert.Contains("\"Round\":2", File.ReadAllText(Bak("state-cup.json")));

        var r = _backup.Restore(_data);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, r.Games);
        Assert.Equal(1, r.Replays);
        Assert.Equal(2, r.Links);
        Assert.True(r.Waiting);
        Assert.True(r.Totals);
        Assert.False(r.Round);                                                           // the mod's round file is newer: it stays
        Assert.Equal("Put back from the backup: 2 games, 1 replay, 2 players' links, the games waiting for Submit and the points totals.", r.Message);

        Assert.True(File.Exists(Mod(G($"game-{a.Id}.json"))));
        Assert.True(File.Exists(Mod(G($"game-{b.Id}.json"))));
        Assert.True(File.Exists(Mod(G($"tt-replay-{a.Id}.json.gz"))));
        var links = LinkRegistry.Load(Mod("links.json"), NullLog.Instance);
        Assert.Equal(new[] { "333", "111", "222" }.OrderBy(x => x), links.All.Select(l => l.DiscordUserId).OrderBy(x => x));
        var waiting = (JsonNode.Parse(File.ReadAllText(Mod(G("waiting.json")))) as JsonArray)!.Select(n => n!["Id"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { c.Id, b.Id }, waiting);                                     // the mod's own first, then the one put back
        // The totals are worked out again from all three games.
        var totals = StatsStore.Load(Mod("stats-cup.json"), "");
        Assert.Equal(3, totals.GamesRecorded);
        Assert.Equal("Cup", totals.Tournament);
        Assert.Equal(3, totals.NextGameNumber("LJ"));

        _backup.Mirror(_data, T0.AddDays(1).AddHours(2));
        Assert.Equal(0, _backup.State.Missing);
        // And from now on the backup follows the mod's folder again (the merged links).
        Assert.Equal(3, LinkRegistry.Load(Bak("links.json"), NullLog.Instance).All.Count);
    }

    [Fact]
    public void Games_removed_on_purpose_can_be_forgotten()
    {
        var (a, b) = Tournament();
        _backup.Mirror(_data, T0.AddHours(1));
        File.Delete(Mod(G($"game-{a.Id}.json")));
        File.Delete(Mod(G($"tt-replay-{a.Id}.json.gz")));
        _backup.Mirror(_data, T0.AddHours(2));
        Assert.Equal(1, _backup.State.Missing);

        Assert.Equal(1, _backup.Forget(_data));
        _backup.Mirror(_data, T0.AddHours(3));
        Assert.Equal(0, _backup.State.Missing);
        Assert.False(File.Exists(Bak(G($"game-{a.Id}.json"))));
        Assert.False(File.Exists(Bak(G($"tt-replay-{a.Id}.json.gz"))));
        Assert.True(File.Exists(Bak(G($"game-{b.Id}.json"))));
    }

    [Fact]
    public void The_backup_keeps_as_many_replays_as_the_mod()
    {
        Tournament();
        for (int i = 0; i < TournamentSession.ReplaysKept + 3; i++)
            Write(G($"tt-replay-LJ-{i + 10}-x.json.gz"), "r", T0.AddMinutes(40 + i));
        _backup.Mirror(_data, T0.AddHours(1));
        Assert.Equal(TournamentSession.ReplaysKept, Directory.GetFiles(Bak(Path.Combine("games", "cup")), "tt-replay-*").Length);
        Assert.False(File.Exists(Bak(G("tt-replay-LJ-10-x.json.gz"))));                 // the oldest went
    }

    [Fact]
    public async Task The_Button_backs_up_shows_it_on_Home_and_restores_from_Settings()
    {
        var (a, _) = Tournament();
        File.WriteAllText(Path.Combine(_game, "Among Us.exe"), "");
        // The Button's own settings file was cut short (by an older version): kept aside, and it starts with defaults.
        Directory.CreateDirectory(Path.Combine(_dir.Path, "app"));
        File.WriteAllText(Path.Combine(_dir.Path, "app", "app.json"), "{\"SetupCode\":\"TT1-");
        var opened = new List<string>();
        var env = ButtonApp.Env(_dir.Path, Path.Combine(_dir.Path, "Steam"));
        env.Open = opened.Add;
        using var app = new AppServer(env, new HttpClient(new FakeHttp()));
        var http = ButtonApp.Client(app);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir.Path, "app"), "app.json.corrupt-*"));
        Assert.Contains("app.json couldn't be read", File.ReadAllText(Path.Combine(_dir.Path, "app", "the-button.log")));

        app.BackupNow();                                                                  // what the 10-second timer does
        var state = await AppHttp.Get(http, "/app/state");
        Assert.Equal(7, state.GetProperty("backup").GetProperty("files").GetInt32());
        Assert.Equal(0, state.GetProperty("backup").GetProperty("missing").GetInt32());
        Assert.True(File.Exists(Bak(G($"game-{a.Id}.json"))));
        var damaged = state.GetProperty("damaged").EnumerateArray().ToList();
        Assert.Contains(damaged, d => d.GetProperty("name").GetString() == "app.json" && d.GetProperty("whose").GetString() == "The Button's");

        // A game goes missing from the mod's folder: Home says so, and Restore puts it back.
        File.Delete(Mod(G($"game-{a.Id}.json")));
        app.BackupNow();
        state = await AppHttp.Get(http, "/app/state");
        Assert.Equal(1, state.GetProperty("backup").GetProperty("missing").GetInt32());
        var r = await AppHttp.Post(http, "/app/backup/restore");
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("Put back from the backup: 1 game and the points totals.", r.GetProperty("message").GetString());
        Assert.True(File.Exists(Mod(G($"game-{a.Id}.json"))));
        state = await AppHttp.Get(http, "/app/state");
        Assert.Equal(0, state.GetProperty("backup").GetProperty("missing").GetInt32());
        Assert.Contains("Restore backup: Put back from the backup", File.ReadAllText(Path.Combine(_dir.Path, "app", "the-button.log")));

        // Removed on purpose: Forget takes it out of the backup too.
        File.Delete(Mod(G($"game-{a.Id}.json")));
        app.BackupNow();
        r = await AppHttp.Post(http, "/app/backup/forget");
        Assert.Equal("The backup no longer keeps that game.", r.GetProperty("message").GetString());
        state = await AppHttp.Get(http, "/app/state");
        Assert.Equal(0, state.GetProperty("backup").GetProperty("missing").GetInt32());

        Assert.True((await AppHttp.Post(http, "/app/open", new { what = "backup" })).GetProperty("ok").GetBoolean());
        Assert.Equal(_backupDir, Assert.Single(opened));

        // Home's notice and Settings → Help.
        using var stream = typeof(AppServer).Assembly.GetManifestResourceStream("ui/index.html")!;
        string home = new StreamReader(stream).ReadToEnd();
        Assert.Contains("id=\"home-files\"", home);
        Assert.Contains("data-backup=\"restore\">Restore<", home);
        Assert.Contains("data-backup=\"forget\"", home);
        Assert.Contains("id=\"backup-restore\">Restore backup<", home);
        Assert.Contains("data-open=\"backup\">Backup folder<", home);
        Assert.Contains("A damaged file was set aside", home);
    }

    [Fact]
    public void The_support_bundle_never_takes_anything_from_the_backup_or_from_side_files()
    {
        Tournament();
        Write("state-cup.json", "{\"Round\":2,\"FeedKey\":\"hunter2\"}", T0.AddHours(1));
        _backup.Mirror(_data, T0.AddHours(2));
        Assert.Contains("hunter2", File.ReadAllText(Bak("state-cup.json")));              // the backup is a true copy, on this PC only
        Write("setup-code.txt.tmp", "TT1-secret", T0);
        Write("host-discord.json.corrupt-20261020120000", "{\"BotTokens\":[\"tok-secret\"]}", T0);
        Write("state-cup.json.corrupt-20261020120000", "{\"FeedKey\":\"hunter2\"", T0);
        string settings = Path.Combine(_dir.Path, "app", "app.json");
        File.WriteAllText(settings, "{}");
        File.WriteAllText(Path.Combine(_game, "Among Us.exe"), "");

        var bundle = ButtonSupport.Build(new ButtonSupport.Inputs { AppVersion = "0.1.65", SettingsFile = settings, GamePath = _game, Mod = ModInstaller.State(_game) });
        using var zip = new ZipArchive(new MemoryStream(bundle.ToZip(T0)));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("backup") || e.FullName.Contains(".tmp") || e.FullName.Contains(".corrupt-") || e.FullName.Contains("setup-code") || e.FullName.Contains("host-discord"));
        foreach (var e in zip.Entries)
        {
            string text = new StreamReader(e.Open()).ReadToEnd();
            Assert.DoesNotContain("hunter2", text);
            Assert.DoesNotContain("tok-secret", text);
            Assert.DoesNotContain("TT1-secret", text);
        }
    }

    [Fact]
    public void Links_from_the_backup_only_fill_in_whats_missing()
    {
        string path = Mod("links.json");
        var links = new LinkRegistry(path);
        links.Link("abc", "Alice", "111", "alice");
        var log = new ListLog();
        // From a backup: only players and Discord accounts it has no link for are added; its own stay.
        int added = links.AddMissing(new[]
        {
            new PlayerLink { PlayerKey = "ABC", PlayerName = "Old Alice", DiscordUserId = "999" },   // has a link already
            new PlayerLink { PlayerKey = "bob", PlayerName = "Bob", DiscordUserId = "111" },         // that Discord account is Alice's now
            new PlayerLink { PlayerKey = "carl", PlayerName = "Carl", DiscordUserId = "333", DiscordName = "carl" },
        });
        Assert.Equal(1, added);
        var again = LinkRegistry.Load(path, log);
        Assert.Equal("111", again.Find("abc")!.DiscordUserId);
        Assert.Equal("333", again.Find("carl")!.DiscordUserId);
        Assert.Null(again.Find("bob"));
    }

    [Fact]
    public void Kept_copies_are_found_for_Homes_warning()
    {
        string app = Path.Combine(_dir.Path, "TheButton"), data = Path.Combine(_dir.Path, "data");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(Path.Combine(data, "games", "cup"));
        var now = new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(Path.Combine(app, "app.json.corrupt-20261020130000"), "x");
        File.WriteAllText(Path.Combine(data, "games", "cup", "waiting.json.corrupt-20261019120000"), "x");
        File.WriteAllText(Path.Combine(data, "links.json.corrupt-20261001120000"), "x");      // weeks ago: not shown any more
        var found = ModBackup.Damaged(app, data, now);
        Assert.Equal(new[] { "app.json", "waiting.json" }, found.Select(d => d.Name));
        Assert.Equal("The Button's", found[0].Whose);
        Assert.Equal("The mod's", found[1].Whose);
        Assert.Equal(new DateTime(2026, 10, 20, 13, 0, 0, DateTimeKind.Utc), found[0].At);
    }
}
