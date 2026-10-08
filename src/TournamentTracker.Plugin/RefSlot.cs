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
                RoomName(true);
                return;
            }
            bool meeting = MeetingHud.Instance != null || ExileController.Instance != null;
            KeepHud(meeting);
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
            GhostZoom.ZoomOut();
            TournamentPlugin.Logger.Info($"Referee ghost: moved to the middle of the map ({at.x:0.0}, {at.y:0.0}).");
        }

        private static float _nextChat;
        private static bool _roomHidden;

        /// <summary>
        /// The referee's own screen: the chat stays there (the game hides it for a player whose role
        /// isn't a ghost role, which the referee's isn't), and no room name ("Storage") at the top.
        /// </summary>
        private static void KeepHud(bool meeting)
        {
            var local = PlayerControl.LocalPlayer;
            bool referee = local != null && local.Data != null && local.Data.IsDead && RefereeId() == local.PlayerId;
            if (!referee || !HudManager.InstanceExists) { RoomName(true); return; }
            RoomName(false);
            if (meeting || Time.unscaledTime < _nextChat) return;
            _nextChat = Time.unscaledTime + 1f;
            var chat = HudManager.Instance.Chat;
            if (chat != null && !chat.gameObject.activeSelf) chat.SetVisible(true);
        }

        private static void RoomName(bool show)
        {
            if (show && !_roomHidden) return;
            if (!HudManager.InstanceExists) { _roomHidden = false; return; }
            var tracker = HudManager.Instance.roomTracker;
            if (tracker == null) return;
            if (!show && tracker.gameObject.activeSelf) tracker.gameObject.SetActive(false);
            else if (show) tracker.gameObject.SetActive(true);
            _roomHidden = !show;
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

        /// <summary>The referee's card off the meeting list (shrunk to nothing, last place); the cards after it move up to fill the gap.</summary>
        private static void HideCard(MeetingHud meeting)
        {
            if (GameData.Instance == null || (Game.IsHost && TournamentPlugin.Session?.RefSlotKey == null)) return;
            // Who the referee is, then their card by the name on it (their game name, or on the host's
            // screen the roster name the nameplates may show instead).
            NetworkedPlayerInfo? data = null;
            if (Game.IsHost)
            {
                var id = RefSlot.RefereeId();
                if (id.HasValue) data = GameData.Instance.GetPlayerById(id.Value);
            }
            else
                foreach (var p in GameData.Instance.AllPlayers)
                    if (RefSlot.IsReferee(p)) { data = p; break; }
            if (data == null) return;
            var names = new HashSet<string>(StringComparer.Ordinal) { data.PlayerName ?? "" };
            var shown = Game.IsHost ? TournamentPlugin.Session?.DisplayName(PlayerSnapshot.MakeKey(data.FriendCode, data.PlayerName ?? "")) : null;
            if (shown != null) names.Add(shown);
            var areas = new List<PlayerVoteArea>();
            PlayerVoteArea? referee = null;
            foreach (var area in meeting.playerStates)
            {
                if (area == null) continue;
                areas.Add(area);
                if (area.gameObject.activeSelf && area.transform.localScale != Vector3.zero && area.NameText != null && names.Contains(area.NameText.text ?? "")) referee = area;
            }
            if (referee == null) return;
            // The places in reading order (top row first, left to right); everyone but the referee takes them in turn.
            var order = areas.OrderByDescending(a => Mathf.Round(a.transform.localPosition.y * 100)).ThenBy(a => a.transform.localPosition.x).ToList();
            var places = order.Select(a => a.transform.localPosition).ToList();
            // Shrunk to nothing rather than switched off: the game still goes through every card when it
            // shows the votes, and a switched-off one stopped it part way (no votes shown at all).
            referee.transform.localScale = Vector3.zero;
            int next = 0;
            foreach (var area in order)
            {
                if (area == referee || !area.gameObject.activeSelf) continue;
                area.transform.localPosition = places[next++];
            }
            referee.transform.localPosition = places[places.Count - 1];
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
        /// <summary>How far out the referee starts each game: most of the map in view.</summary>
        private const float Wide = 11f;
        private static float _size = Normal;
        private static bool _wide;

        /// <summary>The referee ghost: zoomed out as they arrive in the middle of the map.</summary>
        public static void ZoomOut() => _wide = true;

        public static void Update()
        {
            var camera = Camera.main;
            if (camera == null) return;
            var local = PlayerControl.LocalPlayer;
            bool ghost = local != null && local.Data != null && local.Data.IsDead && Game.IsHost
                         && AmongUsClient.Instance.GameState == InnerNetClient.GameStates.Started;
            if (!ghost)
            {
                if (_size != Normal) camera.orthographicSize = Normal;
                _size = Normal;
                _wide = false;
                return;
            }
            if (_wide) { _size = Wide; _wide = false; }

            float step = Input.mouseScrollDelta.y;
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus)) step += 1;
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus)) step -= 1;
            if (step != 0) _size = Mathf.Clamp(_size - step, Normal, Farthest);
            // Kept (the game puts the camera back after a meeting).
            if (_size != Normal && Mathf.Abs(camera.orthographicSize - _size) > 0.01f) camera.orthographicSize = _size;
        }
    }
}
