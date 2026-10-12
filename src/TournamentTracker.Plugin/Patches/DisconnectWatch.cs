using System;
using HarmonyLib;
using InnerNet;

namespace TournamentTracker.Plugin.Patches
{
    /// <summary>
    /// Why Among Us disconnected this game. Every disconnect the server (or the connection) causes
    /// goes through InnerNetClient.HandleDisconnect, with the game's reason (Hacking, Kicked, Error…)
    /// and its words; read here first, while the game still knows the lobby and whether a game was
    /// running. Only reads: the game's own disconnect runs exactly as before. Leaving on purpose
    /// doesn't come this way (the game's ExitGame closes the connection itself).
    /// </summary>
    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HandleDisconnect), typeof(DisconnectReasons), typeof(string))]
    internal static class DisconnectWatch
    {
        public static void Prefix(DisconnectReasons __0, string __1)
        {
            try
            {
                var client = AmongUsClient.Instance;
                // The game ignores it in freeplay (nothing to disconnect from), and so does this.
                if (client == null || client.NetworkMode == NetworkModes.FreePlay) return;
                var history = NetLog.History;
                if (history == null) return;
                var state = client.GameState;
                string? lobby = null;
                if (state != InnerNetClient.GameStates.NotJoined)
                {
                    try { lobby = Game.LobbyCode(); }
                    catch (Exception) { }
                }
                var record = history.Record(__0.ToString(), (int)__0, __1, state == InnerNetClient.GameStates.Started, lobby,
                    client.AmHost, HostAuthority.Modded, Safety.GameOn);      // what the game being played ran with
                TournamentPlugin.Session?.DisconnectSeen(record);
            }
            catch (Exception e)
            {
                TournamentPlugin.Logger.Warn("Couldn't record why Among Us disconnected: " + e.Message);
            }
        }
    }
}
