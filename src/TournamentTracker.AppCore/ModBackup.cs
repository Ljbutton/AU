using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using TournamentTracker.Stats;
using TournamentTracker.Voice;

namespace TournamentTracker.App
{
    /// <summary>What the backup holds, as Home and Settings show it.</summary>
    public sealed class BackupState
    {
        /// <summary>When the backup last took a copy of something (null: not since The Button started).</summary>
        public DateTime? CopiedAt { get; set; }
        /// <summary>How many files the backup holds.</summary>
        public int Files { get; set; }
        /// <summary>Saved games in the backup that the mod's folder doesn't have (after reinstalling Among Us, say).</summary>
        public int Missing { get; set; }
        /// <summary>The last copy that didn't work, in words (null: none).</summary>
        public string? Problem { get; set; }
    }

    /// <summary>A copy of a file that couldn't be read (kept as name.corrupt-…), for Home's warning.</summary>
    public sealed class DamagedFile
    {
        /// <summary>The file that couldn't be read, like links.json.</summary>
        public string Name { get; set; } = "";
        /// <summary>The copy kept next to it, like links.json.corrupt-20261020140200.</summary>
        public string Kept { get; set; } = "";
        /// <summary>When the copy was kept (UTC).</summary>
        public DateTime At { get; set; }
        /// <summary>Whose file: "The mod's" or "The Button's".</summary>
        public string Whose { get; set; } = "";
    }

