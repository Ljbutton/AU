using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>Replay settings (the "replay" part of obs.json).</summary>
    public sealed class ReplaySettings
    {
        public bool On { get; set; } = true;
        /// <summary>How much of each lobby OBS keeps in memory (Source Record's replay buffer).</summary>
        public int BufferSeconds { get; set; } = 30;
        /// <summary>A clip runs from this long before the play…</summary>
        public double PreSeconds { get; set; } = 8;
        /// <summary>…to this long after it.</summary>
        public double PostSeconds { get; set; } = 2;
        /// <summary>Save clips by themselves (the plays listed in AutoSave).</summary>
        public bool OnEveryKill { get; set; } = true;
        /// <summary>Which plays get a clip by themselves: kills, ejections, the game's end, witnessed kills, meetings.</summary>
        public List<string> AutoSave { get; set; } = new List<string> { "kill", "winningKill", "eject", "gameEnd", "witnessedKill", "meeting" };
        /// <summary>ffmpeg for montages (empty: The Button's own copy, or one on the PATH).</summary>
        public string Ffmpeg { get; set; } = "";
        /// <summary>Montage clips: this long before each moment and after it.</summary>
        public double MontageBefore { get; set; } = 3.5;
        public double MontageAfter { get; set; } = 2.0;
        /// <summary>About how long the end-of-game montage is, and the end-of-round one.</summary>
        public double GameMontageSeconds { get; set; } = 45;
        public double RoundMontageSeconds { get; set; } = 120;
        /// <summary>Build montages by themselves at the end of each game and each round.</summary>
        public bool AutoMontages { get; set; } = true;
        /// <summary>The closest the crop zooms in (1 = the whole screen).</summary>
        public double MaxZoom { get; set; } = 2.5;
        /// <summary>The slow push-in from the wide shot takes this long.</summary>
        public double PushInSeconds { get; set; } = 1.8;
        /// <summary>Room left around the players in the crop, as a share of the screen.</summary>
        public double Margin { get; set; } = 0.12;
        /// <summary>If the video arrives later than the data, by this much (milliseconds): shifts the crop to match.</summary>
        public int VideoDelayMs { get; set; }
        /// <summary>Where OBS saves the clips. Empty: Videos\TT Replays.</summary>
        public string Folder { get; set; } = "";
        public string Format { get; set; } = "mp4";
        public string Scene { get; set; } = "TT Replay";
        public string ClipSource { get; set; } = "TT Replay Clip";
        public string TagSource { get; set; } = "TT Replay Tag";
        public string FilterName { get; set; } = "TT Replay";

        /// <summary>Keys for the replay controls (JavaScript key names, e.g. "Space", "ArrowLeft", "KeyJ"). Change them here or in the Caster tab.</summary>
        public Dictionary<string, string> Hotkeys { get; set; } = DefaultHotkeys();

        public static Dictionary<string, string> DefaultHotkeys() => new Dictionary<string, string>
        {
            ["playPause"] = "Space",
            ["back"] = "ArrowLeft",
            ["forward"] = "ArrowRight",
            ["frameBack"] = "Comma",
            ["frameForward"] = "Period",
            ["restart"] = "KeyR",
            ["zoomIn"] = "Equal",
            ["zoomOut"] = "Minus",
            ["panLeft"] = "KeyA",
            ["panRight"] = "KeyD",
            ["panUp"] = "KeyW",
            ["panDown"] = "KeyS",
            ["follow"] = "KeyF",
            ["live"] = "KeyL",
            // Spectator view of the lobby on stream (slot 1).
            ["specLit"] = "KeyM",
            ["specVision"] = "KeyV",
            ["specReport"] = "KeyX",
            ["specEye"] = "KeyE",
            // Layouts.
            ["grid"] = "KeyG",
            ["intermission"] = "KeyI",
            // Graphics.
            ["alertsPause"] = "KeyB",
            ["playerCard"] = "KeyC",
            ["voiceMute"] = "KeyU",
        };
    }

    /// <summary>One moment of a lobby's screen: each player's position on it (0–1, from the top left).</summary>
    public sealed class TrackSample
    {
        public DateTime At { get; set; }
        public Dictionary<int, (double X, double Y, bool Vent)> P { get; set; } = new Dictionary<int, (double, double, bool)>();
    }

    /// <summary>
    /// Every lobby's recent screen positions, on the caster's clock. The host's clock may differ,
    /// so each lobby's offset is learned from when its messages arrive (the quickest arrival wins).
    /// </summary>
    public sealed class Tracks
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, List<TrackSample>> _byLobby = new Dictionary<string, List<TrackSample>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Queue<double>> _offsets = new Dictionary<string, Queue<double>>(StringComparer.OrdinalIgnoreCase);
        public TimeSpan Keep { get; set; } = TimeSpan.FromSeconds(90);

        /// <summary>Notes when a message from a lobby, stamped <paramref name="hostMs"/>, arrived.</summary>
        public void Arrived(string lobby, long hostMs, DateTime now)
        {
            if (hostMs <= 0) return;
            double offset = (now - DateTimeOffset.FromUnixTimeMilliseconds(hostMs).UtcDateTime).TotalMilliseconds;
            lock (_lock)
            {
                if (!_offsets.TryGetValue(lobby, out var q)) _offsets[lobby] = q = new Queue<double>();
                q.Enqueue(offset);
                while (q.Count > 60) q.Dequeue();
            }
        }

        /// <summary>A host time on the caster's clock.</summary>
        public DateTime ToCaster(string lobby, long hostMs)
        {
            double offset;
            lock (_lock) offset = _offsets.TryGetValue(lobby, out var q) && q.Count > 0 ? q.Min() : 0;
            return DateTimeOffset.FromUnixTimeMilliseconds(hostMs).UtcDateTime.AddMilliseconds(offset);
        }

        /// <summary>A "track" message from a lobby.</summary>
        public void Add(string lobby, JsonElement msg, DateTime now)
        {
            if (!msg.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array) return;
            var list = new List<TrackSample>();
            foreach (var s in samples.EnumerateArray())
            {
                if (!s.TryGetProperty("t", out var t) || !s.TryGetProperty("p", out var p)) continue;
                var sample = new TrackSample { At = ToCaster(lobby, t.GetInt64()) };
                foreach (var e in p.EnumerateArray())
                    if (e.GetArrayLength() >= 3)
                        sample.P[e[0].GetInt32()] = (e[1].GetInt32() / 1000.0, e[2].GetInt32() / 1000.0, e.GetArrayLength() > 3 && e[3].GetInt32() == 1);
                list.Add(sample);
            }
            lock (_lock)
            {
                if (!_byLobby.TryGetValue(lobby, out var all)) _byLobby[lobby] = all = new List<TrackSample>();
                all.AddRange(list);
                all.Sort((a, b) => a.At.CompareTo(b.At));
                all.RemoveAll(x => now - x.At > Keep);
            }
        }

        public List<TrackSample> Between(string lobby, DateTime from, DateTime to)
        {
            lock (_lock)
                return _byLobby.TryGetValue(lobby, out var all) ? all.Where(x => x.At >= from && x.At <= to).ToList() : new List<TrackSample>();
        }
    }

    /// <summary>A saved replay clip and what it's about.</summary>
    public sealed class Clip
    {
        public string Id { get; set; } = "";
        public string Lobby { get; set; } = "";
        public string Title { get; set; } = "";
        /// <summary>The play it's of (kill, eject, gameEnd, meeting, witnessedKill…), the game and round.</summary>
        public string Rule { get; set; } = "";
        public string? Game { get; set; }
        public int Round { get; set; }
        public string? Room { get; set; }
        /// <summary>The key player (the card shows them): killer, reporter, caller, ejected.</summary>
        public string? KeyPlayer { get; set; }
        public int? KeyPlayerId { get; set; }
        /// <summary>A still for the Moments library, and whether a custom montage has used it.</summary>
        public string? Thumbnail { get; set; }
        public bool Used { get; set; }
        public string? CardId { get; set; }
        /// <summary>When the play happened (caster's clock).</summary>
        public DateTime EventAt { get; set; }
        /// <summary>When OBS saved the clip: its last frame is about now.</summary>
        public DateTime? SavedAt { get; set; }
        public string? File { get; set; }
        /// <summary>The clip's length once OBS has loaded it.</summary>
        public double? Duration { get; set; }
        public double Pre { get; set; }
        public double Post { get; set; }
        /// <summary>Who the crop keeps in frame (killer and victim; or whoever the play is about).</summary>
        public List<int> Focus { get; set; } = new List<int>();
        /// <summary>Fixed points to keep in frame from a time on (the body, where the kill was).</summary>
        public List<(DateTime From, double X, double Y)> Marks { get; set; } = new List<(DateTime, double, double)>();
        public List<TrackSample> Samples { get; set; } = new List<TrackSample>();
        /// <summary>"saving", "ready", or "failed".</summary>
        public string State { get; set; } = "saving";
        public string? Problem { get; set; }

        /// <summary>A moment of the clip (seconds from its start) on the caster's clock.</summary>
        public DateTime TimeAt(double clipSeconds, int videoDelayMs) =>
            (SavedAt ?? EventAt).AddSeconds(-(Duration ?? 0) + clipSeconds).AddMilliseconds(-videoDelayMs);

        /// <summary>Where in the clip the replay starts (Pre seconds before the play), in seconds.</summary>
        public double StartAt(int videoDelayMs)
        {
            if (Duration == null || SavedAt == null) return 0;
            double fromEnd = (SavedAt.Value - EventAt).TotalSeconds + Pre + videoDelayMs / 1000.0;
            return Math.Max(0, Math.Min(Duration.Value, Duration.Value - fromEnd));
        }
    }

    /// <summary>Where the replay crop looks at one moment: centre (0–1 of the screen) and zoom (1 = whole screen).</summary>
    public readonly struct View
    {
        public readonly double X, Y, Zoom;
        public View(double x, double y, double zoom) { X = x; Y = y; Zoom = zoom; }
        public override string ToString() => $"({X:0.###}, {Y:0.###}) ×{Zoom:0.##}";
    }

    /// <summary>The crop and zoom maths for replays.</summary>
    public static class Framing
    {
        /// <summary>
        /// The tightest view (up to <paramref name="maxZoom"/>) that keeps every point in frame with
        /// <paramref name="margin"/> around them, kept inside the screen.
        /// </summary>
        public static View Fit(IReadOnlyCollection<(double X, double Y)> points, double maxZoom, double margin)
        {
            if (points.Count == 0) return new View(0.5, 0.5, 1);
            double minX = points.Min(p => p.X) - margin, maxX = points.Max(p => p.X) + margin;
            double minY = points.Min(p => p.Y) - margin, maxY = points.Max(p => p.Y) + margin;
            double need = Math.Max(maxX - minX, maxY - minY);
            double zoom = Math.Max(1, Math.Min(maxZoom, 1 / Math.Max(need, 1e-6)));
            return Clamp(new View((minX + maxX) / 2, (minY + maxY) / 2, zoom));
        }

        /// <summary>Keeps the view on the screen (no black edges).</summary>
        public static View Clamp(View v)
        {
            double z = Math.Max(1, v.Zoom), half = 0.5 / z;
            return new View(Math.Max(half, Math.Min(1 - half, v.X)), Math.Max(half, Math.Min(1 - half, v.Y)), z);
        }

        /// <summary>Where the focus players are around <paramref name="at"/> (averaged over a short window so the crop doesn't shake).</summary>
        public static List<(double X, double Y)> PointsAt(Clip clip, DateTime at, double window = 0.4)
        {
            var near = clip.Samples.Where(s => Math.Abs((s.At - at).TotalSeconds) <= window).ToList();
            if (near.Count == 0)
            {
                var closest = clip.Samples.OrderBy(s => Math.Abs((s.At - at).TotalSeconds)).FirstOrDefault();
                if (closest != null && Math.Abs((closest.At - at).TotalSeconds) <= 2) near.Add(closest);
            }
            var points = new List<(double X, double Y)>();
            foreach (int id in clip.Focus)
            {
                var seen = near.Where(s => s.P.ContainsKey(id) && !s.P[id].Vent).Select(s => s.P[id]).ToList();
                if (seen.Count == 0) continue;
                var p = (seen.Average(x => x.X), seen.Average(x => x.Y));
                // Far off screen means they're not in this shot: don't chase them.
                if (p.Item1 > -0.25 && p.Item1 < 1.25 && p.Item2 > -0.25 && p.Item2 < 1.25) points.Add(p);
            }
            foreach (var m in clip.Marks.Where(m => at >= m.From)) points.Add((m.X, m.Y));
            return points.Select(p => (Math.Max(0, Math.Min(1, p.X)), Math.Max(0, Math.Min(1, p.Y)))).ToList();
        }

        /// <summary>
        /// The replay's view at a moment: the crop that keeps the focus players in frame, eased in
        /// from the wide shot over the push-in, with any manual zoom and pan on top.
        /// </summary>
        public static View At(Clip clip, ReplaySettings s, double clipSeconds, double playedSeconds, double zoomBy = 1, double panX = 0, double panY = 0, bool follow = true)
        {
            var target = follow ? Fit(PointsAt(clip, clip.TimeAt(clipSeconds, s.VideoDelayMs)), s.MaxZoom, s.Margin) : new View(0.5, 0.5, 1);
            double k = s.PushInSeconds <= 0 ? 1 : Ease(Math.Max(0, Math.Min(1, playedSeconds / s.PushInSeconds)));
            double zoom = 1 + (target.Zoom - 1) * k;
            double x = 0.5 + (target.X - 0.5) * k, y = 0.5 + (target.Y - 0.5) * k;
            return Clamp(new View(x + panX, y + panY, Math.Max(1, Math.Min(Math.Max(s.MaxZoom, 1) * 2, zoom * zoomBy))));
        }

        /// <summary>Slow in, slow out.</summary>
        public static double Ease(double t) => t * t * (3 - 2 * t);

        /// <summary>The media source's place on the canvas for a view: scaled up, with the view's centre in the middle.</summary>
        public static (double X, double Y, double W, double H) Place(View v, double canvasW, double canvasH)
        {
            double w = canvasW * v.Zoom, h = canvasH * v.Zoom;
            return (canvasW / 2 - v.X * w, canvasH / 2 - v.Y * h, w, h);
        }
    }
}
