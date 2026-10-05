using System;
using AmongUs.GameOptions;
using TournamentTracker.Broadcast;
using TournamentTracker.Voice;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// Reads what the caster's feed needs from the game each tick: where everyone is (and in
    /// which room, and whether they're in a vent), which sabotages are on with their timers, and
    /// what the host's camera shows. Read-only: it never changes the game.
    /// </summary>
    internal static class FeedReader
    {
        private static bool _sabotageFailed, _roomFailed;

        private static readonly SystemTypes[] SabotageSystems =
        {
            SystemTypes.Reactor, SystemTypes.Laboratory, SystemTypes.LifeSupp, SystemTypes.HeliSabotage,
            SystemTypes.Electrical, SystemTypes.Comms, SystemTypes.MushroomMixupSabotage,
        };

        public static FeedFrame Frame(VoicePhase phase)
        {
            var frame = new FeedFrame { Phase = phase, Camera = Camera() };
            var options = GameOptionsManager.Instance?.CurrentGameOptions;
            if (options != null)
            {
                try { frame.KillCooldown = options.GetFloat(FloatOptionNames.KillCooldown); } catch (Exception) { }
                try { frame.KillDistance = KillReach(options.GetInt(Int32OptionNames.KillDistance)); } catch (Exception) { }
            }
            if (phase != VoicePhase.Tasks && phase != VoicePhase.Meeting) return frame;

            var all = PlayerControl.AllPlayerControls;
            for (int i = 0; i < all.Count; i++)
            {
                var pc = all[i];
                if (pc == null || pc.Data == null) continue;
                var pos = pc.GetTruePosition();
                frame.Players.Add(new FeedPlayer
                {
                    Id = pc.PlayerId,
                    X = pos.x,
                    Y = pos.y,
                    Dead = pc.Data.IsDead,
                    InVent = pc.inVent,
                    Disconnected = pc.Data.Disconnected,
                    Room = RoomAt(pos),
                });
            }
            ReadSabotages(frame);
            return frame;
        }

        /// <summary>Where a player is right now, for a kill.</summary>
        public static FeedPlace Place(PlayerControl pc)
        {
            var p = pc.transform.position;
            return new FeedPlace { X = p.x, Y = p.y, Room = RoomAt(new Vector2(p.x, p.y)), Camera = Camera() };
        }

        /// <summary>The game's kill distances: short, medium, long.</summary>
        private static float KillReach(int setting) => setting switch { 0 => 1f, 2 => 2.5f, _ => 1.8f };

        private static FeedCamera? Camera()
        {
            var cam = UnityEngine.Camera.main;
            if (cam == null || !cam.orthographic) return null;
            var p = cam.transform.position;
            float half = cam.orthographicSize;
            return new FeedCamera { X = p.x, Y = p.y, HalfHeight = half, HalfWidth = half * cam.aspect };
        }

        private static string? RoomAt(Vector2 point)
        {
            var ship = ShipStatus.Instance;
            if (ship == null || _roomFailed) return null;
            try
            {
                foreach (var room in ship.AllRooms)
                    if (room != null && room.roomArea != null && room.roomArea.OverlapPoint(point)) return room.RoomId.ToString();
            }
            catch (Exception e)
            {
                _roomFailed = true;
                TournamentPlugin.Logger.Warn("Caster feed: couldn't read rooms (" + e.Message + ").");
            }
            return null;
        }

        private static void ReadSabotages(FeedFrame frame)
        {
            var ship = ShipStatus.Instance;
            if (ship == null || ship.Systems == null || _sabotageFailed) return;
            try
            {
                foreach (var type in SabotageSystems)
                {
                    if (!ship.Systems.ContainsKey(type)) continue;
                    var system = ship.Systems[type];
                    if (system == null) continue;
                    bool on = false;
                    float? left = null;
                    var reactor = system.TryCast<ReactorSystemType>();
                    var o2 = system.TryCast<LifeSuppSystemType>();
                    var heli = system.TryCast<HeliSabotageSystem>();
                    var lights = system.TryCast<SwitchSystem>();
                    var comms = system.TryCast<HudOverrideSystemType>();
                    var hqComms = system.TryCast<HqHudSystemType>();
                    var mushrooms = system.TryCast<MushroomMixupSabotageSystem>();
                    if (reactor != null) { on = reactor.IsActive; left = reactor.Countdown; }
                    else if (o2 != null) { on = o2.IsActive; left = o2.Countdown; }
                    else if (heli != null) { on = heli.IsActive; left = heli.Countdown; }
                    else if (lights != null) on = lights.IsActive;
                    else if (comms != null) on = comms.IsActive;
                    else if (hqComms != null) on = hqComms.IsActive;
                    else if (mushrooms != null) on = mushrooms.IsActive;
                    if (on) frame.Sabotages.Add(new FeedSabotage { System = type.ToString(), TimeLeft = left });
                }
            }
            catch (Exception e)
            {
                _sabotageFailed = true;
                TournamentPlugin.Logger.Warn("Caster feed: couldn't read sabotages (" + e.Message + ").");
            }
        }
    }
}
