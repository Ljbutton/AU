using System;
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

        /// <summary>
        /// When someone last called a meeting (report or button), by the game's clock. The meeting
        /// screen only appears after the report animation, a couple of seconds later; automute
        /// follows the call so voices open sooner.
        /// </summary>
        public static float MeetingCalledAt = -100f;

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
                           || UnityEngine.Time.unscaledTime - MeetingCalledAt < 6f
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
            int done = 0, total = 0, longDone = 0, longTotal = 0;
            var tasks = p.Tasks;
            if (!impostor && tasks != null)
            {
                for (int i = 0; i < tasks.Count; i++)
                {
                    bool isLong = IsLongTask(tasks[i].TypeId);
                    total++;
                    if (isLong) longTotal++;
                    if (tasks[i].Complete)
                    {
                        done++;
                        if (isLong) longDone++;
                    }
                }
            }

            string name = p.PlayerName ?? "";
            var local = PlayerControl.LocalPlayer;
            return new PlayerSnapshot
            {
                IsHost = local != null && local.PlayerId == p.PlayerId,
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
                LongTasksCompleted = longDone,
                LongTasksTotal = longTotal,
            };
        }

        // The map's long tasks, read once per map.
        private static readonly HashSet<byte> LongTasks = new HashSet<byte>();
        private static ShipStatus? _longTasksOf;

        /// <summary>A player's task, by its type ID, is one of the map's long tasks.</summary>
        private static bool IsLongTask(byte typeId)
        {
            var ship = ShipStatus.Instance;
            if (ship == null) return false;
            if (!ReferenceEquals(_longTasksOf, ship))
            {
                _longTasksOf = ship;
                LongTasks.Clear();
                var longTasks = ship.LongTasks;
                if (longTasks != null)
                    for (int i = 0; i < longTasks.Length; i++)
                        if (longTasks[i] != null) LongTasks.Add((byte)longTasks[i].Index);
            }
            return LongTasks.Contains(typeId);
        }

        public static PlayerControl? Player(byte playerId) => Frame.Control(playerId);

        /// <summary>
        /// The game's current options, or null when there are none yet. The getter itself throws
        /// (a NullReferenceException inside the game) before the game has any, e.g. in the menus.
        /// </summary>
        public static IGameOptions? Options()
        {
            try
            {
                var manager = GameOptionsManager.Instance;
                return manager == null ? null : manager.CurrentGameOptions;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string LobbyCode()
        {
            var client = AmongUsClient.Instance;
            return client == null ? "" : GameCode.IntToGameName(client.GameId);
        }

        /// <summary>The server region this game is on ("North America"…), or "" if unknown.</summary>
        public static string Region()
        {
            try
            {
                var servers = ServerManager.InstanceExists ? ServerManager.Instance : null;
                return servers?.CurrentRegion?.Name ?? "";
            }
            catch (Exception) { return ""; }
        }

        public static string MapName()
        {
            var options = Options();
            return options == null ? "Unknown map" : Maps.Name(options.MapId);
        }

        /// <summary>Shows a line in the host's chat only.</summary>
        public static void LocalChat(string text)
        {
            // InstanceExists, not Instance: Instance makes an empty HudManager when there's none.
            var hud = HudManager.InstanceExists ? HudManager.Instance : null;
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
