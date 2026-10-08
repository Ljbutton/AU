using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <remarks>
        /// No "exiled" (or kill) message: sent outside a meeting it got the host kicked from the room
        /// at the start of every game. Instead the host, who keeps everyone's player record, marks the
        /// referee dead there (it reaches every player with the next update, so meetings, kills and the
        /// win check all treat them as dead), and the host's own game makes them a ghost. No ghost role
        /// is sent either (that would be a second role message for them).
        /// </remarks>
        public static void MakeGhost()
        {
            var referee = Referee();
            if (referee == null || referee.Data == null || referee.Data.IsDead) return;
            referee.Die(DeathReason.Exile, false);
            referee.Data.IsDead = true;
            referee.Data.SetDirtyBit(uint.MaxValue);
            TournamentPlugin.Logger.Info($"Referee ghost: {referee.Data.PlayerName} is now a ghost (marked dead in the player record, no exile sent).");
            CentreSoon(1f);
        }

        // ---- Moved to the middle of the map ------------------------------------------------------

        private static float _centreAt = -1;
        private static bool _wasMeeting;

        /// <summary>Moves the referee to the middle of the map once nothing is in the way (the intro, a meeting, Airship's spawn pick).</summary>
        public static void CentreSoon(float delay) => _centreAt = Time.unscaledTime + delay;

        /// <summary>Every frame: the referee goes to the middle of the map as the game starts and after each meeting.</summary>
        public static void Update()
        {
            if (!Game.IsHost || TournamentPlugin.Session?.RefSlotKey == null || ShipStatus.Instance == null
                || AmongUsClient.Instance == null || AmongUsClient.Instance.GameState != InnerNetClient.GameStates.Started)
            {
                _centreAt = -1;
                _wasMeeting = false;
                return;
            }
            bool meeting = MeetingHud.Instance != null || ExileController.Instance != null;
            if (_wasMeeting && !meeting) CentreSoon(0.5f);
            _wasMeeting = meeting;
            if (_centreAt < 0 || Time.unscaledTime < _centreAt) return;
            var referee = Referee();
            if (referee == null || !referee.AmOwner || referee.Data == null) { _centreAt = -1; return; }
            if (!referee.Data.IsDead || meeting || Minigame.Instance != null || UnityEngine.Object.FindObjectOfType<IntroCutscene>() != null)
            {
                _centreAt = Time.unscaledTime + 0.5f;
                return;
            }
            _centreAt = -1;
            var at = MapCentre();
            referee.NetTransform.SnapTo(at);
            TournamentPlugin.Logger.Info($"Referee ghost: moved to the middle of the map ({at.x:0.0}, {at.y:0.0}).");
        }

        /// <summary>The middle of the map: the centre of all its rooms together (the meeting table if it has none).</summary>
        private static Vector2 MapCentre()
        {
            var ship = ShipStatus.Instance;
            Bounds? all = null;
            foreach (var room in ship.AllRooms)
            {
                if (room == null || room.roomArea == null) continue;
                var b = room.roomArea.bounds;
                if (all is Bounds a) { a.Encapsulate(b); all = a; }
                else all = b;
            }
            return all is Bounds m ? (Vector2)m.center : ship.MeetingSpawnCenter;
        }

        // ---- Kept out of the game -----------------------------------------------------------------

        /// <summary>
        /// Whether this is the referee ghost. The host knows; another player's game (with the mod)
        /// tells by what only the referee has: dead, yet still a plain crewmate or impostor role
        /// (everyone else who dies gets a ghost role) and no tasks.
        /// </summary>
        public static bool IsReferee(NetworkedPlayerInfo? data)
        {
            if (data == null || data.Disconnected) return false;
            if (Game.IsHost) return TournamentPlugin.Session?.RefSlotKey != null && RefereeId() == data.PlayerId;
            if (!data.IsDead || data.Role == null) return false;
            var role = data.Role.Role;
            if (role == RoleTypes.CrewmateGhost || role == RoleTypes.ImpostorGhost || role == RoleTypes.GuardianAngel) return false;
            return data.Tasks == null || data.Tasks.Count == 0;
        }
    }

    /// <summary>
    /// The referee ghost kept out of sight, on this game only: in meetings their card is taken off
    /// the list (the others close up), and on another player's game (with the mod) they aren't
    /// drawn on the map. A player's game without the mod lists them as a dead player.
    /// </summary>
    internal static class RefereeHider
    {
        private static float _next;

        public static void Update()
        {
            var meeting = MeetingHud.Instance;
            if (meeting != null && meeting.playerStates != null) HideCard(meeting);
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            if (Game.IsHost || AmongUsClient.Instance == null || AmongUsClient.Instance.GameState != InnerNetClient.GameStates.Started) return;
            var all = PlayerControl.AllPlayerControls;
            for (int i = 0; i < all.Count; i++)
            {
                var pc = all[i];
                if (pc == null || pc.AmOwner || !RefSlot.IsReferee(pc.Data)) continue;
                if (pc.Visible) pc.Visible = false;
            }
        }

        /// <summary>The referee's card off the meeting list; the cards after it move up to fill the gap.</summary>
        private static void HideCard(MeetingHud meeting)
        {
            if (GameData.Instance == null || (Game.IsHost && TournamentPlugin.Session?.RefSlotKey == null)) return;
            byte? hostKnows = Game.IsHost ? RefSlot.RefereeId() : null;
            if (Game.IsHost && hostKnows == null) return;
            var areas = new List<PlayerVoteArea>();
            PlayerVoteArea? referee = null;
            foreach (var area in meeting.playerStates)
            {
                if (area == null) continue;
                areas.Add(area);
                if (!area.gameObject.activeSelf) continue;
                if (hostKnows.HasValue ? area.TargetPlayerId == hostKnows.Value : RefSlot.IsReferee(GameData.Instance.GetPlayerById(area.TargetPlayerId))) referee = area;
            }
            if (referee == null) return;
            // The places in reading order (top row first, left to right); everyone but the referee takes them in turn.
            var order = areas.OrderByDescending(a => Mathf.Round(a.transform.localPosition.y * 100)).ThenBy(a => a.transform.localPosition.x).ToList();
            var places = order.Select(a => a.transform.localPosition).ToList();
            referee.gameObject.SetActive(false);
            int next = 0;
            foreach (var area in order)
            {
                if (area == referee || !area.gameObject.activeSelf) continue;
                area.transform.localPosition = places[next++];
            }
            TournamentPlugin.Logger.Info("Referee ghost: taken off the meeting list.");
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
