using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TournamentTracker.Stats;

namespace TournamentTracker.App.Broadcast
{
    public sealed class ArchivedPlayer
    {
        /// <summary>The player's number in that game.</summary>
        public int Id { get; set; }
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public int Color { get; set; }
        public bool Imp { get; set; }
        public int Kills { get; set; }
        /// <summary>"killed", "ejected", "left", or null for alive at the end.</summary>
        public string? Death { get; set; }
        public string? KilledBy { get; set; }
        public bool? Won { get; set; }
        /// <summary>Points from the tournament's point sheet, once the scored game record is in.</summary>
        public double? Points { get; set; }
    }

    public sealed class ArchivedKill
    {
        public double At { get; set; }
        public string Killer { get; set; } = "";
        public string Victim { get; set; } = "";
        public string? Room { get; set; }
        public bool Winning { get; set; }
        public bool Witnessed { get; set; }
    }

    public sealed class ArchivedMeeting
    {
        public double At { get; set; }
        public string? Caller { get; set; }
        public string? Body { get; set; }
        public string? Ejected { get; set; }
        public bool? WasImpostor { get; set; }
        public bool Skipped { get; set; }
        public bool Tie { get; set; }
        /// <summary>Voter key → target key ("" for a skip).</summary>
        public Dictionary<string, string> Votes { get; set; } = new Dictionary<string, string>();
    }

    public sealed class ArchivedSabotage
    {
        public double At { get; set; }
        public string System { get; set; } = "";
        public double? FixedAt { get; set; }
        public double? LeftWhenFixed { get; set; }
    }

    /// <summary>One finished game, as the caster saw it (and, once it's in, as the tournament scored it).</summary>
    public sealed class ArchivedGame
    {
        public string Id { get; set; } = "";
        public string Lobby { get; set; } = "";
        public int Round { get; set; }
        public string Game { get; set; } = "";
        /// <summary>The referee: whoever hosts the lobby (its label).</summary>
        public string Referee { get; set; } = "";
        public string? Map { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public double Length { get; set; }
        public string? Winner { get; set; }
        public string? How { get; set; }
        public string? Reason { get; set; }
        public int TaskPct { get; set; }
        public bool Scored { get; set; }
        public List<ArchivedPlayer> Players { get; set; } = new List<ArchivedPlayer>();
        public List<ArchivedKill> Kills { get; set; } = new List<ArchivedKill>();
        public List<ArchivedMeeting> Meetings { get; set; } = new List<ArchivedMeeting>();
        public List<ArchivedSabotage> Sabotages { get; set; } = new List<ArchivedSabotage>();

        public ArchivedPlayer? P(string key) => Players.FirstOrDefault(p => p.Key == key);
    }

    /// <summary>
    /// The stats database on the caster's PC: every game (lobby, round, referee, players, roles,
    /// every kill with who/whom/where/when, votes, ejections, sabotages, task bar, winner, how,
    /// length), one JSON file per game in broadcast-games/, built from the lobbies' live data and
    /// completed with the tournament's scored game records when they come in.
    /// </summary>
    public sealed class GameArchive
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly string? _folder;
        private readonly object _lock = new object();
        private readonly List<ArchivedGame> _games = new List<ArchivedGame>();
        private readonly Dictionary<string, ArchivedGame> _live = new Dictionary<string, ArchivedGame>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTime> _clock;

        /// <summary>Raised when a game ends and is saved.</summary>
        public event Action<ArchivedGame>? Finished;

        public GameArchive(string? folder, Func<DateTime>? clock = null)
        {
            _folder = folder;
            _clock = clock ?? (() => DateTime.UtcNow);
            if (folder == null || !Directory.Exists(folder)) return;
            foreach (var f in Directory.GetFiles(folder, "*.json"))
                try { if (JsonSerializer.Deserialize<ArchivedGame>(File.ReadAllText(f), Json) is { } g) _games.Add(g); } catch (Exception) { }
            _games.Sort((a, b) => a.StartedAt.CompareTo(b.StartedAt));
        }

        public string? Folder => _folder;

        public List<ArchivedGame> Games { get { lock (_lock) return _games.ToList(); } }

        /// <summary>Today's games (local date), oldest first.</summary>
        public List<ArchivedGame> Today() { var d = _clock().ToLocalTime().Date; lock (_lock) return _games.Where(g => g.StartedAt.ToLocalTime().Date == d).ToList(); }

        public List<ArchivedGame> InRound(int round) { lock (_lock) return _games.Where(g => g.Round == round).ToList(); }

        public ArchivedGame? Live(string lobby) { lock (_lock) return _live.TryGetValue(lobby, out var g) ? g : null; }

