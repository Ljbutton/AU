using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>One line of a lobby's event feed on the Watch page: when, what kind, and in words (with name tags).</summary>
    public sealed class LobbyEvent
    {
        public DateTime At { get; set; }
        /// <summary>"kill", "body", "meeting", "votes", "eject", "sabotage", "start", "end".</summary>
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// Each lobby's recent events in words for the Watch page's feed: kills (who and where), bodies
    /// reported, emergency meetings, the votes, ejections, sabotages, and the game starting and ending.
    /// Fed with the same live events the desk's cards come from (stale ones never reach it). The last
    /// <see cref="Keep"/> per lobby, at most <see cref="MaxLobbies"/> lobbies. Names as the desk shows
    /// them (the roster's), and impostors are named: the Watch page is behind its code.
    /// </summary>
    public sealed class LobbyEvents
    {
        public const int Keep = 30;
        public const int MaxLobbies = 64;
        private readonly object _lock = new object();
        private readonly Func<DateTime> _clock;
        private readonly Func<string, JsonElement, string?> _tag;
        private readonly Func<string, int, string?> _byId;
        private readonly Dictionary<string, List<LobbyEvent>> _lobbies = new Dictionary<string, List<LobbyEvent>>(StringComparer.OrdinalIgnoreCase);

        /// <param name="tag">A player object in an event as a name tag ([[colour|name]]), for the lobby.</param>
        /// <param name="byId">A player id in the lobby as a name tag (for the votes).</param>
        public LobbyEvents(Func<DateTime> clock, Func<string, JsonElement, string?> tag, Func<string, int, string?> byId)
        {
            _clock = clock;
            _tag = tag;
            _byId = byId;
        }

        /// <summary>A lobby's event, as the lobby sent it.</summary>
        public void Add(string lobby, JsonElement m)
        {
            if (lobby.Length == 0) return;
            var now = _clock();
            var lines = Lines(lobby, m);
            if (lines.Count == 0) return;
            lock (_lock)
            {
                if (!_lobbies.TryGetValue(lobby, out var list))
                {
                    if (_lobbies.Count >= MaxLobbies) _lobbies.Remove(_lobbies.OrderBy(kv => kv.Value.LastOrDefault()?.At ?? default).First().Key);
                    _lobbies[lobby] = list = new List<LobbyEvent>();
                }
                foreach (var (kind, text) in lines) list.Add(new LobbyEvent { At = now, Kind = kind, Text = text });
                if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
            }
        }

        /// <summary>A lobby's last <paramref name="count"/> events, newest first.</summary>
        public List<LobbyEvent> Recent(string lobby, int count = 8)
        {
            lock (_lock)
                return _lobbies.TryGetValue(lobby, out var list)
                    ? list.AsEnumerable().Reverse().Take(count).Select(e => new LobbyEvent { At = e.At, Kind = e.Kind, Text = e.Text }).ToList()
                    : new List<LobbyEvent>();
        }

        public void Forget(Func<string, bool> which)
        {
            lock (_lock) foreach (var k in _lobbies.Keys.Where(which).ToList()) _lobbies.Remove(k);
        }

        private List<(string Kind, string Text)> Lines(string lobby, JsonElement m)
        {
            var lines = new List<(string, string)>();
            string? Who(string prop) => m.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object ? _tag(lobby, p) : null;
            string In(string? room) => string.IsNullOrEmpty(room) ? "" : " in " + room;
            switch (m.Str("kind") ?? "")
            {
                case "kill":
                    lines.Add(("kill", $"{Who("killer") ?? "Someone"} killed {Who("victim") ?? "someone"}{In(m.Str("room"))}{(m.Flag("winning") ? ": impostors win" : "")}"));
                    break;
                case "meeting":
                    lines.Add(m.Flag("emergency") || !m.TryGetProperty("body", out var b) || b.ValueKind != JsonValueKind.Object
                        ? ("meeting", $"{Who("caller") ?? "Someone"} called an emergency meeting")
                        : ("body", $"{Who("caller") ?? "Someone"} reported {Who("body") ?? "a"}'s body"));
                    break;
                case "eject":
                {
                    if (Votes(lobby, m) is { } votes) lines.Add(("votes", votes));
                    string? who = Who("ejected");
                    lines.Add(("eject", who == null ? (m.Flag("tie") ? "Tied vote: nobody ejected" : "Vote skipped: nobody ejected")
                        : $"{who} ejected: {(m.Flag("wasImpostor") ? "Impostor" : "not the Impostor")}"));
                    break;
                }
                case "sabotage":
                    if (m.Str("state") == "start" && m.Str("system") is { } sys)
                        lines.Add(("sabotage", $"{sys} sabotaged{(m.Num("timeLeft") is { } left ? $" ({Math.Ceiling(left):0} s)" : "")}"));
                    break;
                case "gameStart":
                    lines.Add(("start", $"Game started{(m.Str("map") is { } map ? " on " + map : "")}"));
                    break;
                case "gameEnd":
                {
                    string how = m.Str("how") switch { "tasks" => " on tasks", "vote" => " by vote", "kills" => " by kills", "sabotage" => " by sabotage", "disconnect" => " (someone left)", _ => "" };
                    string winner = m.Str("winner") ?? "";
                    lines.Add(("end", m.Flag("abandoned") ? "Game abandoned" : winner.Length > 0 ? $"{winner} win{how}" : "Game over"));
                    break;
                }
            }
            return lines;
        }

        /// <summary>The meeting's votes in a line ("Votes: Ann 4, Bob 1, skipped 2"), or null when the lobby didn't send them.</summary>
        private string? Votes(string lobby, JsonElement m)
        {
            if (!m.TryGetProperty("votes", out var votes) || votes.ValueKind != JsonValueKind.Array || votes.GetArrayLength() == 0) return null;
            var counts = new Dictionary<int, int>();
            int skipped = 0;
            foreach (var v in votes.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object) continue;
                if (v.Int("target") is int target) counts[target] = counts.GetValueOrDefault(target) + 1;
                else skipped++;
            }
            var parts = counts.OrderByDescending(kv => kv.Value).Select(kv => $"{_byId(lobby, kv.Key) ?? "someone"} {kv.Value}").ToList();
            if (skipped > 0) parts.Add($"skipped {skipped}");
            return parts.Count == 0 ? null : "Votes: " + string.Join(", ", parts);
        }
    }
}
