using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Red Alert's own copy of every game's replay, built from the "replay" messages each host's
    /// mod sends as the game goes. Saved as a replay file (the same as the host's) in the
    /// "game-replays" folder, so a game can still be watched, and judged, when the host's PC drops out
    /// before the game is posted. Kept for the last <see cref="Keep"/> games.
    /// </summary>
    /// <remarks>
    /// Frames are kept as the text the mod sent (never parsed into objects and copied again), and a
    /// game being played is written to disk away from the feed's request thread, one write at a time
    /// per game, from a quick copy of its lists: building the whole file under the lock on every
    /// message cost about a tenth of a second per lobby every 10 s and held up the other lobbies' feed
    /// and the desk. Once a game's final copy is on disk its frames leave memory (the file is read
    /// back when it's asked for), so a long event day doesn't grow to gigabytes.
    /// </remarks>
    public sealed class GameReplays
    {
        public const int Keep = 200;
        private readonly string? _folder;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Game> _games = new Dictionary<string, Game>(StringComparer.Ordinal);

        private sealed class Game
        {
            public string Id = "", Lobby = "", Name = "", Tournament = "", Map = "";
            public int MapId = -1, Round;
            public JsonNode? Players;
            public JsonNode? Geometry;
            /// <summary>Each frame as the mod sent it (JSON text, UTF-8), by its number; null once the final copy is on disk.</summary>
            public SortedDictionary<int, byte[]>? Frames = new SortedDictionary<int, byte[]>();
            public List<byte[]>? Events = new List<byte[]>();
            public readonly HashSet<string> EventSeen = new HashSet<string>(StringComparer.Ordinal);
            public JsonNode? End;
            public DateTime First, Last;
            public DateTime Saved;
            public bool Sim;
            /// <summary>The time (game seconds) of the latest frame, and its number.</summary>
            public double LastTime;
            public int LastIndex = -1;
            /// <summary>One write at a time; at most one more waiting (it takes the newest frames).</summary>
            public readonly SemaphoreSlim Writing = new SemaphoreSlim(1, 1);
            public int Queued;
            /// <summary>Forgotten (simulation stopped): a write under way doesn't bring its file back.</summary>
            public bool Gone;
            public bool OnDisk => Frames == null;
        }

        public GameReplays(string? folder, Func<DateTime>? clock = null)
        {
            _folder = folder;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>How often a game still being played is written to disk.</summary>
        public TimeSpan SaveEvery { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>One "replay" message from a lobby.</summary>
        public void Add(string lobby, JsonElement msg, bool sim = false)
        {
            string id = Str(msg, "id") ?? "";
            if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            var now = _clock();
            Game g;
            bool save, first;
            lock (_lock)
            {
                if (!_games.TryGetValue(id, out g!))
                {
                    g = new Game { Id = id, Lobby = lobby, First = now, Sim = sim };
                    _games[id] = g;
                    foreach (var old in _games.Values.OrderByDescending(x => x.Last).Skip(Keep).ToList()) _games.Remove(old.Id);
                }
                g.Last = now;
                if (g.OnDisk) return;         // the final copy is saved: a message sent again adds nothing
                g.Name = Str(msg, "name") ?? g.Name;
                g.Tournament = Str(msg, "tournament") ?? g.Tournament;
                g.Map = Str(msg, "map") ?? g.Map;
                if (msg.TryGetProperty("mapId", out var m) && m.TryGetInt32(out var mi)) g.MapId = mi;
                if (msg.TryGetProperty("round", out var r) && r.TryGetInt32(out var ri)) g.Round = ri;
                if (msg.TryGetProperty("players", out var pl) && pl.ValueKind == JsonValueKind.Array) g.Players = JsonNode.Parse(pl.GetRawText());
                if (msg.TryGetProperty("geometry", out var geo) && geo.ValueKind == JsonValueKind.Object) g.Geometry = JsonNode.Parse(geo.GetRawText());
                int from = msg.TryGetProperty("from", out var f) && f.TryGetInt32(out var fi) ? fi : g.Frames!.Count;
                if (msg.TryGetProperty("frames", out var fr) && fr.ValueKind == JsonValueKind.Array)
                {
                    int i = from;
                    foreach (var frame in fr.EnumerateArray())
                    {
                        g.Frames![i] = Encoding.UTF8.GetBytes(frame.GetRawText());
                        if (i >= g.LastIndex) { g.LastIndex = i; g.LastTime = Time(frame); }
                        i++;
                    }
                }
                if (msg.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array)
                    foreach (var e in ev.EnumerateArray())
                    {
                        // The same event sent again (after a drop) is kept once.
                        string text = e.GetRawText();
                        if (g.EventSeen.Add(text)) g.Events!.Add(Encoding.UTF8.GetBytes(text));
                    }
                if (msg.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.Object) g.End = JsonNode.Parse(end.GetRawText());
                first = g.Saved == default;
                save = g.End != null || first || now - g.Saved >= SaveEvery;
                if (save) g.Saved = now;
            }
            // The game's end (and its first message, so a crash right after the start still leaves a
            // copy) is written before answering; the saves in between go to the background.
            if (save) Save(g, wait: g.End != null || first);
        }

        /// <summary>The games kept, newest first.</summary>
        public List<object> List()
        {
            var now = _clock();
            lock (_lock)
                return _games.Values.OrderByDescending(g => g.First).Select(g => (object)new
                {
                    g.Id, g.Lobby, g.Name, g.Map, g.Round,
                    Seconds = g.LastIndex < 0 ? 0 : Math.Round(g.LastTime, 0),
                    Ended = g.End != null,
                    Winner = g.End?["winner"]?.GetValue<string?>(),
                    Voided = g.End?["voided"]?.GetValue<bool>() == true,
                    // Still sending, or stopped part way (the host dropped out).
                    Live = g.End == null && now - g.Last < TimeSpan.FromSeconds(15),
                    Cut = g.End == null && now - g.Last >= TimeSpan.FromSeconds(15),
                    g.Sim,
                }).ToList();
        }

        /// <summary>The newest kept replay of a lobby's game ("LJ-3"), or null.</summary>
        public string? Find(string lobby, string game)
        {
            lock (_lock)
                return _games.Values.Where(g => string.Equals(g.Name, game, StringComparison.OrdinalIgnoreCase)
                        && (lobby.Length == 0 || string.Equals(g.Lobby, lobby, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(g => g.First).FirstOrDefault()?.Id;
        }

        /// <summary>The replay file (gzipped, the viewer's format) for a kept game, or null.</summary>
        public byte[]? File(string id)
        {
            Game? g;
            lock (_lock) _games.TryGetValue(id, out g);
            if (g != null && !g.OnDisk && Build(g) is { } built) return built;
            string? path = PathOf(id);
            if (path == null || !System.IO.File.Exists(path)) return null;
            // Not while it's being written.
            if (g != null) g.Writing.Wait();
            try { return System.IO.File.ReadAllBytes(path); }
            finally { g?.Writing.Release(); }
        }

        /// <summary>Waits for every write under way (for tests, and before Red Alert closes).</summary>
        public void Flush()
        {
            List<Game> all;
            lock (_lock) all = _games.Values.ToList();
            foreach (var g in all)
            {
                for (int i = 0; i < 100 && Volatile.Read(ref g.Queued) == 1; i++) Thread.Sleep(10);
                g.Writing.Wait();
                g.Writing.Release();
            }
        }

        /// <summary>Forgets Simulation's games (and their files).</summary>
        public void Forget(Func<string, bool> which)
        {
            List<Game> gone;
            lock (_lock)
            {
                gone = _games.Values.Where(g => g.Sim || which(g.Lobby)).ToList();
                foreach (var g in gone) { _games.Remove(g.Id); g.Gone = true; }
            }
            foreach (var g in gone)
            {
                g.Writing.Wait();
                try { if (PathOf(g.Id) is { } p && System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch (Exception) { }
                finally { g.Writing.Release(); }
            }
        }

        public static string FileNameFor(string id) => $"tt-replay-{id}.json.gz";

        private string? PathOf(string id) =>
            _folder == null || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : Path.Combine(_folder, FileNameFor(id));

        /// <summary>Writes the game's file: now (waiting for one under way), or in the background (one more at most).</summary>
        private void Save(Game g, bool wait)
        {
            if (_folder == null || g.Sim) return;
            if (wait) { Write(g); return; }
            if (Interlocked.Exchange(ref g.Queued, 1) == 1) return;
            _ = Task.Run(() =>
            {
                Volatile.Write(ref g.Queued, 0);
                Write(g);
            });
        }

        private void Write(Game g)
        {
            g.Writing.Wait();
            try
            {
                bool final;
                lock (_lock) { if (g.Gone || g.OnDisk) return; final = g.End != null; }
                var bytes = Build(g);
                if (bytes == null) return;
                Directory.CreateDirectory(_folder!);
                string path = PathOf(g.Id)!, temp = path + ".tmp";
                System.IO.File.WriteAllBytes(temp, bytes);
                System.IO.File.Move(temp, path, overwrite: true);
                // The final copy is on disk: the frames leave memory (the file is read back when asked for).
                if (final) lock (_lock) { g.Frames = null; g.Events = null; g.EventSeen.Clear(); g.Players = null; g.Geometry = null; }
            }
            catch (Exception) { }
            finally { g.Writing.Release(); }
        }

        private static double Time(JsonElement frame) =>
            frame.ValueKind == JsonValueKind.Array && frame.GetArrayLength() > 0 && frame[0].ValueKind == JsonValueKind.Number ? frame[0].GetDouble() : 0;

        /// <summary>The replay in the viewer's format (as the host's mod writes it), gzipped; null once its frames have left memory.</summary>
        private byte[]? Build(Game g)
        {
            // Under the lock only the lists are copied (the frames themselves are never changed once in).
            byte[][] frames, events;
            JsonNode? players, geometry, end;
            string id, name, tournament, map;
            int round, mapId;
            double lastTime;
            lock (_lock)
            {
                if (g.Frames == null || g.Events == null) return null;
                frames = g.Frames.Values.ToArray();
                events = g.Events.ToArray();
                players = g.Players; geometry = g.Geometry; end = g.End;
                id = g.Id; name = g.Name; tournament = g.Tournament; map = g.Map; round = g.Round; mapId = g.MapId;
                lastTime = g.LastTime;
            }
            var points = new Dictionary<int, double>();
            if (end?["points"] is JsonArray pts)
                foreach (var p in pts) if (p?["id"] != null) points[p["id"]!.GetValue<int>()] = p["points"]?.GetValue<double>() ?? 0;
            bool cut = end == null;
            using var output = new MemoryStream();
            using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var w = new Utf8JsonWriter(gz))
            {
                void Node(string prop, JsonNode? node) { w.WritePropertyName(prop); if (node == null) w.WriteNullValue(); else node.WriteTo(w); }
                w.WriteStartObject();
                w.WriteNumber("v", 1);
                w.WriteString("id", id);
                w.WriteString("name", name);
                w.WriteString("tournament", tournament);
                w.WriteNumber("round", round);
                w.WriteString("map", map);
                w.WriteNumber("mapId", mapId);
                Node("winner", end?["winner"]);
                if (end?["endReason"] is { } reason) Node("endReason", reason);
                else if (cut) w.WriteString("endReason", "Interrupted");
                else w.WriteNull("endReason");
                if (end?["voided"] is { } voided) Node("voided", voided); else w.WriteBoolean("voided", false);
                w.WriteStartArray("players");
                if (players is JsonArray pl)
                    foreach (var p in pl)
                    {
                        if (p == null) continue;
                        var copy = p.DeepClone().AsObject();
                        int pid = copy["id"]?.GetValue<int>() ?? 0;
                        copy["points"] = points.TryGetValue(pid, out var pt) ? pt : 0;
                        copy.WriteTo(w);
                    }
                w.WriteEndArray();
                Node("geometry", geometry);
                w.WriteStartArray("events");
                foreach (var e in events) w.WriteRawValue(e, skipInputValidation: true);
                if (cut && frames.Length > 0)
                    new JsonObject { ["t"] = lastTime, ["kind"] = "note", ["text"] = "The host's game stopped sending here (Red Alert's copy ends)." }.WriteTo(w);
                w.WriteEndArray();
                // Frames in order; a gap (messages that never came) is simply skipped.
                w.WriteStartArray("frames");
                foreach (var f in frames) w.WriteRawValue(f, skipInputValidation: true);
                w.WriteEndArray();
                w.WriteString("keptBy", "Red Alert");
                w.WriteEndObject();
            }
            return output.ToArray();
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
