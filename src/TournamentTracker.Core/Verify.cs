using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using TournamentTracker.Discord;
using TournamentTracker.Stats;

namespace TournamentTracker
{
    /// <summary>
    /// The host checks every game before Discord sees it. A finished game waits in The Button's
    /// Referee tab: its points can be changed or the game voided there, and only Verify sends it
    /// (the report, the standings, the round's summary). A voided game that was never sent stays
    /// off Discord. The host can also void or change any game already sent (Discord is told), and
    /// start the points again from nothing (Reset all points: Discord isn't touched).
    /// </summary>
    public sealed partial class TournamentSession
    {
        private sealed class HeldGame
        {
            public string Id { get; set; } = "";
            public bool RoundDone { get; set; }
            /// <summary>Not saved: read back from the game's file when needed.</summary>
            [System.Text.Json.Serialization.JsonIgnore]
            public GameRecord? Game { get; set; }
        }

        private List<HeldGame>? _held;

        private string HeldPath => Path.Combine(_gamesDir, "waiting.json");

        /// <summary>Somewhere results go: a results webhook, or the shared results channel.</summary>
        private bool PostsResults => Shared != null || !string.IsNullOrWhiteSpace(_settings.StatsWebhookUrl);

        /// <summary>Games waiting for the host to verify them, oldest first.</summary>
        private List<HeldGame> Held
        {
            get
            {
                if (_held != null) return _held;
                _held = new List<HeldGame>();
                try
                {
                    if (File.Exists(HeldPath))
                        foreach (var h in JsonSerializer.Deserialize<List<HeldGame>>(File.ReadAllText(HeldPath)) ?? new List<HeldGame>())
                        {
                            h.Game = LoadGameFile(h.Id);
                            if (h.Game != null) _held.Add(h);
                        }
                }
                catch (Exception e) { _log.Warn("Couldn't read the games waiting to be verified: " + e.Message); }
                return _held;
            }
        }

        public IReadOnlyList<GameRecord> WaitingGames => Held.Where(h => h.Game != null).Select(h => h.Game!).ToList();

        private void SaveHeld() => TrySave(() =>
        {
            Directory.CreateDirectory(_gamesDir);
            File.WriteAllText(HeldPath, JsonSerializer.Serialize(Held));
        }, "games waiting to be verified");

        private void Hold(GameRecord game, bool roundDone)
        {
            Held.Add(new HeldGame { Id = game.Id, RoundDone = roundDone, Game = game });
            SaveHeld();
            Reply($"Game {game.Name} is waiting for you: check it in The Button (Referee), then press Verify to send it to Discord.", false);
        }

