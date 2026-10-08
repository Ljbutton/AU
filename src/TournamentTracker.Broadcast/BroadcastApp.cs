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
        /// <summary>How long each big graphic (a table, a player card, a note) stays before the next, in seconds.</summary>
        public double GraphicSeconds { get; set; } = 8;
        /// <summary>After a game, put the lobby's table up by itself (the old way) instead of asking on the Live desk.</summary>
        public bool AfterGameAuto { get; set; }
        /// <summary>Where Among Us shows the room code on the lobby screen, as a share of the game picture (covered by "Room code hidden").</summary>
        public CodeBox RoomCodeBox { get; set; } = new CodeBox();

        public sealed class CodeBox
        {
            public double X { get; set; } = 0.34;
            public double Y { get; set; } = 0.84;
            public double W { get; set; } = 0.32;
            public double H { get; set; } = 0.14;
        }

        public static Dictionary<string, bool> DefaultElements() => new Dictionary<string, bool>
        {
            ["lobbyLabels"] = true,       // the lobby's name on each feed
            ["gridTiles"] = true,         // Part 12: grid tiles' status line, pulsing border, logo in empty tiles
            ["statusBar"] = false,        // Part 9: one cell per lobby along the bottom (off: the top 3 and the stats ticker took its place)
            ["top3"] = true,              // the top 3 of a lobby's round, top left, taking turns through the lobbies
            ["ticker"] = true,            // leader boards along the bottom (vote %, kills…), taking turns
            ["impostorTags"] = true,      // Part 10: "IMPOSTORS: …" on each feed
            ["sabotage"] = true,          // Part 10: reactor/O2 countdown, lights/comms icon
            ["standings"] = false,        // Part 14: the standings table
            ["pointsOnTheLine"] = false,  // Part 14: what each outcome does to the standings
            ["standingsChange"] = true,   // Part 14: arrows after each game
            ["storyline"] = false,        // Part 13: a note as a lower third
            ["alerts"] = true,            // Part 18: banners for plays in lobbies that aren't on screen
            ["winCounter"] = true,        // Part 20: impostor wins v crewmate wins, in the corner
            ["playerCards"] = true,       // Part 21: a player's card as a lower third
            ["hideRoomCode"] = true,      // a "Room code hidden" box over the room code while a lobby on stream is in its lobby or menu
        };

        public static readonly Dictionary<string, string> ElementNames = new Dictionary<string, string>
        {
            ["lobbyLabels"] = "Lobby labels",
            ["gridTiles"] = "Grid tiles",
            ["statusBar"] = "Status bar",
            ["top3"] = "Top 3 by lobby",
            ["ticker"] = "Stats ticker",
            ["impostorTags"] = "Impostor tags",
            ["sabotage"] = "Sabotage countdown",
            ["standings"] = "Standings",
            ["pointsOnTheLine"] = "Points on the line",
            ["standingsChange"] = "After-game table",
            ["storyline"] = "Storyline note",
            ["alerts"] = "Off-screen alerts",
            ["winCounter"] = "Win counter",
            ["playerCards"] = "Player cards",
            ["hideRoomCode"] = "Hide room code",
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
            s.RoomCodeBox ??= new CodeBox();
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

        /// <summary>The graphics queue's hold time and the after-game choice, saved.</summary>
        public void SetQueue(double? seconds, bool? afterGameAuto)
        {
            if (seconds is { } sec) Current.GraphicSeconds = Math.Max(3, Math.Min(60, Math.Round(sec)));
            if (afterGameAuto is { } a) Current.AfterGameAuto = a;
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
            // The big graphics take turns, each where it belongs (or waiting for its lobby).
            desk.Graphics.CanShow = CanShow;
            // Simulation stopped: the ticker's boards (kept for 10 s) go at once, fake names and all.
            desk.SimStopped += _ => _ticker = null;
            desk.AfterGameAuto = () => Settings.Current.AfterGameAuto;
            desk.AfterGameOn = () => Settings.Current.Elements.GetValueOrDefault("standingsChange");
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
            // A multiview (quad or grid) with 4 or more lobbies on it, presented by a "multiview" sponsor.
            Extras["multiviewSponsor"] = () =>
            {
                var air = desk.OnAir;
                bool on = air.Layout is "4up" or "grid" && air.Slots.Count(x => x != null) >= 4;
                var sp = on ? desk.Sponsors.Current("multiview") : null;
                if (sp == null) { desk.Sponsors.End("multiview"); return null; }
                desk.Sponsors.Begin("multiview", sp, "multiview");
                return new { name = sp.Name, logo = SponsorBook.LogoUrl(sp), tagline = sp.Tagline };
            };
            Extras["sponsorBreak"] = () => desk.OnAir.Layout == "break" && desk.Break is { } b
                ? new { name = b.Sponsor.Name, logo = SponsorBook.LogoUrl(b.Sponsor), video = SponsorBook.VideoUrl(b.Sponsor), tagline = b.Sponsor.Tagline, left = Math.Max(0, Math.Ceiling((b.Until - DateTime.UtcNow).TotalSeconds)), total = Math.Max(5, b.Sponsor.BreakSeconds) }
                : null;
        }

        /// <summary>
        /// Whether a big graphic can be on stream now: one about a lobby only on that lobby (full screen,
        /// or its tile when the tile is big enough) or in intermission; the standings table not over
        /// intermission's own; nothing over a replay.
        /// </summary>
        public bool CanShow(Graphic g)
        {
            var air = _desk.OnAir;
            if (air.Layout is "replay" or "none") return false;
            bool brk = air.Layout is "intermission" or "slate";
            if (g.Kind == "standings") return !brk;
            if (g.Lobby == null) return !brk || g.Kind == "playerCard";
            if (brk) return true;
            var (_, _, slots) = Layout(air);
            int i = slots.FindIndex(x => string.Equals(x.Lobby, g.Lobby, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            return air.Layout is "full" or "break" or "cam" || slots[i].Box.W >= _desk.PlayerCardMinTile;
        }

        /// <summary>Where a graphic about a lobby goes: its tile (multi-view), or the whole screen.</summary>
        private object Place(string? lobby, double w, double h, List<(string? Lobby, Box Box)> slots)
        {
            var air = _desk.OnAir;
            var slot = air.Layout is "2up" or "4up" or "grid" && lobby != null ? slots.FirstOrDefault(x => string.Equals(x.Lobby, lobby, StringComparison.OrdinalIgnoreCase)) : default;
            var b = slot.Lobby != null ? slot.Box : new Box(0, 0, w, h);
            return new { x = b.X, y = b.Y, w = b.W, h = b.H, full = slot.Lobby == null };
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
            _desk.Graphics.HoldSeconds = s.GraphicSeconds;
            // The standings switch: a table that stays up while it's on (others wait their turn).
            bool standingsOn = s.Elements.GetValueOrDefault("standings");
            if (standingsOn && !_desk.Graphics.Has("standings")) _desk.Graphics.Add("standings", null, "Standings", persistent: true);
            if (!standingsOn && _desk.Graphics.Has("standings")) _desk.Graphics.Remove("standings");
            var big = _desk.Graphics.Current();
            var air = _desk.OnAir;
            var (w, h, slots) = Layout(air);
            var ranking = _desk.Board.Ranking();
            var lobbies = new Dictionary<string, object>();
            var now = DateTime.UtcNow;
            int round = _desk.Tables.CurrentRound;
            // The number viewers see: in a multi-view, where the lobby is on screen (1 top left…);
            // otherwise its fixed number (the order lobbies connected, the same as on the desk).
            bool multi = air.Layout is "2up" or "4up" or "grid";
            var numbers = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ranking.OrderBy(r => _desk.NumberOf(r.Lobby)))
            {
                int at = slots.FindIndex(x => string.Equals(x.Lobby, r.Lobby, StringComparison.OrdinalIgnoreCase));
                int? number = multi ? (at >= 0 ? at + 1 : null) : _desk.NumberOf(r.Lobby);
                numbers[r.Lobby] = number;
                var live = _desk.Board.Lobby(r.Lobby);
                // Part 22: a lobby that's dropped keeps its last state, marked, and nothing of it counts down as if live.
                var health = _desk.Health.Lobbies.Contains(r.Lobby, StringComparer.OrdinalIgnoreCase) ? _desk.Health.Status(r.Lobby) : null;
                bool reconnecting = health != null && (health.Level == "red" || health.DataAge >= _desk.HealthConfig.DataYellowSeconds);
                string Who(LobbyPlayer p) => live == null ? p.Name : _desk.Board.DisplayName(live, p.Id, p.Name);
                lobbies[r.Lobby] = new
                {
                    number,
                    order = multi ? (at >= 0 ? at + 1 : 100 + _desk.NumberOf(r.Lobby)) : _desk.NumberOf(r.Lobby),
                    onScreen = at >= 0,
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
                    twitch = _desk.TwitchOf(r.Lobby),
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
                codeBox = s.RoomCodeBox,
                lobbies,
                // One big graphic at a time (the graphics queue): which, and where.
                big = big == null ? null : new { kind = big.Kind, lobby = big.Lobby, id = big.Id },
                standings = big?.Kind == "standings" ? Standings() : null,
                top3 = s.Elements.GetValueOrDefault("top3") ? Top3(ranking) : null,
                ticker = s.Elements.GetValueOrDefault("ticker") ? Ticker() : null,
                standingsChange = big?.Kind == "afterGame" ? new { change = big.Data, at = Place(big.Lobby, w, h, slots) } : null,
                storyline = big?.Kind == "storyline" ? big.Data as string : null,
                intermission = air.Layout is "intermission" or "slate" ? Intermission(round, air.Layout == "slate") : null,
                wins = s.Elements.GetValueOrDefault("winCounter") || air.Layout is "intermission" or "slate" ? new { impostors = _desk.Wins().Impostors, crew = _desk.Wins().Crew, scope = _desk.WinScope } : null,
                playerCard = s.Elements.GetValueOrDefault("playerCards") || air.Layout == "replay" ? PlayerCard(air, w, h, slots) : null,
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
                if (slot.Lobby != null) box = slot.Box;
            }
            var b = box ?? new Box(0, 0, w, h);
            return new { id = c.Key + "|" + c.At.Ticks, data, x = b.X, y = b.Y, w = b.W, h = b.H, full = box == null };
        }

        /// <summary>Close to ending: worth showing what's on the line.</summary>
        private static bool Close(LobbyRank r) =>
            (r.Phase == "ingame" || r.Phase == "meeting") && r.Plays.Any(p => p.Rule is "oneKillFromWin" or "taskBar" or "finalPlayers" or "criticalSabotage" or "closeCounts");

        /// <summary>The standings on stream: the featured lobby's own round, every lobby's round, or the tournament.</summary>
        private object Standings()
        {
            var (title, round, lobby, rows) = _desk.StandingsTable();
            return new
            {
                title, round, lobby,
                rows = rows.Take(10).Select(r => new { r.Rank, r.Name, r.Color, r.Points, r.Games, r.Wins, r.Advancing }).ToList(),
            };
        }

        /// <summary>Each playing lobby's top 3 for its own round (they take turns on stream).</summary>
        private object Top3(IReadOnlyList<LobbyRank> ranking)
        {
            var list = new List<object>();
            foreach (var r in ranking.Where(r => r.Online || r.Phase != "menu").OrderBy(r => r.Lobby, StringComparer.OrdinalIgnoreCase))
            {
                var (round, rows) = _desk.Tables.LobbyTable(r.Lobby, r.Round > 0 ? r.Round : _desk.Tables.CurrentRound);
                if (rows.Count == 0) continue;
                list.Add(new { lobby = r.Lobby, twitch = _desk.TwitchOf(r.Lobby), round, rows = rows.Take(3).Select(x => new { x.Rank, x.Name, x.Color, x.Points }).ToList() });
            }
            return list;
        }

        /// <summary>The stats ticker's boards (worked out at most every 10 s: every counted game is read).</summary>
        private object Ticker()
        {
            var now = DateTime.UtcNow;
            if (_ticker == null || now - _tickerAt > TimeSpan.FromSeconds(10))
            {
                _ticker = _desk.Tables.Leaders().Select(b => new { title = b.Title, unit = b.Unit, rows = b.Rows.Select((x, i) => new { rank = i + 1, name = x.Name, color = x.Color, value = x.Value }).ToList() }).ToList<object>();
                _tickerAt = now;
            }
            return _ticker;
        }
        private List<object>? _ticker;
        private DateTime _tickerAt;

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
