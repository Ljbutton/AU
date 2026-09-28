using System;
using System.Collections.Generic;
using System.Linq;
using TournamentTracker.Stats;

namespace TournamentTracker
{
    /// <summary>
    /// Fair impostor rotation (off unless the setup code turns it on): within a round, the
    /// impostors are drawn at random from the players who've been impostor the fewest times,
    /// so nobody gets a second impostor game before everyone has had one. The host's game
    /// still hands out roles; the mod then swaps them to the picked players.
    /// </summary>
    public sealed partial class TournamentSession
    {
        private readonly Random _rotationRandom = new Random();

        public bool RotationOn => _settings.ImpostorRotation;

        /// <summary>Impostor games per player (by key) in a round, from this lobby's counted games.</summary>
        private Dictionary<int, Dictionary<string, int>> _impostorGames = new Dictionary<int, Dictionary<string, int>>();

        /// <summary>
        /// Who should be impostor this game, or null when rotation is off. Picks
        /// <paramref name="count"/> player IDs from <paramref name="players"/> (the referee ghost left out).
        /// </summary>
        public IReadOnlyList<byte>? PickImpostors(IReadOnlyList<PlayerSnapshot> players, int count)
        {
            if (!RotationOn || count <= 0) return null;
            var eligible = WithoutReferee(players).Where(p => !p.Disconnected).ToList();
            if (eligible.Count <= count) return null;
            var counts = _impostorGames.TryGetValue(Round, out var c) ? c : new Dictionary<string, int>();
            return Rotation.Pick(eligible.Select(p => (p.PlayerId, p.Key)).ToList(), counts, count, _rotationRandom);
        }

        private void CountImpostorGames(GameRecord game, int change)
        {
            if (!RotationOn) return;
            if (!_impostorGames.TryGetValue(game.Round, out var counts)) _impostorGames[game.Round] = counts = new Dictionary<string, int>();
            foreach (var p in game.Players.Where(p => p.IsImpostor))
                counts[p.Key] = Math.Max(0, (counts.TryGetValue(p.Key, out var n) ? n : 0) + change);
            SaveState();
        }
    }

    public static class Rotation
    {
        public static List<byte> Pick(IReadOnlyList<(byte Id, string Key)> players, IReadOnlyDictionary<string, int> impostorGames, int count, Random random)
        {
            int Games((byte Id, string Key) p) => impostorGames.TryGetValue(p.Key, out var n) ? n : 0;
            return players
                .OrderBy(Games)
                .ThenBy(_ => random.Next())
                .Take(count)
                .Select(p => p.Id)
                .ToList();
        }
    }
}
