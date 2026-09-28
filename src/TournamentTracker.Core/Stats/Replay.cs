using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TournamentTracker.Stats
{
    /// <summary>One player's position at a moment. State bits: 1 dead, 2 in a vent, 4 disconnected.</summary>
    public readonly struct ReplayPosition
    {
        public ReplayPosition(byte playerId, float x, float y, bool dead, bool inVent, bool disconnected)
        {
            PlayerId = playerId; X = x; Y = y;
            State = (dead ? 1 : 0) | (inVent ? 2 : 0) | (disconnected ? 4 : 0);
        }

        public byte PlayerId { get; }
        public float X { get; }
        public float Y { get; }
        public int State { get; }
    }

    /// <summary>The map as the game has it: wall lines and room areas, in game units.</summary>
    public sealed class ReplayMap
    {
        [JsonPropertyName("walls")] public List<float[]> Walls { get; set; } = new List<float[]>();
        [JsonPropertyName("rooms")] public List<ReplayRoom> Rooms { get; set; } = new List<ReplayRoom>();
        [JsonPropertyName("vents")] public List<float[]> Vents { get; set; } = new List<float[]>();
    }

    public sealed class ReplayRoom
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        /// <summary>Outline as x0,y0,x1,y1…</summary>
        [JsonPropertyName("area")] public float[] Area { get; set; } = Array.Empty<float>();
    }

    /// <summary>
    /// Records a game for the replay viewer: every player's position about ten times a second,
    /// the map's walls and rooms, and the game's timeline. Saved gzipped next to the game.
    /// </summary>
    public sealed class ReplayRecorder
    {
        public const double Interval = 0.1;
        private readonly List<double[]> _frames = new List<double[]>();
        private readonly Dictionary<byte, int> _index = new Dictionary<byte, int>();
        private readonly List<GamePlayer> _players;
        private double _last = double.NegativeInfinity;

        public ReplayRecorder(GameRecord game)
        {
            _players = game.Players;
            for (int i = 0; i < _players.Count; i++) _index[_players[i].PlayerId] = i;
        }

        public ReplayMap? Map { get; set; }
        public int Frames => _frames.Count;

        /// <summary>Adds a frame at <paramref name="seconds"/> into the game (skipped if too soon after the last).</summary>
        public void Record(double seconds, IEnumerable<ReplayPosition> positions)
        {
            if (seconds - _last < Interval * 0.9) return;
            _last = seconds;
            var frame = new double[1 + 3 * _players.Count];
            frame[0] = Math.Round(seconds, 2);
            for (int i = 0; i < _players.Count; i++) frame[1 + 3 * i + 2] = 8;   // 8: not seen this frame
            foreach (var p in positions)
            {
                if (!_index.TryGetValue(p.PlayerId, out int i)) continue;
                frame[1 + 3 * i] = Math.Round(p.X, 2);
                frame[1 + 3 * i + 1] = Math.Round(p.Y, 2);
                frame[1 + 3 * i + 2] = p.State;
            }
            _frames.Add(frame);
        }

        /// <summary>The replay file: gzipped JSON the viewer reads.</summary>
        public byte[] Build(GameRecord game)
        {
            var replay = new
            {
                v = 1,
                id = game.Id,
                name = game.Name,
                tournament = game.Tournament,
                round = game.Round,
                map = game.Map,
                winner = game.Winner,
                endReason = game.EndReason,
                voided = game.Voided,
                players = game.Players.Select(p => new { id = p.PlayerId, name = p.Name, color = p.ColorId, impostor = p.IsImpostor, points = p.Points }),
                geometry = Map,
                events = game.Timeline.Select(e => new { t = e.AtSeconds, kind = e.Kind, text = e.Text }),
                frames = _frames,
            };
            using var output = new MemoryStream();
            using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(replay);
                gz.Write(json, 0, json.Length);
            }
            return output.ToArray();
        }

        public static string FileNameFor(GameRecord game) => $"tt-replay-{game.Id}.json.gz";
    }
}
