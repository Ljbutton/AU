using System;
using System.Collections.Generic;
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
            var options = Game.Options();
            if (options != null)
            {
                try { frame.KillCooldown = options.GetFloat(FloatOptionNames.KillCooldown); } catch (Exception) { }
                try { frame.KillDistance = KillReach(options.GetInt(Int32OptionNames.KillDistance)); } catch (Exception) { }
            }
            if (phase != VoicePhase.Tasks && phase != VoicePhase.Meeting) return frame;

            foreach (var p in Frame.Players)
            {
                var pos = p.Pc.GetTruePosition();
                frame.Players.Add(new FeedPlayer
                {
                    Id = p.Id,
                    X = pos.x,
                    Y = pos.y,
                    Dead = p.Dead,
                    InVent = p.InVent,
                    Disconnected = p.Disconnected,
                    Room = RoomOf(p.Id, pos),
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

        // The map's rooms, read once per map: each room's area and its box (checked first, in plain
        // .NET, so the game's own point-in-area test only runs for the rooms the point could be in).
        private sealed class Room { public Collider2D Area = null!; public string Name = ""; public float MinX, MinY, MaxX, MaxY; }
        private static readonly List<Room> Rooms = new List<Room>();
        private static ShipStatus? _roomsOf;

        // Each player's room from the last tick, kept while they stay put.
        private static readonly Dictionary<byte, (float X, float Y, string? Room)> LastRoom = new Dictionary<byte, (float, float, string?)>();
        private const float SameSpot = 0.05f;

        private static string? RoomOf(byte id, Vector2 at)
        {
            if (LastRoom.TryGetValue(id, out var last) && MathF.Abs(last.X - at.x) < SameSpot && MathF.Abs(last.Y - at.y) < SameSpot && ReferenceEquals(_roomsOf, ShipStatus.Instance))
                return last.Room;
            string? room = RoomAt(at);
            LastRoom[id] = (at.x, at.y, room);
            return room;
        }

        private static string? RoomAt(Vector2 point)
        {
            var ship = ShipStatus.Instance;
            if (ship == null || _roomFailed) return null;
            try
            {
                if (!ReferenceEquals(_roomsOf, ship) || _roomsOf == null)
                {
                    Rooms.Clear();
                    LastRoom.Clear();
                    _roomsOf = ship;
                    foreach (var room in ship.AllRooms)
                    {
                        if (room == null || room.roomArea == null) continue;
                        var b = room.roomArea.bounds;
                        Rooms.Add(new Room { Area = room.roomArea, Name = room.RoomId.ToString(), MinX = b.min.x, MinY = b.min.y, MaxX = b.max.x, MaxY = b.max.y });
                    }
                }
                foreach (var room in Rooms)
                {
                    if (point.x < room.MinX || point.x > room.MaxX || point.y < room.MinY || point.y > room.MaxY) continue;
                    if (room.Area != null && room.Area.OverlapPoint(point)) return room.Name;
                }
            }
            catch (Exception e)
            {
                _roomFailed = true;
                TournamentPlugin.Logger.Warn("Caster feed: couldn't read rooms (" + e.Message + ").");
            }
            return null;
        }

        /// <summary>Two hands / two codes fix a critical sabotage: how many of the two are done (0–1).</summary>
        private static float? Fixing(Func<int> done)
        {
            try { return Math.Min(1f, done() / 2f); }
            catch (Exception) { return null; }
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
                    float? left = null, fixing = null;
                    var reactor = system.TryCast<ReactorSystemType>();
                    var o2 = system.TryCast<LifeSuppSystemType>();
                    var heli = system.TryCast<HeliSabotageSystem>();
                    var lights = system.TryCast<SwitchSystem>();
                    var comms = system.TryCast<HudOverrideSystemType>();
                    var hqComms = system.TryCast<HqHudSystemType>();
                    var mushrooms = system.TryCast<MushroomMixupSabotageSystem>();
                    if (reactor != null) { on = reactor.IsActive; left = reactor.Countdown; fixing = Fixing(() => reactor.UserConsolePairs.Count); }
                    else if (o2 != null) { on = o2.IsActive; left = o2.Countdown; fixing = Fixing(() => o2.CompletedConsoles.Count); }
                    else if (heli != null) { on = heli.IsActive; left = heli.Countdown; fixing = Fixing(() => heli.CompletedConsoles.Count); }
                    else if (lights != null) on = lights.IsActive;
                    else if (comms != null) on = comms.IsActive;
                    else if (hqComms != null) on = hqComms.IsActive;
                    else if (mushrooms != null) on = mushrooms.IsActive;
                    if (on) frame.Sabotages.Add(new FeedSabotage { System = type.ToString(), TimeLeft = left, Fixing = fixing });
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
