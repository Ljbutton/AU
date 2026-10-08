using System;
using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker
{
    /// <summary>
    /// The roles the game chose, adjusted for the tournament before any are sent: the referee ghost
    /// never impostor (and a plain crewmate), and the impostor rotation's picks. The host sends each
    /// player's role once, as the game would: Among Us's servers kick a host that sends a player's
    /// role a second time, which is what changing them afterwards did.
    /// Roles are the game's role numbers; 0 is a plain crewmate.
    /// </summary>
    public static class RolePlan
    {
        public const ushort Crewmate = 0;

        /// <param name="chosen">Each player's role as the game chose it, and whether it's an impostor role.</param>
        /// <param name="referee">The referee ghost's player ID, if there is one.</param>
        /// <param name="picks">The rotation's impostors (null: keep the game's).</param>
        /// <returns>Every player's final role.</returns>
        public static Dictionary<byte, ushort> Plan(IReadOnlyDictionary<byte, (ushort Role, bool Impostor)> chosen, byte? referee,
            IReadOnlyCollection<byte>? picks, Random random)
        {
            var role = chosen.ToDictionary(kv => kv.Key, kv => kv.Value.Role);
            var imp = chosen.ToDictionary(kv => kv.Key, kv => kv.Value.Impostor);
            var impostors = imp.Where(kv => kv.Value).Select(kv => kv.Key).ToList();

            // Who should be impostor: the rotation's picks (when they fit), else the game's.
            var want = picks != null && picks.Count == impostors.Count && picks.All(role.ContainsKey) ? picks.ToList() : impostors.ToList();
            if (referee is byte r && want.Contains(r))
            {
                var instead = role.Keys.Where(id => id != r && !want.Contains(id)).OrderBy(_ => random.Next()).ToList();
                want.Remove(r);
                if (instead.Count > 0) want.Add(instead[0]);
            }

            // Swap roles: each impostor who shouldn't be trades roles with someone who should.
            var outgoing = impostors.Where(id => !want.Contains(id)).ToList();
            var incoming = want.Where(id => !imp[id]).ToList();
            for (int i = 0; i < Math.Min(outgoing.Count, incoming.Count); i++)
            {
                byte a = outgoing[i], b = incoming[i];
                (role[a], role[b]) = (role[b], role[a]);
                (imp[a], imp[b]) = (imp[b], imp[a]);
            }
            // No one to swap with (the referee as the only choice): plain crewmate.
            foreach (var id in outgoing.Skip(incoming.Count)) { role[id] = Crewmate; imp[id] = false; }

            // The referee: a plain crewmate (an engineer or scientist role goes to a plain crewmate instead).
            if (referee is byte rf && role.TryGetValue(rf, out var rr) && rr != Crewmate)
            {
                var plain = role.Keys.Where(id => id != rf && role[id] == Crewmate && !imp[id]).OrderBy(_ => random.Next()).ToList();
                if (plain.Count > 0 && !imp[rf]) role[plain[0]] = rr;
                role[rf] = Crewmate;
                imp[rf] = false;
            }
            return role;
        }
    }
}
