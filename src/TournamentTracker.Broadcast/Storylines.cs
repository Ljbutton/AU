using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>A talking point for the caster.</summary>
    public sealed class Note
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        /// <summary>The note, with name tags ("[[colour|Name]]").</summary>
        public string Text { get; set; } = "";
        public List<string> Players { get; set; } = new List<string>();
        public double Weight { get; set; }
        public bool Pinned { get; set; }
    }

    /// <summary>
    /// Storyline notes from the stats database: win records, streaks, first blood, most kills, fastest
    /// wins, who keeps killing whom, who keeps getting voted out, and milestones about to be hit.
    /// Notes about players in the lobbies on stream come first. Pin, dismiss or mark used
    /// (kept in notes-state.json).
    /// </summary>
    public sealed class Storylines
    {
        private sealed class State
        {
            public HashSet<string> Pinned { get; set; } = new HashSet<string>();
            public HashSet<string> Dismissed { get; set; } = new HashSet<string>();
            public HashSet<string> Used { get; set; } = new HashSet<string>();
        }

        private readonly GameArchive _archive;
        private readonly Func<Tables?> _tables;
        private readonly string? _statePath;
        private readonly object _lock = new object();
        private State _state = new State();

        public Storylines(GameArchive archive, Func<Tables?> tables, string? statePath)
        {
            _archive = archive;
            _tables = tables;
            _statePath = statePath;
            try { if (statePath != null && File.Exists(statePath)) _state = JsonSerializer.Deserialize<State>(File.ReadAllText(statePath)) ?? new State(); } catch (Exception) { }
        }

        /// <summary>pin / unpin / dismiss / used / restore.</summary>
        public void Mark(string id, string what)
        {
            lock (_lock)
            {
                switch (what)
                {
                    case "pin": _state.Pinned.Add(id); _state.Dismissed.Remove(id); break;
                    case "unpin": _state.Pinned.Remove(id); break;
                    case "dismiss": _state.Dismissed.Add(id); _state.Pinned.Remove(id); break;
                    case "used": _state.Used.Add(id); _state.Pinned.Remove(id); break;
                    case "restore": _state.Dismissed.Remove(id); _state.Used.Remove(id); break;
                }
                try { if (_statePath != null) File.WriteAllText(_statePath, JsonSerializer.Serialize(_state)); } catch (Exception) { }
            }
        }

        private static string Ord(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
        private static string Time(double s) => $"{(int)s / 60}:{(int)s % 60:00}";

        /// <summary>
        /// Every note that applies now, most relevant first. <paramref name="focus"/>: players in the
        /// lobbies on stream (or about to be), whose notes go first.
        /// </summary>
        public List<Note> Notes(ISet<string> focus, int max = 40)
        {
            var today = _archive.Today();
            var notes = new List<Note>();
            string day = DateTime.Now.ToString("yyyy-MM-dd");
            var names = new Dictionary<string, (string Name, int Color)>();
            foreach (var g in today) foreach (var p in g.Players) names[p.Key] = (p.Name, p.Color);
            string T(string key) => names.TryGetValue(key, out var n) ? NameTag.Make(n.Color, n.Name) : key;
            void Add(string kind, string text, double weight, params string[] players) =>
                notes.Add(new Note { Id = $"{day}|{kind}|{string.Join(",", players)}|{text.GetHashCode():x}", Kind = kind, Text = text, Weight = weight, Players = players.ToList() });

            var finished = today.Where(g => g.Winner != null).ToList();
            // Win records and streaks.
            foreach (var key in names.Keys)
            {
                var mine = finished.Where(g => g.P(key) != null).ToList();
                var asImp = mine.Where(g => g.P(key)!.Imp).ToList();
                var asCrew = mine.Where(g => !g.P(key)!.Imp).ToList();
                if (asImp.Count >= 2) Add("record", $"{T(key)} is {asImp.Count(g => g.P(key)!.Won == true)}–{asImp.Count(g => g.P(key)!.Won != true)} as impostor today", 2 + asImp.Count * 0.3, key);
                if (asCrew.Count >= 3) Add("record", $"{T(key)} is {asCrew.Count(g => g.P(key)!.Won == true)}–{asCrew.Count(g => g.P(key)!.Won != true)} as crewmate today", 1.5 + asCrew.Count * 0.2, key);
                int streak = 0; bool? wins = null;
                foreach (var g in mine.AsEnumerable().Reverse())
                {
                    bool won = g.P(key)!.Won == true;
                    if (wins == null) wins = won;
                    if (won != wins) break;
                    streak++;
                }
                if (streak >= 3) Add("streak", wins == true ? $"{T(key)} has won {streak} in a row" : $"{T(key)} has lost {streak} in a row", 3 + streak * 0.5, key);
                if (streak == 4 && wins == true) Add("milestone", $"One more win and {T(key)} makes it 5 in a row", 4, key);
                int kills = today.Sum(g => g.P(key)?.Kills ?? 0);
                if (kills is 4 or 9 or 14 or 19 or 24) Add("milestone", $"{T(key)} is one kill from {kills + 1} today", 3, key);
            }
            // First blood of the day, and of the round.
            var first = today.Where(g => g.Kills.Count > 0).OrderBy(g => g.StartedAt.AddSeconds(g.Kills[0].At)).FirstOrDefault();
            if (first != null) Add("firstBlood", $"First blood of the day: {T(first.Kills[0].Killer)} on {T(first.Kills[0].Victim)}{(first.Kills[0].Room != null ? " in " + first.Kills[0].Room : "")} ({first.Game})", 2, first.Kills[0].Killer, first.Kills[0].Victim);
            int round = today.Select(g => g.Round).DefaultIfEmpty(0).Max();
            var firstRound = today.Where(g => g.Round == round && g.Kills.Count > 0).OrderBy(g => g.StartedAt.AddSeconds(g.Kills[0].At)).FirstOrDefault();
            if (firstRound != null && firstRound != first) Add("firstBlood", $"First blood of round {round}: {T(firstRound.Kills[0].Killer)} on {T(firstRound.Kills[0].Victim)}", 2, firstRound.Kills[0].Killer, firstRound.Kills[0].Victim);
            // Most kills today.
            var killers = names.Keys.Select(k => (Key: k, Kills: today.Sum(g => g.P(k)?.Kills ?? 0))).Where(x => x.Kills >= 3).OrderByDescending(x => x.Kills).ToList();
            if (killers.Count > 0) Add("mostKills", $"{T(killers[0].Key)} leads today with {killers[0].Kills} kills", 2.5, killers[0].Key);
            // Fastest win.
            foreach (var side in new[] { "Impostors", "Crewmates" })
            {
                var fast = finished.Where(g => g.Winner == side && g.Length > 0).OrderBy(g => g.Length).FirstOrDefault();
                if (fast != null) Add("fastest", $"Fastest {side.ToLowerInvariant()} win today: {Time(fast.Length)} ({fast.Game}{(fast.How != null ? ", by " + fast.How : "")})", 1.5, fast.Players.Where(p => p.Imp == (side == "Impostors")).Select(p => p.Key).ToArray());
            }
            // Who keeps killing whom.
            foreach (var pair in today.SelectMany(g => g.Kills).GroupBy(k => (k.Killer, k.Victim)).Where(x => x.Count() >= 2))
                Add("rivalry", $"{T(pair.Key.Killer)} has killed {T(pair.Key.Victim)} {pair.Count()} times today", 3 + pair.Count(), pair.Key.Killer, pair.Key.Victim);
            // Who keeps getting voted out.
            foreach (var ej in today.SelectMany(g => g.Meetings).Where(m => m.Ejected != null).GroupBy(m => m.Ejected!).Where(x => x.Count() >= 2))
                Add("votedOut", $"{T(ej.Key)} has been voted out {ej.Count()} times today", 2 + ej.Count() * 0.5, ej.Key);
            // Standings: who leads, and by how much.
            var tables = _tables();
            if (tables != null)
            {
                int r = tables.CurrentRound;
                var rows = r > 0 ? tables.Round(r) : new List<TableRow>();
                if (rows.Count >= 2 && rows[0].Points > 0)
                {
                    double gap = Math.Round(rows[0].Points - rows[1].Points, 2);
                    string a = NameTag.Make(rows[0].Color, rows[0].Name), b = NameTag.Make(rows[1].Color, rows[1].Name);
                    Add("standings", gap <= 5
                        ? $"{a} leads round {r} by {gap} over {b}: one win could flip it"
                        : $"{a} leads round {r} by {gap} points; one more win all but clinches first place", 3, rows[0].Key, rows[1].Key);
                }
            }

            lock (_lock)
            {
                foreach (var n in notes)
                {
                    n.Pinned = _state.Pinned.Contains(n.Id);
                    if (n.Players.Any(focus.Contains)) n.Weight += 5;
                    if (n.Pinned) n.Weight += 100;
                }
                return notes.Where(n => !_state.Dismissed.Contains(n.Id) && !_state.Used.Contains(n.Id))
                    .GroupBy(n => n.Id).Select(g => g.First())
                    .OrderByDescending(n => n.Weight).Take(max).ToList();
            }
        }
    }
}
