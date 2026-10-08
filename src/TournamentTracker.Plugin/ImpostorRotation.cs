using System;
using System.Collections.Generic;
using AmongUs.GameOptions;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// The roles for a game, sent once per player. While the game hands out roles (RoleManager.SelectRoles)
    /// its role messages are held back; then the impostor rotation and the referee ghost slot are
    /// applied to them (TournamentTracker.RolePlan) and each player's role is sent once. Changing a role
    /// afterwards meant a second role message for that player, and Among Us's servers kick a host for that.
    /// </summary>
    internal static class RoleChoice
    {
        /// <summary>Holding the game's role messages back (only while it hands out roles, and only when there's something to change).</summary>
        public static bool Collecting;
        /// <summary>Sending the final roles: those go through.</summary>
        public static bool Sending;
        public static readonly Dictionary<byte, (PlayerControl Pc, RoleTypes Role, bool Override)> Chosen = new Dictionary<byte, (PlayerControl, RoleTypes, bool)>();
        private static readonly Random Random = new Random();

        public static void Begin()
        {
            Chosen.Clear();
            var session = TournamentPlugin.Session;
            Collecting = Game.IsHost && session != null && (session.RotationOn || session.RefSlotKey != null);
        }

        public static void Finish()
        {
            if (!Collecting) return;
            Collecting = false;
            var session = TournamentPlugin.Session;
            if (Chosen.Count == 0)
            {
                TournamentPlugin.Logger.Warn("Roles: the game didn't hand them out as expected, so the rotation and the referee slot were left out this game.");
                return;
            }
            var plan = new Dictionary<byte, ushort>();
            try
            {
                var chosen = new Dictionary<byte, (ushort, bool)>();
                foreach (var kv in Chosen) chosen[kv.Key] = ((ushort)kv.Value.Role, RoleManager.IsImpostorRole(kv.Value.Role));
                var players = Game.Players();
                byte? referee = session?.RefSlotPlayerId(players);
                int impostors = 0;
                foreach (var c in chosen.Values) if (c.Item2) impostors++;
                var picks = session?.PickImpostors(players, impostors);
                plan = RolePlan.Plan(chosen, referee, picks, Random);
            }
            catch (Exception e)
            {
                TournamentPlugin.Logger.Error("Roles: couldn't apply the rotation or referee slot (" + e.Message + "); the game's roles stand.");
            }
            // Each player's role, once.
            Sending = true;
            try
            {
                foreach (var kv in Chosen)
                {
                    var role = plan.TryGetValue(kv.Key, out var r) ? (RoleTypes)r : kv.Value.Role;
                    try { kv.Value.Pc.RpcSetRole(role, kv.Value.Override); }
                    catch (Exception e) { TournamentPlugin.Logger.Error($"Roles: couldn't set player {kv.Key}'s role: {e.Message}"); }
                }
            }
            finally { Sending = false; Chosen.Clear(); }
        }
    }
}
