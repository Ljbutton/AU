using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TournamentTracker.Discord;
using TournamentTracker.Stats;

namespace TournamentTracker
{
    /// <summary>
    /// Void games: a game that had to be stopped and restarted is kept for the record but
    /// scores nothing and doesn't count toward any leaderboard or round. The host types !void
    /// in-game (during the game, or after it for the last one); a referee types
    /// !void LJ-3 reason in the private results channel. Only !unvoid brings a game back:
    /// once seen, the void is reposted with the game, so deleting the message changes nothing.
    /// </summary>
    public sealed partial class TournamentSession
    {
        private readonly HashSet<string> _settledVoids = new HashSet<string>();

        /// <summary>The last game this session finished, for !void after the fact.</summary>
        public GameRecord? LastGame { get; private set; }

        private void VoidCommand(bool makeVoid, string[] args)
        {
            string reason = string.Join(" ", args).Trim();

            var current = Tracker.Current;
            if (current != null && (makeVoid || current.Voided))
            {
                current.Voided = makeVoid;
                current.VoidReason = makeVoid ? reason : "";
                Reply(makeVoid
                    ? $"Game {current.Name} is void: it's kept for the record but won't count. Restart when ready."
                    : $"Game {current.Name} counts again.", true);
                return;
            }

            var game = LastGame;
            if (game == null)
            {
                Reply($"No game to {(makeVoid ? "void" : "unvoid")} since the mod started. A referee can type !{(makeVoid ? "void" : "unvoid")} <game> in the results channel.", false);
                return;
            }
            if (game.Voided == makeVoid)
            {
                Reply($"Game {game.Name} is already {(makeVoid ? "void" : "counted")}.", false);
                return;
            }

            if (makeVoid && game.Counted) { Store.Remove(game); CountRoundGame(game, -1); CountImpostorGames(game, -1); }
            game.Voided = makeVoid;
            game.VoidReason = makeVoid ? reason : "";
            if (!makeVoid && game.Counted) { Store.Apply(game); CountRoundGame(game, 1); CountImpostorGames(game, 1); }
            TrySave(() => Store.Save(_statsPath), "stats");
            TrySave(() => SaveGameFile(game), "game record");

            string text = makeVoid
                ? $"Game {game.Name} is void{(reason.Length > 0 ? ": " + reason : "")}. It no longer counts."
                : $"Game {game.Name} counts again.";
            Reply(text, true);
            AnnounceVoid(game, text);
        }

        /// <summary>Tells Discord, and republishes the game so the combined standings pick it up.</summary>
        private void AnnounceVoid(GameRecord game, string text)
        {
            var message = new WebhookMessage { Username = ReportFormatter.BotName, Content = text };
            string url = _settings.StatsWebhookUrl;
            if (_settings.Mode == TrackerMode.Preliminary)
            {
                // The new copy of the data file is what the organiser's job goes by.
                if (!string.IsNullOrWhiteSpace(url))
                    Chain(async () =>
                    {
                        var result = await _rest.ExecuteWebhookWithFileAsync(url, message, SharedResults.FileNameFor(game), SharedResults.FileFor(game)).ConfigureAwait(false);
                        if (!result.Ok) _log.Error("Discord webhook post failed: " + result);
                    });
                return;
            }

            Post(url, message);
            if (Shared != null)
            {
                Chain(async () =>
                {
                    await Shared.PublishAsync(game).ConfigureAwait(false);
                    if (_settings.Mode == TrackerMode.Tournament) await PostLobbyStandingsAsync().ConfigureAwait(false);
                    else if (_settings.PostLeaderboardAfterEachGame) await PostCombinedAsync().ConfigureAwait(false);
                });
            }
            else if (_settings.PostLeaderboardAfterEachGame)
            {
                Post(url, LeaderboardMessage());
            }
        }

        /// <summary>
        /// Reposts this lobby's games that a referee voided or unvoided in the results channel,
        /// so the change sticks, and keeps the local copy in step.
        /// </summary>
        private async Task SettleVoidsAsync(SharedLoad load)
        {
            if (Shared == null || load.Unsettled.Count == 0) return;
            string lobby = LobbyLabel();
            foreach (var file in load.Unsettled)
            {
                var game = load.GameRecords.FirstOrDefault(g => g.Id == file.Key);
                if (game == null || !string.Equals(game.Host, lobby, StringComparison.OrdinalIgnoreCase)) continue;
                var commands = load.Voids.Where(v => v.Game == game && !v.Settled).ToList();
                string key = string.Join(",", commands.Select(c => c.Command.MessageId));
                if (!_settledVoids.Add(key)) continue;
                if (!await Shared.SettleVoidAsync(Shared.ChannelId, game, file.Value, commands).ConfigureAwait(false))
                {
                    _settledVoids.Remove(key);
                    continue;
                }
                var built = commands.Select(c => c.Command.MessageId).ToList();
                _mainThread.Enqueue(() => MatchLocal(game.Id, game.Voided, game.VoidReason, built));
            }
        }

        private void MatchLocal(string id, bool voided, string reason, List<string> built)
        {
            var game = LastGame != null && LastGame.Id == id ? LastGame : LoadGameFile(id);
            if (game == null) return;
            // Keep the referee's commands on the local copy, so a later !void or !unvoid here builds on them.
            game.VoidCommands.AddRange(built.Where(b => !game.VoidCommands.Contains(b)));
            if (game.Voided == voided)
            {
                TrySave(() => SaveGameFile(game), "game record");
                return;
            }
            if (voided && game.Counted) { Store.Remove(game); CountRoundGame(game, -1); CountImpostorGames(game, -1); }
            game.Voided = voided;
            game.VoidReason = voided ? reason : "";
            if (!voided && game.Counted) { Store.Apply(game); CountRoundGame(game, 1); CountImpostorGames(game, 1); }
            TrySave(() => Store.Save(_statsPath), "stats");
            TrySave(() => SaveGameFile(game), "game record");
            Reply(voided ? $"A referee voided game {game.Name}." : $"A referee brought back game {game.Name}.", false);
        }

        private static readonly string[] GameFileSuffixes = { "", "-abandoned", "-void" };

        private void SaveGameFile(GameRecord game)
        {
            Directory.CreateDirectory(_gamesDir);
            foreach (var suffix in GameFileSuffixes)
            {
                string old = Path.Combine(_gamesDir, $"game-{game.Id}{suffix}.json");
                if (File.Exists(old)) File.Delete(old);
            }
            string tag = game.Voided ? "-void" : game.Counted ? "" : "-abandoned";
            File.WriteAllText(Path.Combine(_gamesDir, $"game-{game.Id}{tag}.json"), JsonSerializer.Serialize(game, GameJson));
        }

        private GameRecord? LoadGameFile(string id)
        {
            foreach (var suffix in GameFileSuffixes)
            {
                string path = Path.Combine(_gamesDir, $"game-{id}{suffix}.json");
                if (!File.Exists(path)) continue;
                try { return JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(path)); }
                catch (Exception e) { _log.Error($"Could not read {path}: {e.Message}"); }
            }
            return null;
        }
    }
}
