using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TournamentTracker.Stats;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Simulation mode: fake lobbies playing fake games, sending exactly what a host's mod sends
    /// (events and a snapshot a second), so the caster tab, the ranking and OBS switching can be
    /// tried without real games. Same seed, same games.
    /// </summary>
    public sealed class FeedSimulator
    {
        private static readonly string[] Colours = { "Red", "Blue", "Green", "Pink", "Orange", "Yellow", "Black", "White", "Purple", "Brown", "Cyan", "Lime" };
        private static readonly string[] Names = { "Soggy", "Mal", "Kai", "Ana", "Fred", "Zed", "Bo", "Cy", "Di", "Millie", "Rex", "Ivy" };
        // The simulated tournament's players: matched by Discord for most, by friend code for some, one left unmatched.
        private static readonly string[] RealNames = { "Jake Rivera", "Maria Lopez", "Sam Okafor", "Priya Shah", "Leo Brandt", "Nina Park", "Omar Haddad", "Chloe Martin", "Theo Grant", "Zoe Ito", "Ravi Kumar" };

        /// <summary>Roster entries for the simulated players (simulation mode adds them to the roster, not to the file).</summary>
        public static List<RosterEntry> SimRoster()
        {
            var list = new List<RosterEntry>();
            for (int i = 0; i < RealNames.Length; i++)
                list.Add(new RosterEntry
                {
                    Name = RealNames[i],
                    DiscordId = i % 4 == 3 ? null : (900000000000000000L + i).ToString(),
                    FriendCodes = new List<string> { $"simfox{i}#{1000 + i}" },
                    InGameNames = new List<string> { Names[i % Names.Length] },
                    Pronunciation = i == 0 ? "JAKE rih-VAIR-uh" : null,
                });
            return list;
        }
        private static readonly Dictionary<string, string[]> Rooms = new Dictionary<string, string[]>
        {
            ["The Skeld"] = new[] { "Cafeteria", "Weapons", "O2", "Navigation", "Shields", "Communications", "Storage", "Admin", "Electrical", "Lower Engine", "Upper Engine", "Security", "Reactor", "MedBay" },
            ["Polus"] = new[] { "Office", "Laboratory", "Specimen Room", "Admin", "Communications", "Weapons", "O2", "Electrical", "Security", "Storage", "Dropship", "Boiler Room" },
            ["MIRA HQ"] = new[] { "Launchpad", "Reactor", "Laboratory", "Locker Room", "Communications", "MedBay", "Office", "Admin", "Greenhouse", "Balcony", "Cafeteria", "Storage" },
        };
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly List<SimLobby> _lobbies;
        private static readonly ScoringRules Rules = new ScoringRules();
        private readonly List<GameRecord> _games = new List<GameRecord>();

        /// <summary>The simulated tournament's finished games, scored with the tournament's point sheet.</summary>
        public List<GameRecord> Games { get { lock (_games) return _games.ToList(); } }

        public FeedSimulator(DateTime start, int lobbies = 4, int seed = 7)
        {
            var labels = Enumerable.Range(1, 6).Select(i => Prefix + i).ToArray();
            _lobbies = Enumerable.Range(0, Math.Min(lobbies, labels.Length))
                .Select(i => new SimLobby(labels[i], new Random(seed * 31 + i), start, i, g => { lock (_games) _games.Add(g); })).ToList();
        }

        /// <summary>Fake lobbies are called SIM-1, SIM-2…: never mistaken for a real one, and all removed when simulation stops.</summary>
        public const string Prefix = "SIM-";
        public static bool IsSim(string? lobby) => lobby != null && lobby.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>Takes a lobby offline (or back), like a host's video dropping.</summary>
        public void SetOffline(string lobby, bool offline)
        {
            var l = _lobbies.FirstOrDefault(x => x.Label == lobby);
            if (l != null) l.Offline = offline;
        }

        public IEnumerable<string> Lobbies => _lobbies.Select(l => l.Label);

        /// <summary>
        /// Part 22 test buttons: "video", "audio" or "data" drops, "all" drops, "lag" (data arrives late),
        /// "crash" (the referee's game dies mid-game and comes back in the lobby), "reconnect" (all fixed).
        /// While data is down, the referee's page holds the lobby's messages and sends them on reconnect.
        /// </summary>
        public bool Fail(string lobby, string what, DateTime now)
        {
            var l = _lobbies.FirstOrDefault(x => string.Equals(x.Label, lobby, StringComparison.OrdinalIgnoreCase));
            if (l == null) return false;
            switch (what)
            {
                case "video": l.VideoLost = true; break;
                case "audio": l.AudioLost = true; break;
                case "data": l.DataLost = true; break;
                case "all": l.VideoLost = l.AudioLost = l.DataLost = true; break;
                case "lag": l.LagMs = l.LagMs > 0 ? 0 : 4000; break;
                case "crash": l.Crash(); break;
                case "reconnect": l.Reconnect(now); break;
                default: return false;
            }
            return true;
        }

        /// <summary>What's switched off for a lobby (for the buttons).</summary>
        public object? Failures(string lobby)
        {
            var l = _lobbies.FirstOrDefault(x => string.Equals(x.Label, lobby, StringComparison.OrdinalIgnoreCase));
            return l == null ? null : new { video = l.VideoLost, audio = l.AudioLost, data = l.DataLost, lag = l.LagMs > 0, crash = l.Crashed };
        }

        /// <summary>A "spec …" command, as a real host's mod would take it; the next snapshot shows it.</summary>
        public bool Spec(string lobby, string command)
        {
            var l = _lobbies.FirstOrDefault(x => x.Label == lobby);
            if (l == null) return false;
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "spec") return false;
            string what = parts[1], value = parts.Length > 2 ? parts[2] : "";
            switch (what)
            {
                case "lit": l.SpecLit = value == "on" || value != "off" && !l.SpecLit; break;
                case "report": l.SpecReport = value == "on" || value != "off" && !l.SpecReport; break;
                case "eye": l.SpecEye = value == "on" || value != "off" && !l.SpecEye; break;
                case "vision": l.SpecVision = value is "off" or "focus" or "rings" ? value : l.SpecVision == "off" ? "focus" : l.SpecVision == "focus" ? "rings" : "off"; break;
                case "focus": l.SpecFocus = int.TryParse(value, out var id) ? id : (int?)null; break;
                default: return false;
            }
            return true;
        }

        /// <summary>Everything the lobbies sent up to <paramref name="now"/>, oldest first.</summary>
        public List<string> Advance(DateTime now)
        {
            var all = new List<(DateTime At, string Json)>();
            foreach (var l in _lobbies) all.AddRange(l.Advance(now));
            return all.Select((m, i) => (m, i)).OrderBy(x => x.m.At).ThenBy(x => x.i).Select(x => x.m.Json).ToList();
        }

        private sealed class SimPlayer
        {
            public byte Id;
            public string Name = "";
            public int Color;
            public bool Imp, Dead;
            public string Key = "";
            public string? Discord;
            public double X = 0.5, Y = 0.5, Vx, Vy;
            public object Who => new { id = Id, name = Name, color = Color, colorName = Colours[Color], imp = Imp };
            public object Roster(bool game) => new { id = Id, name = Name, color = Color, key = Key, discord = Discord, imp = game ? Imp : (bool?)null, dead = game ? Dead : (bool?)null };
        }

        private sealed class SimLobby
        {
            public readonly string Label;
            public bool Offline;
            // Part 22: what's broken, and the referee page's held messages (released when the link is back).
            public bool VideoLost, AudioLost, DataLost, Crashed;
            public double LagMs;
            private string _src;
            private long _seq;
            private readonly List<(DateTime Release, DateTime At, string Json)> _outbox = new List<(DateTime, DateTime, string)>();
            public bool SpecLit = true, SpecReport = true, SpecEye = true;
            public string SpecVision = "focus";
            public int? SpecFocus;
            private readonly Random _r;
            private DateTime _now, _gameStart, _phaseEnds, _nextSnap;
            private string _phase = "lobby";
            private string _map = "The Skeld";
            private int _round = 1, _gameNo;
            private readonly List<SimPlayer> _players = new List<SimPlayer>();
            private double _tasks;
            private string? _sab;
            private double? _sabLeft;
            private double _sabFix;
            private bool _danger;
            private DateTime _dangerEnds;
            private (SimPlayer Imp, SimPlayer Crew, string Room)? _dangerPair;
            private SimPlayer? _bodyToReport;
            private DateTime _reportAt;
            private readonly List<(DateTime, string)> _out = new List<(DateTime, string)>();

            private readonly Action<GameRecord> _finished;
            private GameRecord? _rec;
            private object? _ifEnded;
            private DateTime _nextIfEnded;

            public SimLobby(string label, Random r, DateTime start, int index, Action<GameRecord> finished)
            {
                _finished = finished;
                Label = label;
                _r = r;
                _now = start;
                _nextSnap = start;
                _phaseEnds = start.AddSeconds(4 + index * 6);    // staggered, so the lobbies aren't in step
                _src = NewSrc();
            }

            private string NewSrc() => "sim" + _r.Next(100000, 999999);

            /// <summary>The referee's game dies: nothing more from the lobby until it reconnects, back in the lobby.</summary>
            public void Crash() => Crashed = true;

            public void Reconnect(DateTime now)
            {
                if (Crashed)
                {
                    // Among Us restarted: a new run of the mod, back in the lobby, and the game it was in never ended.
                    Crashed = false;
                    _src = NewSrc();
                    _seq = 0;
                    _rec = null;
                    _phase = "lobby";
                    _phaseEnds = _now.AddSeconds(Rand(12, 20));
                    _sab = null; _sabLeft = null; _danger = false; _bodyToReport = null;
                    foreach (var p in _players) { p.Dead = false; }
                }
                VideoLost = AudioLost = false;
                LagMs = 0;
                if (DataLost)
                {
                    DataLost = false;
                    // Sent again with their own times, marked as sent again.
                    for (int i = 0; i < _outbox.Count; i++)
                        if (_outbox[i].Release == DateTime.MaxValue) _outbox[i] = (now, _outbox[i].At, "{\"re\":true," + _outbox[i].Json.Substring(1));
                }
            }

            private double Rand(double a, double b) => a + _r.NextDouble() * (b - a);
            private bool Chance(double perSecond) => _r.NextDouble() < perSecond;
            private string Room() { var rooms = Rooms[_map]; return rooms[_r.Next(rooms.Length)]; }
            private List<SimPlayer> Alive(bool? imp = null) => _players.Where(p => !p.Dead && (imp == null || p.Imp == imp)).ToList();
            private SimPlayer Pick(List<SimPlayer> list) => list[_r.Next(list.Count)];

            public List<(DateTime, string)> Advance(DateTime until)
            {
                _out.Clear();
                while (_now < until)
                {
                    _now = _now.AddSeconds(0.5);
                    if (Offline || Crashed) continue;
                    Step(0.5);
                    Move(0.5);
                    if (_now >= _nextSnap)
                    {
                        _nextSnap = _now.AddSeconds(1);
                        Snap();
                        TrackOut();
                        VoiceOut();
                        HealthOut();
                    }
                }
                // What gets through to the caster now: held while the data link is down, late with lag.
                foreach (var (at, json, queued) in _pending)
                {
                    if (DataLost) { if (queued) _outbox.Add((DateTime.MaxValue, at, json)); continue; }
                    _outbox.Add((at.AddMilliseconds(LagMs), at, json));
                }
                _pending.Clear();
                var ready = _outbox.Where(x => x.Release <= until).ToList();
                _outbox.RemoveAll(x => x.Release <= until);
                foreach (var x in ready) _out.Add((x.Release, x.Json));
                return _out.ToList();
            }

            private void Emit(string type, string? kind, Dictionary<string, object?> data)
            {
                var msg = new Dictionary<string, object?>
                {
                    [TournamentTracker.Broadcast.FeedProtocol.V] = TournamentTracker.Broadcast.FeedProtocol.Version,
                    ["type"] = type,
                    ["lobby"] = Label,
                    ["round"] = _round,
                    ["game"] = _gameNo > 0 ? $"{Label}-{_gameNo}" : null,
                    ["t"] = new DateTimeOffset(_now).ToUnixTimeMilliseconds(),
                    ["clock"] = _phase == "ingame" || _phase == "meeting" ? Math.Round((_now - _gameStart).TotalSeconds, 1) : (double?)null,
                    ["sim"] = true,
                };
                if (kind != null) msg["kind"] = kind;
                foreach (var kv in data) msg[kv.Key] = kv.Value;
                // The mod's messages are numbered (so repeats after a drop are dropped); the page's own aren't, and aren't held.
                bool queued = type is not ("voice" or "health" or "host");
                if (queued) { msg["src"] = _src; msg["seq"] = ++_seq; }
                _pending.Add((_now, JsonSerializer.Serialize(msg, Json), queued));
            }

            private readonly List<(DateTime At, string Json, bool Queued)> _pending = new List<(DateTime, string, bool)>();

            // The referee's page says whether its screen share is sending.
            private void HealthOut() => Emit("health", null, new Dictionary<string, object?> { ["video"] = VideoLost ? "lost" : "ok" });

            private void Event(string kind, Dictionary<string, object?> data) => Emit("event", kind, data);

            // Everyone wanders around the host's screen, for replays.
            private readonly List<object> _samples = new List<object>();
            private void Move(double dt)
            {
                if (_phase != "ingame") return;
                foreach (var p in Alive())
                {
                    p.Vx = Math.Max(-0.12, Math.Min(0.12, p.Vx + (_r.NextDouble() - 0.5) * 0.08));
                    p.Vy = Math.Max(-0.12, Math.Min(0.12, p.Vy + (_r.NextDouble() - 0.5) * 0.08));
                    p.X += p.Vx * dt; p.Y += p.Vy * dt;
                    if (p.X < 0.05 || p.X > 0.95) { p.Vx = -p.Vx; p.X = Math.Max(0.05, Math.Min(0.95, p.X)); }
                    if (p.Y < 0.05 || p.Y > 0.95) { p.Vy = -p.Vy; p.Y = Math.Max(0.05, Math.Min(0.95, p.Y)); }
                }
                _samples.Add(new { t = new DateTimeOffset(_now).ToUnixTimeMilliseconds(), p = Alive().Select(p => new[] { (int)p.Id, (int)(p.X * 1000), (int)(p.Y * 1000), 0 }).ToList() });
            }

            // The referee's lobby voice status (Part 11): talking louder in meetings, the game quieter.
            private void VoiceOut()
            {
                double voice = AudioLost ? -60 : _phase == "meeting" ? Rand(-18, -6) : _r.NextDouble() < 0.3 ? Rand(-35, -15) : -60;
                double game = AudioLost ? -60 : _phase == "ingame" ? Rand(-30, -16) : -60;
                // Who's hosting, from the host's page: a made-up Twitch channel for each fake lobby.
                Emit("host", null, new Dictionary<string, object?> { ["twitch"] = Label.Replace("-", "").ToLowerInvariant() + "_plays_au" });
                Emit("voice", null, new Dictionary<string, object?>
                {
                    ["on"] = true, ["sending"] = !AudioLost, ["problem"] = AudioLost ? "voice stream dropped" : null, ["discord"] = "capturing", ["game"] = "capturing",
                    ["voiceDb"] = Math.Round(voice, 1), ["gameDb"] = Math.Round(game, 1), ["mic"] = false,
                });
            }

            private void TrackOut()
            {
                if (_samples.Count == 0) return;
                Emit("track", null, new Dictionary<string, object?> { ["samples"] = _samples.ToList() });
                _samples.Clear();
            }

            private void Snap()
            {
                bool game = _phase != "lobby";
                Emit("snap", null, new Dictionary<string, object?>
                {
                    ["phase"] = _phase,
                    ["map"] = game ? _map : null,
                    ["crewAlive"] = game ? Alive(false).Count : (int?)null,
                    ["impAlive"] = game ? Alive(true).Count : (int?)null,
                    ["alive"] = game ? Alive().Count : (int?)null,
                    ["taskPct"] = game ? (int)_tasks : (int?)null,
                    ["sabotage"] = _sab == null ? null : new { system = _sab, critical = _sabLeft.HasValue, timeLeft = _sabLeft.HasValue ? Math.Round(_sabLeft.Value, 1) : (double?)null, fixing = _sabLeft.HasValue ? _sabFix : (double?)null },
                    ["danger"] = _danger,
                    ["video"] = true,
                    ["players"] = _players.Select(p => p.Roster(_phase != "lobby")).ToList(),
                    ["ifEnded"] = IfEnded(),
                    ["spec"] = new { lit = SpecLit, vision = SpecVision, report = SpecReport, eye = SpecEye, focus = SpecFocus, focusing = SpecFocus ?? Alive(false).Select(p => (int?)p.Id).FirstOrDefault(), on = true },
                });
            }

            private void Step(double dt)
            {
                switch (_phase)
                {
                    case "lobby":
                        if (_now >= _phaseEnds) StartGame();
                        break;
                    case "ended":
                        if (_now >= _phaseEnds) { _phase = "lobby"; _phaseEnds = _now.AddSeconds(Rand(10, 20)); }
                        break;
                    case "meeting":
                        if (_now >= _phaseEnds) Vote();
                        break;
                    case "ingame":
                        Playing(dt);
                        break;
                }
            }

            private void StartGame()
            {
                _gameNo++;
                if (_gameNo > 3) { _gameNo = 1; _round++; }
                _map = Rooms.Keys.ElementAt(_r.Next(Rooms.Count));
                _players.Clear();
                var colours = Enumerable.Range(0, Colours.Length).OrderBy(_ => _r.Next()).Take(10).ToList();
                for (int i = 0; i < 10; i++)
                {
                    // Players 0–10 of the simulated roster; the 11th spot is someone not on it.
                    int who = (i + Label.Length) % 12;
                    _players.Add(new SimPlayer
                    {
                        Id = (byte)i, Name = Names[who % Names.Length], Color = colours[i],
                        Key = who < 11 ? $"simfox{who}#{1000 + who}" : "stranger#9999",
                        Discord = who < 11 && who % 4 != 3 ? (900000000000000000L + who).ToString() : null,
                    });
                }
                foreach (var i in Enumerable.Range(0, 10).OrderBy(_ => _r.Next()).Take(2)) _players[i].Imp = true;
                foreach (var p in _players) { p.X = Rand(0.1, 0.9); p.Y = Rand(0.1, 0.9); }
                _tasks = 0;
                _sab = null; _sabLeft = null; _danger = false; _bodyToReport = null;
                _phase = "ingame";
                _gameStart = _now;
                _rec = new GameRecord { GameNumber = _gameNo, Host = Label, Round = _round, Map = _map, StartedUtc = _now, Mode = "Tournament", Tournament = "Simulated Cup" };
                _rec.Id = $"{Label}-{_gameNo}-{_now:yyyyMMdd-HHmmss}";
                foreach (var p in _players)
                    _rec.Players.Add(new GamePlayer { PlayerId = p.Id, Key = p.Key, Name = p.Name, DiscordId = p.Discord, ColorId = p.Color, Role = p.Imp ? "Impostor" : "Crewmate", IsImpostor = p.Imp, TasksTotal = p.Imp ? 0 : 10 });
                Event("gameStart", new Dictionary<string, object?>
                {
                    ["map"] = _map, ["players"] = _players.Select(p => p.Who).ToList(), ["roster"] = _players.Select(p => p.Roster(true)).ToList(), ["crewAlive"] = 8, ["impAlive"] = 2,
                });
            }

            private void Playing(double dt)
            {
                double clock = (_now - _gameStart).TotalSeconds;
                _tasks = Math.Min(100, _tasks + dt * Rand(0.25, 0.9));

                // Reactor/O2 count down; someone usually fixes it.
                if (_sab != null)
                {
                    if (_sabLeft.HasValue)
                    {
                        _sabLeft -= dt;
                        // Someone runs to the panels: one hand on, then the other.
                        if (_sabFix < 1 && Chance(0.12)) _sabFix = Math.Min(1, _sabFix + 0.5);
                        if (_sabFix >= 1) { FixSabotage(); return; }
                        if (_sabLeft <= 0) { End("Impostors", "ImpostorsBySabotage", "sabotage"); return; }
                        if (Chance(_sabLeft < 12 ? 0.05 : 0.03)) FixSabotage();
                    }
                    else if (Chance(0.04)) FixSabotage();
                }
                else if (clock > 15 && Chance(0.012))
                {
                    var options = new[] { ("Reactor", (double?)30), ("O2", 30), ("Lights", null), ("Comms", null) };
                    var (sys, left) = options[_r.Next(options.Length)];
                    _sab = sys; _sabLeft = left; _sabFix = 0;
                    Event("sabotage", new Dictionary<string, object?> { ["system"] = sys, ["state"] = "start", ["critical"] = left.HasValue, ["timeLeft"] = left, ["by"] = Pick(Alive(true)).Who });
                }

                // Danger: an impostor alone with someone, which often ends in a kill.
                if (_danger)
                {
                    if (_now >= _dangerEnds)
                    {
                        _danger = false;
                        var (imp, crew, room) = _dangerPair!.Value;
                        if (Chance(0.55) && !imp.Dead && !crew.Dead) Kill(imp, crew, room);
                        else Event("danger", new Dictionary<string, object?> { ["state"] = "end", ["impostor"] = imp.Who, ["crewmate"] = crew.Who });
                    }
                }
                else if (clock > 12 && _bodyToReport == null && Chance(0.035))
                {
                    var imp = Pick(Alive(true));
                    var crew = Pick(Alive(false));
                    string room = Room();
                    _danger = true;
                    _dangerPair = (imp, crew, room);
                    imp.X = Math.Min(0.95, crew.X + 0.04); imp.Y = crew.Y;
                    _dangerEnds = _now.AddSeconds(Rand(2, 7));
                    Event("danger", new Dictionary<string, object?> { ["state"] = "start", ["impostor"] = imp.Who, ["crewmate"] = crew.Who, ["room"] = room, ["distance"] = Math.Round(Rand(0.8, 2.5), 2) });
                }

                if (clock > 10 && Chance(0.02))
                {
                    var imp = Pick(Alive(true));
                    Event("vent", new Dictionary<string, object?> { ["player"] = imp.Who, ["action"] = "enter", ["room"] = Room() });
                }

                // A body gets found a while after a kill; sometimes someone presses the button.
                if (_bodyToReport != null && _now >= _reportAt)
                {
                    var caller = Pick(Alive());
                    Meeting(caller, _bodyToReport);
                    return;
                }
                if (clock > 40 && Chance(0.004)) { Meeting(Pick(Alive()), null); return; }

                if (_tasks >= 100) End("Crewmates", "HumansByTask", "tasks");
            }

            private void FixSabotage()
            {
                Event("sabotage", new Dictionary<string, object?> { ["system"] = _sab, ["state"] = "fixed", ["critical"] = _sabLeft.HasValue, ["timeLeft"] = _sabLeft.HasValue ? Math.Round(_sabLeft.Value, 1) : (double?)null });
                _sab = null; _sabLeft = null;
            }

            private void Kill(SimPlayer imp, SimPlayer victim, string room)
            {
                victim.Dead = true;
                if (_rec != null)
                {
                    bool first = _rec.Players.All(x => x.DeathCause != "Killed");
                    var k = _rec.ById(imp.Id)!; var v = _rec.ById(victim.Id)!;
                    k.Kills++; if (first) k.FirstBlood = true;
                    v.DeathCause = "Killed"; v.KilledByKey = k.Key; v.DiedFirst = first; v.DiedAtSeconds = (_now - _gameStart).TotalSeconds;
                }
                int crew = Alive(false).Count, imps = Alive(true).Count;
                double sx = Math.Round(victim.X, 3), sy = Math.Round(victim.Y, 3);
                imp.X = victim.X; imp.Y = victim.Y;
                Event("kill", new Dictionary<string, object?>
                {
                    ["killer"] = imp.Who, ["victim"] = victim.Who, ["room"] = room, ["crewAlive"] = crew, ["impAlive"] = imps,
                    ["winning"] = imps >= crew,
                    ["pos"] = new { x = Math.Round(Rand(-20, 20), 2), y = Math.Round(Rand(-15, 5), 2) },
                    ["screen"] = new { x = sx, y = sy, onScreen = true },
                });
                if (imps >= crew) { End("Impostors", "ImpostorsByKill", "kills"); return; }
                _bodyToReport = victim;
                _reportAt = _now.AddSeconds(Rand(4, 25));
            }

            private void Meeting(SimPlayer caller, SimPlayer? body)
            {
                if (_sab != null) { _sab = null; _sabLeft = null; }
                _danger = false;
                _phase = "meeting";
                _phaseEnds = _now.AddSeconds(Rand(20, 35));
                _bodyToReport = null;
                Event("meeting", new Dictionary<string, object?> { ["caller"] = caller.Who, ["body"] = body?.Who, ["emergency"] = body == null });
            }

            private void Vote()
            {
                double roll = _r.NextDouble();
                SimPlayer? ejected = roll < 0.35 ? Pick(Alive(true)) : roll < 0.7 ? Pick(Alive(false)) : null;
                if (ejected != null)
                {
                    ejected.Dead = true;
                    if (_rec?.ById(ejected.Id) is { } e)
                    {
                        e.DeathCause = "Ejected";
                        if (e.IsImpostor) e.ImpostorEjectOrder = 1 + _rec.Players.Count(x => x.ImpostorEjectOrder.HasValue);
                        // Most of the crew voted with the room.
                        foreach (var voter in _rec.Players.Where(x => x.DeathCause == null && !x.IsImpostor).Take(4))
                            if (e.IsImpostor) voter.EjectVotesOnImpostor++; else voter.EjectVotesOnCrewmate++;
                    }
                }
                int crew = Alive(false).Count, imps = Alive(true).Count;
                Event("eject", new Dictionary<string, object?>
                {
                    ["ejected"] = ejected?.Who, ["wasImpostor"] = ejected?.Imp, ["skipped"] = ejected == null, ["tie"] = false,
                    ["crewAlive"] = crew, ["impAlive"] = imps,
                });
                _phase = "ingame";
                if (imps == 0) End("Crewmates", "HumansByVote", "vote");
                else if (imps >= crew) End("Impostors", "ImpostorsByVote", "vote");
            }

            /// <summary>Each ending's points, as a real host's mod sends them.</summary>
            private object? IfEnded()
            {
                if (_rec == null || (_phase != "ingame" && _phase != "meeting")) return null;
                if (_now < _nextIfEnded && _ifEnded != null) return _ifEnded;
                _nextIfEnded = _now.AddSeconds(5);
                SyncTasks();
                _ifEnded = WhatIf.Points(_rec, Rules);
                return _ifEnded;
            }

            private void SyncTasks()
            {
                if (_rec == null) return;
                foreach (var p in _rec.Players.Where(x => !x.IsImpostor)) p.TasksCompleted = (int)Math.Round(_tasks / 10);
            }

            private void End(string winner, string reason, string how)
            {
                if (_rec != null)
                {
                    SyncTasks();
                    _rec.EndedUtc = _now; _rec.EndReason = reason; _rec.Winner = winner;
                    foreach (var p in _rec.Players)
                    {
                        p.Survived = p.DeathCause == null;
                        p.Won = p.IsImpostor == (winner == Outcome.Impostors);
                    }
                    Scoring.ScoreGame(_rec, Rules);
                    _finished(_rec);
                    _rec = null;
                }
                _phase = "ended";
                _sab = null; _sabLeft = null; _danger = false; _bodyToReport = null;
                _phaseEnds = _now.AddSeconds(8);
                Event("gameEnd", new Dictionary<string, object?>
                {
                    ["winner"] = winner, ["reason"] = reason, ["how"] = how, ["abandoned"] = false,
                    ["crewAlive"] = Alive(false).Count, ["impAlive"] = Alive(true).Count,
                    ["impostors"] = _players.Where(p => p.Imp).Select(p => p.Who).ToList(),
                });
            }
        }
    }
}
