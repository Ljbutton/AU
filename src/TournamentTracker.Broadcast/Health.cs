using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>When a lobby counts as degraded or down, and what happens then (health.json).</summary>
    public sealed class HealthSettings
    {
        public const string FileName = "health.json";
        /// <summary>No data (the referee's once-a-second snapshot) for this long: yellow; this long: red.</summary>
        public double DataYellowSeconds { get; set; } = 3;
        public double DataRedSeconds { get; set; } = 10;
        /// <summary>The referee reports no video for this long: red ("video lost").</summary>
        public double VideoRedSeconds { get; set; } = 3;
        /// <summary>The lobby's sound silent for this long (while it's sending voice): yellow.</summary>
        public double AudioSilentSeconds { get; set; } = 120;
        /// <summary>Data arriving this much later than usual: yellow ("high lag").</summary>
        public double LagYellowMs { get; set; } = 2000;
        /// <summary>Switch away from an on-air lobby that goes red, even when switching by hand.</summary>
        public bool AutoSwitch { get; set; } = true;
        /// <summary>A lobby down this long leaves the grid (the rest close up); until then its tile says RECONNECTING.</summary>
        public double ReflowAfterSeconds { get; set; } = 45;
        /// <summary>Nothing left to show: the "be right back" slate (or intermission between games).</summary>
        public bool Slate { get; set; } = true;
        /// <summary>Data this much older than now (sent again after a drop) still counts for stats, not as live.</summary>
        public double StaleSeconds { get; set; } = 8;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        public static HealthSettings Load(string? path)
        {
            try
            {
                if (path == null) return new HealthSettings();
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(new HealthSettings(), Json));
                }
                return JsonSerializer.Deserialize<HealthSettings>(File.ReadAllText(path), Json) ?? new HealthSettings();
            }
            catch (Exception) { return new HealthSettings(); }
        }

        public void Save(string? path)
        {
            if (path == null) return;
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(this, Json)); }
            catch (Exception) { }
        }
    }

    /// <summary>One lobby's health now: green (all good), yellow (degraded) or red (down), and why in plain words.</summary>
    public sealed class HealthStatus
    {
        public string Lobby { get; set; } = "";
        public string Level { get; set; } = "green";
        public List<string> Problems { get; set; } = new List<string>();
        public double? DataAge { get; set; }
        /// <summary>"ok", "lost" or "unknown" (the referee's Button hasn't said).</summary>
        public string Video { get; set; } = "unknown";
        /// <summary>"ok", "silent", "off" (not sending voice) or "unknown".</summary>
        public string Audio { get; set; } = "unknown";
        public double? LagMs { get; set; }
        /// <summary>Red since (for the grid reflow and the panel).</summary>
        public DateTime? DownSince { get; set; }
    }

    /// <summary>
    /// Part 22: each lobby's health from what arrives: data (any message, the snapshot every second),
    /// video (the referee's send page reports whether its screen share is sending frames), audio (the
    /// lobby voice levels from Part 11) and lag (how much later than usual the data arrives).
    /// </summary>
    public sealed class LobbyHealth
    {
        private sealed class Track
        {
            public DateTime LastData;
            public double? LastDelayMs;
            public readonly List<(DateTime At, double Ms)> Delays = new List<(DateTime, double)>();
            public string Video = "unknown";
            public DateTime VideoAt, VideoBadSince;
            public bool VoiceOn;
            public DateTime LastLoud, VoiceAt;
            public string? AudioProblem;
            public DateTime? DownSince;
        }

        private readonly Func<DateTime> _clock;
        private readonly Func<HealthSettings> _settings;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Track> _lobbies = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);

        public LobbyHealth(Func<DateTime> clock, Func<HealthSettings> settings)
        {
            _clock = clock;
            _settings = settings;
        }

        private Track Get(string lobby)
        {
            if (!_lobbies.TryGetValue(lobby, out var t)) _lobbies[lobby] = t = new Track { LastLoud = _clock() };
            return t;
        }

        /// <summary>
        /// Anything arrived from the lobby; <paramref name="hostMs"/> is when the referee's PC sent it (its
        /// clock). Sent again after a drop (<paramref name="resent"/>): the link is up, but its delay isn't lag.
        /// </summary>
        public void Data(string lobby, long? hostMs, bool resent = false)
        {
            var now = _clock();
            lock (_lock)
            {
                var t = Get(lobby);
                t.LastData = now;
                if (hostMs is { } ms && !resent)
                {
                    // Delay = arrival − sent, on two clocks; lag is how much more than the lobby's best delay lately.
                    double delay = new DateTimeOffset(now).ToUnixTimeMilliseconds() - ms;
                    t.LastDelayMs = delay;
                    t.Delays.Add((now, delay));
                    t.Delays.RemoveAll(d => (now - d.At).TotalSeconds > 120);
                }
            }
        }

        /// <summary>The referee's send page says whether its screen share is sending video ("ok", "lost", "unknown").</summary>
        public void Video(string lobby, string state)
        {
            var now = _clock();
            lock (_lock)
            {
                var t = Get(lobby);
                if (state == "lost" && t.Video != "lost") t.VideoBadSince = now;
                t.Video = state;
                t.VideoAt = now;
            }
        }

        /// <summary>The lobby voice report (Part 11): sending or not, levels, any problem.</summary>
        public void Audio(string lobby, bool sending, double voiceDb, double gameDb, string? problem)
        {
            var now = _clock();
            lock (_lock)
            {
                var t = Get(lobby);
                if (sending && !t.VoiceOn) t.LastLoud = now;              // just started: give it time
                t.VoiceOn = sending;
                t.VoiceAt = now;
                t.AudioProblem = problem;
                if (Math.Max(voiceDb, gameDb) > -50) t.LastLoud = now;
            }
        }

        public void Forget(string lobby) { lock (_lock) _lobbies.Remove(lobby); }

        /// <summary>The lobby's last data arrived this long ago (null: never).</summary>
        public double? DataAge(string lobby) { lock (_lock) return _lobbies.TryGetValue(lobby, out var t) ? (_clock() - t.LastData).TotalSeconds : null; }

        public List<string> Lobbies { get { lock (_lock) return _lobbies.Keys.ToList(); } }

        public HealthStatus Status(string lobby)
        {
            var s = _settings();
            var now = _clock();
            lock (_lock)
            {
                var st = new HealthStatus { Lobby = lobby };
                if (!_lobbies.TryGetValue(lobby, out var t)) { st.Level = "red"; st.Problems.Add("never connected"); return st; }
                double age = (now - t.LastData).TotalSeconds;
                st.DataAge = Math.Round(age, 1);
                int level = 0;
                void Bad(int l, string why) { level = Math.Max(level, l); st.Problems.Add(why); }
                if (age >= s.DataRedSeconds) Bad(2, $"no data for {(int)age}s");
                else if (age >= s.DataYellowSeconds) Bad(1, $"no data for {(int)age}s");

                // Video: only what the referee's page last said, and only while it's still talking to us.
                bool videoFresh = (now - t.VideoAt).TotalSeconds < Math.Max(10, s.DataRedSeconds);
                st.Video = videoFresh ? t.Video : "unknown";
                if (videoFresh && t.Video == "lost" && (now - t.VideoBadSince).TotalSeconds >= s.VideoRedSeconds) Bad(2, "video lost");

                bool voiceFresh = (now - t.VoiceAt).TotalSeconds < Math.Max(10, s.DataRedSeconds);
                if (!voiceFresh) st.Audio = "unknown";
                else if (!t.VoiceOn) st.Audio = "off";
                else if ((now - t.LastLoud).TotalSeconds >= s.AudioSilentSeconds) { st.Audio = "silent"; Bad(1, "audio silent"); }
                else st.Audio = "ok";
                if (voiceFresh && t.AudioProblem != null) Bad(1, "audio: " + t.AudioProblem);

                if (t.LastDelayMs is { } d && t.Delays.Count > 0)
                {
                    double lag = Math.Max(0, d - t.Delays.Min(x => x.Ms));
                    st.LagMs = Math.Round(lag);
                    // So late it isn't live any more: down; late: degraded.
                    if (lag >= s.StaleSeconds * 1000 && age < s.DataYellowSeconds) Bad(2, $"data {lag / 1000:0}s late");
                    else if (lag >= s.LagYellowMs && age < s.DataYellowSeconds) Bad(1, $"high lag ({lag / 1000:0.#}s)");
                }

                st.Level = level == 2 ? "red" : level == 1 ? "yellow" : "green";
                if (level == 2) t.DownSince ??= now; else t.DownSince = null;
                st.DownSince = t.DownSince;
                return st;
            }
        }
    }
}
