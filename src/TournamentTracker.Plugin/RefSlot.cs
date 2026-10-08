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
            var id = RefereeId(fresh: true);
            return id.HasValue ? Game.Player(id.Value) : null;
        }

        private static byte? _refId;
        private static string? _refKey;
        private static float _refAt = -1;

        /// <summary>
        /// The referee's player ID this game, if the slot is on (their role is decided in RoleChoice).
        /// Looked up by their key (friend code and name), at most twice a second unless <paramref name="fresh"/>.
        /// </summary>
        public static byte? RefereeId(bool fresh = false)
        {
            var key = TournamentPlugin.Session?.RefSlotKey;
            if (key == null) return null;
            float now = Time.unscaledTime;
            if (!fresh && now < _refAt && key == _refKey) return _refId;
            _refAt = now + 0.5f;
            _refKey = key;
            _refId = null;
            foreach (var p in Frame.Players)
                if (p.Data != null && PlayerSnapshot.MakeKey(p.Data.FriendCode, p.Data.PlayerName ?? "") == key) { _refId = p.Id; break; }
            return _refId;
        }

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

        // ---- Out of sight, watching the middle of the map ----------------------------------------
        // The referee's ghost is parked off the edge of the map, where no player (dead ones see
        // ghosts, mod or not) ever looks, and isn't drawn on the host's own screen either. The
        // host's camera is held over the middle of the map instead of following the ghost; the
        // arrow keys (or WASD) move it, Home brings it back.

        private static float _centreAt = -1;
        private static bool _wasMeeting;
        private static Vector2? _view;
        private static bool _camHeld;

        /// <summary>Moves the referee out of sight once nothing is in the way (the intro, a meeting, Airship's spawn pick).</summary>
        public static void CentreSoon(float delay) => _centreAt = Time.unscaledTime + delay;

        /// <summary>Every frame: the referee is parked out of sight as the game starts and after each meeting.</summary>
        public static void Update()
        {
            if (!Game.IsHost || TournamentPlugin.Session?.RefSlotKey == null || ShipStatus.Instance == null
                || AmongUsClient.Instance == null || AmongUsClient.Instance.GameState != InnerNetClient.GameStates.Started)
            {
                _centreAt = -1;
                _wasMeeting = false;
                _view = null;
                RoomName(true);
                HoldCamera(false);
                return;
            }
            bool meeting = MeetingHud.Instance != null || ExileController.Instance != null;
            KeepHud(meeting);
            KeepOutOfSight(meeting);
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
            var centre = MapCentre(out var bounds);
            var hideout = new Vector2(centre.x, bounds.min.y - 40f);
            referee.NetTransform.SnapTo(hideout);
            referee.moveable = false;
            _view ??= centre;
            GhostZoom.ZoomOut();
            TournamentPlugin.Logger.Info($"Referee ghost: parked out of sight ({hideout.x:0.0}, {hideout.y:0.0}); the camera watches the middle of the map ({centre.x:0.0}, {centre.y:0.0}).");
        }

        /// <summary>Every frame: the ghost not drawn here, the camera held over the map (moved with the keys).</summary>
        private static void KeepOutOfSight(bool meeting)
        {
            var local = PlayerControl.LocalPlayer;
            bool referee = LocalIsRefereeGhost();
            if (referee && local != null)
            {
                if (local.Visible) local.Visible = false;
                if (local.moveable && _view != null) local.moveable = false;
            }
            if (!referee || meeting || _view == null || !HudManager.InstanceExists) { HoldCamera(false); return; }
            var camera = Camera.main;
            if (camera == null) return;
            var view = _view.Value;
            if (!Typing())
            {
                float x = 0, y = 0;
                if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) x -= 1;
                if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) x += 1;
                if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S)) y -= 1;
                if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W)) y += 1;
                float speed = camera.orthographicSize * 1.1f * Time.unscaledDeltaTime;
                view += new Vector2(x, y) * speed;
                if (Input.GetKeyDown(KeyCode.Home)) view = MapCentre(out _);
            }
            _view = view;
            HoldCamera(true);
            var t = camera.transform;
            if (Math.Abs(t.position.x - view.x) > 1e-3f || Math.Abs(t.position.y - view.y) > 1e-3f)
                t.position = new Vector3(view.x, view.y, t.position.z);
        }

        /// <summary>The camera stops following the (parked) ghost while held.</summary>
        private static void HoldCamera(bool hold)
        {
            if (hold == _camHeld) return;
            if (!HudManager.InstanceExists) { _camHeld = false; return; }
            var cam = HudManager.Instance.PlayerCam;
            if (cam == null) return;
            cam.Locked = hold;
            _camHeld = hold;
        }

        /// <summary>The host is typing in the chat (the keys are letters then, not camera moves).</summary>
        private static bool Typing()
        {
            try
            {
                var chat = HudManager.Instance.Chat;
                return chat != null && chat.IsOpenOrOpening;
            }
            catch (Exception) { return false; }
        }

        private static float _nextChat;
        private static bool _roomHidden;

        /// <summary>
        /// The referee's own screen: the chat stays there (the game hides it for a player whose role
        /// isn't a ghost role, which the referee's isn't), and no room name ("Storage") at the top.
        /// </summary>
        private static void KeepHud(bool meeting)
        {
            bool referee = LocalIsRefereeGhost();
            if (!referee || !HudManager.InstanceExists) { RoomName(true); TaskBar(false); return; }
            RoomName(false);
            if (!meeting) TaskBar(true);
            if (meeting || Time.unscaledTime < _nextChat) return;
            _nextChat = Time.unscaledTime + 0.5f;
            var chat = HudManager.Instance.Chat;
            if (chat == null) return;
            if (!chat.gameObject.activeSelf) chat.gameObject.SetActive(true);
            if (chat.chatButton != null && !chat.chatButton.gameObject.activeSelf) chat.SetVisible(true);
        }

        /// <summary>This game's player is the referee ghost (the host, dead, in the referee slot).</summary>
        public static bool LocalIsRefereeGhost()
        {
            var local = PlayerControl.LocalPlayer;
            if (local == null || !Game.IsHost || AmongUsClient.Instance == null || AmongUsClient.Instance.GameState != InnerNetClient.GameStates.Started) return false;
            var me = Frame.Get(local.PlayerId);
            return me != null && me.Dead && RefereeId() == local.PlayerId;
        }

        // ---- The task bar on the referee's screen ------------------------------------------------
        // Smaller (it's in the way on the stream), and filled from the real task count every frame,
        // whatever the lobby's task bar setting (the game only fills it in meetings, or never, for some).

        private const float BarScale = 0.6f;
        private static ProgressTracker? _bar;
        private static MeshRenderer? _barFill;
        private static Vector3 _barScale;
        private static float _barValue, _barLook;

        private static void TaskBar(bool referee)
        {
            if (!referee)
            {
                if (_bar != null) _bar.transform.localScale = _barScale;
                _bar = null;
                _barFill = null;
                return;
            }
            if (_bar == null)
            {
                if (Time.unscaledTime < _barLook) return;
                _barLook = Time.unscaledTime + 1f;
                var found = UnityEngine.Object.FindObjectsOfType<ProgressTracker>(true);
                if (found == null || found.Length == 0) return;
                _bar = found[0];
                _barScale = _bar.transform.localScale;
                _bar.transform.localScale = _barScale * BarScale;
                _barFill = _bar.GetComponentInChildren<MeshRenderer>(true);
                _barValue = 0;
            }
            if (!_bar.gameObject.activeSelf) _bar.gameObject.SetActive(true);
            if (_barFill == null) return;
            if (!_barFill.enabled) _barFill.enabled = true;
            var data = GameData.Instance;
            if (data == null || data.TotalTasks <= 0) return;
            // One section per crewmate doing tasks (not the impostors, the gone, or the referee).
            int buckets = 0;
            byte? refId = RefereeId();
            foreach (var p in Frame.Players)
                if (!p.Impostor && !p.Disconnected && p.Id != refId) buckets++;
            if (buckets == 0) return;
            float target = (float)data.CompletedTasks / data.TotalTasks * buckets;
            _barValue += (target - _barValue) * Math.Min(1f, Time.deltaTime * 2f);
            var material = _barFill.material;
            material.SetFloat("_Buckets", buckets);
            material.SetFloat("_FullBuckets", _barValue);
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
        private static Vector2 MapCentre(out Bounds bounds)
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
            bounds = all ?? new Bounds(ship.MeetingSpawnCenter, new Vector3(40, 30, 0));
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
        private static MeetingHud? _hiddenIn;
        private static PlayerVoteArea? _hidden;

        private static void HideCard(MeetingHud meeting)
        {
            // Already hidden in this meeting: nothing to do (checked every frame, so kept cheap).
            if (ReferenceEquals(_hiddenIn, meeting) && _hidden != null && _hidden.transform.localScale == Vector3.zero) return;
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
            _hiddenIn = meeting;
            _hidden = referee;
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
        /// <summary>How far out the referee starts each game: all the way.</summary>
        private const float Wide = Farthest;
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
