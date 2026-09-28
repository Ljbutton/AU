using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TournamentTracker.Stats
{
    public sealed class PlannedPlayer
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string? DiscordId { get; set; }
        /// <summary>Points in the round they qualified from.</summary>
        public double Points { get; set; }
        public string From { get; set; } = "";
    }

    public sealed class PlannedLobby
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public List<PlannedPlayer> Players { get; set; } = new List<PlannedPlayer>();
    }

    /// <summary>
    /// The next round's lobbies: everyone who moved on, snake-seeded by their points (1st,
    /// 10th, 11th, 20th… into the first lobby), plus the best of the rest as alternates. The
    /// organiser adjusts it from the results channel before starting the round.
    /// </summary>
    public sealed class LobbyPlan
    {
        public int Round { get; set; }
        public List<PlannedLobby> Lobbies { get; set; } = new List<PlannedLobby>();
        /// <summary>Players who didn't move on, best first: the first to call on if someone can't continue.</summary>
        public List<PlannedPlayer> Alternates { get; set; } = new List<PlannedPlayer>();

        public static LobbyPlan Build(IReadOnlyList<GameRecord> games, int round, int advance, int gamesPerRound, int lobbySize = 10)
        {
            int from = round - 1;
            var ids = LatestDiscordIds(games);
            var moving = new List<PlannedPlayer>();
            var rest = new List<PlannedPlayer>();
            foreach (var host in games.Where(g => g.Counted && g.Round == from).Select(g => g.Host).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var row in Standings.Lobby(games, host, from, advance, gamesPerRound))
                {
                    var p = new PlannedPlayer
                    {
                        Key = row.Stats.Key, Name = row.Stats.Name, Points = row.Stats.Points, From = host,
                        DiscordId = ids.TryGetValue(row.Stats.Key, out var id) ? id : null,
                    };
                    (row.Advancing ? moving : rest).Add(p);
                }
            }

            var plan = new LobbyPlan { Round = round, Alternates = rest.OrderByDescending(p => p.Points).ToList() };
            int count = Math.Max(1, (int)Math.Ceiling(moving.Count / (double)lobbySize));
            for (int i = 0; i < count; i++)
                plan.Lobbies.Add(new PlannedLobby { Name = count == 1 ? "Final" : "Lobby " + (char)('A' + i) });

            var seeded = moving.OrderByDescending(p => p.Points).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < seeded.Count; i++)
            {
                int pass = i / count, pos = i % count;
                plan.Lobbies[pass % 2 == 0 ? pos : count - 1 - pos].Players.Add(seeded[i]);
            }
            return plan;
        }

        /// <summary>Each player's Discord account from the newest game that had them linked.</summary>
        private static Dictionary<string, string> LatestDiscordIds(IEnumerable<GameRecord> games)
        {
            var ids = new Dictionary<string, string>();
            foreach (var g in games.OrderBy(g => g.StartedUtc))
                foreach (var p in g.Players)
                    if (!string.IsNullOrEmpty(p.DiscordId)) ids[p.Key] = p.DiscordId!;
            return ids;
        }

        // ---- Changes from the results channel ------------------------------------------

        public PlannedLobby? FindLobby(string text)
        {
            string t = text.Trim();
            return Lobbies.FirstOrDefault(l => string.Equals(l.Name, t, StringComparison.OrdinalIgnoreCase))
                ?? Lobbies.FirstOrDefault(l => l.Name.EndsWith(" " + t, StringComparison.OrdinalIgnoreCase))
                ?? Lobbies.FirstOrDefault(l => string.Equals(l.Host, t, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A player in a lobby (or, with <paramref name="alternates"/>, among the alternates) by name or unique start of a name.</summary>
        public (PlannedPlayer Player, PlannedLobby? Lobby)? FindPlayer(string text, bool alternates = false)
        {
            var all = Lobbies.SelectMany(l => l.Players.Select(p => (p, (PlannedLobby?)l)))
                .Concat(alternates ? Alternates.Select(p => (p, (PlannedLobby?)null)) : Enumerable.Empty<(PlannedPlayer, PlannedLobby?)>())
                .ToList();
            var exact = all.Where(x => string.Equals(x.Item1.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            var prefix = all.Where(x => x.Item1.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();
            return prefix.Count == 1 ? prefix[0] : ((PlannedPlayer, PlannedLobby?)?)null;
        }

        /// <summary>Applies one change. Returns what happened, for the channel.</summary>
        public string Apply(string command, IReadOnlyList<string> args)
        {
            switch (command)
            {
                case "move":
                {
                    if (args.Count < 2) return "Usage: !move <player> <lobby>";
                    var lobby = FindLobby(args[args.Count - 1]);
                    var found = FindPlayer(string.Join(" ", args.Take(args.Count - 1)));
                    if (lobby == null) return $"No lobby called {args[args.Count - 1]}.";
                    if (found == null) return $"Couldn't find {string.Join(" ", args.Take(args.Count - 1))} in the lobbies.";
                    found.Value.Lobby!.Players.Remove(found.Value.Player);
                    lobby.Players.Add(found.Value.Player);
                    return $"Moved {found.Value.Player.Name} to {lobby.Name}.";
                }
                case "swap":
                {
                    if (args.Count != 2) return "Usage: !swap <player> <player> (use the start of a name if it has spaces)";
                    var a = FindPlayer(args[0]);
                    var b = FindPlayer(args[1]);
                    if (a == null || b == null) return $"Couldn't find {(a == null ? args[0] : args[1])} in the lobbies.";
                    var la = a.Value.Lobby!; var lb = b.Value.Lobby!;
                    int ia = la.Players.IndexOf(a.Value.Player), ib = lb.Players.IndexOf(b.Value.Player);
                    la.Players[ia] = b.Value.Player;
                    lb.Players[ib] = a.Value.Player;
                    return $"Swapped {a.Value.Player.Name} ({lb.Name}) and {b.Value.Player.Name} ({la.Name}).";
                }
                case "drop":
                {
                    if (args.Count == 0) return "Usage: !drop <player>";
                    var found = FindPlayer(string.Join(" ", args));
                    if (found == null) return $"Couldn't find {string.Join(" ", args)} in the lobbies.";
                    found.Value.Lobby!.Players.Remove(found.Value.Player);
                    var next = Alternates.Take(3).Select(p => $"{p.Name} ({Pts(p.Points)}, {p.From})");
                    return $"Dropped {found.Value.Player.Name} from {found.Value.Lobby.Name}." +
                           (Alternates.Count > 0 ? $" Next up: {string.Join(", ", next)}. Add one with !add <player> {found.Value.Lobby.Name.Replace("Lobby ", "")}." : "");
                }
                case "add":
                {
                    if (args.Count < 2) return "Usage: !add <player> <lobby>";
                    var lobby = FindLobby(args[args.Count - 1]);
                    string name = string.Join(" ", args.Take(args.Count - 1));
                    if (lobby == null) return $"No lobby called {args[args.Count - 1]}.";
                    var found = FindPlayer(name, alternates: true);
                    var player = found?.Player ?? new PlannedPlayer { Key = "name:" + name.ToLowerInvariant(), Name = name, From = "added" };
                    found?.Lobby?.Players.Remove(player);
                    Alternates.Remove(player);
                    lobby.Players.Add(player);
                    return $"Added {player.Name} to {lobby.Name}{(found == null ? " (not in any earlier round)" : "")}.";
                }
                case "host":
                {
                    if (args.Count < 2) return "Usage: !host <lobby> <host label>";
                    var lobby = FindLobby(args[0]);
                    if (lobby == null) return $"No lobby called {args[0]}.";
                    lobby.Host = string.Join(" ", args.Skip(1));
                    return $"{lobby.Name} is hosted by {lobby.Host}.";
                }
                default:
                    return "";
            }
        }

        public static string Pts(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
