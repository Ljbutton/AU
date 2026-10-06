using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

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

        public FeedSimulator(DateTime start, int lobbies = 4, int seed = 7)
        {
            var labels = new[] { "LJ", "MAL", "Soggy", "Kai", "Ana", "Bo" };
            _lobbies = Enumerable.Range(0, Math.Min(lobbies, labels.Length))
                .Select(i => new SimLobby(labels[i], new Random(seed * 31 + i), start, i)).ToList();
        }

        /// <summary>Takes a lobby offline (or back), like a host's video dropping.</summary>
        public void SetOffline(string lobby, bool offline)
        {
            var l = _lobbies.FirstOrDefault(x => x.Label == lobby);
            if (l != null) l.Offline = offline;
        }

        public IEnumerable<string> Lobbies => _lobbies.Select(l => l.Label);

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
            return all.OrderBy(m => m.At).Select(m => m.Json).ToList();
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

            public SimLobby(string label, Random r, DateTime start, int index)
            {
                Label = label;
                _r = r;
                _now = start;
                _nextSnap = start;
                _phaseEnds = start.AddSeconds(4 + index * 6);    // staggered, so the lobbies aren't in step
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
                    if (Offline) continue;
                    Step(0.5);
                    Move(0.5);
                    if (_now >= _nextSnap)
                    {
                        _nextSnap = _now.AddSeconds(1);
                        Snap();
                        TrackOut();
                    }
                }
                return _out.ToList();
            }

            private void Emit(string type, string? kind, Dictionary<string, object?> data)
            {
                var msg = new Dictionary<string, object?>
                {
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
                _out.Add((_now, JsonSerializer.Serialize(msg, Json)));
            }

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
                if (ejected != null) ejected.Dead = true;
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

            private void End(string winner, string reason, string how)
            {
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
