using System;
using System.Collections.Generic;
using TournamentTracker.Stats;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>Feeds the replay: player positions while a game runs, and the map's walls and rooms at the start.</summary>
    internal static class ReplayCapture
    {
        private static float _next;
        private static bool _mapFailed;

        public static void Update()
        {
            var session = TournamentPlugin.Session;
            if (session == null || !session.RecordingReplay || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + (float)ReplayRecorder.Interval;

            var players = Frame.Players;
            var positions = new List<ReplayPosition>(players.Count);
            foreach (var p in players)
                positions.Add(new ReplayPosition(p.Id, p.Pos.x, p.Pos.y, p.Dead, p.InVent, p.Disconnected));
            session.RecordPositions(positions);
        }

        /// <summary>Reads the map from the game: the vision-blocking walls, the rooms and the vents.</summary>
        public static void CaptureMap()
        {
            var session = TournamentPlugin.Session;
            var ship = ShipStatus.Instance;
            if (session == null || ship == null || !session.RecordingReplay) return;
            try
            {
                var map = new ReplayMap();
                int shadow = LayerMask.NameToLayer("Shadow");
                foreach (var col in ship.GetComponentsInChildren<Collider2D>(true))
                {
                    if (col == null || col.gameObject.layer != shadow) continue;
                    var outline = Outline(col);
                    if (outline != null) map.Walls.Add(outline);
                }
                foreach (var room in ship.AllRooms)
                {
                    if (room == null || room.roomArea == null) continue;
                    var area = Outline(room.roomArea);
                    if (area != null) map.Rooms.Add(new ReplayRoom { Name = room.RoomId.ToString(), Area = area });
                }
                foreach (var vent in ship.AllVents)
                {
                    if (vent == null) continue;
                    var p = vent.transform.position;
                    map.Vents.Add(new[] { Round(p.x), Round(p.y) });
                }
                session.ReplayMapLoaded(map);
            }
            catch (Exception e)
            {
                if (!_mapFailed) TournamentPlugin.Logger.Warn("Replay: couldn't read the map (" + e.Message + "); the replay will show positions only.");
                _mapFailed = true;
            }
            try { CaptureDetails(session); }
            catch (Exception e) { TournamentPlugin.Logger.Warn("Replay: couldn't read the outfits (" + e.Message + ")."); }
            try
            {
                var map = session.ReplayMapInUse;
                int mapId = Game.Options()?.MapId ?? -1;
                if (map != null) map.Background = MapPhoto.For(mapId, map);
            }
            catch (Exception e) { TournamentPlugin.Logger.Warn("Replay: no map picture (" + e.Message + ")."); }
        }

        /// <summary>The map number and everyone's cosmetics, so the in-game replay can dress them.</summary>
        private static void CaptureDetails(TournamentSession session)
        {
            var outfits = new Dictionary<byte, ReplayOutfit>();
            var all = PlayerControl.AllPlayerControls;
            for (int i = 0; i < all.Count; i++)
            {
                var o = all[i]?.Data?.DefaultOutfit;
                if (o == null) continue;
                outfits[all[i].PlayerId] = new ReplayOutfit { Hat = o.HatId ?? "", Skin = o.SkinId ?? "", Visor = o.VisorId ?? "", Pet = o.PetId ?? "", NamePlate = o.NamePlateId ?? "" };
            }
            int mapId = Game.Options()?.MapId ?? -1;
            session.ReplayDetails(mapId, outfits);
        }

        /// <summary>A collider as a line of world points x0,y0,x1,y1… (closed for areas).</summary>
        private static float[]? Outline(Collider2D col)
        {
            var t = col.transform;
            var points = new List<float>();
            void Add(Vector2 local)
            {
                var w = t.TransformPoint(new Vector3(local.x, local.y, 0));
                points.Add(Round(w.x));
                points.Add(Round(w.y));
            }

            var edge = col.TryCast<EdgeCollider2D>();
            if (edge != null)
            {
                foreach (var p in edge.points) Add(p + edge.offset);
                return points.Count >= 4 ? points.ToArray() : null;
            }
            var poly = col.TryCast<PolygonCollider2D>();
            if (poly != null)
            {
                if (poly.pathCount == 0) return null;
                var path = poly.GetPath(0);
                foreach (var p in path) Add(p + poly.offset);
                if (path.Length > 0) Add(path[0] + poly.offset);
                return points.Count >= 6 ? points.ToArray() : null;
            }
            var box = col.TryCast<BoxCollider2D>();
            if (box != null)
            {
                var h = box.size / 2f;
                var o = box.offset;
                Add(o + new Vector2(-h.x, -h.y)); Add(o + new Vector2(h.x, -h.y));
                Add(o + new Vector2(h.x, h.y)); Add(o + new Vector2(-h.x, h.y)); Add(o + new Vector2(-h.x, -h.y));
                return points.ToArray();
            }
            return null;
        }

        private static float Round(float v) => (float)Math.Round(v, 2);
    }
}
