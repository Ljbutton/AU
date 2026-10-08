using System.Collections.Generic;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// The players, read from the game once a frame and shared by everything in the mod that needs
    /// them (each read from the game crosses into its native code, so they aren't repeated).
    /// </summary>
    internal static class Frame
    {
        public sealed class Player
        {
            public PlayerControl Pc = null!;
            public NetworkedPlayerInfo? Data;
            public byte Id;
            public bool Dead, Disconnected, InVent, Impostor;
            /// <summary>Where they are (their transform), as of this frame's last <see cref="RefreshPositions"/>.</summary>
            public Vector2 Pos;
        }

        private static readonly List<Player> Pool = new List<Player>();
        private static readonly List<Player> List = new List<Player>();
        private static readonly Dictionary<byte, Player> ById = new Dictionary<byte, Player>();
        private static int _frame = -1, _posFrame = -1;

        /// <summary>Everyone with player data this frame.</summary>
        public static List<Player> Players
        {
            get { Refresh(); return List; }
        }

        public static Player? Get(byte id)
        {
            Refresh();
            return ById.TryGetValue(id, out var p) ? p : null;
        }

        public static PlayerControl? Control(byte id) => Get(id)?.Pc;

        private static void Refresh()
        {
            int now = Time.frameCount;
            if (now == _frame) return;
            _frame = now;
            _posFrame = now;
            List.Clear();
            ById.Clear();
            var all = PlayerControl.AllPlayerControls;
            if (all == null) return;
            int n = all.Count;
            for (int i = 0; i < n; i++)
            {
                var pc = all[i];
                if (pc == null) continue;
                var data = pc.Data;
                if (data == null) continue;
                if (List.Count == Pool.Count) Pool.Add(new Player());
                var p = Pool[List.Count];
                p.Pc = pc;
                p.Data = data;
                p.Id = pc.PlayerId;
                p.Dead = data.IsDead;
                p.Disconnected = data.Disconnected;
                p.InVent = pc.inVent;
                var role = data.Role;
                p.Impostor = role != null && role.IsImpostor;
                var pos = pc.transform.position;
                p.Pos = new Vector2(pos.x, pos.y);
                List.Add(p);
                ById[p.Id] = p;
            }
        }

        /// <summary>Positions again (late in the frame, after everyone has moved), once a frame.</summary>
        public static void RefreshPositions()
        {
            Refresh();
            int now = Time.frameCount;
            if (_posFrame == -2 - now) return;
            _posFrame = -2 - now;
            foreach (var p in List)
            {
                var pos = p.Pc.transform.position;
                p.Pos = new Vector2(pos.x, pos.y);
                p.InVent = p.Pc.inVent;
            }
        }
    }
}
