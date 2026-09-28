using System;
using AmongUs.GameOptions;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using InnerNet;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// The referee ghost slot, done by the host: keep the referee a plain crewmate, give them
    /// no tasks, and make them a ghost as the game starts. Experimental: it relies on the game
    /// accepting a player dying before anything has happened.
    /// </summary>
    internal static class RefSlot
    {
        private static PlayerControl? Referee()
        {
            var session = TournamentPlugin.Session;
            if (session?.RefSlotKey == null) return null;
            var id = session.RefSlotPlayerId(Game.Players());
            return id.HasValue ? Game.Player(id.Value) : null;
        }

        /// <summary>After roles are handed out: if the referee drew impostor, give it to someone else.</summary>
        public static void KeepCrewmate()
        {
            var referee = Referee();
            if (referee == null || referee.Data == null || referee.Data.Role == null) return;
            if (referee.Data.Role.IsImpostor)
            {
                var role = referee.Data.Role.Role;
                var others = new System.Collections.Generic.List<PlayerControl>();
                var all = PlayerControl.AllPlayerControls;
                for (int i = 0; i < all.Count; i++)
                {
                    var pc = all[i];
                    if (pc != null && pc.PlayerId != referee.PlayerId && pc.Data != null && !pc.Data.Disconnected
                        && pc.Data.Role != null && !pc.Data.Role.IsImpostor)
                        others.Add(pc);
                }
                if (others.Count > 0) others[new System.Random().Next(others.Count)].RpcSetRole(role, true);
            }
            if (referee.Data.Role.Role != RoleTypes.Crewmate) referee.RpcSetRole(RoleTypes.Crewmate, true);
        }

        /// <summary>After tasks are handed out: the referee gets none, so the task bar isn't held back.</summary>
        public static void ClearTasks()
        {
            var referee = Referee();
            referee?.Data?.RpcSetTasks(new Il2CppStructArray<byte>(0));
        }

        /// <summary>As the game starts: the referee becomes a ghost, with no body left behind.</summary>
        public static void MakeGhost()
        {
            var referee = Referee();
            if (referee == null || referee.Data == null || referee.Data.IsDead) return;
            var writer = AmongUsClient.Instance.StartRpcImmediately(referee.NetId, (byte)RpcCalls.Exiled, SendOption.Reliable, -1);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
            referee.Exiled();
            TournamentPlugin.Logger.Info($"Referee ghost: {referee.Data.PlayerName} is now a ghost.");
        }
    }

    /// <summary>
    /// The host, as a ghost, can zoom the camera out with the mouse wheel (or + and -) to watch
    /// the whole map, for refereeing and streaming. Only while dead, so it never helps a
    /// living player, and only for the host, who is the only referee ghost.
    /// </summary>
    internal static class GhostZoom
    {
        private const float Normal = 3f;
        private const float Farthest = 15f;
        private static bool _zoomed;

        public static void Update()
        {
            var camera = Camera.main;
            if (camera == null) return;
            var local = PlayerControl.LocalPlayer;
            bool ghost = local != null && local.Data != null && local.Data.IsDead && Game.IsHost
                         && AmongUsClient.Instance.GameState == InnerNetClient.GameStates.Started;
            if (!ghost)
            {
                if (_zoomed) camera.orthographicSize = Normal;
                _zoomed = false;
                return;
            }

            float step = Input.mouseScrollDelta.y;
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus)) step += 1;
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus)) step -= 1;
            if (step == 0) return;
            float size = Mathf.Clamp(camera.orthographicSize - step, Normal, Farthest);
            camera.orthographicSize = size;
            _zoomed = size > Normal;
        }
    }
}
