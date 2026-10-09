using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TournamentTracker.Stats;

namespace TournamentTracker
{
    /// <summary>
    /// Game replays: the plugin feeds positions and the map while a game runs; when it ends the
    /// replay is saved next to the game, on this PC only, for watching in Freeplay (F8). Nothing
    /// goes to Discord, and only the newest few are kept.
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
            // Packed and saved off the game's main thread (it's the biggest file a game makes).
            Work.Post(() => SaveReplay(replay, game));
        }

        private void SaveReplay(ReplayRecorder replay, GameRecord game)
        {
            byte[] file = replay.Build(game);
            string name = ReplayRecorder.FileNameFor(game);
            TrySave(() =>
            {
                Directory.CreateDirectory(_gamesDir);
                File.WriteAllBytes(Path.Combine(_gamesDir, name), file);
            }, "replay");

            TrySave(PruneReplays, "old replays");
        }

        /// <summary>How many replays are kept on this PC (the newest).</summary>
        public const int ReplaysKept = 20;

        private void PruneReplays()
        {
            var dir = new DirectoryInfo(_gamesDir);
            if (!dir.Exists) return;
            foreach (var old in ReplayLibrary.Find(new[] { dir.FullName }, int.MaxValue).Skip(ReplaysKept))
                old.Delete();
        }
    }
}
