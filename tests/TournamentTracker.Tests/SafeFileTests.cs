using System.Text;
using System.Text.Json;
using TournamentTracker.Stats;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// Safe saving (Core/SafeFile, used by the mod and both apps): a save that fails part-way (a crash, a full disk)
/// leaves the old file whole, a side file left by a crash is never read and is cleaned up, a busy file is tried
/// again, and a file that can't be read is kept as name.corrupt-… before anything is written over it.
/// </summary>
public class SafeFileTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string PathOf(string name) => Path.Combine(_dir.Path, name);

    [Fact]
    public void A_save_replaces_the_file_whole_and_leaves_no_side_file()
    {
        string path = PathOf(Path.Combine("sub", "links.json"));
        SafeFile.WriteAllText(path, "first");                      // its folder is made
        SafeFile.WriteAllText(path, "second");                     // over an old one: File.Replace
        Assert.Equal("second", File.ReadAllText(path));
        Assert.False(File.Exists(path + SafeFile.TempSuffix));
        SafeFile.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void A_save_that_fails_part_way_leaves_the_old_file_as_it_was()
    {
        string path = PathOf("waiting.json");
        SafeFile.WriteAllText(path, "[{\"Id\":\"LJ-1\"}]");
        // The disk fills up half-way through the new contents.
        var e = Assert.Throws<IOException>(() => SafeFile.Write(path, s =>
        {
            s.Write(Encoding.UTF8.GetBytes("[{\"Id\":\"LJ-1\"},{\"Id\":"));
            throw new IOException("There is not enough space on the disk.");
        }));
        Assert.Contains("not enough space", e.Message);
        Assert.Equal("[{\"Id\":\"LJ-1\"}]", File.ReadAllText(path));
        Assert.False(File.Exists(path + SafeFile.TempSuffix));

        // A first save that fails leaves nothing behind at all.
        string fresh = PathOf("new.json");
        Assert.Throws<InvalidOperationException>(() => SafeFile.Write(fresh, s => { s.WriteByte(1); throw new InvalidOperationException("crash"); }));
        Assert.False(File.Exists(fresh));
        Assert.False(File.Exists(fresh + SafeFile.TempSuffix));
    }

    [Fact]
    public void Text_is_written_like_File_WriteAllText()
    {
        string path = PathOf("roster.csv");
        SafeFile.WriteAllText(path, "naïve");
        Assert.Equal(Encoding.UTF8.GetBytes("naïve"), File.ReadAllBytes(path));        // UTF-8, no byte order mark
        SafeFile.WriteAllText(path, "naïve", Encoding.UTF8);
        Assert.Equal(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("naïve")), File.ReadAllBytes(path));   // with one, for Excel
        Assert.Equal("naïve", SafeFile.ReadAllText(path));
    }

    [Fact]
    public void A_busy_side_file_is_tried_again()
    {
        string path = PathOf("state-cup.json");
        SafeFile.WriteAllText(path, "old");
        // Another program has the side file open for a moment (a second copy of the app saving, antivirus).
        var holder = new FileStream(path + SafeFile.TempSuffix, FileMode.Create, FileAccess.Write, FileShare.None);
        // Its own thread, not the thread pool: a busy test run can start a pool task too late for the retries.
        var release = new Thread(() => { Thread.Sleep(40); holder.Dispose(); }) { IsBackground = true };
        release.Start();
        var took = System.Diagnostics.Stopwatch.StartNew();
        SafeFile.WriteAllText(path, "new");
        release.Join();
        Assert.True(took.ElapsedMilliseconds >= 30, "waited " + took.ElapsedMilliseconds);
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void A_broken_file_is_kept_as_a_corrupt_copy_once_and_left_in_place()
    {
        string path = PathOf("links.json");
        File.WriteAllText(path, "[{\"PlayerKey\":\"abc\",\"Disc");      // cut off half-way (written by an older version)
        var warned = new List<string>();
        Assert.Null(SafeFile.ReadOrRecover(path, t => JsonSerializer.Deserialize<List<PlayerLink>>(t), warned.Add));
        var kept = Directory.GetFiles(_dir.Path, "links.json" + SafeFile.CorruptMark + "*");
        Assert.Single(kept);
        Assert.Equal("[{\"PlayerKey\":\"abc\",\"Disc", File.ReadAllText(kept[0]));
        Assert.True(File.Exists(path));                                   // the original stays until something is saved over it
        Assert.Contains("links.json couldn't be read", Assert.Single(warned));
        Assert.Contains("A copy is kept as " + Path.GetFileName(kept[0]), warned[0]);
        Assert.NotNull(SafeFile.CorruptAt(Path.GetFileName(kept[0])));

        // Loading it again (the next start) doesn't make another copy of the same thing.
        Assert.Null(SafeFile.ReadOrRecover(path, t => JsonSerializer.Deserialize<List<PlayerLink>>(t), warned.Add));
        Assert.Single(Directory.GetFiles(_dir.Path, "links.json" + SafeFile.CorruptMark + "*"));

        // An empty file (a save cut short by an older version) counts as broken too; no file at all is just nothing.
        string empty = PathOf("state-cup.json");
        File.WriteAllText(empty, "");
        Assert.Null(SafeFile.ReadOrRecover(empty, t => JsonSerializer.Deserialize<object>(t), warned.Add));
        Assert.Single(Directory.GetFiles(_dir.Path, "state-cup.json" + SafeFile.CorruptMark + "*"));
        Assert.Null(SafeFile.ReadOrRecover(PathOf("missing.json"), t => t, warned.Add));
        Assert.Equal(3, warned.Count);
    }

    [Fact]
    public void A_side_file_left_by_a_crash_is_never_read_and_is_cleaned_up()
    {
        string path = PathOf("stats-cup.json");
        new StatsStore { Tournament = "Cup", GamesRecorded = 3 }.Save(path);
        File.WriteAllText(path + SafeFile.TempSuffix, "{\"GamesRecorded\":");          // the crash: half a new copy
        File.SetLastWriteTimeUtc(path + SafeFile.TempSuffix, DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(3, StatsStore.Load(path, "Cup").GamesRecorded);
        Assert.False(File.Exists(path + SafeFile.TempSuffix));

        // One that's still being written (just now) is left alone; the next save writes over it anyway.
        File.WriteAllText(path + SafeFile.TempSuffix, "{");
        Assert.Equal(3, StatsStore.Load(path, "Cup").GamesRecorded);
        Assert.True(File.Exists(path + SafeFile.TempSuffix));
        new StatsStore { Tournament = "Cup", GamesRecorded = 4 }.Save(path);
        Assert.False(File.Exists(path + SafeFile.TempSuffix));
        Assert.Equal(4, StatsStore.Load(path, "Cup").GamesRecorded);

        // A whole folder (the mod's, at the start of a session).
        File.WriteAllText(PathOf("a.json.tmp"), "x");
        File.SetLastWriteTimeUtc(PathOf("a.json.tmp"), DateTime.UtcNow.AddHours(-1));
        SafeFile.RemoveLeftovers(_dir.Path);
        Assert.False(File.Exists(PathOf("a.json.tmp")));
        Assert.True(SafeFile.IsSideFile("tt-replay-LJ-1.json.gz.tmp"));
        Assert.True(SafeFile.IsSideFile("links.json.corrupt-20261020140000"));
        Assert.False(SafeFile.IsSideFile("links.json"));
    }

    [Fact]
    public void Broken_links_are_kept_before_the_first_new_link_saves_over_them()
    {
        string path = PathOf("links.json");
        File.WriteAllText(path, "not json");
        var log = new ListLog();
        var links = LinkRegistry.Load(path, log);
        Assert.Empty(links.All);
        Assert.Contains(log.All, l => l.Contains("A copy is kept as links.json.corrupt-") && l.EndsWith("Starting with no links."));
        links.Link("abc", "Alice", "111", "alice");
        Assert.Equal("not json", File.ReadAllText(Assert.Single(Directory.GetFiles(_dir.Path, "links.json.corrupt-*"))));
        Assert.Single(LinkRegistry.Load(path, log).All);
    }
}
