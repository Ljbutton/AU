using System;
using System.Collections.Generic;
using System.Linq;
using TournamentTracker.Stats;

namespace TournamentTracker
{
    /// <summary>
    /// Impostor rotation (off unless the setup code turns it on): last game's impostors are rarely
    /// impostor again straight away. Each of them gets a small chance (2% by default) and everyone
    /// else shares the rest equally, so back-to-back impostor games are possible but unlikely, and
    /// three in a row almost never happen. The host's game still hands out roles; the mod then
    /// swaps them to the picked players.
    /// </summary>
    public sealed partial class TournamentSession
    {
        private readonly Random _rotationRandom = new Random();

        public bool RotationOn => _settings.ImpostorRotation;

        /// <summary>The impostors of this lobby's last counted game (player keys), whatever the round.</summary>
        private List<string> _lastImpostors = new List<string>();

        /// <summary>
        /// Who should be impostor this game, or null when rotation is off. Picks
        /// <paramref name="count"/> player IDs from <paramref name="players"/> (the referee ghost left out).
        /// </summary>
        public IReadOnlyList<byte>? PickImpostors(IReadOnlyList<PlayerSnapshot> players, int count)
        {
            if (!RotationOn || count <= 0) return null;
            var eligible = WithoutReferee(players).Where(p => !p.Disconnected).ToList();
            if (eligible.Count <= count) return null;
            return Rotation.Pick(eligible.Select(p => (p.PlayerId, p.Key)).ToList(), _lastImpostors, count, _rotationRandom, _settings.RepeatImpostorChance);
        }

        /// <summary>A game counted (or uncounted, when voided): remember its impostors for the next pick.</summary>
        private void CountImpostorGames(GameRecord game, int change)
        {
            if (!RotationOn) return;
            var keys = game.Players.Where(p => p.IsImpostor).Select(p => p.Key).ToList();
            if (change > 0) _lastImpostors = keys;
            else if (_lastImpostors.SequenceEqual(keys)) _lastImpostors = new List<string>();   // the last game was voided
            SaveState();
        }
    }

    public static class Rotation
    {
        /// <summary>
        /// Each of last game's impostors is impostor again with probability <paramref name="repeatChance"/>
        /// (on their own, one after another); the slots left are drawn evenly from everyone else.
        /// </summary>
        public static List<byte> Pick(IReadOnlyList<(byte Id, string Key)> players, IReadOnlyCollection<string> lastImpostors, int count, Random random, double repeatChance = 0.02)
        {
            var last = players.Where(p => lastImpostors.Contains(p.Key)).OrderBy(_ => random.Next()).ToList();
            var others = players.Where(p => !lastImpostors.Contains(p.Key)).OrderBy(_ => random.Next()).ToList();
            var picked = new List<byte>();
            foreach (var p in last)
                if (picked.Count < count && random.NextDouble() < repeatChance) picked.Add(p.Id);
            foreach (var p in others)
                if (picked.Count < count) picked.Add(p.Id);
            // A lobby too small to avoid them: fill up with last game's impostors.
            foreach (var p in last)
                if (picked.Count < count && !picked.Contains(p.Id)) picked.Add(p.Id);
            return picked;
        }
    }
}
