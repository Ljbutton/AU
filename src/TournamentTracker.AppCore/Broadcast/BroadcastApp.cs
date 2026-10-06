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
            ["alerts"] = true,            // Part 18: banners for plays in lobbies that aren't on screen
            ["winCounter"] = true,        // Part 20: impostor wins v crewmate wins, in the corner
            ["playerCards"] = true,       // Part 21: a player's card as a lower third
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
            ["alerts"] = "Off-screen alerts",
            ["winCounter"] = "Win counter",
            ["playerCards"] = "Player cards",
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
            // Part 16: sponsors on the standings, the grid's empty tiles and the split-screen break.
            Extras["standingsSponsor"] = () =>
            {
                bool on = Settings.Current.Elements.GetValueOrDefault("standings");
                var sp = on ? desk.Sponsors.Current("standings") : null;
                if (sp == null) { desk.Sponsors.End("standings"); return null; }
                desk.Sponsors.Begin("standings", sp, "standings");
                return new { name = sp.Name, logo = SponsorBook.LogoUrl(sp), tagline = sp.Tagline };
            };
            Extras["sponsorTiles"] = () => desk.OnAir.Layout != "grid" ? null
                : desk.GridSponsors.ToDictionary(kv => kv.Key.ToString(), kv => new { name = kv.Value.Name, logo = SponsorBook.LogoUrl(kv.Value), tagline = kv.Value.Tagline });
            Extras["sponsorBreak"] = () => desk.OnAir.Layout == "break" && desk.Break is { } b
                ? new { name = b.Sponsor.Name, logo = SponsorBook.LogoUrl(b.Sponsor), video = SponsorBook.VideoUrl(b.Sponsor), tagline = b.Sponsor.Tagline, left = Math.Max(0, Math.Ceiling((b.Until - DateTime.UtcNow).TotalSeconds)), total = Math.Max(5, b.Sponsor.BreakSeconds) }
                : null;
        }

        /// <summary>The canvas and where each lobby is on it, for the layout on stream.</summary>
        public (double W, double H, List<(string? Lobby, Box Box)> Slots) Layout(OnAir air)
        {
            var obs = _obs();
            double w = obs?.Connected == true ? obs.Width : 1920, h = obs?.Connected == true ? obs.Height : 1080;
            double gap = obs?.Settings.Gap ?? 8;
            var layout = air.Layout;
            if (layout is "none" or "replay" or "intermission" or "slate") return (w, h, new List<(string?, Box)>());
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
            var now = DateTime.UtcNow;
            int round = _desk.Tables.CurrentRound;
            int number = 0;
            var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ranking.OrderBy(r => r.Lobby, StringComparer.OrdinalIgnoreCase))
            {
                number++;
                numbers[r.Lobby] = number;
                var live = _desk.Board.Lobby(r.Lobby);
                // Part 22: a lobby that's dropped keeps its last state, marked, and nothing of it counts down as if live.
                var health = _desk.Health.Lobbies.Contains(r.Lobby, StringComparer.OrdinalIgnoreCase) ? _desk.Health.Status(r.Lobby) : null;
                bool reconnecting = health != null && (health.Level == "red" || health.DataAge >= _desk.HealthConfig.DataYellowSeconds);
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
                    onTheLine = s.Elements.GetValueOrDefault("pointsOnTheLine") && live != null && Close(r)
                        ? _desk.Tables.OnTheLine(r.Lobby, r.Round > 0 ? r.Round : round, live.IfEnded, r.TaskPct ?? 0, r.People.Where(p => p.Key.Length > 0).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => (Who(g.First()), g.First().Color)))
                        : null,
                    game = r.Game,
                    round = r.Round,
                    health = health?.Level ?? "green",
                    reconnecting,
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
                standings = s.Elements.GetValueOrDefault("standings") ? Standings(round) : null,
                standingsChange = s.Elements.GetValueOrDefault("standingsChange") && _desk.Tables.Change != null && (now - _desk.Tables.ChangeAt).TotalSeconds < 15 ? _desk.Tables.Change : null,
                storyline = s.Elements.GetValueOrDefault("storyline") && _desk.ShownNote is { } note && (now - note.At).TotalSeconds < 12 ? note.Text : null,
                intermission = air.Layout is "intermission" or "slate" ? Intermission(round, air.Layout == "slate") : null,
                wins = s.Elements.GetValueOrDefault("winCounter") || air.Layout is "intermission" or "slate" ? new { impostors = _desk.Wins().Impostors, crew = _desk.Wins().Crew, scope = _desk.WinScope } : null,
                playerCard = s.Elements.GetValueOrDefault("playerCards") ? PlayerCard(air, w, h, slots) : null,
                alerts = s.Elements.GetValueOrDefault("alerts") ? _desk.Alerts.Active().Select(a => new { a.Id, a.Lobby, number = numbers.GetValueOrDefault(a.Lobby), a.Kind, a.Text, a.Count }).ToList() : null,
                extras = Extras.ToDictionary(kv => kv.Key, kv => { try { return kv.Value(); } catch (Exception) { return null; } }),
            };
        }

        /// <summary>The intermission screen: countdown to the next round, standings, notes taking turns, the montage up next.</summary>
        private object Intermission(int round, bool slate = false)
        {
            var notes = _desk.Storylines.Notes(new HashSet<string>(), 6);
            int turn = notes.Count == 0 ? 0 : (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond / 8 % notes.Count);
            return new
            {
                title = Settings.Current.Theme.TournamentName,
                // Part 22: "Technical difficulties: be right back" when every lobby is down.
                slate,
                nextRoundAt = _desk.NextRoundAt?.ToString("o"),
                nextRoundIn = _desk.NextRoundAt is { } at ? Math.Max(0, (int)Math.Ceiling((at - DateTime.UtcNow).TotalSeconds)) : (int?)null,
                round,
                standings = Standings(round),
                note = notes.Count > 0 ? notes[turn].Text : null,
                montage = _desk.QueuedMontage?.Title,
            };
        }

        /// <summary>The player card: what it says, and where (on its lobby's tile in a multi-view, else the bottom left).</summary>
        private object? PlayerCard(OnAir air, double w, double h, List<(string? Lobby, Box Box)> slots)
        {
            if (_desk.CardNow() is not { } c) return null;
            var data = _desk.PlayerCardData(c.Key, c.Lobby);
            if (data == null) return null;
            Box? box = null;
            if (air.Layout is "2up" or "4up" or "grid" && c.Lobby != null)
            {
                var slot = slots.FirstOrDefault(x => string.Equals(x.Lobby, c.Lobby, StringComparison.OrdinalIgnoreCase));
                if (slot.Lobby != null)
                {
                    if (slot.Box.W < _desk.PlayerCardMinTile) return null;
                    box = slot.Box;
                }
            }
            var b = box ?? new Box(0, 0, w, h);
            return new { id = c.Key + "|" + c.At.Ticks, data, x = b.X, y = b.Y, w = b.W, h = b.H, full = box == null };
        }

        /// <summary>Close to ending: worth showing what's on the line.</summary>
        private static bool Close(LobbyRank r) =>
            (r.Phase == "ingame" || r.Phase == "meeting") && r.Plays.Any(p => p.Rule is "oneKillFromWin" or "taskBar" or "finalPlayers" or "criticalSabotage" or "closeCounts");

        private object Standings(int round)
        {
            bool overall = _desk.StandingsScope == "overall" || round == 0;
            var rows = overall ? _desk.Tables.Overall() : _desk.Tables.Round(round);
            return new
            {
                title = overall ? "Tournament standings" : $"Round {round} standings",
                rows = rows.Take(10).Select(r => new { r.Rank, r.Name, r.Color, r.Points, r.Games, r.Wins, r.Advancing }).ToList(),
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
