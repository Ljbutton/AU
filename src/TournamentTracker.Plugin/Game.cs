using System.Collections.Generic;
using AmongUs.GameOptions;
using InnerNet;
using TournamentTracker.Voice;

namespace TournamentTracker.Plugin
{
    /// <summary>Everything the mod reads from the game lives here, so a game update breaks one file.</summary>
    internal static class Game
    {
        /// <summary>True while this client hosts a real (not freeplay) lobby: the only time the mod acts.</summary>
        public static bool IsHost
        {
            get
            {
                var client = AmongUsClient.Instance;
                return client != null && client.AmHost && client.NetworkMode != NetworkModes.FreePlay;
            }
        }

        public static VoicePhase Phase()
        {
            if (!IsHost) return VoicePhase.Menu;
            switch (AmongUsClient.Instance.GameState)
            {
                case InnerNetClient.GameStates.Joined:
                    return VoicePhase.Lobby;
                case InnerNetClient.GameStates.Ended:
                    return VoicePhase.GameOver;
                case InnerNetClient.GameStates.Started:
                    return MeetingHud.Instance != null || ExileController.Instance != null
                        ? VoicePhase.Meeting
                        : VoicePhase.Tasks;
                default:
                    return VoicePhase.Menu;
            }
        }

        public static List<PlayerSnapshot> Players()
        {
            var list = new List<PlayerSnapshot>();
            var data = GameData.Instance;
            if (data == null) return list;
            var all = data.AllPlayers;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p != null) list.Add(Snapshot(p));
            }
            return list;
        }

        public static PlayerSnapshot Snapshot(NetworkedPlayerInfo p)
        {
            var role = p.Role;
            bool impostor = role != null && role.IsImpostor;
            int done = 0, total = 0;
            var tasks = p.Tasks;
            if (!impostor && tasks != null)
            {
                for (int i = 0; i < tasks.Count; i++)
                {
                    total++;
                    if (tasks[i].Complete) done++;
                }
            }

            string name = p.PlayerName ?? "";
            return new PlayerSnapshot
            {
                PlayerId = p.PlayerId,
                Key = PlayerSnapshot.MakeKey(p.FriendCode, name),
                Name = name,
                ColorId = p.DefaultOutfit != null ? p.DefaultOutfit.ColorId : 0,
                Role = role != null ? role.Role.ToString() : "Crewmate",
                IsImpostor = impostor,
                IsDead = p.IsDead,
                Disconnected = p.Disconnected,
                TasksCompleted = done,
                TasksTotal = total,
            };
        }

        public static string LobbyCode()
        {
            var client = AmongUsClient.Instance;
            return client == null ? "" : GameCode.IntToGameName(client.GameId);
        }

        public static string MapName()
        {
            var options = GameOptionsManager.Instance?.CurrentGameOptions;
            return options == null ? "Unknown map" : Maps.Name(options.MapId);
        }

        /// <summary>Shows a line in the host's chat only.</summary>
        public static void LocalChat(string text)
        {
            var hud = HudManager.Instance;
            var local = PlayerControl.LocalPlayer;
            if (hud == null || hud.Chat == null || local == null)
            {
                TournamentPlugin.Logger.Info("[chat] " + text);
                return;
            }
            hud.Chat.AddChat(local, "[Tracker] " + text, false);
        }

        /// <summary>Sends a line to the whole lobby, as the host. Only used in the lobby, where chat is open.</summary>
        public static bool PublicChat(string text)
        {
            var local = PlayerControl.LocalPlayer;
            if (local == null || AmongUsClient.Instance == null
                || AmongUsClient.Instance.GameState != InnerNetClient.GameStates.Joined)
                return false;
            if (text.Length > 100) text = text.Substring(0, 99) + "…";
            local.RpcSendChat(text);
            return true;
        }
    }
}