        /// <summary>
        /// One message from a lobby. <paramref name="who"/> turns a player object into (key, real name, colour).
        /// </summary>
        public void Feed(string lobby, JsonElement m, Func<JsonElement, (string Key, string Name, int Color)?> who)
        {
            if (!m.TryGetProperty("kind", out var k) || m.GetProperty("type").GetString() != "event") return;
            string kind = k.GetString() ?? "";
            double at = m.TryGetProperty("clock", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
            string? Key(string prop) => m.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object ? who(p)?.Key : null;
            ArchivedGame? done = null;
            lock (_lock)
            {
                _live.TryGetValue(lobby, out var g);
                switch (kind)
                {
                    case "gameStart":
                        g = _live[lobby] = new ArchivedGame
                        {
                            Lobby = lobby, Referee = lobby,
                            Round = m.TryGetProperty("round", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0,
                            Game = m.TryGetProperty("game", out var gn) ? gn.GetString() ?? "" : "",
                            Map = m.TryGetProperty("map", out var map) ? map.GetString() : null,
                            StartedAt = _clock(),
                        };
                        g.Id = $"{Safe(g.Game.Length > 0 ? g.Game : lobby)}-{g.StartedAt:yyyyMMdd-HHmmss}";
                        if (m.TryGetProperty("players", out var ps))
                            foreach (var p in ps.EnumerateArray())
                                if (who(p) is { } w)
                                    g.Players.Add(new ArchivedPlayer { Id = p.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt32() : -1, Key = w.Key, Name = w.Name, Color = w.Color, Imp = p.TryGetProperty("imp", out var i) && i.ValueKind == JsonValueKind.True });
                        break;
                    case "kill" when g != null:
                    {
                        var kill = new ArchivedKill { At = at, Killer = Key("killer") ?? "", Victim = Key("victim") ?? "", Room = Str(m, "room"), Winning = m.TryGetProperty("winning", out var w) && w.ValueKind == JsonValueKind.True };
                        g.Kills.Add(kill);
                        if (g.P(kill.Killer) is { } killer) killer.Kills++;
                        if (g.P(kill.Victim) is { } victim) { victim.Death = "killed"; victim.KilledBy = kill.Killer; }
                        break;
                    }
                    case "witnessed_kill" when g != null:
                        if (g.Kills.LastOrDefault(x => x.Killer == Key("impostor")) is { } seen) seen.Witnessed = true;
                        break;
                    case "meeting" when g != null:
                        g.Meetings.Add(new ArchivedMeeting { At = at, Caller = Key("caller"), Body = Key("body") });
                        break;
                    case "eject" when g != null:
                    {
                        var mt = g.Meetings.LastOrDefault() ?? new ArchivedMeeting { At = at };
                        if (!g.Meetings.Contains(mt)) g.Meetings.Add(mt);
                        mt.Ejected = Key("ejected");
                        mt.WasImpostor = m.TryGetProperty("wasImpostor", out var wi) && wi.ValueKind == JsonValueKind.True ? true : mt.Ejected == null ? null : false;
                        mt.Skipped = m.TryGetProperty("skipped", out var sk) && sk.ValueKind == JsonValueKind.True;
                        mt.Tie = m.TryGetProperty("tie", out var tie) && tie.ValueKind == JsonValueKind.True;
                        if (m.TryGetProperty("votes", out var votes) && votes.ValueKind == JsonValueKind.Array)
                            foreach (var v in votes.EnumerateArray())
                            {
                                string? voter = PlayerKey(g, v, "voter"), target = PlayerKey(g, v, "target");
                                if (voter != null) mt.Votes[voter] = target ?? "";
                            }
                        if (mt.Ejected != null && g.P(mt.Ejected) is { } out1) out1.Death = "ejected";
                        break;
                    }
                    case "sabotage" when g != null:
                    {
                        string sys = Str(m, "system") ?? "";
                        if (Str(m, "state") == "start") g.Sabotages.Add(new ArchivedSabotage { At = at, System = sys });
                        else if (g.Sabotages.LastOrDefault(x => x.System == sys && x.FixedAt == null) is { } open)
                        {
                            open.FixedAt = at;
                            open.LeftWhenFixed = m.TryGetProperty("timeLeft", out var tl) && tl.ValueKind == JsonValueKind.Number ? tl.GetDouble() : null;
                        }
                        break;
                    }
                    case "tasks" when g != null:
                        if (m.TryGetProperty("pct", out var pct) && pct.ValueKind == JsonValueKind.Number) g.TaskPct = pct.GetInt32();
                        break;
                    case "gameEnd" when g != null:
                        g.EndedAt = _clock();
                        g.Length = at > 0 ? at : (g.EndedAt.Value - g.StartedAt).TotalSeconds;
                        g.Winner = Str(m, "winner");
                        g.How = Str(m, "how");
                        g.Reason = Str(m, "reason");
                        foreach (var p in g.Players)
                            p.Won = g.Winner == null ? null : p.Imp == (g.Winner == Outcome.Impostors) && p.Death != "left";
                        _live.Remove(lobby);
                        if (m.TryGetProperty("abandoned", out var ab) && ab.ValueKind == JsonValueKind.True) break;
                        _games.Add(g);
                        done = g;
                        break;
                }
            }
            if (done != null)
            {
                Save(done);
                Finished?.Invoke(done);
            }
        }

        private static string? PlayerKey(ArchivedGame g, JsonElement v, string prop) =>
            v.TryGetProperty(prop, out var id) && id.ValueKind == JsonValueKind.Number ? g.Players.FirstOrDefault(p => p.Id == id.GetInt32())?.Key : null;

        /// <summary>Fills in points and wins from the tournament's scored game records (the shared results).</summary>
        public void Merge(IEnumerable<GameRecord> records)
        {
            var changed = new List<ArchivedGame>();
            lock (_lock)
                foreach (var rec in records)
                {
                    var g = _games.FirstOrDefault(x => !x.Scored && x.Game == rec.Name && x.Round == rec.Round && Math.Abs((x.StartedAt - rec.StartedUtc).TotalMinutes) < 15);
                    if (g == null) continue;
                    foreach (var p in rec.Players)
                    {
                        var ap = g.P(p.Key);
                        if (ap == null) continue;
                        ap.Points = p.Points;
                        ap.Won = p.Won;
                    }
                    g.Scored = true;
                    changed.Add(g);
                }
            foreach (var g in changed) Save(g);
        }

        private void Save(ArchivedGame g)
        {
            if (_folder == null) return;
            try
            {
                Directory.CreateDirectory(_folder);
                File.WriteAllText(Path.Combine(_folder, g.Id + ".json"), JsonSerializer.Serialize(g, Json));
            }
            catch (Exception) { }
        }

        private static string? Str(JsonElement m, string p) => m.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
    }
}
