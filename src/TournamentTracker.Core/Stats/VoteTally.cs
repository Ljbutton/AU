using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker.Stats
{
    /// <summary>
    /// A meeting's result worked out the game's way, with votes for one player (the referee ghost)
    /// counted as skips: the most votes goes; a tie at the top, or skip on top, sends nobody.
    /// </summary>
    public static class VoteTally
    {
        public static bool AnyFor(IEnumerable<VoteCast> votes, byte player) => votes.Any(v => v.VotedForId == player);

        /// <summary>Who goes (null: nobody) and whether it was a tie.</summary>
        public static (byte? Exiled, bool Tie) Result(IEnumerable<VoteCast> votes, byte asSkip)
        {
            var counts = new Dictionary<byte, int>();
            foreach (var v in votes)
            {
                byte to = v.VotedForId;
                if (to == VoteCast.DeadVote || to == VoteCast.MissedVote || to == VoteCast.HasNotVoted) continue;
                if (to == asSkip) to = VoteCast.SkippedVote;
                counts[to] = counts.TryGetValue(to, out var n) ? n + 1 : 1;
            }
            if (counts.Count == 0) return (null, false);
            int top = counts.Values.Max();
            var leaders = counts.Where(kv => kv.Value == top).Select(kv => kv.Key).ToList();
            if (leaders.Count > 1) return (null, true);
            return leaders[0] == VoteCast.SkippedVote ? (null, false) : (leaders[0], false);
        }
    }
}
