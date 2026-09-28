using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.Stats
{
    public sealed class PlaybackPlayer
    {
        public byte Id { get; set; }
        public string Name { get; set; } = "";
        public int Color { get; set; }
        public bool Impostor { get; set; }
        public ReplayOutfit Outfit { get; set; } = new ReplayOutfit();
    }

    /// <summary>Where a player is at a moment of the replay.</summary>
    public struct PlaybackState
    {
        public float X, Y;
        public bool Dead, InVent, Gone;
        /// <summary>Walking left (for flipping the sprite).</summary>
        public bool FacingLeft;
        /// <summary>Moved since the last frame (for the walk animation).</summary>
        public bool Moving;
    }

    public sealed class PlaybackBody
    {
        public int Player { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public double From { get; set; }
        public double Until { get; set; }
    }

    public sealed class PlaybackEvent
    {
        public double T { get; set; }
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// A replay file ready to play: positions at any moment (smoothed between frames but not
    /// across teleports like meetings and vents), bodies, and events. Used by the in-game
    /// replay; the web viewer does the same in JavaScript.
    /// </summary>
    public sealed class ReplayPlayback
    {
        private const float Teleport = 2.5f;
        private readonly List<double[]> _frames;

        public string Name { get; }
        public string Tournament { get; }
        public int Round { get; }
        public string Map { get; }
        public int MapId { get; }
        public string? Winner { get; }
        public IReadOnlyList<PlaybackPlayer> Players { get; }
        public IReadOnlyList<PlaybackEvent> Events { get; }
        public IReadOnlyList<PlaybackBody> Bodies { get; }
        public double Duration => _frames.Count == 0 ? 0 : _frames[_frames.Count - 1][0];

        private ReplayPlayback(JsonElement root)
        {
            Name = Str(root, "name");
            Tournament = Str(root, "tournament");
            Round = root.TryGetProperty("round", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
            Map = Str(root, "map");
            MapId = root.TryGetProperty("mapId", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : MapIdFromName(Map);
            Winner = root.TryGetProperty("winner", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;

            var players = new List<PlaybackPlayer>();
            foreach (var p in root.GetProperty("players").EnumerateArray())
            {
                var outfit = new ReplayOutfit();
                if (p.TryGetProperty("outfit", out var o) && o.ValueKind == JsonValueKind.Object)
                    outfit = o.Deserialize<ReplayOutfit>() ?? outfit;
                players.Add(new PlaybackPlayer
                {
                    Id = (byte)p.GetProperty("id").GetInt32(), Name = Str(p, "name"), Color = p.GetProperty("color").GetInt32(),
                    Impostor = p.TryGetProperty("impostor", out var i) && i.ValueKind == JsonValueKind.True, Outfit = outfit,
                });
            }
            Players = players;

            Events = root.TryGetProperty("events", out var ev)
                ? ev.EnumerateArray().Select(e => new PlaybackEvent { T = e.GetProperty("t").GetDouble(), Kind = Str(e, "kind"), Text = Str(e, "text") }).ToList()
                : new List<PlaybackEvent>();

            _frames = root.GetProperty("frames").EnumerateArray()
                .Select(f => f.EnumerateArray().Select(x => x.GetDouble()).ToArray())
                .Where(f => f.Length >= 1 + 3 * players.Count)
                .ToList();
            Bodies = FindBodies();
        }

        /// <summary>Reads a replay file (gzipped or plain JSON).</summary>
        public static ReplayPlayback Load(byte[] file)
        {
            byte[] json = file;
            if (file.Length > 2 && file[0] == 0x1f && file[1] == 0x8b)
            {
                using var gz = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
                using var output = new MemoryStream();
                gz.CopyTo(output);
                json = output.ToArray();
            }
            using var doc = JsonDocument.Parse(json);
            return new ReplayPlayback(doc.RootElement.Clone());
        }

        public PlaybackState[] At(double time)
        {
            var result = new PlaybackState[Players.Count];
            if (_frames.Count == 0) return result;
            int a = FrameAt(time), b = Math.Min(a + 1, _frames.Count - 1);
            var fa = _frames[a];
            var fb = _frames[b];
            var prev = _frames[Math.Max(0, a - 1)];
            double k = fb[0] > fa[0] ? Math.Clamp((time - fa[0]) / (fb[0] - fa[0]), 0, 1) : 0;
            for (int i = 0; i < Players.Count; i++)
            {
                int s = (int)fa[3 + 3 * i];
                float x = (float)fa[1 + 3 * i], y = (float)fa[2 + 3 * i];
                float x2 = (float)fb[1 + 3 * i], y2 = (float)fb[2 + 3 * i];
                bool nextSeen = ((int)fb[3 + 3 * i] & 8) == 0;
                if (nextSeen && Math.Sqrt((x2 - x) * (x2 - x) + (y2 - y) * (y2 - y)) < Teleport)
                {
                    x += (float)((x2 - x) * k);
                    y += (float)((y2 - y) * k);
                }
                float dx = (float)(fb[1 + 3 * i] - prev[1 + 3 * i]);
                result[i] = new PlaybackState
                {
                    X = x, Y = y,
                    Dead = (s & 1) != 0, InVent = (s & 2) != 0, Gone = (s & 12) != 0,
                    FacingLeft = dx < -0.01f,
                    Moving = Math.Abs(fb[1 + 3 * i] - fa[1 + 3 * i]) + Math.Abs(fb[2 + 3 * i] - fa[2 + 3 * i]) > 0.01,
                };
            }
            // Keep facing the same way while standing still.
            for (int i = 0; i < Players.Count; i++)
                if (!result[i].Moving && _lastFacing.TryGetValue(i, out var left)) result[i].FacingLeft = left;
                else _lastFacing[i] = result[i].FacingLeft;
            return result;
        }

        private readonly Dictionary<int, bool> _lastFacing = new Dictionary<int, bool>();

        /// <summary>Bodies lying on the map at this moment (a kill leaves one until the next meeting).</summary>
        public IEnumerable<PlaybackBody> BodiesAt(double time) => Bodies.Where(b => time >= b.From && time < b.Until);

        public IEnumerable<PlaybackEvent> EventsUpTo(double time, int count) => Events.Where(e => e.T <= time).Reverse().Take(count).Reverse();

        /// <summary>Between a meeting being called and its result.</summary>
        public bool InMeeting(double time)
        {
            var last = Events.LastOrDefault(e => e.T <= time && (e.Kind == "meeting" || e.Kind == "eject"));
            return last != null && last.Kind == "meeting";
        }

        private int FrameAt(double time)
        {
            int lo = 0, hi = _frames.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (_frames[mid][0] <= time) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        private List<PlaybackBody> FindBodies()
        {
            var bodies = new List<PlaybackBody>();
            var meetings = Events.Where(e => e.Kind == "meeting").Select(e => e.T).ToList();
            var ejects = Events.Where(e => e.Kind == "eject").Select(e => e.T).ToList();
            for (int i = 0; i < Players.Count; i++)
            {
                for (int f = 1; f < _frames.Count; f++)
                {
                    int was = (int)_frames[f - 1][3 + 3 * i], now = (int)_frames[f][3 + 3 * i];
                    if ((was & 1) != 0 || (now & 1) == 0 || (now & 8) != 0) continue;
                    double at = _frames[f][0];
                    if (ejects.Any(e => Math.Abs(e - at) < 4)) break;       // ejected: no body
                    bodies.Add(new PlaybackBody
                    {
                        Player = i, From = at,
                        Until = meetings.Where(m => m > at).DefaultIfEmpty(double.PositiveInfinity).First(),
                        X = (float)_frames[f - 1][1 + 3 * i], Y = (float)_frames[f - 1][2 + 3 * i],
                    });
                    break;
                }
            }
            return bodies;
        }

        public static int MapIdFromName(string map) => map switch
        {
            "The Skeld" => 0, "MIRA HQ" => 1, "Polus" => 2, "dlekS ehT" => 3, "Airship" => 4, "The Fungle" => 5, _ => -1,
        };

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    /// <summary>Replay files on this computer: the host's own games and any downloaded from Discord.</summary>
    public static class ReplayLibrary
    {
        public static List<FileInfo> Find(IEnumerable<string> folders, int max = 9)
        {
            var files = new List<FileInfo>();
            foreach (var folder in folders)
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    files.AddRange(new DirectoryInfo(folder).EnumerateFiles("tt-replay-*.json*", SearchOption.AllDirectories));
                }
                catch (Exception) { /* a folder we can't read */ }
            }
            return files.GroupBy(f => f.Name).Select(g => g.OrderByDescending(f => f.LastWriteTimeUtc).First())
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(max).ToList();
        }

        /// <summary>"LJ-3" from "tt-replay-LJ-3-20261003-192144.json.gz".</summary>
        public static string Label(FileInfo file)
        {
            string name = file.Name.Replace("tt-replay-", "").Replace(".json.gz", "").Replace(".json", "");
            var parts = name.Split('-');
            return parts.Length >= 4 ? string.Join("-", parts.Take(parts.Length - 2)) : name;
        }
    }
}
