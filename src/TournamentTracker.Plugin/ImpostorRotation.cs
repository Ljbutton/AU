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
            // Safe mode is held from here (the game's first move) until the lobby: the referee slot
            // can't switch on or off half way through a game.
            if (Game.IsHost) Safety.HoldForGame();
            bool wanted = Game.IsHost && session != null && (session.RotationOn || session.RefSlotKey != null);
            // Safe mode: the game's own role messages go out untouched (holding them back and sending
            // them again is exactly what a newer game might not accept).
            if (wanted && Safety.GameOn && session != null)
            {
                if (session.RotationOn) Safety.HeldBack("the impostor rotation");
                if (session.RefSlotKey != null) Safety.HeldBack("the referee ghost slot", Safety.RefSlotAdvice);
                TournamentPlugin.Logger.Warn("Roles: safe mode is on, so the game's own roles stand this game (no rotation, no referee slot).");
                wanted = false;
            }
            Collecting = wanted;
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
                    NetLog.Sent($"role for player {kv.Key}: {role}");
                    try { kv.Value.Pc.RpcSetRole(role, kv.Value.Override); }
                    catch (Exception e) { TournamentPlugin.Logger.Error($"Roles: couldn't set player {kv.Key}'s role: {e.Message}"); }
                }
            }
            finally { Sending = false; Chosen.Clear(); }
        }
    }
}
