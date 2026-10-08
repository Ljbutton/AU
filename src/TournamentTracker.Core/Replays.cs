using System;
using System.Collections.Generic;
using System.IO;
using TournamentTracker.Discord;
using TournamentTracker.Stats;

namespace TournamentTracker
{
    /// <summary>
    /// Game replays: the plugin feeds positions and the map while a game runs; when it ends the
    /// replay is saved next to the game and sent where the game's data goes (the results
    /// channel for tournaments, the organiser's channel for preliminaries), for referees to
    /// open in the replay viewer.
    /// </summary>
    public sealed partial class TournamentSession
    {
        private ReplayRecorder? _replay;

        public bool RecordingReplay => _replay != null;

        private void StartReplay(GameRecord game) => _replay = _settings.RecordReplays ? new ReplayRecorder(game) : null;

        /// <summary>The map being recorded, so the plugin can add its picture.</summary>
        public ReplayMap? ReplayMapInUse => _replay?.Map;

        public void ReplayMapLoaded(ReplayMap map)
        {
            if (_replay != null) _replay.Map = map;
        }

        /// <summary>The map number and everyone's cosmetics, for the in-game replay.</summary>
        public void ReplayDetails(int mapId, IReadOnlyDictionary<byte, ReplayOutfit> outfits)
        {
            if (_replay == null) return;
            _replay.MapId = mapId;
            foreach (var kv in outfits) _replay.Outfits[kv.Key] = kv.Value;
        }

        public void RecordPositions(IEnumerable<ReplayPosition> positions)
        {
            var game = Tracker.Current;
            if (_replay == null || game == null) return;
            _replay.Record(Math.Max(0, (_clock() - game.StartedUtc).TotalSeconds), positions);
        }

        private void FinishReplay(GameRecord game)
        {
            // The last of it to the caster, with the result.
            try { ReplayFeed(_clock(), game); } catch (Exception e) { _log.Warn("Replay for the caster: " + e.Message); }
            var replay = _replay;
            _replay = null;
            if (replay == null || replay.Frames == 0) return;
            byte[] file = replay.Build(game);
            string name = ReplayRecorder.FileNameFor(game);
            TrySave(() =>
            {
                Directory.CreateDirectory(_gamesDir);
                File.WriteAllBytes(Path.Combine(_gamesDir, name), file);
            }, "replay");

            string text = $"Replay of game {game.Name}: open it in the replay viewer.";
            if (Shared != null && _settings.Mode == TrackerMode.Tournament)
            {
                Chain(async () =>
                {
                    var result = await _rest.PostFileAsync(Shared.Token, Shared.ChannelId, text, name, file).ConfigureAwait(false);
                    if (!result.Ok) _log.Error("Could not post the replay: " + result);
                });
            }
            else if (_settings.Mode == TrackerMode.Preliminary && !string.IsNullOrWhiteSpace(_settings.StatsWebhookUrl))
            {
                string url = _settings.StatsWebhookUrl;
                Chain(async () =>
                {
                    var result = await _rest.ExecuteWebhookWithFileAsync(url, new WebhookMessage { Username = ReportFormatter.BotName, Content = text }, name, file).ConfigureAwait(false);
                    if (!result.Ok) _log.Error("Could not post the replay: " + result);
                });
            }
        }
    }
}
