using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;

namespace TournamentTracker.Plugin
{
    /// <summary>After the game hands out roles, swaps the impostor role onto the players the rotation picked.</summary>
    internal static class ImpostorRotation
    {
        public static void Apply()
        {
            var session = TournamentPlugin.Session;
            if (session == null || !session.RotationOn || !Game.IsHost) return;

            var controls = new List<PlayerControl>();
            var all = PlayerControl.AllPlayerControls;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].Data != null && all[i].Data.Role != null && !all[i].Data.Disconnected) controls.Add(all[i]);

            var impostors = controls.Where(pc => pc.Data.Role.IsImpostor).ToList();
            var picked = session.PickImpostors(Game.Players(), impostors.Count);
            if (picked == null) return;

            var role = impostors.Count > 0 ? impostors[0].Data.Role.Role : RoleTypes.Impostor;
            foreach (var pc in impostors.Where(pc => !picked.Contains(pc.PlayerId)))
                pc.RpcSetRole(RoleTypes.Crewmate, true);
            foreach (var pc in controls.Where(pc => picked.Contains(pc.PlayerId) && !pc.Data.Role.IsImpostor))
                pc.RpcSetRole(role, true);
        }
    }
}
