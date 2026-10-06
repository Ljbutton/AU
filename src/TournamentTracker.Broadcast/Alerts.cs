using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>Which off-screen alerts show, for how long, and how many at once (alerts.json).</summary>
    public sealed class AlertSettings
    {
        public bool Paused { get; set; }
        /// <summary>kill, win, report, button.</summary>
        public Dictionary<string, bool> Types { get; set; } = new Dictionary<string, bool> { ["kill"] = true, ["win"] = true, ["report"] = true, ["button"] = true };
        public double Seconds { get; set; } = 4;
        public int Max { get; set; } = 3;
    }

    /// <summary>One banner: a lobby that isn't on screen, and what just happened there.</summary>
    public sealed class Alert
    {
        public long Id { get; set; }
        public string Lobby { get; set; } = "";
        public string Kind { get; set; } = "";
        /// <summary>With name tags ("[[colour|Name]]").</summary>
        public string Text { get; set; } = "";
        public int Count { get; set; } = 1;
        public DateTime Queued { get; set; }
        public DateTime? Until { get; set; }
    }

    /// <summary>
    /// Part 18: banners for kills, wins, body reports and emergency buttons in lobbies that aren't on
    /// screen. About four seconds each, at most three at once, the rest wait their turn; a second
    /// play in the same lobby joins its banner (×2) instead of adding one. Each kind can be switched
    /// off, and all of them paused (a key in the caster tab).
    /// </summary>
    public sealed class AlertQueue
    {
        public static readonly string[] Kinds = { "kill", "win", "report", "button" };
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly Func<DateTime> _clock;
        private readonly string? _path;
        private readonly object _lock = new object();
        private readonly List<Alert> _list = new List<Alert>();
        private long _seq;

        public AlertSettings Settings { get; private set; } = new AlertSettings();

        public AlertQueue(Func<DateTime> clock, string? path)
        {
            _clock = clock;
            _path = path;
            try
            {
                if (path != null && File.Exists(path)) Settings = JsonSerializer.Deserialize<AlertSettings>(File.ReadAllText(path), Json) ?? new AlertSettings();
                foreach (var k in Kinds) if (!Settings.Types.ContainsKey(k)) Settings.Types[k] = true;
            }
            catch (Exception) { Settings = new AlertSettings(); }
        }

        public void Save()
        {
            try { if (_path != null) File.WriteAllText(_path, JsonSerializer.Serialize(Settings, Json)); } catch (Exception) { }
        }

        public void SetPaused(bool paused)
        {
            Settings.Paused = paused;
            if (paused) lock (_lock) _list.Clear();
            Save();
        }

        public void SetType(string kind, bool on)
        {
            Settings.Types[kind] = on;
            if (!on) lock (_lock) _list.RemoveAll(a => a.Kind == kind);
            Save();
        }

        /// <summary>The alert kind for a play, or null: kills, the game's end, a reported body, the emergency button.</summary>
        public static string? KindOf(string rule, string text)
        {
            switch (rule)
            {
                case "kill": case "winningKill": return "kill";
                case "gameEnd": return text.StartsWith("Game abandoned", StringComparison.Ordinal) ? null : "win";
                case "meeting":
                    string plain = NameTag.Plain(text);
                    return plain.Contains("emergency meeting", StringComparison.Ordinal) ? "button" : plain.Contains("reported", StringComparison.Ordinal) ? "report" : null;
                default: return null;
            }
        }

        /// <summary>A play in a lobby that isn't on screen.</summary>
        public void Push(string lobby, string kind, string text)
        {
            if (Settings.Paused || !Settings.Types.GetValueOrDefault(kind, true)) return;
            var now = _clock();
            lock (_lock)
            {
                var same = _list.FirstOrDefault(a => string.Equals(a.Lobby, lobby, StringComparison.OrdinalIgnoreCase));
                if (same != null)
                {
                    same.Count++;
                    same.Text = text;
                    // The win is the bigger news.
                    if (kind == "win" || same.Kind != "win") same.Kind = kind;
                    if (same.Until != null) same.Until = now.AddSeconds(Settings.Seconds);
                    return;
                }
                _list.Add(new Alert { Id = ++_seq, Lobby = lobby, Kind = kind, Text = text, Queued = now });
            }
        }

        /// <summary>A lobby came on screen: its banner goes.</summary>
        public void Seen(string lobby)
        {
            lock (_lock) _list.RemoveAll(a => string.Equals(a.Lobby, lobby, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The banners showing now (oldest first); the next in line come up as others finish.</summary>
        public List<Alert> Active()
        {
            var now = _clock();
            lock (_lock)
            {
                _list.RemoveAll(a => a.Until != null && a.Until <= now || a.Until == null && (now - a.Queued).TotalSeconds > 30);
                if (Settings.Paused) { _list.Clear(); return new List<Alert>(); }
                foreach (var a in _list.Where(a => a.Until == null).ToList())
                {
                    if (_list.Count(x => x.Until != null) >= Math.Max(1, Settings.Max)) break;
                    a.Until = now.AddSeconds(Settings.Seconds);
                }
                return _list.Where(a => a.Until != null).OrderBy(a => a.Id).Select(a => new Alert { Id = a.Id, Lobby = a.Lobby, Kind = a.Kind, Text = a.Text, Count = a.Count, Queued = a.Queued, Until = a.Until }).ToList();
            }
        }

        public int Waiting { get { lock (_lock) return _list.Count(a => a.Until == null); } }
    }
}
