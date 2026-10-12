using System;
using HarmonyLib;
using InnerNet;

namespace TournamentTracker.Plugin.Patches
{
    /// <summary>
    /// Host authority: the host tells Among Us's servers it runs a host-only mod by adding 25 to the
    /// game's network version (Innersloth's documented "+25 modded flag", the one Town of Host, TOHE
    /// and Endless Host Roles use). The lobby then runs in "host authority mode": the host, not the
    /// server, keeps every player's record (NetworkedPlayerInfo), so the referee's record, which the
    /// host keeps dead, reaches players without the mod (they never see the referee on the map, and
    /// see their card crossed out in meetings). Without it the server keeps the records and the host's
    /// "dead" never leaves the host's game. Modded lobbies stay out of public matchmaking; codes work.
    /// </summary>
    /// <remarks>
    /// The version goes to the server as Among Us connects, so the setting (hostauth on|off, on by
    /// default) is read in the menus and held for the whole connection: changed in a lobby, it applies
    /// from the main menu. Online games only. Nothing new is sent to anyone: the referee stays
    /// record-only (no exile, kill, second role or task message).
    /// </remarks>
    internal static class HostAuthority
    {
        /// <summary>What the version said when Among Us last connected (read again whenever it's in the menus).</summary>
        private static bool _sent;

        /// <summary>
        /// The host wants it: the session's setting (on until there's a session), unless safe mode is on
        /// (a newer Among Us may not take the flag, or the host-authority rules may have changed).
        /// </summary>
        private static bool Wanted
        {
            get
            {
                bool wanted = TournamentPlugin.Session?.HostAuthority ?? true;
                if (!wanted || !Safety.On) return wanted;
                Safety.HeldBack("host authority (the +25 modded flag)");
                return false;
            }
        }

        private static bool Online(AmongUsClient? client) => client != null && client.NetworkMode == NetworkModes.OnlineGame;

        /// <summary>The network version this game sends: 25 added, once, while host authority is on.</summary>
        public static int Version(int version)
        {
            var client = AmongUsClient.Instance;
            if (!Online(client)) return version;
            // In the menus or connecting: the setting as it is now. In a lobby or a game: as it was sent.
            if (client!.GameState == InnerNetClient.GameStates.NotJoined) _sent = Wanted;
            // Revision 25 and up is already modded (another mod, or asked twice): never added twice.
            if (!_sent || version % 50 >= 25) return version;
            return version + 25;
        }

        /// <summary>This connection said it's modded (the game's own check asks this).</summary>
        public static bool Modded => _sent && Online(AmongUsClient.Instance);

        /// <summary>For the app: whether host authority is really on in the lobby this game hosts (null when not hosting).</summary>
        public static bool? ActiveWhileHosting()
        {
            if (!Game.IsHost) return null;
            try { return AmongUsClient.Instance.AmModdedHost; }
            catch (Exception) { return null; }
        }
    }

    [Risky]
    [HarmonyPatch(typeof(Constants), nameof(Constants.GetBroadcastVersion))]
    internal static class HostAuthorityVersionPatch
    {
        public static void Postfix(ref int __result)
        {
            try { __result = HostAuthority.Version(__result); }
            catch (Exception) { }
        }
    }

    // The game's "is this version modded" check, answered directly: IL2CPP can build the version into
    // it, past the patch above (Town of Host, TOHE and Endless Host Roles answer it the same way).
    [Risky]
    [HarmonyPatch(typeof(Constants), nameof(Constants.IsVersionModded))]
    internal static class HostAuthorityModdedPatch
    {
        public static bool Prefix(ref bool __result)
        {
            if (!HostAuthority.Modded) return true;
            __result = true;
            return false;
        }
    }

    // With host authority the server leaves exiles, kills, meetings, roles, names, guardian angel
    // shields and turned-down shapeshifts to the host, so only the host ever sends these. One arriving
    // at the host came from another player's game (a cheat, or a broken game): the host's game doesn't
    // act on it, and the host is told on Home. Nobody is kicked. Without host authority the server
    // itself sends some of these (kills), so nothing is checked then; nor in safe mode (a newer game
    // may send these differently, and a turned-down one would leave the host's game out of step).
    [Risky]
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    internal static class HostOnlyMessageGuard
    {
        public static bool Prefix(PlayerControl __instance, byte __0)
        {
            switch ((RpcCalls)__0)
            {
                case RpcCalls.Exiled:
                case RpcCalls.SetName:
                case RpcCalls.MurderPlayer:
                case RpcCalls.StartMeeting:
                case RpcCalls.SetRole:
                case RpcCalls.ProtectPlayer:
                case RpcCalls.RejectShapeshift:
                    break;
                default:
                    return true;
            }
            try
            {
                var client = AmongUsClient.Instance;
                if (client == null || !client.AmHost || __instance == null || !client.AmModdedHost || Safety.GameOn) return true;
                var data = __instance.Data;
                string who = data != null ? data.PlayerName ?? "?" : "?";
                TournamentPlugin.Session?.HostOnlyMessageBlocked(who, ((RpcCalls)__0).ToString());
                return false;
            }
            catch (Exception) { return true; }
        }
    }
}
