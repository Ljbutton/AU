using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>One reason a lobby is worth showing right now, with its points after fading.</summary>
    public sealed class Play
    {
        /// <summary>Merges repeats: the same key updates the same play (e.g. "sabotage:Reactor").</summary>
        public string Key { get; set; } = "";
        /// <summary>The config rule it scores by.</summary>
        public string Rule { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTime At { get; set; }
        /// <summary>Last time it happened again (repeats bump it).</summary>
        public DateTime Updated { get; set; }
        /// <summary>For states (danger, meeting): when it stopped. Null while it lasts, or for one-off plays.</summary>
        public DateTime? Ended { get; set; }
        public bool IsState { get; set; }
        public int Repeats { get; set; } = 1;
        public double Value { get; set; }
        /// <summary>The event that made it, as the lobby sent it (for the card and the replay).</summary>
        public JsonElement? Source { get; set; }
    }

    /// <summary>A player in a lobby, with what's needed to match them to the roster.</summary>
    public sealed class LobbyPlayer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Color { get; set; }
        public string Key { get; set; } = "";
        public string? Discord { get; set; }
        public bool? Imp { get; set; }
        public bool? Dead { get; set; }
    }

    /// <summary>A lobby as the caster sees it: the latest snapshot plus what's been happening.</summary>
    public sealed class LobbyLive
    {
        public string Lobby { get; set; } = "";
        public DateTime LastSeen { get; set; }
        public string Phase { get; set; } = "menu";
        public string? Game { get; set; }
        public int Round { get; set; }
        public string? Map { get; set; }
        public int? Crew { get; set; }
        public int? Imps { get; set; }
        public int? TaskPct { get; set; }
        public string? Sabotage { get; set; }
        public bool SabotageCritical { get; set; }
        public double? SabotageLeft { get; set; }
        public double? SabotageFixing { get; set; }
        public bool Danger { get; set; }
        public string? DangerText { get; set; }
        public string? MeetingText { get; set; }
        /// <summary>The meeting event (caller and body), for the meeting card's players and replay.</summary>
        public JsonElement? MeetingSource { get; set; }
        public bool Video { get; set; }
        public double? Clock { get; set; }
        /// <summary>The spectator view on the host's screen (lit, vision, "!", eye, focus), as it last said.</summary>
        public JsonElement? Spec { get; set; }
        /// <summary>This game's players (from its start), for picking whose vision to show.</summary>
        public List<JsonElement> Players { get; set; } = new List<JsonElement>();
        /// <summary>Each ending's points for this game, from the referee's mod ("points on the line").</summary>
        public JsonElement? IfEnded { get; set; }
        /// <summary>Everyone in the lobby now (from the snapshots), by player id.</summary>
        public Dictionary<int, LobbyPlayer> People { get; } = new Dictionary<int, LobbyPlayer>();
        public Dictionary<string, Play> Plays { get; } = new Dictionary<string, Play>();
    }

    /// <summary>A lobby's ranking: its score, tier, a one-line status and the plays behind it.</summary>
    public sealed class LobbyRank
    {
        public string Lobby { get; set; } = "";
        public bool Online { get; set; }
        public double Score { get; set; }
        public string Tier { get; set; } = "low";
        public string Line { get; set; } = "";
        public string Phase { get; set; } = "";
        public int? Crew { get; set; }
        public int? Imps { get; set; }
        public int? TaskPct { get; set; }
        public string? Game { get; set; }
        public int Round { get; set; }
        public JsonElement? Spec { get; set; }
        public List<JsonElement> Players { get; set; } = new List<JsonElement>();
        public List<LobbyPlayer> People { get; set; } = new List<LobbyPlayer>();
        public string? Map { get; set; }
        public string? Sabotage { get; set; }
        public bool SabotageCritical { get; set; }
        public double? SabotageLeft { get; set; }
        public double? SabotageFixing { get; set; }
        public bool Danger { get; set; }
        public double? Clock { get; set; }
        public List<Play> Plays { get; set; } = new List<Play>();
    }

    /// <summary>
    /// Every lobby's live state, fed with the messages the lobbies send (events and 1 s
    /// snapshots), and the ranking: who's most worth showing right now.
    /// </summary>
    public sealed class LobbyBoard
    {
        private readonly Func<PriorityConfig> _config;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly Dictionary<string, LobbyLive> _lobbies = new Dictionary<string, LobbyLive>(StringComparer.OrdinalIgnoreCase);

        public LobbyBoard(Func<PriorityConfig> config, Func<DateTime>? clock = null)
        {
            _config = config;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>A player's roster name (key, Discord ID, in-game name), or null to use the game's name.</summary>
        public Func<string, string?, string, string?>? RosterName { get; set; }

        /// <summary>Raised for every new or updated play, for the notification cards.</summary>
        public event Action<string, Play>? PlayChanged;

        public LobbyLive? Lobby(string name)
        {
            lock (_lock) return _lobbies.TryGetValue(name, out var l) ? l : null;
        }

        // ---- Taking messages ----------------------------------------------------------------

        /// <summary>One message from a lobby (a feed item, as JSON).</summary>
        public void Apply(string json)
        {
            using var doc = JsonDocument.Parse(json);
            Apply(doc.RootElement);
        }

        public void Apply(JsonElement m)
        {
            string lobby = Str(m, "lobby") ?? "";
            if (lobby.Length == 0) return;
            lock (_lock)
            {
                if (!_lobbies.TryGetValue(lobby, out var l)) _lobbies[lobby] = l = new LobbyLive { Lobby = lobby };
                var now = _clock();
                l.LastSeen = now;
                l.Game = Str(m, "game") ?? l.Game;
                l.Round = Int(m, "round") ?? l.Round;
                l.Clock = Dbl(m, "clock");
                if (Str(m, "type") == "snap") Snapshot(l, m, now);
                else if (Str(m, "type") == "event") Event(l, m, now);
            }
        }

        private void Snapshot(LobbyLive l, JsonElement m, DateTime now)
        {
            l.Phase = Str(m, "phase") ?? l.Phase;
            l.Map = Str(m, "map") ?? l.Map;
            l.Crew = Int(m, "crewAlive");
            l.Imps = Int(m, "impAlive");
            l.TaskPct = Int(m, "taskPct");
            l.Danger = Bool(m, "danger") ?? false;
            l.Video = Bool(m, "video") ?? false;
            if (m.TryGetProperty("spec", out var spec) && spec.ValueKind == JsonValueKind.Object) l.Spec = spec.Clone();
            if (m.TryGetProperty("players", out var people) && people.ValueKind == JsonValueKind.Array) People(l, people, replace: true);
            l.IfEnded = m.TryGetProperty("ifEnded", out var ie) && ie.ValueKind == JsonValueKind.Object ? ie.Clone() : null;
            if (m.TryGetProperty("sabotage", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                l.Sabotage = Str(s, "system");
                l.SabotageCritical = Bool(s, "critical") ?? false;
                l.SabotageLeft = Dbl(s, "timeLeft");
                l.SabotageFixing = Dbl(s, "fixing");
            }
            else
            {
                l.Sabotage = null;
                l.SabotageLeft = null;
                l.SabotageFixing = null;
                l.SabotageCritical = false;
            }
            States(l, now);
        }

        /// <summary>The ongoing states, from the latest snapshot. A state that stops fades out.</summary>
        private void States(LobbyLive l, DateTime now)
        {
            var c = _config();
            bool inGame = l.Phase == "ingame" || l.Phase == "meeting";
            int crew = l.Crew ?? 0, imps = l.Imps ?? 0;

            State(l, now, "lobby", "lobby", !inGame, l.Phase == "ended" ? "Game over" : l.Phase == "menu" ? "Not in a lobby" : "In the lobby");
            State(l, now, "inGame", "inGame", inGame, "Playing");
            State(l, now, "meeting", "meeting", l.Phase == "meeting", l.MeetingText ?? "Meeting", l.MeetingSource);
            State(l, now, "danger", "danger", inGame && l.Danger, l.DangerText ?? "Danger: an impostor is alone with a crewmate");
            var crit = c.Rule("criticalSabotage");
            bool critical = inGame && l.SabotageCritical && l.SabotageLeft.HasValue && l.SabotageLeft.Value < (crit.Threshold ?? 15);
            string sabText = l.Sabotage == null ? "" : l.SabotageLeft.HasValue ? $"{l.Sabotage} {Math.Ceiling(l.SabotageLeft.Value)}s" : $"{l.Sabotage} sabotaged";
            State(l, now, "criticalSabotage", "criticalSabotage", critical, sabText + ", nobody fixing");
            State(l, now, "sabotage", "sabotage", inGame && l.Sabotage != null && !critical, sabText);
            State(l, now, "oneKillFromWin", "oneKillFromWin", inGame && imps > 0 && crew - imps == 1, $"One kill from an impostor win ({crew} crew v {imps})");
            State(l, now, "taskBar", "taskBar", inGame && (l.TaskPct ?? 0) >= (c.Rule("taskBar").Threshold ?? 85), $"Task bar {l.TaskPct}%");
            State(l, now, "finalPlayers", "finalPlayers", inGame && crew + imps > 0 && crew + imps <= (c.Rule("finalPlayers").Threshold ?? 3), $"Final {crew + imps}");
            State(l, now, "closeCounts", "closeCounts", inGame && imps > 0 && crew - imps > 1 && crew - imps <= (c.Rule("closeCounts").Threshold ?? 2), $"Close: {crew} crew v {imps}");
        }

        private void State(LobbyLive l, DateTime now, string key, string rule, bool on, string text, JsonElement? source = null)
        {
            l.Plays.TryGetValue(key, out var p);
            if (on)
            {
                if (p == null || p.Ended != null)
                {
                    p = new Play { Key = key, Rule = rule, IsState = true, At = now, Updated = now, Text = text, Source = source };
                    l.Plays[key] = p;
                    PlayChanged?.Invoke(l.Lobby, p);
                }
                else if (p.Text != text)
                {
                    p.Text = text;
                    p.Updated = now;
                    PlayChanged?.Invoke(l.Lobby, p);
                }
            }
            else if (p != null && p.Ended == null)
            {
                p.Ended = now;
            }
        }

        private void People(LobbyLive l, JsonElement list, bool replace)
        {
            if (replace) l.People.Clear();
            foreach (var p in list.EnumerateArray())
            {
                int id = Int(p, "id") ?? -1;
                if (id < 0) continue;
                l.People[id] = new LobbyPlayer
                {
                    Id = id, Name = Str(p, "name") ?? "", Color = Int(p, "color") ?? 0, Key = Str(p, "key") ?? "",
                    Discord = Str(p, "discord"), Imp = Bool(p, "imp"), Dead = Bool(p, "dead"),
                };
            }
        }

        /// <summary>A player's name as the caster shows it: the roster's, else the game's.</summary>
        public string DisplayName(LobbyLive l, int id, string fallback)
        {
            if (l.People.TryGetValue(id, out var p) && RosterName?.Invoke(p.Key, p.Discord, p.Name) is { } real) return real;
            return fallback;
        }

        /// <summary>A name tag ([[colour|name]]) for a player object in an event.</summary>
        private string? Tag(LobbyLive l, JsonElement m, string prop)
        {
            if (!m.TryGetProperty(prop, out var p) || p.ValueKind != JsonValueKind.Object) return null;
            int id = Int(p, "id") ?? -1;
            string fallback = Str(p, "display") ?? Str(p, "name") ?? Str(p, "colorName") ?? "?";
            return NameTag.Make(Int(p, "color") ?? 0, DisplayName(l, id, fallback));
        }

        private void Event(LobbyLive l, JsonElement m, DateTime now)
        {
            string kind = Str(m, "kind") ?? "";
            string? Name(string prop) => Tag(l, m, prop);
            string Id(string prop) => m.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object ? (Int(p, "id") ?? -1).ToString() : "?";
            string In(string? room) => room == null ? "" : " in " + room;
            switch (kind)
            {
                case "kill":
                {
                    bool winning = Bool(m, "winning") ?? false;
                    string text = $"{Name("killer") ?? "Someone"} killed {Name("victim") ?? "someone"}{In(Str(m, "room"))}";
                    Add(l, now, $"kill:{Long(m, "t")}", winning ? "winningKill" : "kill", winning ? text + ": impostors win" : text, m);
                    break;
                }
                case "eject":
                {
                    string? who = Name("ejected");
                    if (who == null) Add(l, now, $"eject:{Long(m, "t")}", "ejectSkipped", Bool(m, "tie") == true ? "Tied vote: nobody ejected" : "Vote skipped", m);
                    else Add(l, now, $"eject:{Long(m, "t")}", "eject", $"{who} ejected: {(Bool(m, "wasImpostor") == true ? "Impostor" : "not the Impostor")}", m);
                    l.MeetingText = null;
                    l.MeetingSource = null;
                    break;
                }
                case "meeting":
                    l.MeetingSource = m.Clone();
                    l.MeetingText = Bool(m, "emergency") == true
                        ? $"{Name("caller") ?? "Someone"} called an emergency meeting"
                        : $"{Name("caller") ?? "Someone"} reported {Name("body") ?? "a"}'s body";
                    if (l.Plays.TryGetValue("meeting", out var mp) && mp.Ended == null) { mp.Text = l.MeetingText; mp.Updated = now; mp.Source = l.MeetingSource; PlayChanged?.Invoke(l.Lobby, mp); }
                    break;
                case "danger":
                    if (Str(m, "state") == "start")
                    {
                        l.DangerText = $"{Name("impostor") ?? "An impostor"} alone with {Name("crewmate") ?? "a crewmate"}{In(Str(m, "room"))}, kill ready";
                        if (l.Plays.TryGetValue("danger", out var dp) && dp.Ended == null) { dp.Text = l.DangerText; dp.Updated = now; dp.Source = m.Clone(); PlayChanged?.Invoke(l.Lobby, dp); }
                    }
                    break;
                case "vent":
                    if (Str(m, "action") == "enter")
                        Add(l, now, $"vent:{Id("player")}", "vent", $"{Name("player") ?? "Someone"} vented{In(Str(m, "room"))}", m);
                    break;
                case "witnessed_kill":
                    Add(l, now, $"witnessed:{Long(m, "t")}", "witnessedKill", $"{Name("witness") ?? "Someone"} SAW {Name("impostor") ?? "the impostor"} kill{In(Str(m, "room"))}", m);
                    break;
                case "witnessed_vent":
                    Add(l, now, $"witnessedVent:{Id("witness")}:{Id("impostor")}", "witnessedVent", $"{Name("witness") ?? "Someone"} SAW {Name("impostor") ?? "the impostor"} vent{In(Str(m, "room"))}", m);
                    break;
                case "gameStart":
                    l.Map = Str(m, "map") ?? l.Map;
                    l.Players = m.TryGetProperty("players", out var ps) && ps.ValueKind == JsonValueKind.Array ? ps.EnumerateArray().Select(x => x.Clone()).ToList() : new List<JsonElement>();
                    if (m.TryGetProperty("roster", out var rs) && rs.ValueKind == JsonValueKind.Array) People(l, rs, replace: true);
                    l.DangerText = l.MeetingText = null;
                    l.MeetingSource = null;
                    foreach (var key in l.Plays.Keys.Where(k => !l.Plays[k].IsState).ToList()) l.Plays.Remove(key);
                    Add(l, now, "gameStart", "gameStart", $"Game started on {l.Map ?? "the map"}", m);
                    break;
                case "gameEnd":
                {
                    string how = Str(m, "how") switch { "tasks" => " on tasks", "vote" => " by vote", "kills" => " by kills", "sabotage" => " by sabotage", "disconnect" => " (someone left)", _ => "" };
                    string winner = Str(m, "winner") ?? "";
                    Add(l, now, "gameEnd", "gameEnd", Bool(m, "abandoned") == true ? "Game abandoned" : winner.Length > 0 ? $"{winner} win{how}" : "Game over", m);
                    break;
                }
            }
        }

        private void Add(LobbyLive l, DateTime now, string key, string rule, string text, JsonElement source)
        {
            if (l.Plays.TryGetValue(key, out var p) && !p.IsState)
            {
                p.Repeats++;
                p.Updated = now;
                p.Text = text;
                p.Source = source.Clone();
            }
            else
            {
                p = new Play { Key = key, Rule = rule, Text = text, At = now, Updated = now, Source = source.Clone() };
                l.Plays[key] = p;
            }
            PlayChanged?.Invoke(l.Lobby, p);
        }

        // ---- Ranking ------------------------------------------------------------------------

        /// <summary>A play's points now: full while it lasts or holds, then halving every half-life.</summary>
        public static double ValueOf(Play p, PriorityRule rule, double halfLife, DateTime now)
        {
            if (!rule.On) return 0;
            if (p.IsState && p.Ended == null) return rule.Points;
            var since = p.IsState ? p.Ended!.Value : p.Updated;
            double age = (now - since).TotalSeconds - (p.IsState ? 0 : rule.HoldSeconds);
            if (age <= 0) return rule.Points;
            return rule.Points * Math.Pow(0.5, age / Math.Max(0.1, halfLife));
        }

        public List<LobbyRank> Ranking()
        {
            var c = _config();
            var now = _clock();
            var list = new List<LobbyRank>();
            lock (_lock)
            {
                foreach (var l in _lobbies.Values)
                {
                    bool online = (now - l.LastSeen).TotalSeconds <= c.OfflineAfterSeconds;
                    foreach (var p in l.Plays.Values) p.Value = Math.Round(ValueOf(p, c.Rule(p.Rule), c.HalfLifeSeconds, now), 1);
                    // Faded-out plays go (states that are still on stay).
                    foreach (var k in l.Plays.Where(kv => kv.Value.Value < 0.5 && !(kv.Value.IsState && kv.Value.Ended == null)).Select(kv => kv.Key).ToList())
                        l.Plays.Remove(k);
                    var plays = l.Plays.Values.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenByDescending(p => p.Updated).ToList();
                    double score = 0;
                    if (online && plays.Count > 0)
                    {
                        // Other plays add a little, but never lift a lobby into a higher tier than its top play's.
                        double top = plays[0].Value;
                        score = Math.Min(c.CeilingFor(top), top + c.OthersShare * plays.Skip(1).Sum(p => p.Value));
                    }
                    list.Add(new LobbyRank
                    {
                        Lobby = l.Lobby,
                        Online = online,
                        Score = Math.Round(score, 1),
                        Tier = online ? c.TierOf(score) : "offline",
                        Line = online ? Line(l, plays) : "Offline: no data from this lobby",
                        Phase = l.Phase,
                        Crew = l.Crew,
                        Imps = l.Imps,
                        TaskPct = l.TaskPct,
                        Game = l.Game,
                        Round = l.Round,
                        Spec = l.Spec,
                        Players = l.Players,
                        People = l.People.Values.OrderBy(p => p.Id).ToList(),
                        Map = l.Map,
                        Sabotage = l.Sabotage,
                        SabotageCritical = l.SabotageCritical,
                        SabotageLeft = l.SabotageLeft,
                        SabotageFixing = l.SabotageFixing,
                        Danger = l.Danger,
                        Clock = l.Clock,
                        Plays = plays.Select(Copy).ToList(),
                    });
                }
            }
            return list.OrderByDescending(r => r.Online).ThenByDescending(r => r.Score).ThenBy(r => r.Lobby).ToList();
        }

        /// <summary>The status line: the top play, plus who's left.</summary>
        private static string Line(LobbyLive l, List<Play> plays)
        {
            string top = plays.Count > 0 ? plays[0].Text : l.Phase == "lobby" ? "In the lobby" : "Quiet";
            bool inGame = l.Phase == "ingame" || l.Phase == "meeting";
            return inGame && l.Crew.HasValue ? $"{top} · {l.Crew} crew v {l.Imps} imp · tasks {l.TaskPct}%" : top;
        }

        private static Play Copy(Play p) => new Play
        {
            Key = p.Key, Rule = p.Rule, Text = p.Text, At = p.At, Updated = p.Updated, Ended = p.Ended,
            IsState = p.IsState, Repeats = p.Repeats, Value = p.Value, Source = p.Source,
        };

        // ---- JSON helpers -------------------------------------------------------------------

        private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static int? Int(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;
        private static long? Long(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : (long?)null;
        private static double? Dbl(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null;
        private static bool? Bool(JsonElement e, string p) => e.TryGetProperty(p, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : (bool?)null;
    }
}
