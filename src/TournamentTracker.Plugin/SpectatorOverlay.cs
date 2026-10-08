using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using InnerNet;
using TMPro;
using TournamentTracker.Broadcast;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// What the referee ghost's screen shows for the stream, drawn into the game world on this
    /// one game only (nothing is sent to anyone, so players never see it):
    /// <list type="bullet">
    /// <item>the whole map lit, every player shown (vents still hide impostors);</item>
    /// <item>every living crewmate's real, wall-blocked vision with the rest of the map slightly
    /// dimmed (or just one crewmate's, when the caster picks one), or every living player's vision
    /// outline in their colour (RINGS);</item>
    /// <item>"!" over anyone close enough to report a body, by the game's own report check;</item>
    /// <item>a faint eye by a crewmate's name while an impostor is in their sight, flashing when
    /// they see a kill or a vent (which also tells the caster: witnessed_kill / witnessed_vent).</item>
    /// </list>
    /// The vision follows the players every frame, after the camera moves; where each player's
    /// sight reaches (the raycasts) is worked out again at most 20 times a second, sooner when
    /// they move. The other checks run 15 times a second; fades and the bounce every frame.
    /// Hot loops use plain .NET maths (Unity's crosses into the game's native code on each call)
    /// and reuse their buffers, so a frame makes no garbage.
    /// </summary>
    internal static class SpectatorOverlay
    {
        private const float Interval = 1f / 15f;
        private const int VisionRays = 120, FarRays = 60, RingRays = 72;
        /// <summary>Zoomed out past this (camera half-height), fewer rays: each pixel covers more of the map.</summary>
        private const float FarZoom = 6f;
        private const float Z = -0.9f;
        /// <summary>The dim layer: a small picture of the camera's view (a little past its edges), smoothed when stretched.</summary>
        private const int DimW = 192, DimH = 108;
        /// <summary>How soft the edge of a crewmate's sight is, in world units.</summary>
        private const float Soft = 0.22f;
        /// <summary>Raycasts again: at most this often, when moved this far, and at least this often (doors).</summary>
        private const float CastEvery = 0.05f, MovedFar = 0.05f, CastAnyway = 0.25f;
        private const float TwoPi = MathF.PI * 2;

        private static float _next;
        private static bool _active;
        private static float _spectatorAt = -1;
        private static bool _spectator;
        private static GameObject? _root;
        private static Material? _material;
        private static Sprite? _eyeSprite;
        private static bool _shadowOff;
        private static int _wallMask = -1;

        private static readonly List<Frame.Player> Alive = new List<Frame.Player>();
        private static readonly List<Frame.Player> Crew = new List<Frame.Player>();
        private static readonly List<byte> Ids = new List<byte>();

        // Where each player's sight reaches, kept between frames.
        private sealed class Sight
        {
            public float[] Reach = Array.Empty<float>();
            public int Rays;
            public float Radius, X, Y, CastAt = -1;
            public int Version;
            public float Fade;
        }
        private static readonly Dictionary<byte, Sight> Sights = new Dictionary<byte, Sight>();

        // The dim layer.
        private static Texture2D? _dimTex;
        private static Il2CppStructArray<Color32>? _dimPx;
        private static byte[]? _alphaShown;
        private static float[]? _seen;
        private static SpriteRenderer? _dim;
        private static readonly List<float> DimKey = new List<float>(), DimKeyShown = new List<float>();

        // The outlines.
        private sealed class Ring { public LineRenderer Line = null!; public Il2CppStructArray<Vector3> Points = null!; public int Version = -1; public float X = float.NaN, Y = float.NaN; public bool Shown; }
        private static readonly Dictionary<byte, Ring> Rings = new Dictionary<byte, Ring>();

        // "!" and the eye.
        private static readonly List<DeadBody> Bodies = new List<DeadBody>();
        private static float _bodiesAt = -1;
        private static bool _bodiesDirty = true;
        private static readonly Dictionary<byte, Bang> Bangs = new Dictionary<byte, Bang>();
        private static readonly Dictionary<byte, Eye> Eyes = new Dictionary<byte, Eye>();
        private static readonly Dictionary<byte, bool> WasInVent = new Dictionary<byte, bool>();

        private sealed class Bang { public GameObject Go = null!; public TextMeshPro Text = null!; public float Since; public bool On, Shown, Settled; }
        private sealed class Eye
        {
            public GameObject Go = null!; public SpriteRenderer Sprite = null!;
            public float Alpha = -1, Scale = 1; public bool Sees, Shown; public float FlashUntil;
            /// <summary>Where the eye sits from the player: just right of their name (measured 15 times a second).</summary>
            public float OffX = 0.55f, OffY = 0.62f;
        }

        public static void Update()
        {
            var session = TournamentPlugin.Session;
            bool active = session != null && Game.IsHost && Spectator(session)
                && AmongUsClient.Instance != null && AmongUsClient.Instance.GameState == InnerNetClient.GameStates.Started
                && ShipStatus.Instance != null && MeetingHud.Instance == null && ExileController.Instance == null;
            if (active)
            {
                var local = PlayerControl.LocalPlayer;
                active = local != null && Frame.Get(local.PlayerId) is { Dead: true };
            }
            _active = active;
            if (!active)
            {
                Clear();
                return;
            }
            var s = session!.Spectator;
            Lit(s.Lit);
            using (FrameProfiler.Time(FrameProfiler.Part.OverlayAnimate)) Animate(s);
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;
            using (FrameProfiler.Time(FrameProfiler.Part.OverlayCheck)) Check(session, s);
        }

        /// <summary>The host plays as the referee ghost (asked of the session a few times a second, not every frame).</summary>
        private static bool Spectator(TournamentSession session)
        {
            float now = Time.unscaledTime;
            if (now >= _spectatorAt)
            {
                _spectatorAt = now + 0.5f;
                _spectator = session.IsSpectator;
            }
            return _spectator;
        }

        /// <summary>Every frame, after the camera has moved: the vision, so it moves with the players.</summary>
        public static void LateUpdate()
        {
            var session = TournamentPlugin.Session;
            if (!_active || session == null || ShipStatus.Instance == null) return;
            var s = session.Spectator;
            Root();
            Frame.RefreshPositions();
            GatherAlive(session);
            if (s.Vision == "focus" && s.Dim > 0.001f)
            {
                // Every living crewmate, or just the one the caster picked.
                Crew.Clear();
                bool picked = false;
                if (s.Focus is int one)
                    foreach (var p in Alive)
                        if (!p.Impostor && p.Id == one) { Crew.Add(p); picked = true; }
                if (!picked)
                    foreach (var p in Alive)
                        if (!p.Impostor) Crew.Add(p);
                using (FrameProfiler.Time(FrameProfiler.Part.OverlayDim)) DrawDim(Crew, s.Dim);
            }
            else HideDim();
            if (s.Vision == "rings") using (FrameProfiler.Time(FrameProfiler.Part.OverlayRings)) DrawRings(Alive);
            else HideRings();
        }

        /// <summary>The living players in the game (not the referee), into <see cref="Alive"/>.</summary>
        private static void GatherAlive(TournamentSession session)
        {
            Alive.Clear();
            var game = session.Tracker.Current;
            foreach (var p in Frame.Players)
            {
                if (p.Dead || p.Disconnected) continue;
                if (game != null && game.ById(p.Id) == null) continue;      // the referee
                Alive.Add(p);
            }
        }

        private static bool IsAlive(byte id)
        {
            foreach (var p in Alive) if (p.Id == id) return true;
            return false;
        }

        // ---- Lit map --------------------------------------------------------------------------

        private static void Lit(bool on)
        {
            // Runs every frame from the main menu on, so it must never touch HudManager.Instance
            // when there's no HUD: that getter makes an empty HudManager, which then throws a
            // NullReferenceException every frame for the rest of the session.
            if (!on && !_shadowOff) return;
            if (!HudManager.InstanceExists) { _shadowOff = false; return; }
            var quad = HudManager.Instance.ShadowQuad;
            if (quad == null) return;
            if (on && quad.gameObject.activeSelf) { quad.gameObject.SetActive(false); _shadowOff = true; }
            else if (!on && _shadowOff) { quad.gameObject.SetActive(true); _shadowOff = false; }
        }

        // ---- The checks (15 a second) ----------------------------------------------------------

        private static void Check(TournamentSession session, SpectatorSettings s)
        {
            Root();
            GatherAlive(session);

            // "!" and the eye.
            if (s.Report) Rescan();
            foreach (var pc in Alive)
            {
                bool canReport = false;
                if (s.Report)
                    foreach (var body in Bodies)
                        if (body != null && !body.Reported && CanReport(pc.Pc, body)) { canReport = true; break; }
                SetBang(pc, canReport);

                bool sees = false;
                if (s.Eye && !pc.Impostor)
                    foreach (var other in Alive)
                        if (other.Impostor && !other.InVent && Sees(pc, other)) { sees = true; break; }
                SetEye(pc, sees);
            }
            foreach (var (id, bang) in Bangs) if (!IsAlive(id)) bang.On = false;
            foreach (var (id, eye) in Eyes) if (!IsAlive(id)) eye.Sees = false;

            // Someone saw an impostor go into a vent.
            foreach (var pc in Alive)
            {
                bool was = WasInVent.TryGetValue(pc.Id, out var v) && v;
                WasInVent[pc.Id] = pc.InVent;
                if (was || !pc.InVent || !pc.Impostor) continue;
                foreach (var crew in Alive)
                    if (!crew.Impostor && Sees(crew, pc))
                    {
                        Flash(crew.Id);
                        session.Witnessed("vent", crew.Id, pc.Id, FeedReader.Place(pc.Pc));
                    }
            }
        }

        /// <summary>The bodies on the map: looked for again after a kill, else at most once a second.</summary>
        private static void Rescan()
        {
            float now = Time.unscaledTime;
            if (!_bodiesDirty && now < _bodiesAt) return;
            _bodiesDirty = false;
            _bodiesAt = now + 1f;
            Bodies.Clear();
            var found = UnityEngine.Object.FindObjectsOfType<DeadBody>();
            for (int i = 0; i < found.Length; i++) if (found[i] != null) Bodies.Add(found[i]);
        }

        /// <summary>A kill just happened: any crewmate with the killer in sight saw it (called from the kill hook).</summary>
        public static void OnKill(PlayerControl killer, PlayerControl victim)
        {
            _bodiesDirty = true;
            var session = TournamentPlugin.Session;
            if (session == null || !session.IsSpectator || killer == null) return;
            var k = Frame.Get(killer.PlayerId);
            if (k == null) return;
            var kp = killer.transform.position;
            k.Pos = new Vector2(kp.x, kp.y);
            foreach (var pc in Frame.Players)
            {
                if (pc.Dead || pc.Disconnected || pc.Id == victim.PlayerId || pc.Impostor) continue;
                if (session.Tracker.Current?.ById(pc.Id) == null) continue;
                if (!Sees(pc, k)) continue;
                Flash(pc.Id);
                session.Witnessed("kill", pc.Id, killer.PlayerId, FeedReader.Place(victim));
            }
        }

        private static int WallMask => _wallMask >= 0 ? _wallMask : (_wallMask = 1 << LayerMask.NameToLayer("Shadow"));

        /// <summary>How far a player sees right now (their role's vision, lights sabotage included), in world units.</summary>
        private static float Radius(Frame.Player p) => ShipStatus.Instance.CalculateLightRadius(p.Data);

        /// <summary>The viewer has the other player in their real sight: close enough, and no wall in between.</summary>
        private static bool Sees(Frame.Player viewer, Frame.Player other)
        {
            float dx = other.Pos.x - viewer.Pos.x, dy = other.Pos.y - viewer.Pos.y;
            float r = Radius(viewer);
            if (dx * dx + dy * dy > r * r) return false;
            return Physics2D.Linecast(viewer.Pos, other.Pos, WallMask).collider == null;
        }

        /// <summary>The game's own report check: within the report distance, nothing in the way.</summary>
        private static bool CanReport(PlayerControl pc, DeadBody body)
        {
            Vector2 me = pc.GetTruePosition(), at = body.TruePosition;
            float dx = at.x - me.x, dy = at.y - me.y, max = pc.MaxReportDistance;
            if (dx * dx + dy * dy > max * max) return false;
            return !PhysicsHelpers.AnythingBetween(me, at, Constants.ShipAndObjectsMask, false);
        }

        /// <summary>
        /// Where a player's sight reaches in each direction (walls stop it), cast again only when it
        /// may have changed: they moved, their vision changed, or it's been a while (a door).
        /// </summary>
        private static Sight SightOf(Frame.Player p, int rays)
        {
            if (!Sights.TryGetValue(p.Id, out var sight)) Sights[p.Id] = sight = new Sight();
            float now = Time.unscaledTime;
            float r = Radius(p);
            float dx = p.Pos.x - sight.X, dy = p.Pos.y - sight.Y;
            bool stale = sight.Rays != rays || MathF.Abs(r - sight.Radius) > 0.01f || now - sight.CastAt >= CastAnyway
                || (dx * dx + dy * dy > MovedFar * MovedFar && now - sight.CastAt >= CastEvery);
            if (!stale) return sight;
            if (sight.Reach.Length != rays) sight.Reach = new float[rays];
            var origin = p.Pos;
            for (int i = 0; i < rays; i++)
            {
                float a = TwoPi * i / rays;
                var hit = Physics2D.Raycast(origin, new Vector2(MathF.Cos(a), MathF.Sin(a)), r, WallMask);
                // No wall: a zero distance (nothing hit). Players never start inside a wall's edge.
                float d = hit.distance;
                sight.Reach[i] = d > 0f ? d : r;
            }
            sight.Rays = rays;
            sight.Radius = r;
            sight.X = origin.x;
            sight.Y = origin.y;
            sight.CastAt = now;
            sight.Version++;
            return sight;
        }

        // ---- Drawing ----------------------------------------------------------------------------

        private static void Root()
        {
            if (_root != null) return;
            _root = new GameObject("TT Spectator View");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _material = new Material(Shader.Find("Sprites/Default"));
        }

        private static void Layer(GameObject go)
        {
            var local = PlayerControl.LocalPlayer;
            if (local != null) go.layer = local.gameObject.layer;
            go.transform.SetParent(_root!.transform, false);
        }

        /// <summary>
        /// Everything outside the crewmates' sight a little darker. Drawn as a small picture of the
        /// camera's view: each pixel is lit if any of them sees it, with a soft edge, and the picture
        /// is smoothed as it's stretched over the screen, so it looks like the game's own vision.
        /// Drawn again only when something in it moved, and only the pixels that changed are sent.
        /// </summary>
        private static void DrawDim(List<Frame.Player> crew, float dim)
        {
            var camera = Camera.main;
            if (camera == null) { HideDim(); return; }
            if (_dim == null || _dimTex == null)
            {
                _dimTex = new Texture2D(DimW, DimH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                _dimTex.hideFlags = HideFlags.HideAndDontSave;
                _dimPx = new Il2CppStructArray<Color32>(DimW * DimH);
                _alphaShown = new byte[DimW * DimH];
                _seen = new float[DimW * DimH];
                for (int i = 0; i < _alphaShown.Length; i++) _alphaShown[i] = 1;      // differs from anything drawn: all sent the first time
                var go = new GameObject("TT Dim");
                Layer(go);
                _dim = go.AddComponent<SpriteRenderer>();
                _dim.sprite = Sprite.Create(_dimTex, new Rect(0, 0, DimW, DimH), new Vector2(0.5f, 0.5f), DimW);
                _dim.sprite.hideFlags = HideFlags.HideAndDontSave;
                _dim.material = _material;
                DimKeyShown.Clear();
            }

            // The camera's view, 10% past each edge.
            float halfH = camera.orthographicSize * 1.1f, halfW = halfH * camera.aspect;
            var cp = camera.transform.position;
            float cx = cp.x, cy = cp.y;
            float x0 = cx - halfW, y0 = cy - halfH, sx = halfW * 2 / DimW, sy = halfH * 2 / DimH;
            int rays = camera.orthographicSize > FarZoom ? FarRays : VisionRays;
            float dt = Time.unscaledDeltaTime;

            // What the picture depends on; unchanged since last frame: nothing to draw.
            DimKey.Clear();
            DimKey.Add(x0); DimKey.Add(y0); DimKey.Add(sx); DimKey.Add(sy); DimKey.Add(dim);
            foreach (var (id, sight) in Sights)
            {
                bool shown = false;
                foreach (var p in crew) if (p.Id == id) { shown = !p.InVent; break; }
                if (!shown) sight.Fade = MathF.Max(0f, sight.Fade - dt / 0.2f);
            }
            foreach (var p in crew)
            {
                var sight = SightOf(p, rays);
                sight.Fade = p.InVent ? MathF.Max(0f, sight.Fade - dt / 0.2f) : MathF.Min(1f, sight.Fade + dt / 0.2f);
                if (sight.Fade <= 0.001f) continue;
                DimKey.Add(p.Id); DimKey.Add(p.Pos.x); DimKey.Add(p.Pos.y); DimKey.Add(sight.Version); DimKey.Add(sight.Fade);
            }
            bool same = DimKey.Count == DimKeyShown.Count;
            for (int i = 0; same && i < DimKey.Count; i++) same = DimKey[i] == DimKeyShown[i];
            if (!same)
            {
                DimKeyShown.Clear();
                DimKeyShown.AddRange(DimKey);
                Rasterize(crew, x0, y0, sx, sy, dim);
                Place(cx, cy, halfW, halfH);
            }
            if (!_dim.gameObject.activeSelf) _dim.gameObject.SetActive(true);
        }

        private static float _dimX = float.NaN, _dimY, _dimW, _dimH;

        private static void Place(float cx, float cy, float halfW, float halfH)
        {
            if (cx == _dimX && cy == _dimY && halfW == _dimW && halfH == _dimH) return;
            _dimX = cx; _dimY = cy; _dimW = halfW; _dimH = halfH;
            _dim!.transform.position = new Vector3(cx, cy, Z);
            _dim.transform.localScale = new Vector3(halfW * 2, halfH * 2 / ((float)DimH / DimW), 1);
        }

        private static void Rasterize(List<Frame.Player> crew, float x0, float y0, float sx, float sy, float dim)
        {
            var seen = _seen!;
            Array.Clear(seen, 0, seen.Length);
            foreach (var p in crew)
            {
                if (!Sights.TryGetValue(p.Id, out var sight) || sight.Fade <= 0.001f) continue;
                float fade = sight.Fade;
                // Centred where they are now (the reach from the last cast, moved with them: at most a few hundredths off).
                float ox = p.Pos.x, oy = p.Pos.y;
                var reach = sight.Reach;
                int n = sight.Rays;
                float outer = sight.Radius + Soft, outer2 = outer * outer;
                int ix0 = Math.Max(0, (int)MathF.Floor((ox - outer - x0) / sx)), ix1 = Math.Min(DimW - 1, (int)MathF.Ceiling((ox + outer - x0) / sx));
                int iy0 = Math.Max(0, (int)MathF.Floor((oy - outer - y0) / sy)), iy1 = Math.Min(DimH - 1, (int)MathF.Ceiling((oy + outer - y0) / sy));
                float perRad = n / TwoPi, invSoft = 1f / Soft, half = Soft * 0.5f;
                for (int iy = iy0; iy <= iy1; iy++)
                {
                    float dy = y0 + (iy + 0.5f) * sy - oy;
                    int row = iy * DimW;
                    for (int ix = ix0; ix <= ix1; ix++)
                    {
                        float dx = x0 + (ix + 0.5f) * sx - ox;
                        float d2 = dx * dx + dy * dy;
                        if (d2 > outer2) continue;
                        float d = MathF.Sqrt(d2);
                        float a = MathF.Atan2(dy, dx);
                        if (a < 0) a += TwoPi;
                        float f = a * perRad;
                        int i0 = (int)f;
                        float t = f - i0;
                        if (i0 >= n) i0 -= n;
                        int i1 = i0 + 1 == n ? 0 : i0 + 1;
                        float limit = reach[i0] + (reach[i1] - reach[i0]) * t;
                        float v = (limit + half - d) * invSoft;
                        if (v <= 0) continue;
                        if (v > 1) v = 1;
                        v *= fade;
                        if (v > seen[row + ix]) seen[row + ix] = v;
                    }
                }
            }

            // Only the pixels that changed go to the picture; it's sent to the graphics card only if any did.
            var shown = _alphaShown!;
            var px = _dimPx!;
            float most = Math.Clamp(dim, 0f, 1f) * 255f;
            bool changed = false;
            for (int i = 0; i < shown.Length; i++)
            {
                byte a = (byte)(most * (1f - seen[i]));
                if (a == shown[i]) continue;
                shown[i] = a;
                Color32 c = default;
                c.a = a;
                px[i] = c;
                changed = true;
            }
            if (!changed) return;
            _dimTex!.SetPixels32(px);
            _dimTex.Apply(false);
        }

        private static void HideDim()
        {
            if (_dim != null && _dim.gameObject.activeSelf) _dim.gameObject.SetActive(false);
            DimKeyShown.Clear();
            foreach (var sight in Sights.Values) sight.Fade = 0;
        }

        /// <summary>A thin, faint outline of each living player's sight, in their colour.</summary>
        private static void DrawRings(List<Frame.Player> alive)
        {
            foreach (var p in alive)
            {
                if (!Rings.TryGetValue(p.Id, out var ring) || ring.Line == null)
                {
                    var go = new GameObject("TT Ring " + p.Id);
                    Layer(go);
                    var line = go.AddComponent<LineRenderer>();
                    line.material = _material;
                    line.loop = true;
                    line.widthMultiplier = 0.035f;
                    line.useWorldSpace = true;
                    var c = ColorOf(p.Data);
                    c.a = 0.45f;
                    line.startColor = c;
                    line.endColor = c;
                    line.positionCount = RingRays;
                    Rings[p.Id] = ring = new Ring { Line = line, Points = new Il2CppStructArray<Vector3>(RingRays), Shown = true };
                }
                if (p.InVent) { Show(ring, false); continue; }
                var sight = SightOf(p, RingRays);
                float ox = p.Pos.x, oy = p.Pos.y;
                if (sight.Version != ring.Version || ox != ring.X || oy != ring.Y)
                {
                    ring.Version = sight.Version;
                    ring.X = ox;
                    ring.Y = oy;
                    var pts = ring.Points;
                    for (int i = 0; i < RingRays; i++)
                    {
                        float a = TwoPi * i / RingRays;
                        // Never right on top of the player (inside a wall's edge): keeps the line from folding over.
                        float d = MathF.Max(sight.Reach[i], 0.15f);
                        Vector3 v = default;
                        v.x = ox + MathF.Cos(a) * d;
                        v.y = oy + MathF.Sin(a) * d;
                        v.z = Z - 0.01f;
                        pts[i] = v;
                    }
                    ring.Line.SetPositions(pts);
                }
                Show(ring, true);
            }
            foreach (var (id, ring) in Rings)
                if (ring.Line != null && !IsIn(alive, id)) Show(ring, false);
        }

        private static bool IsIn(List<Frame.Player> list, byte id)
        {
            foreach (var p in list) if (p.Id == id) return true;
            return false;
        }

        private static void Show(Ring ring, bool on)
        {
            if (ring.Shown == on) return;
            ring.Shown = on;
            ring.Line.gameObject.SetActive(on);
        }

        private static void HideRings()
        {
            foreach (var ring in Rings.Values) if (ring.Line != null) Show(ring, false);
        }

        private static Color ColorOf(NetworkedPlayerInfo? data)
        {
            int id = data != null && data.DefaultOutfit != null ? data.DefaultOutfit.ColorId : 0;
            var colors = Palette.PlayerColors;
            return id >= 0 && id < colors.Length ? (Color)colors[id] : Color.white;
        }

        private static void SetBang(Frame.Player p, bool on)
        {
            if (!Bangs.TryGetValue(p.Id, out var bang) || bang.Go == null)
            {
                if (!on) return;
                var go = new GameObject("TT Report " + p.Id);
                Layer(go);
                var text = go.AddComponent<TextMeshPro>();
                text.text = "!";
                text.fontSize = 5f;
                text.fontStyle = FontStyles.Bold;
                text.alignment = TextAlignmentOptions.Center;
                text.color = new Color(1f, 0.85f, 0.2f, 1f);
                text.outlineWidth = 0.25f;
                text.outlineColor = new Color32(0, 0, 0, 255);
                bang = Bangs[p.Id] = new Bang { Go = go, Text = text, Shown = true };
            }
            if (on && !bang.On) { bang.Since = Time.unscaledTime; bang.Settled = false; }
            bang.On = on;
            bang.Go.transform.position = new Vector3(p.Pos.x, p.Pos.y + 0.95f, Z - 0.02f);
        }

        private static void SetEye(Frame.Player p, bool sees)
        {
            if (!Eyes.TryGetValue(p.Id, out var eye) || eye.Go == null)
            {
                if (!sees) return;
                var go = new GameObject("TT Eye " + p.Id);
                Layer(go);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = EyeSprite();
                sr.color = new Color(1, 1, 1, 0);
                eye = Eyes[p.Id] = new Eye { Go = go, Sprite = sr, Shown = true };
            }
            eye.Sees = sees;
            Measure(p, eye);
            Follow(p, eye);
        }

        /// <summary>Where just right of the player's name is (its width is measured here, 15 times a second, not every frame).</summary>
        private static void Measure(Frame.Player p, Eye eye)
        {
            eye.OffX = 0.55f;
            eye.OffY = 0.62f;
            try
            {
                var name = p.Pc.cosmetics != null ? p.Pc.cosmetics.nameText : null;
                if (name != null)
                {
                    var np = name.transform.position;
                    eye.OffX = np.x + name.preferredWidth * name.transform.lossyScale.x / 2 + 0.2f - p.Pos.x;
                    eye.OffY = np.y - p.Pos.y;
                }
            }
            catch (Exception) { }
        }

        private static void Follow(Frame.Player p, Eye eye) =>
            eye.Go.transform.position = new Vector3(p.Pos.x + eye.OffX, p.Pos.y + eye.OffY, Z - 0.02f);

        private static void Flash(byte id)
        {
            if (Eyes.TryGetValue(id, out var eye) && eye.Go != null) eye.FlashUntil = Time.unscaledTime + 2f;
            else
            {
                var p = Frame.Get(id);
                if (p == null) return;
                SetEye(p, true);
                if (Eyes.TryGetValue(id, out eye)) eye.FlashUntil = Time.unscaledTime + 2f;
            }
        }

        /// <summary>Every frame: fade the eyes (0.2 s), flash them, bounce the "!" in, follow the players.</summary>
        private static void Animate(SpectatorSettings s)
        {
            float dt = Time.unscaledDeltaTime, now = Time.unscaledTime;
            foreach (var (id, eye) in Eyes)
            {
                if (eye.Go == null) continue;
                bool flashing = now < eye.FlashUntil;
                float target = flashing ? 1f : eye.Sees && s.Eye ? 0.4f : 0f;
                float alpha = MoveTowards(Math.Max(eye.Alpha, 0f), target, dt / 0.2f * (flashing ? 1f : 0.4f));
                if (flashing) alpha = 0.75f + 0.25f * MathF.Sin(now * 14f);
                bool on = alpha > 0.01f;
                if (alpha != eye.Alpha)
                {
                    eye.Alpha = alpha;
                    if (on) eye.Sprite.color = new Color(1, 1, 1, alpha);
                }
                float scale = flashing ? 1.3f : 1f;
                if (scale != eye.Scale) { eye.Scale = scale; eye.Go.transform.localScale = new Vector3(scale, scale, 1); }
                if (on != eye.Shown) { eye.Shown = on; eye.Go.SetActive(on); }
                if (!on) continue;
                var p = Frame.Get(id);
                if (p != null) Follow(p, eye);
            }
            foreach (var (id, bang) in Bangs)
            {
                if (bang.Go == null) continue;
                bool on = bang.On && s.Report;
                if (on != bang.Shown) { bang.Shown = on; bang.Go.SetActive(on); }
                if (!on) continue;
                float e = Math.Clamp((now - bang.Since) / 0.3f, 0f, 1f);
                if (e < 1f)
                {
                    float k = e < 0.6f ? 0.3f + (1.25f - 0.3f) * (e / 0.6f) : 1.25f + (1f - 1.25f) * ((e - 0.6f) / 0.4f);
                    bang.Go.transform.localScale = new Vector3(k, k, 1);
                }
                else if (!bang.Settled) { bang.Settled = true; bang.Go.transform.localScale = new Vector3(1, 1, 1); }
                var p = Frame.Get(id);
                if (p != null) bang.Go.transform.position = new Vector3(p.Pos.x, p.Pos.y + 0.95f, Z - 0.02f);
            }
        }

        private static float MoveTowards(float current, float target, float maxDelta) =>
            MathF.Abs(target - current) <= maxDelta ? target : current + MathF.Sign(target - current) * maxDelta;

        /// <summary>A small eye, drawn once: white almond outline with a dark pupil.</summary>
        private static Sprite EyeSprite()
        {
            if (_eyeSprite != null) return _eyeSprite;
            const int w = 64, h = 40;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float nx = (x - w / 2f) / (w / 2f - 2), ny = (y - h / 2f) / (h / 2f - 2);
                    // The almond: two arcs meeting at the corners.
                    float edge = 1 - nx * nx;
                    float inside = MathF.Abs(ny) - edge * 0.95f;
                    float pupil = MathF.Sqrt(nx * nx * 2.4f + ny * ny * 2.4f);
                    Color32 c = new Color32(0, 0, 0, 0);
                    if (inside < 0) c = new Color32(255, 255, 255, 235);
                    if (inside < 0 && inside > -0.16f) c = new Color32(20, 20, 24, 255);
                    if (pupil < 0.55f && inside < 0) c = new Color32(20, 20, 24, 255);
                    if (pupil < 0.2f && inside < 0) c = new Color32(255, 255, 255, 255);
                    px[y * w + x] = c;
                }
            tex.SetPixels32(px);
            tex.Apply();
            _eyeSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 150f);
            _eyeSprite.hideFlags = HideFlags.HideAndDontSave;
            tex.hideFlags = HideFlags.HideAndDontSave;
            return _eyeSprite;
        }

        /// <summary>Everything off and gone (the map's shadows back), e.g. in meetings, the lobby or when not the referee.</summary>
        private static void Clear()
        {
            Lit(false);
            if (_root == null) return;
            UnityEngine.Object.Destroy(_root);
            _root = null;
            if (_dim != null && _dim.sprite != null) UnityEngine.Object.Destroy(_dim.sprite);
            _dim = null;
            if (_dimTex != null) UnityEngine.Object.Destroy(_dimTex);
            _dimTex = null;
            _dimPx = null;
            _alphaShown = null;
            _seen = null;
            _dimX = float.NaN;
            DimKeyShown.Clear();
            Sights.Clear();
            Rings.Clear();
            Bangs.Clear();
            Eyes.Clear();
            WasInVent.Clear();
            Bodies.Clear();
            _bodiesDirty = true;
            _spectatorAt = -1;
        }
    }
    /// <summary>
    /// On the referee's screen only: players' nameplates show their roster names (from the caster),
    /// in the game and in meetings. Purely local text; nobody else's game changes.
    /// </summary>
    internal static class Nameplates
    {
        private static float _next;
        private static bool _applied;

        public static void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            var session = TournamentPlugin.Session;
            bool on = session != null && Game.IsHost && session.IsSpectator;
            if (!on && !_applied) return;                           // nothing to show, nothing to put back
            var all = PlayerControl.AllPlayerControls;
            for (int i = 0; i < all.Count; i++)
            {
                var pc = all[i];
                if (pc == null || pc.Data == null || pc.cosmetics == null || pc.cosmetics.nameText == null) continue;
                string real = pc.Data.PlayerName ?? "";
                string? shown = on ? session!.DisplayName(PlayerSnapshot.MakeKey(pc.Data.FriendCode, real)) : null;
                string want = shown ?? real;
                if (shown == null && !_applied) continue;          // never touched: leave the game's own text alone
                if (pc.cosmetics.nameText.text != want) pc.cosmetics.nameText.text = want;
            }
            // Meetings: the vote areas show names too. Matched by their text (real name ↔ roster name).
            var meeting = MeetingHud.Instance;
            if (meeting != null && meeting.playerStates != null)
            {
                var toShow = new System.Collections.Generic.Dictionary<string, string>();
                var toReal = new System.Collections.Generic.Dictionary<string, string>();
                for (int i = 0; i < all.Count; i++)
                {
                    var pc = all[i];
                    if (pc == null || pc.Data == null) continue;
                    string real = pc.Data.PlayerName ?? "";
                    string? shown = session?.DisplayName(PlayerSnapshot.MakeKey(pc.Data.FriendCode, real));
                    if (shown == null || shown == real) continue;
                    toShow[real] = shown;
                    toReal[shown] = real;
                }
                foreach (var area in meeting.playerStates)
                {
                    if (area == null || area.NameText == null) continue;
                    string text = area.NameText.text ?? "";
                    if (on && toShow.TryGetValue(text, out var shown)) area.NameText.text = shown;
                    else if (!on && toReal.TryGetValue(text, out var real)) area.NameText.text = real;
                }
            }
            _applied = on;
        }
    }
}
