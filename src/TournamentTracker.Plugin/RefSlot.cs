using System;
using AmongUs.GameOptions;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using InnerNet;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// The referee ghost slot, done by the host: the referee is a plain crewmate (RoleChoice), gets
    /// no tasks, and becomes a ghost as the game starts. Experimental: it relies on the game
    /// accepting a player dying before anything has happened. Every message the game sends for
    /// them is sent once, as the game would: a second role or task message gets the host kicked.
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

        /// <summary>The referee's player ID this game, if the slot is on (their role is decided in RoleChoice).</summary>
        public static byte? RefereeId() => Referee()?.PlayerId;

        /// <summary>
        /// While the game hands out tasks (ShipStatus.Begin): the referee's one task message carries
        /// no tasks, so the task bar isn't held back. (A second, empty one afterwards got the host kicked.)
        /// </summary>
        public static bool HandingOutTasks;

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