    /// <summary>What a restore did.</summary>
    public sealed class RestoreResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public int Games { get; set; }
        public int Replays { get; set; }
        public int Links { get; set; }
        public bool Waiting { get; set; }
        public bool Round { get; set; }
        public bool Totals { get; set; }
    }

    /// <summary>
    /// The Button's copy of the mod's tournament data, outside the Among Us folder (%LOCALAPPDATA%\TheButton\backup),
    /// so reinstalling Among Us, a file check or a deleted folder loses no games. What's copied (and nothing else):
    /// the saved games under games/ (each game, the waiting list, the replays), links.json, and each tournament's
    /// round state (state-*.json) and totals (stats-*.json). Never the setup code, the host's Discord, the connection
    /// files or the count message (it has a webhook in it).
    /// <para>
    /// <see cref="Mirror"/> runs on The Button's 10-second timer: when the mod's files changed (by name, size and time)
    /// it copies what's new or newer, each copy keeping its file's time. A newer file is never replaced by an older one,
    /// either way. A game saved under a new name (voided, back again, put away by Reset all points) loses its old name in
    /// the backup too; a game that's gone from the mod's folder stays in the backup, and while any are, the backup keeps
    /// its own links, waiting list, round and totals for that tournament rather than taking the folder's fresh ones.
    /// </para>
    /// <para>
    /// <see cref="Restore"/> (Among Us closed) puts back what's missing or older in the mod's folder. Where the mod's copy
    /// is newer (made again after a reinstall, say) it stays, but links and the waiting list are added to: players and
    /// Discord accounts it has no link for, and the games just put back that were waiting for Submit. A tournament that got
    /// games back has its totals worked out again from its games.
    /// </para>
    /// </summary>
    public sealed class ModBackup
    {
        /// <summary>The backup's folder, next to The Button's settings.</summary>
        public const string FolderName = "backup";

        /// <summary>Everything is compared again this often, even when nothing seems to have changed.</summary>
        public static readonly TimeSpan FullLook = TimeSpan.FromMinutes(10);

        /// <summary>A kept copy of a damaged file is shown on Home for this long.</summary>
        public static readonly TimeSpan DamagedShown = TimeSpan.FromDays(7);

        private const string Games = "games", Links = "links.json", Waiting = "waiting.json";

        private readonly object _lock = new object();
        private string _seen = "";
        private DateTime _nextFull;
        private volatile BackupState _state = new BackupState();

        public ModBackup(string folder) => Folder = folder;

        public string Folder { get; }

        /// <summary>What the last look found.</summary>
        public BackupState State => _state;

        // ---- What goes in -----------------------------------------------------------------------

        /// <summary>
        /// Whether a file of the mod's data folder (by its path inside it) goes in the backup: games/&lt;tournament&gt;/…
        /// game-*.json, waiting.json and tt-replay-*; links.json; state-*.json and stats-*.json. Never a side file of a
        /// save or a kept broken copy, and nothing else (no tokens, codes or webhooks).
        /// </summary>
        public static bool Wanted(string relative)
        {
            var parts = Split(relative);
            string n = parts[parts.Length - 1].ToLowerInvariant();
            if (SafeFile.IsSideFile(n)) return false;
            if (parts.Length == 1) return n == Links || ((n.StartsWith("state-") || n.StartsWith("stats-")) && n.EndsWith(".json"));
            if (parts.Length < 3 || !parts[0].Equals(Games, StringComparison.OrdinalIgnoreCase)) return false;
            return (n.StartsWith("game-") && n.EndsWith(".json")) || n == Waiting || (n.StartsWith("tt-replay-") && (n.EndsWith(".json.gz") || n.EndsWith(".json")));
        }

        private static string[] Split(string relative) => relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>A saved game's tournament folder and id ("cup/LJ-3-20261003-192144"), whatever it's saved as (-void, -abandoned); null for other files.</summary>
        private static (string Tournament, string Id)? GameOf(string relative)
        {
            var parts = Split(relative);
            string name = parts[parts.Length - 1];
            if (parts.Length < 3 || !name.StartsWith("game-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
            string id = name.Substring(5, name.Length - 10);
            foreach (var suffix in new[] { "-void", "-abandoned" })
                if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { id = id.Substring(0, id.Length - suffix.Length); break; }
            return (parts[1], id);
        }

        private static string Key((string Tournament, string Id) g) => g.Tournament + "/" + g.Id;

        /// <summary>The tournament a single file belongs to: links.json is everyone's (""), state-x.json, stats-x.json and games/x/waiting.json are x's; null for games and replays.</summary>
        private static string? SingleOf(string relative)
        {
            var parts = Split(relative);
            string n = parts[parts.Length - 1];
            if (parts.Length == 1)
                return n.Equals(Links, StringComparison.OrdinalIgnoreCase) ? "" : n.Length > 11 ? n.Substring(6, n.Length - 11) : null;
            return n.Equals(Waiting, StringComparison.OrdinalIgnoreCase) && parts.Length == 3 ? parts[1] : null;
        }

        /// <summary>The wanted files under a folder, by their path inside it.</summary>
        private static Dictionary<string, FileInfo> List(string root)
        {
            var list = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dir = new DirectoryInfo(root);
                if (!dir.Exists) return list;
                foreach (var f in dir.EnumerateFiles())
                    if (Wanted(f.Name)) list[f.Name] = f;
                var games = new DirectoryInfo(Path.Combine(root, Games));
                if (games.Exists)
                    foreach (var f in games.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(root, f.FullName);
                        if (Wanted(rel)) list[rel] = f;
                    }
            }
            catch (Exception) { /* what could be read */ }
            return list;
        }

        /// <summary>Each saved game (by tournament and id) and the paths it's saved under.</summary>
        private static Dictionary<string, List<string>> GamesIn(IEnumerable<string> relatives)
        {
            var games = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in relatives)
                if (GameOf(rel) is { } g)
                {
                    string key = Key(g);
                    if (!games.TryGetValue(key, out var at)) games[key] = at = new List<string>();
                    at.Add(rel);
                }
            return games;
        }

        /// <summary>A copy of the file, with its time (so newer and older can be told apart later).</summary>
        private static void Copy(FileInfo from, string to)
        {
            var time = from.LastWriteTimeUtc;
            SafeFile.WriteAllBytes(to, SafeFile.ReadAllBytes(from.FullName));
            File.SetLastWriteTimeUtc(to, time);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        // ---- Keeping it up to date --------------------------------------------------------------

        /// <summary>
        /// Brings the backup up to date with the mod's data folder when anything there changed (or every 10 minutes).
        /// Skipped while a restore (or the last look) is still going.
        /// </summary>
        public void Mirror(string? dataDir, DateTime? utcNow = null)
        {
            if (dataDir == null || !Monitor.TryEnter(_lock)) return;
            try
            {
                var now = utcNow ?? DateTime.UtcNow;
                var source = List(dataDir);
                string seen = dataDir + "\n" + string.Join("\n", source.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => $"{kv.Key}|{kv.Value.Length}|{kv.Value.LastWriteTimeUtc.Ticks}"));
                if (seen == _seen && now < _nextFull) return;

                var backup = List(Folder);
                var inSource = GamesIn(source.Keys);
                var inBackup = GamesIn(backup.Keys);
                // Tournaments with games only the backup has: their links, waiting list, round and totals stay the backup's.
                var careful = new HashSet<string>(inBackup.Keys.Where(k => !inSource.ContainsKey(k)).Select(k => k.Substring(0, k.IndexOf('/'))), StringComparer.OrdinalIgnoreCase);
                int copied = 0;
                string? problem = null;
                foreach (var kv in source)
                {
                    if (backup.TryGetValue(kv.Key, out var had))
                    {
                        // Older, or the same (a drive that keeps times to 2 s can show two saves with one time: then the size tells).
                        if (kv.Value.LastWriteTimeUtc < had.LastWriteTimeUtc || (kv.Value.LastWriteTimeUtc == had.LastWriteTimeUtc && kv.Value.Length == had.Length)) continue;
                        if (SingleOf(kv.Key) is { } of && (of.Length == 0 ? careful.Count > 0 : careful.Contains(of))) continue;
                    }
                    try { Copy(kv.Value, Path.Combine(Folder, kv.Key)); copied++; }
                    catch (Exception e) { problem ??= $"Couldn't copy {kv.Key} ({e.Message})."; }
                }
                // A game now saved under another name (voided, back again, put away by Reset all points): its old name goes.
                foreach (var kv in inBackup)
                    if (inSource.TryGetValue(kv.Key, out var names))
                        foreach (var old in kv.Value.Where(r => !names.Contains(r, StringComparer.OrdinalIgnoreCase)))
                            TryDelete(Path.Combine(Folder, old));
                PruneReplays();

                var after = List(Folder);
                _state = new BackupState
                {
                    CopiedAt = copied > 0 ? now : _state.CopiedAt,
                    Files = after.Count,
                    Missing = GamesIn(after.Keys).Keys.Count(k => !inSource.ContainsKey(k)),
                    Problem = problem,
                };
                _seen = problem == null ? seen : "";
                _nextFull = now + FullLook;
            }
            finally { Monitor.Exit(_lock); }
        }

        /// <summary>The backup keeps the newest replays of each tournament, as many as the mod does.</summary>
        private void PruneReplays()
        {
            try
            {
                var games = new DirectoryInfo(Path.Combine(Folder, Games));
                if (!games.Exists) return;
                foreach (var tournament in games.EnumerateDirectories())
                    foreach (var old in ReplayLibrary.Find(new[] { tournament.FullName }, int.MaxValue).Skip(TournamentSession.ReplaysKept))
                        TryDelete(old.FullName);
            }
            catch (Exception) { }
        }

        /// <summary>The backup is to be looked at again in full on the next <see cref="Mirror"/> (after a restore, say).</summary>
        public void LookAgain()
        {
            lock (_lock) _seen = "";
        }

        // ---- Putting it back --------------------------------------------------------------------

        /// <summary>
        /// Puts back into the mod's data folder what's missing there or older than the backup's copy (call it with
        /// Among Us closed): games (a newer copy of a game replaces the mod's, whatever either is saved as), replays,
        /// round state and totals; links and waiting lists are added to. A tournament that got games back has its totals
        /// worked out again from its games. Nothing newer is ever replaced by something older.
        /// </summary>
        public RestoreResult Restore(string dataDir)
        {
            lock (_lock)
            {
                var r = new RestoreResult();
                var backup = List(Folder);
                if (backup.Count == 0) { r.Message = "There's no backup on this PC yet."; return r; }
                var source = List(dataDir);
                var inSource = GamesIn(source.Keys);
                var inBackup = GamesIn(backup.Keys);
                var returned = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);   // tournament → games that were missing
                var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);                        // tournaments whose games changed
                var failed = new List<string>();

                void Put(string rel, Action copy)
                {
                    try { copy(); }
                    catch (Exception e) { failed.Add($"{rel} ({e.Message})"); throw; }
                }

                foreach (var kv in inBackup)
                {
                    string rel = kv.Value.OrderByDescending(x => backup[x].LastWriteTimeUtc).First();
                    var file = backup[rel];
                    var game = GameOf(rel)!.Value;
                    try
                    {
                        if (inSource.TryGetValue(kv.Key, out var names))
                        {
                            if (file.LastWriteTimeUtc <= names.Max(x => source[x].LastWriteTimeUtc)) continue;   // the mod's copy is as new or newer
                            Put(rel, () => Copy(file, Path.Combine(dataDir, rel)));
                            foreach (var old in names.Where(x => !x.Equals(rel, StringComparison.OrdinalIgnoreCase))) TryDelete(Path.Combine(dataDir, old));
                        }
                        else
                        {
                            Put(rel, () => Copy(file, Path.Combine(dataDir, rel)));
                            if (!returned.TryGetValue(game.Tournament, out var ids)) returned[game.Tournament] = ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            ids.Add(game.Id);
                        }
                        changed.Add(game.Tournament);
                        r.Games++;
                    }
                    catch (Exception) { }
                }

                foreach (var kv in backup)
                {
                    if (GameOf(kv.Key) != null || SingleOf(kv.Key) != null) continue;
                    if (source.TryGetValue(kv.Key, out var have) && have.LastWriteTimeUtc >= kv.Value.LastWriteTimeUtc) continue;
                    try { Put(kv.Key, () => Copy(kv.Value, Path.Combine(dataDir, kv.Key))); r.Replays++; }
                    catch (Exception) { }
                }

                foreach (var kv in backup)
                {
                    string? of = SingleOf(kv.Key);
                    if (of == null) continue;
                    string to = Path.Combine(dataDir, kv.Key);
                    string name = Path.GetFileName(kv.Key).ToLowerInvariant();
                    bool older = !source.TryGetValue(kv.Key, out var have) || have.LastWriteTimeUtc < kv.Value.LastWriteTimeUtc;   // the mod's is missing or older
                    try
                    {
                        if (name.StartsWith("stats-") && changed.Contains(of)) continue;    // worked out again below
                        if (older)
                        {
                            Put(kv.Key, () => Copy(kv.Value, to));
                            if (name.Equals(Links)) r.Links += LinkRegistry.Load(to, NullLog.Instance).All.Count;
                            else if (name.Equals(Waiting)) r.Waiting |= (JsonNode.Parse(SafeFile.ReadAllText(to)) as JsonArray)?.Count > 0;
                            else if (name.StartsWith("state-")) r.Round = true;
                            else r.Totals = true;
                        }
                        else if (name.Equals(Links))
                        {
                            // The mod's are newer (made again after a reinstall, say): they stay, and players and Discord accounts it has no link for come back.
                            var bak = LinkRegistry.Load(kv.Value.FullName, NullLog.Instance);
                            Put(kv.Key, () => r.Links = LinkRegistry.Load(to, NullLog.Instance).AddMissing(bak.All));
                        }
                        else if (name.Equals(Waiting) && returned.TryGetValue(of, out var ids))
                        {
                            Put(kv.Key, () => r.Waiting |= AddWaiting(to, kv.Value.FullName, ids));
                        }
                    }
                    catch (Exception) { }
                }

                foreach (var tournament in changed)
                {
                    try { Put("stats-" + tournament + ".json", () => RebuildTotals(dataDir, tournament)); r.Totals = true; }
                    catch (Exception) { }
                }

                _seen = "";
                var parts = new List<string>();
                if (r.Games > 0) parts.Add(r.Games == 1 ? "1 game" : $"{r.Games} games");
                if (r.Replays > 0) parts.Add(r.Replays == 1 ? "1 replay" : $"{r.Replays} replays");
                if (r.Links > 0) parts.Add(r.Links == 1 ? "1 player's link" : $"{r.Links} players' links");
                if (r.Waiting) parts.Add("the games waiting for Submit");
                if (r.Round) parts.Add("the round");
                if (r.Totals) parts.Add("the points totals");
                r.Ok = failed.Count == 0;
                r.Message = (parts.Count == 0 ? "Nothing to put back: the mod's folder already has everything in the backup, as new or newer."
                        : "Put back from the backup: " + Join(parts) + ".")
                    + (failed.Count > 0 ? $" Couldn't put back {string.Join(", ", failed.Take(3))}{(failed.Count > 3 ? "…" : "")}." : "");
                return r;
            }
        }

        private static string Join(List<string> parts) =>
            parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];

        /// <summary>Adds to the mod's waiting list the backup's entries for the games just put back (the mod's entries stay). True when it added any.</summary>
        private static bool AddWaiting(string modFile, string backupFile, HashSet<string> returned)
        {
            var mine = JsonNode.Parse(SafeFile.ReadAllText(modFile)) as JsonArray ?? new JsonArray();
            var theirs = JsonNode.Parse(SafeFile.ReadAllText(backupFile)) as JsonArray ?? new JsonArray();
            static string? Id(JsonNode? n) => n is JsonObject o && o["Id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            var have = new HashSet<string>(mine.Select(Id).Where(i => i != null).Select(i => i!), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var entry in theirs)
                if (Id(entry) is { } id && returned.Contains(id) && have.Add(id))
                {
                    mine.Add(entry!.DeepClone());
                    added++;
                }
            if (added > 0) SafeFile.WriteAllText(modFile, mine.ToJsonString());
            return added > 0;
        }

        /// <summary>A tournament's totals worked out again from the games in its folder (as the mod adds them up, oldest first).</summary>
        private static void RebuildTotals(string dataDir, string tournament)
        {
            string path = Path.Combine(dataDir, $"stats-{tournament}.json");
            var dir = new DirectoryInfo(Path.Combine(dataDir, Games, tournament));
            var games = new List<GameRecord>();
            foreach (var f in dir.Exists ? dir.EnumerateFiles("game-*.json") : Enumerable.Empty<FileInfo>())
            {
                try { if (JsonSerializer.Deserialize<GameRecord>(SafeFile.ReadAllText(f.FullName)) is { } g) games.Add(g); }
                catch (Exception) { /* a broken game is left out, as the mod leaves it out */ }
            }
            string name = File.Exists(path) ? StatsStore.Load(path, "").Tournament : "";
            var store = new StatsStore { Tournament = name.Length > 0 ? name : games.Select(g => g.Tournament).FirstOrDefault(t => t.Length > 0) ?? "" };
            foreach (var g in games.OrderBy(g => g.StartedUtc))
            {
                store.Apply(g);
                store.NoteFinished(g);
            }
            store.Save(path);
        }

        /// <summary>
        /// The games only the backup has are taken out of it (the host removed them from the mod's folder on purpose),
        /// with their replays. Gives how many.
        /// </summary>
        public int Forget(string dataDir)
        {
            lock (_lock)
            {
                var backup = List(Folder);
                var inSource = GamesIn(List(dataDir).Keys);
                int gone = 0;
                foreach (var kv in GamesIn(backup.Keys).Where(kv => !inSource.ContainsKey(kv.Key)))
                {
                    var game = GameOf(kv.Value[0])!.Value;
                    foreach (var rel in kv.Value) TryDelete(Path.Combine(Folder, rel));
                    string replay = $"tt-replay-{game.Id}.json";
                    foreach (var rel in backup.Keys.Where(rel => Split(rel) is { Length: >= 3 } p && p[1].Equals(game.Tournament, StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(rel).StartsWith(replay, StringComparison.OrdinalIgnoreCase)))
                        TryDelete(Path.Combine(Folder, rel));
                    gone++;
                }
                _seen = "";
                return gone;
            }
        }

        // ---- Damaged files ----------------------------------------------------------------------

        /// <summary>
        /// The copies kept of files that couldn't be read (name.corrupt-…) in the last 7 days: The Button's own
        /// (next to its settings) and the mod's (its data folder and each tournament's games folder), newest first.
        /// </summary>
        public static List<DamagedFile> Damaged(string? appFolder, string? dataDir, DateTime utcNow)
        {
            var list = new List<DamagedFile>();
            void Look(string? folder, string whose)
            {
                try
                {
                    if (folder == null || !Directory.Exists(folder)) return;
                    foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*" + SafeFile.CorruptMark + "*"))
                    {
                        var at = SafeFile.CorruptAt(f.Name) ?? f.LastWriteTimeUtc;
                        if (utcNow - at > DamagedShown) continue;
                        list.Add(new DamagedFile { Name = f.Name.Substring(0, f.Name.LastIndexOf(SafeFile.CorruptMark, StringComparison.Ordinal)), Kept = f.Name, At = at, Whose = whose });
                    }
                }
                catch (Exception) { }
            }
            Look(appFolder, "The Button's");
            if (dataDir != null)
            {
                Look(dataDir, "The mod's");
                try
                {
                    var games = new DirectoryInfo(Path.Combine(dataDir, Games));
                    if (games.Exists) foreach (var t in games.EnumerateDirectories()) Look(t.FullName, "The mod's");
                }
                catch (Exception) { }
            }
            return list.OrderByDescending(d => d.At).Take(5).ToList();
        }
    }
}
