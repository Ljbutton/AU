using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Red Alert's own copy of every game's replay, built from the "replay" messages each host's
    /// mod sends as the game goes. Saved as a replay file (the same as the host's) in the
    /// "game-replays" folder, so a game can still be watched, and judged, when the host's PC drops out
    /// before the game is posted. Kept for the last <see cref="Keep"/> games.
    /// </summary>
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
            public readonly SortedDictionary<int, JsonNode> Frames = new SortedDictionary<int, JsonNode>();
            public readonly List<JsonNode> Events = new List<JsonNode>();
            public JsonNode? End;
            public DateTime First, Last;
            public bool Dirty;
            public DateTime Saved;
            public bool Sim;
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
            lock (_lock)
            {
                if (!_games.TryGetValue(id, out g!))
                {
                    g = new Game { Id = id, Lobby = lobby, First = now, Sim = sim };
                    _games[id] = g;
                    foreach (var old in _games.Values.OrderByDescending(x => x.Last).Skip(Keep).ToList()) _games.Remove(old.Id);
                }
                g.Last = now;
                g.Name = Str(msg, "name") ?? g.Name;
                g.Tournament = Str(msg, "tournament") ?? g.Tournament;
                g.Map = Str(msg, "map") ?? g.Map;
                if (msg.TryGetProperty("mapId", out var m) && m.TryGetInt32(out var mi)) g.MapId = mi;
                if (msg.TryGetProperty("round", out var r) && r.TryGetInt32(out var ri)) g.Round = ri;
                if (msg.TryGetProperty("players", out var pl) && pl.ValueKind == JsonValueKind.Array) g.Players = JsonNode.Parse(pl.GetRawText());
                if (msg.TryGetProperty("geometry", out var geo) && geo.ValueKind == JsonValueKind.Object) g.Geometry = JsonNode.Parse(geo.GetRawText());
                int from = msg.TryGetProperty("from", out var f) && f.TryGetInt32(out var fi) ? fi : g.Frames.Count;
                if (msg.TryGetProperty("frames", out var fr) && fr.ValueKind == JsonValueKind.Array)
                {
                    int i = from;
                    foreach (var frame in fr.EnumerateArray()) g.Frames[i++] = JsonNode.Parse(frame.GetRawText())!;
                }
                if (msg.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array)
                    foreach (var e in ev.EnumerateArray()) g.Events.Add(JsonNode.Parse(e.GetRawText())!);
                if (msg.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.Object) g.End = JsonNode.Parse(end.GetRawText());
                g.Dirty = true;
            }
            if (g.End != null || now - g.Saved >= SaveEvery) Save(g);
        }

        /// <summary>The games kept, newest first.</summary>
        public List<object> List()
        {
            lock (_lock)
                return _games.Values.OrderByDescending(g => g.First).Select(g => (object)new
                {
                    g.Id, g.Lobby, g.Name, g.Map, g.Round,
                    Seconds = g.Frames.Count == 0 ? 0 : Math.Round(Time(g.Frames.Values.Last()), 0),
                    Ended = g.End != null,
                    Winner = g.End?["winner"]?.GetValue<string?>(),
                    Voided = g.End?["voided"]?.GetValue<bool>() == true,
                    // Still sending, or stopped part way (the host dropped out).
                    Live = g.End == null && _clock() - g.Last < TimeSpan.FromSeconds(15),
                    Cut = g.End == null && _clock() - g.Last >= TimeSpan.FromSeconds(15),
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
            if (g != null) return Build(g);
            string? path = PathOf(id);
            return path != null && System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
        }

        /// <summary>Forgets Simulation's games (and their files).</summary>
        public void Forget(Func<string, bool> which)
        {
            List<Game> gone;
            lock (_lock)
            {
                gone = _games.Values.Where(g => g.Sim || which(g.Lobby)).ToList();
                foreach (var g in gone) _games.Remove(g.Id);
            }
            foreach (var g in gone) try { if (PathOf(g.Id) is { } p && System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch (Exception) { }
        }

        public static string FileNameFor(string id) => $"tt-replay-{id}.json.gz";

        private string? PathOf(string id) =>
            _folder == null || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : Path.Combine(_folder, FileNameFor(id));

        private void Save(Game g)
        {
            g.Saved = _clock();
            if (_folder == null || g.Sim) return;
            try
            {
                Directory.CreateDirectory(_folder);
                System.IO.File.WriteAllBytes(PathOf(g.Id)!, Build(g));
                lock (_lock) g.Dirty = false;
            }
            catch (Exception) { }
        }

        private static double Time(JsonNode frame) => frame is JsonArray a && a.Count > 0 ? a[0]!.GetValue<double>() : 0;

        /// <summary>The replay in the viewer's format (as the host's mod writes it), gzipped.</summary>
        private byte[] Build(Game g)
        {
            JsonObject replay;
            lock (_lock)
            {
                var points = new Dictionary<int, double>();
                if (g.End?["points"] is JsonArray pts)
                    foreach (var p in pts) if (p?["id"] != null) points[p["id"]!.GetValue<int>()] = p["points"]?.GetValue<double>() ?? 0;
                var players = new JsonArray();
                if (g.Players is JsonArray pl)
                    foreach (var p in pl)
                    {
                        if (p == null) continue;
                        var copy = JsonNode.Parse(p.ToJsonString())!.AsObject();
                        int id = copy["id"]?.GetValue<int>() ?? 0;
                        copy["points"] = points.TryGetValue(id, out var pt) ? pt : 0;
                        players.Add(copy);
                    }
                // Frames in order; a gap (messages that never came) is simply skipped.
                var frames = new JsonArray();
                foreach (var f in g.Frames.Values) frames.Add(JsonNode.Parse(f.ToJsonString()));
                var events = new JsonArray();
                foreach (var e in g.Events.GroupBy(e => e.ToJsonString()).Select(x => x.First())) events.Add(JsonNode.Parse(e.ToJsonString()));
                bool cut = g.End == null;
                if (cut && g.Frames.Count > 0)
                    events.Add(new JsonObject { ["t"] = Time(g.Frames.Values.Last()), ["kind"] = "note", ["text"] = "The host's game stopped sending here (Red Alert's copy ends)." });
                replay = new JsonObject
                {
                    ["v"] = 1,
                    ["id"] = g.Id,
                    ["name"] = g.Name,
                    ["tournament"] = g.Tournament,
                    ["round"] = g.Round,
                    ["map"] = g.Map,
                    ["mapId"] = g.MapId,
                    ["winner"] = g.End?["winner"]?.DeepClone(),
                    ["endReason"] = g.End?["endReason"]?.DeepClone() ?? (cut ? "Interrupted" : null),
                    ["voided"] = g.End?["voided"]?.DeepClone() ?? false,
                    ["players"] = players,
                    ["geometry"] = g.Geometry?.DeepClone(),
                    ["events"] = events,
                    ["frames"] = frames,
                    ["keptBy"] = "Red Alert",
                };
            }
            using var output = new MemoryStream();
            using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(replay);
                gz.Write(json, 0, json.Length);
            }
            return output.ToArray();
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