        /// <summary>A game by its name ("LJ-3") or number ("3"): waiting, the last one, or from its file.</summary>
        private GameRecord? FindGame(string name)
        {
            name = name.Trim();
            bool Is(GameRecord g) => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)
                                     || g.GameNumber.ToString(CultureInfo.InvariantCulture) == name || g.Id == name;
            var waiting = Held.FirstOrDefault(h => h.Game != null && Is(h.Game));
            if (waiting != null) return waiting.Game;
            if (LastGame != null && Is(LastGame)) return LastGame;
            foreach (var f in LocalGameFiles())
            {
                try
                {
                    var g = JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(f.FullName));
                    if (g != null && Is(g)) return g;
                }
                catch (Exception) { }
            }
            return null;
        }

        private bool IsWaiting(GameRecord game) => Held.Any(h => h.Id == game.Id);

        /// <summary>verify LJ-3 · verify all: sends the games to Discord.</summary>
        private void VerifyCommand(string[] args)
        {
            string which = args.FirstOrDefault() ?? "all";
            var list = which.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? Held.ToList()
                : Held.Where(h => h.Game != null && FindGame(which)?.Id == h.Id).ToList();
            if (list.Count == 0)
            {
                Reply(Held.Count == 0 ? "No games are waiting to be verified." : $"Game {which} isn't waiting to be verified.", false);
                return;
            }
            int sent = 0, kept = 0;
            foreach (var h in list)
            {
                Held.Remove(h);
                if (h.Game == null) continue;
                if (h.Game.Voided) { kept++; continue; }     // voided before it was ever sent: Discord never hears of it
                PostResults(h.Game, h.RoundDone);
                sent++;
            }
            SaveHeld();
            Reply(sent > 0
                ? $"Verified: {(list.Count == 1 ? "game " + list[0].Game?.Name : sent + " games")} sent to Discord." + (kept > 0 ? $" {kept} voided game{(kept == 1 ? "" : "s")} left off." : "")
                : "Verified: the voided game stays off Discord.", false);
        }

        /// <summary>void LJ-3 reason · unvoid LJ-3: any game, waiting or sent.</summary>
        private void VoidGameCommand(bool makeVoid, string[] args)
        {
            if (args.Length == 0) { Reply("Which game? e.g. voidgame LJ-3 lobby restarted", false); return; }
            var game = FindGame(args[0]);
            if (game == null) { Reply($"No game {args[0]} on this PC.", false); return; }
            string reason = string.Join(" ", args.Skip(1)).Trim();
            if (game.Voided == makeVoid) { Reply($"Game {game.Name} is already {(makeVoid ? "void" : "counted")}.", false); return; }
            SetVoid(game, makeVoid, reason);
            if (IsWaiting(game))
            {
                SaveHeld();
                Reply(makeVoid ? $"Game {game.Name} is void: it won't count, and won't go to Discord." : $"Game {game.Name} counts again (still waiting for Verify).", false);
                return;
            }
            string text = makeVoid
                ? $"Game {game.Name} is void{(reason.Length > 0 ? ": " + reason : "")}. It no longer counts."
                : $"Game {game.Name} counts again.";
            Reply(text, false);
            AnnounceVoid(game, text);
        }

        private void SetVoid(GameRecord game, bool makeVoid, string reason)
        {
            if (makeVoid && game.Counted) { Store.Remove(game); CountRoundGame(game, -1); CountImpostorGames(game, -1); }
            game.Voided = makeVoid;
            game.VoidReason = makeVoid ? reason : "";
            if (!makeVoid && game.Counted) { Store.Apply(game); CountRoundGame(game, 1); CountImpostorGames(game, 1); }
            if (LastGame != null && LastGame.Id == game.Id && !ReferenceEquals(LastGame, game)) { LastGame.Voided = game.Voided; LastGame.VoidReason = game.VoidReason; }
            TrySave(() => Store.Save(_statsPath), "stats");
            TrySave(() => SaveGameFile(game), "game record");
            _points = null;
        }

        /// <summary>adjust LJ-3 &lt;player key&gt; &lt;+/-points&gt; reason: a referee's change to one player's points in one game.</summary>
        private void AdjustCommand(string[] args)
        {
            if (args.Length < 3) { Reply("adjust <game> <player> <points> [reason], e.g. adjust LJ-3 Red -2 left early", false); return; }
            var game = FindGame(args[0]);
            if (game == null) { Reply($"No game {args[0]} on this PC.", false); return; }
            var player = game.ByKey(args[1])
                         ?? game.Players.FirstOrDefault(p => string.Equals(p.Name, args[1], StringComparison.OrdinalIgnoreCase))
                         ?? game.Players.FirstOrDefault(p => string.Equals(Colors.Name(p.ColorId), args[1], StringComparison.OrdinalIgnoreCase));
            if (player == null) { Reply($"No player {args[1]} in game {game.Name}.", false); return; }
            if (!double.TryParse(args[2].Replace("+", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double change) || change == 0 || Math.Abs(change) > 1000)
            {
                Reply($"\"{args[2]}\" isn't a number of points (e.g. 2 or -1.5).", false);
                return;
            }
            string reason = string.Join(" ", args.Skip(3)).Trim();
            bool counted = game.Counted;
            if (counted) Store.Remove(game);
            player.PointBreakdown.Add(new PointLine("Referee" + (reason.Length > 0 ? ": " + reason : ""), change));
            player.Points = Math.Round(player.Points + change, 2);
            if (counted) Store.Apply(game);
            TrySave(() => Store.Save(_statsPath), "stats");
            TrySave(() => SaveGameFile(game), "game record");
            _points = null;
            string what = $"{player.Name} {(change > 0 ? "+" : "")}{change.ToString("0.##", CultureInfo.InvariantCulture)} in game {game.Name}{(reason.Length > 0 ? " (" + reason + ")" : "")}";
            if (IsWaiting(game)) { Reply($"Points changed: {what}. Still waiting for Verify.", false); return; }
            Reply($"Points changed: {what}.", false);
            AnnounceVoid(game, $"Referee · points changed: {what}.");
        }

        /// <summary>
        /// Starts the points again from nothing: every game on this PC is put away in a dated
        /// folder (not deleted), the totals and round counts go back to zero, and nothing waits
        /// for Verify. Discord isn't touched (what was posted there stays).
        /// </summary>
        private void ResetPointsCommand()
        {
            int games = 0;
            TrySave(() =>
            {
                var files = LocalGameFiles();
                if (files.Count == 0) return;
                string to = Path.Combine(_gamesDir, "reset " + _clock().ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(to);
                foreach (var f in files) { f.MoveTo(Path.Combine(to, f.Name)); games++; }
            }, "reset");
            Held.Clear();
            SaveHeld();
            Store = new StatsStore { Tournament = _settings.TournamentName };
            TrySave(() => Store.Save(_statsPath), "stats");
            _roundGames.Clear();
            SaveState();
            LastGame = null;
            lock (_localGameCache) _localGameCache.Clear();
            _localGames = new List<(FileInfo, GameRecord?)>();
            _points = null;
            Reply($"All points reset: {games} game{(games == 1 ? "" : "s")} put away (in the games folder, under \"reset\"). Discord wasn't changed.", false);
        }

        /// <summary>The games for the Referee tab: waiting first, then the newest sent ones.</summary>
        private object GamesForApp()
        {
            var waiting = Held.Where(h => h.Game != null).Select(h => GameForApp(h.Game!, true)).ToList();
            var ids = new HashSet<string>(Held.Select(h => h.Id));
            var recent = _localGames.Where(x => x.Game != null && !ids.Contains(x.Game.Id))
                .Select(x => x.Game!).OrderByDescending(g => g.EndedUtc ?? g.StartedUtc).Take(12)
                .Select(g => GameForApp(g, false)).ToList();
            return new { Waiting = waiting, Recent = recent, Holds = PostsResults && _settings.VerifyResults };
        }

        private object GameForApp(GameRecord g, bool waiting) => new
        {
            g.Id,
            g.Name,
            g.Round,
            g.Map,
            Winner = g.Winner,
            g.Voided,
            g.VoidReason,
            Waiting = waiting,
            Ended = (g.EndedUtc ?? g.StartedUtc).ToString("o"),
            Minutes = Math.Round(g.DurationSeconds / 60, 1),
            g.SettingsIssues,
            Players = g.Players.OrderByDescending(p => p.Points).Select(p => new
            {
                p.Key,
                Name = DisplayName(p.Key) ?? p.Name,
                Color = p.ColorId,
                Impostor = p.IsImpostor,
                Points = Math.Round(p.Points, 2),
                Lines = p.PointBreakdown.Select(l => new { l.Rule, Points = Math.Round(l.Points, 2) }).ToList(),
            }).ToList(),
        };
    }
}
