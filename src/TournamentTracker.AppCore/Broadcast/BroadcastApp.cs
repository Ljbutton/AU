using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>The tournament's look on stream: colours, fonts and logo.</summary>
    public sealed class BroadcastTheme
    {
        public string TournamentName { get; set; } = "";
        public string Primary { get; set; } = "#1fa143";
        public string Accent { get; set; } = "#ffc15a";
        public string Danger { get; set; } = "#ff2d55";
        public string Text { get; set; } = "#ffffff";
        public string Panel { get; set; } = "rgba(8,10,14,0.82)";
        /// <summary>Font for text and for headings: a Google Fonts family name (loaded from Google), or the built-in Rajdhani / Teko.</summary>
        public string Font { get; set; } = "Rajdhani";
        public string HeadingFont { get; set; } = "Teko";
        /// <summary>The tournament logo: a file on this PC or a web link (shown on the grid's empty tiles and the standings).</summary>
        public string Logo { get; set; } = "";
    }

    /// <summary>
    /// The on-stream graphics: the theme, and which elements are on (switched from the caster tab).
    /// In broadcast.json next to The Button's settings.
    /// </summary>
    public sealed class BroadcastSettings
    {
        public const string FileName = "broadcast.json";

        public BroadcastTheme Theme { get; set; } = new BroadcastTheme();
        public Dictionary<string, bool> Elements { get; set; } = DefaultElements();

        public static Dictionary<string, bool> DefaultElements() => new Dictionary<string, bool>
        {
            ["lobbyLabels"] = true,       // the lobby's name on each feed
            ["gridTiles"] = true,         // Part 12: grid tiles' status line, pulsing border, logo in empty tiles
            ["statusBar"] = true,         // Part 9: one cell per lobby along the bottom
            ["impostorTags"] = true,      // Part 10: "IMPOSTORS: …" on each feed
            ["sabotage"] = true,          // Part 10: reactor/O2 countdown, lights/comms icon
            ["standings"] = false,        // Part 14: the standings table
            ["pointsOnTheLine"] = false,  // Part 14: what each outcome does to the standings
            ["standingsChange"] = true,   // Part 14: arrows after each game
            ["storyline"] = false,        // Part 13: a note as a lower third
        };

        public static readonly Dictionary<string, string> ElementNames = new Dictionary<string, string>
        {
            ["lobbyLabels"] = "Lobby labels",
            ["gridTiles"] = "Grid tiles",
            ["statusBar"] = "Status bar",
            ["impostorTags"] = "Impostor tags",
            ["sabotage"] = "Sabotage countdown",
            ["standings"] = "Standings",
            ["pointsOnTheLine"] = "Points on the line",
            ["standingsChange"] = "Standings change",
            ["storyline"] = "Storyline note",
        };

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        };

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        public static BroadcastSettings Parse(string json)
        {
            var s = JsonSerializer.Deserialize<BroadcastSettings>(json, Json) ?? new BroadcastSettings();
            s.Theme ??= new BroadcastTheme();
            var el = new Dictionary<string, bool>(s.Elements ?? new Dictionary<string, bool>(), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in DefaultElements()) if (!el.ContainsKey(kv.Key)) el[kv.Key] = kv.Value;
            s.Elements = el;
            return s;
        }
    }

    /// <summary>broadcast.json: written with defaults if missing, re-read when it changes; switches are saved back.</summary>
    public sealed class BroadcastSettingsFile
    {
        private readonly string? _path;
        private DateTime _stamp, _nextCheck;
        public BroadcastSettings Current { get; private set; } = new BroadcastSettings();
        public string? Problem { get; private set; }
        public string? Path => _path;

        public BroadcastSettingsFile(string? path)
        {
            _path = path;
            Refresh(force: true);
        }

        public BroadcastSettings Refresh(bool force = false)
        {
            if (_path == null) return Current;
            var now = DateTime.UtcNow;
            if (!force && now < _nextCheck) return Current;
            _nextCheck = now.AddSeconds(2);
            try
            {
                if (!File.Exists(_path)) Save();
                var stamp = File.GetLastWriteTimeUtc(_path);
                if (stamp == _stamp) return Current;
                _stamp = stamp;
                Current = BroadcastSettings.Parse(File.ReadAllText(_path));
                Problem = null;
            }
            catch (Exception e) { Problem = $"{BroadcastSettings.FileName} couldn't be read ({e.Message}); using the last good settings."; }
            return Current;
        }

        public void Set(string element, bool on)
        {
            Current.Elements[element] = on;
            Save();
        }

        private void Save()
        {
            if (_path == null) return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, Current.ToJson());
            _stamp = File.GetLastWriteTimeUtc(_path);
        }
    }

    /// <summary>
    /// The on-stream graphics app (/broadcast on the caster port, a transparent 1920×1080 page added to
    /// every TT scene in OBS): this builds what it draws from, including where each lobby's picture
    /// is on screen right now, so per-lobby graphics land on the right feed in every layout.
    /// </summary>
    public sealed class BroadcastApp
    {
        private readonly CasterDesk _desk;
        private readonly Func<ObsDirector?> _obs;
        public BroadcastSettingsFile Settings { get; }
        /// <summary>Extra pieces later parts add to the state (standings, notes, sponsors…), by name.</summary>
        public Dictionary<string, Func<object?>> Extras { get; } = new Dictionary<string, Func<object?>>();

        public BroadcastApp(CasterDesk desk, Func<ObsDirector?> obs, string? settingsPath)
        {
            _desk = desk;
            _obs = obs;
            Settings = new BroadcastSettingsFile(settingsPath);
        }

        /// <summary>The canvas and where each lobby is on it, for the layout on stream.</summary>
        public (double W, double H, List<(string? Lobby, Box Box)> Slots) Layout(OnAir air)
        {
            var obs = _obs();
            double w = obs?.Connected == true ? obs.Width : 1920, h = obs?.Connected == true ? obs.Height : 1080;
            double gap = obs?.Settings.Gap ?? 8;
            var layout = air.Layout;
            if (layout == "none" || layout == "replay") return (w, h, new List<(string?, Box)>());
            var boxes = air.Boxes ?? ObsDirector.Slots(layout, w, h, layout == "full" ? 0 : gap, air.Slots.Count);
            var slots = new List<(string?, Box)>();
            for (int i = 0; i < boxes.Count; i++) slots.Add((i < air.Slots.Count ? air.Slots[i] : null, boxes[i]));
            return (w, h, slots);
        }

        public object State()
        {
            var s = Settings.Refresh();
            var air = _desk.OnAir;
            var (w, h, slots) = Layout(air);
            var ranking = _desk.Board.Ranking();
            var lobbies = new Dictionary<string, object>();
            int number = 0;
            foreach (var r in ranking.OrderBy(r => r.Lobby, StringComparer.OrdinalIgnoreCase))
            {
                number++;
                var live = _desk.Board.Lobby(r.Lobby);
                string Who(LobbyPlayer p) => live == null ? p.Name : _desk.Board.DisplayName(live, p.Id, p.Name);
                lobbies[r.Lobby] = new
                {
                    number,
                    online = r.Online,
                    phase = r.Phase,
                    crew = r.Crew, imps = r.Imps, tasks = r.TaskPct,
                    tier = r.Tier, score = r.Score,
                    map = r.Map,
                    clock = r.Clock,
                    sabotage = r.Sabotage == null ? null : new { system = r.Sabotage, critical = r.SabotageCritical, left = r.SabotageLeft, fixing = r.SabotageFixing },
                    impostors = r.People.Where(p => p.Imp == true).Select(p => new { name = Who(p), color = p.Color, dead = p.Dead == true }).ToList(),
                    game = r.Game,
                    round = r.Round,
                };
            }
            return new
            {
                canvas = new { w, h },
                layout = air.Layout,
                scene = air.Scene,
                slots = slots.Select((x, i) => new { lobby = x.Lobby, slot = i + 1, x = x.Box.X, y = x.Box.Y, w = x.Box.W, h = x.Box.H }).ToList(),
                theme = s.Theme,
                logo = string.IsNullOrEmpty(s.Theme.Logo) ? null : s.Theme.Logo.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? s.Theme.Logo : "/broadcast/logo",
                elements = s.Elements,
                lobbies,
                extras = Extras.ToDictionary(kv => kv.Key, kv => { try { return kv.Value(); } catch (Exception) { return null; } }),
            };
        }

        /// <summary>The logo file, when the theme names one on this PC.</summary>
        public (string Type, byte[] Body)? Logo()
        {
            string path = Settings.Current.Theme.Logo;
            if (string.IsNullOrEmpty(path) || path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            string type = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".svg" => "image/svg+xml", ".gif" => "image/gif", ".webp" => "image/webp", _ => "application/octet-stream" };
            return (type, File.ReadAllBytes(path));
        }
    }
}
