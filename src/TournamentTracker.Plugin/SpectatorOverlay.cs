using System;
using System.Collections.Generic;
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
    /// The vision is drawn every frame, after the camera moves, so it stays with the players; the
    /// other checks run 15 times a second; fades and the bounce every frame.
    /// </summary>
    internal static class SpectatorOverlay
    {
        private const float Interval = 1f / 15f;
        private const int VisionRays = 120, RingRays = 72;
        private const float Z = -0.9f;
        /// <summary>The dim layer: a small picture of the camera's view (a little past its edges), smoothed when stretched.</summary>
        private const int DimW = 192, DimH = 108;
        /// <summary>How soft the edge of a crewmate's sight is, in world units.</summary>
        private const float Soft = 0.22f;

        private static float _next;
        private static bool _active;
        private static GameObject? _root;
        private static Material? _material;
        private static Texture2D? _dimTex;
        private static Color32[]? _dimPx;
        private static float[]? _seen;
        private static SpriteRenderer? _dim;
        private static readonly Dictionary<byte, float> Fade = new Dictionary<byte, float>();
        private static Sprite? _eyeSprite;
        private static bool _shadowOff;
        private static int _wallMask = -1;
        private static readonly Dictionary<byte, LineRenderer> Rings = new Dictionary<byte, LineRenderer>();
        private static readonly Dictionary<byte, Bang> Bangs = new Dictionary<byte, Bang>();
        private static readonly Dictionary<byte, Eye> Eyes = new Dictionary<byte, Eye>();
        private static readonly Dictionary<byte, bool> WasInVent = new Dictionary<byte, bool>();

        private sealed class Bang { public GameObject Go = null!; public TextMeshPro Text = null!; public float Since; public bool On; }
        private sealed class Eye { public GameObject Go = null!; public SpriteRenderer Sprite = null!; public float Alpha; public bool Sees; public float FlashUntil; }

        public static void Update()
        {
            var session = TournamentPlugin.Session;
            var local = PlayerControl.LocalPlayer;
            bool active = session != null && Game.IsHost && session.IsSpectator
                && local != null && local.Data != null && local.Data.IsDead
                && AmongUsClient.Instance != null && AmongUsClient.Instance.GameState == InnerNetClient.GameStates.Started
                && ShipStatus.Instance != null && MeetingHud.Instance == null && ExileController.Instance == null;
            _active = active;
            if (!active)
            {
                Clear();
                return;
            }
            var s = session!.Spectator;
            Lit(s.Lit);
            Animate(s);
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;
            Check(session, s);
        }

        /// <summary>Every frame, after the camera has moved: the vision, so it moves with the players.</summary>
        public static void LateUpdate()
        {
            var session = TournamentPlugin.Session;
            if (!_active || session == null || ShipStatus.Instance == null) return;
            var s = session.Spectator;
            Root();
            var game = session.Tracker.Current;
            var alive = new List<PlayerControl>();
            var all = PlayerControl.AllPlayerControls;
            for (int i = 0; i < all.Count; i++)
            {
                var pc = all[i];
                if (pc == null || pc.Data == null || pc.Data.IsDead || pc.Data.Disconnected) continue;
                if (game != null && game.ById(pc.PlayerId) == null) continue;      // the referee
                alive.Add(pc);
            }
            if (s.Vision == "focus")
            {
                // Every living crewmate, or just the one the caster picked.
                var crew = alive.FindAll(p => !IsImpostor(p));
                if (s.Focus is int picked && crew.Exists(p => p.PlayerId == picked)) crew = crew.FindAll(p => p.PlayerId == picked);
                DrawDim(crew, s.Dim);
            }
            else HideDim();
            if (s.Vision == "rings") DrawRings(alive); else HideRings();
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
            var game = session.Tracker.Current;
            var all = PlayerControl.AllPlayerControls;
            var alive = new List<PlayerControl>();
            for (int i = 0; i < all.Count; i++)
            {
                var pc = all[i];
                if (pc == null || pc.Data == null || pc.Data.IsDead || pc.Data.Disconnected) continue;
                if (game != null && game.ById(pc.PlayerId) == null) continue;      // the referee
                alive.Add(pc);
            }

            // "!" and the eye.
            var bodies = s.Report ? UnityEngine.Object.FindObjectsOfType<DeadBody>() : null;
            foreach (var pc in alive)
            {
                bool canReport = false;
                if (bodies != null)
                    for (int b = 0; b < bodies.Length; b++)
                        if (bodies[b] != null && !bodies[b].Reported && CanReport(pc, bodies[b])) { canReport = true; break; }
                SetBang(pc, canReport);

                bool sees = false;
                if (s.Eye && !IsImpostor(pc))
                    foreach (var other in alive)
                        if (IsImpostor(other) && !other.inVent && Sees(pc, other)) { sees = true; break; }
                SetEye(pc, sees);
            }
            foreach (var id in new List<byte>(Bangs.Keys)) if (!alive.Exists(p => p.PlayerId == id)) Bangs[id].On = false;
            foreach (var id in new List<byte>(Eyes.Keys)) if (!alive.Exists(p => p.PlayerId == id)) Eyes[id].Sees = false;

            // Someone saw an impostor go into a vent.
            foreach (var pc in alive)
            {
                bool was = WasInVent.TryGetValue(pc.PlayerId, out var v) && v;
                WasInVent[pc.PlayerId] = pc.inVent;
                if (was || !pc.inVent || !IsImpostor(pc)) continue;
                foreach (var crew in alive)
                    if (!IsImpostor(crew) && Sees(crew, pc))
                    {
                        Flash(crew.PlayerId);
                        session.Witnessed("vent", crew.PlayerId, pc.PlayerId, FeedReader.Place(pc));
                    }
            }
        }

        /// <summary>A kill just happened: any crewmate with the killer in sight saw it (called from the kill hook).</summary>
        public static void OnKill(PlayerControl killer, PlayerControl victim)
        {
            var session = TournamentPlugin.Session;
            if (session == null || !session.IsSpectator || killer == null) return;
            var all = PlayerControl.AllPlayerControls;
            for (int i = 0; i < all.Count; i++)
            {
                var pc = all[i];
                if (pc == null || pc.Data == null || pc.Data.IsDead || pc.Data.Disconnected || pc.PlayerId == victim.PlayerId || IsImpostor(pc)) continue;
                if (session.Tracker.Current?.ById(pc.PlayerId) == null) continue;
                if (!Sees(pc, killer)) continue;
                Flash(pc.PlayerId);
                session.Witnessed("kill", pc.PlayerId, killer.PlayerId, FeedReader.Place(victim));
            }
        }

        private static bool IsImpostor(PlayerControl pc) => pc.Data != null && pc.Data.Role != null && pc.Data.Role.IsImpostor;

        private static int WallMask => _wallMask >= 0 ? _wallMask : (_wallMask = 1 << LayerMask.NameToLayer("Shadow"));

        /// <summary>How far a player sees right now (their role's vision, lights sabotage included), in world units.</summary>
        private static float Radius(PlayerControl pc) => ShipStatus.Instance.CalculateLightRadius(pc.Data);

        /// <summary>The viewer has the other player in their real sight: close enough, and no wall in between.</summary>
        private static bool Sees(PlayerControl viewer, PlayerControl other)
        {
            Vector2 a = viewer.transform.position, b = other.transform.position;
            float d = Vector2.Distance(a, b);
            if (d > Radius(viewer)) return false;
            return Physics2D.Linecast(a, b, WallMask).collider == null;
        }

        /// <summary>The game's own report check: within the report distance, nothing in the way.</summary>
        private static bool CanReport(PlayerControl pc, DeadBody body)
        {
            Vector2 me = pc.GetTruePosition(), at = body.TruePosition;
            if (Vector2.Distance(me, at) > pc.MaxReportDistance) return false;
            return !PhysicsHelpers.AnythingBetween(me, at, Constants.ShipAndObjectsMask, false);
        }

        /// <summary>How far a player's sight reaches in each direction (walls stop it), from their position.</summary>
        private static float[] Reach(PlayerControl pc, int rays, float r)
        {
            Vector2 o = pc.transform.position;
            var reach = new float[rays];
            for (int i = 0; i < rays; i++)
            {
                float a = Mathf.PI * 2 * i / rays;
                var hit = Physics2D.Raycast(o, new Vector2(Mathf.Cos(a), Mathf.Sin(a)), r, WallMask);
                reach[i] = hit.collider != null ? hit.distance : r;
            }
            return reach;
        }

        /// <summary>Fades a player's vision in and out (0.2 s) as they come and go (a vent, a death, picked or not).</summary>
        private static float FadeOf(byte id, bool on)
        {
            Fade.TryGetValue(id, out var f);
            f = Mathf.MoveTowards(f, on ? 1f : 0f, Time.unscaledDeltaTime / 0.2f);
            Fade[id] = f;
            return f;
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
        /// </summary>
        private static void DrawDim(List<PlayerControl> crew, float dim)
        {
            var camera = Camera.main;
            if (camera == null) { HideDim(); return; }
            if (_dim == null || _dimTex == null)
            {
                _dimTex = new Texture2D(DimW, DimH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                _dimTex.hideFlags = HideFlags.HideAndDontSave;
                _dimPx = new Color32[DimW * DimH];
                _seen = new float[DimW * DimH];
                var go = new GameObject("TT Dim");
                Layer(go);
                _dim = go.AddComponent<SpriteRenderer>();
                _dim.sprite = Sprite.Create(_dimTex, new Rect(0, 0, DimW, DimH), new Vector2(0.5f, 0.5f), DimW);
                _dim.sprite.hideFlags = HideFlags.HideAndDontSave;
                _dim.material = _material;
            }

            // The camera's view, 10% past each edge.
            float halfH = camera.orthographicSize * 1.1f, halfW = halfH * camera.aspect;
            Vector2 c = camera.transform.position;
            float x0 = c.x - halfW, y0 = c.y - halfH, sx = halfW * 2 / DimW, sy = halfH * 2 / DimH;
            var seen = _seen!;
            Array.Clear(seen, 0, seen.Length);

            // Fades for those no longer shown.
            foreach (var id in new List<byte>(Fade.Keys))
                if (!crew.Exists(p => p.PlayerId == id)) FadeOf(id, false);

            foreach (var pc in crew)
            {
                float fade = FadeOf(pc.PlayerId, !pc.inVent);
                if (fade <= 0.001f) continue;
                Vector2 o = pc.transform.position;
                float r = Radius(pc);
                var reach = Reach(pc, VisionRays, r);
                float outer = r + Soft;
                int ix0 = Mathf.Max(0, Mathf.FloorToInt((o.x - outer - x0) / sx)), ix1 = Mathf.Min(DimW - 1, Mathf.CeilToInt((o.x + outer - x0) / sx));
                int iy0 = Mathf.Max(0, Mathf.FloorToInt((o.y - outer - y0) / sy)), iy1 = Mathf.Min(DimH - 1, Mathf.CeilToInt((o.y + outer - y0) / sy));
                float perRad = VisionRays / (Mathf.PI * 2);
                for (int iy = iy0; iy <= iy1; iy++)
                {
                    float dy = y0 + (iy + 0.5f) * sy - o.y;
                    int row = iy * DimW;
                    for (int ix = ix0; ix <= ix1; ix++)
                    {
                        float dx = x0 + (ix + 0.5f) * sx - o.x;
                        float d2 = dx * dx + dy * dy;
                        if (d2 > outer * outer) continue;
                        float d = Mathf.Sqrt(d2);
                        float a = Mathf.Atan2(dy, dx);
                        if (a < 0) a += Mathf.PI * 2;
                        float f = a * perRad;
                        int i0 = (int)f % VisionRays;
                        float limit = Mathf.Lerp(reach[i0], reach[(i0 + 1) % VisionRays], f - Mathf.Floor(f));
                        float v = Mathf.Clamp01((limit + Soft * 0.5f - d) / Soft) * fade;
                        if (v > seen[row + ix]) seen[row + ix] = v;
                    }
                }
            }

            var px = _dimPx!;
            float most = Mathf.Clamp01(dim) * 255f;
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, (byte)(most * (1f - seen[i])));
            _dimTex.SetPixels32(px);
            _dimTex.Apply(false);

            _dim.transform.position = new Vector3(c.x, c.y, Z);
            _dim.transform.localScale = new Vector3(halfW * 2, halfH * 2 / ((float)DimH / DimW), 1);
            _dim.gameObject.SetActive(true);
        }

        private static void HideDim()
        {
            if (_dim != null) _dim.gameObject.SetActive(false);
            Fade.Clear();
        }

        /// <summary>A thin, faint outline of each living player's sight, in their colour.</summary>
        private static void DrawRings(List<PlayerControl> alive)
        {
            foreach (var pc in alive)
            {
                if (!Rings.TryGetValue(pc.PlayerId, out var line) || line == null)
                {
                    var go = new GameObject("TT Ring " + pc.PlayerId);
                    Layer(go);
                    line = go.AddComponent<LineRenderer>();
                    line.material = _material;
                    line.loop = true;
                    line.widthMultiplier = 0.035f;
                    line.useWorldSpace = true;
                    Rings[pc.PlayerId] = line;
                }
                if (pc.inVent) { line.gameObject.SetActive(false); continue; }
                var c = ColorOf(pc);
                c.a = 0.45f;
                line.startColor = c;
                line.endColor = c;
                Vector2 o = pc.transform.position;
                var reach = Reach(pc, RingRays, Radius(pc));
                var pts = new Vector3[RingRays];
                for (int i = 0; i < RingRays; i++)
                {
                    float a = Mathf.PI * 2 * i / RingRays;
                    // Never right on top of the player (inside a wall's edge): keeps the line from folding over.
                    float d = Mathf.Max(reach[i], 0.15f);
                    pts[i] = new Vector3(o.x + Mathf.Cos(a) * d, o.y + Mathf.Sin(a) * d, Z - 0.01f);
                }
                line.positionCount = pts.Length;
                line.SetPositions(pts);
                line.gameObject.SetActive(true);
            }
            foreach (var (id, line) in Rings)
                if (line != null && !alive.Exists(p => p.PlayerId == id)) line.gameObject.SetActive(false);
        }

        private static void HideRings()
        {
            foreach (var line in Rings.Values) if (line != null) line.gameObject.SetActive(false);
        }

        private static Color ColorOf(PlayerControl pc)
        {
            int id = pc.Data.DefaultOutfit != null ? pc.Data.DefaultOutfit.ColorId : 0;
            var colors = Palette.PlayerColors;
            return id >= 0 && id < colors.Length ? (Color)colors[id] : Color.white;
        }

        private static void SetBang(PlayerControl pc, bool on)
        {
            if (!Bangs.TryGetValue(pc.PlayerId, out var bang) || bang.Go == null)
            {
                if (!on) return;
                var go = new GameObject("TT Report " + pc.PlayerId);
                Layer(go);
                var text = go.AddComponent<TextMeshPro>();
                text.text = "!";
                text.fontSize = 5f;
                text.fontStyle = FontStyles.Bold;
                text.alignment = TextAlignmentOptions.Center;
                text.color = new Color(1f, 0.85f, 0.2f, 1f);
                text.outlineWidth = 0.25f;
                text.outlineColor = new Color32(0, 0, 0, 255);
                bang = Bangs[pc.PlayerId] = new Bang { Go = go, Text = text };
            }
            if (on && !bang.On) bang.Since = Time.unscaledTime;
            bang.On = on;
            var p = pc.transform.position;
            bang.Go.transform.position = new Vector3(p.x, p.y + 0.95f, Z - 0.02f);
        }

        private static void SetEye(PlayerControl pc, bool sees)
        {
            if (!Eyes.TryGetValue(pc.PlayerId, out var eye) || eye.Go == null)
            {
                if (!sees) return;
                var go = new GameObject("TT Eye " + pc.PlayerId);
                Layer(go);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = EyeSprite();
                sr.color = new Color(1, 1, 1, 0);
                eye = Eyes[pc.PlayerId] = new Eye { Go = go, Sprite = sr };
            }
            eye.Sees = sees;
            Place(pc, eye);
        }

        /// <summary>Just to the right of the player's name.</summary>
        private static void Place(PlayerControl pc, Eye eye)
        {
            var p = pc.transform.position;
            float x = p.x + 0.55f, y = p.y + 0.62f;
            try
            {
                var name = pc.cosmetics != null ? pc.cosmetics.nameText : null;
                if (name != null)
                {
                    var np = name.transform.position;
                    x = np.x + name.preferredWidth * name.transform.lossyScale.x / 2 + 0.2f;
                    y = np.y;
                }
            }
            catch (Exception) { }
            eye.Go.transform.position = new Vector3(x, y, Z - 0.02f);
        }

        private static void Flash(byte id)
        {
            if (Eyes.TryGetValue(id, out var eye) && eye.Go != null) eye.FlashUntil = Time.unscaledTime + 2f;
            else
            {
                var pc = Game.Player(id);
                if (pc == null) return;
                SetEye(pc, true);
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
                eye.Alpha = Mathf.MoveTowards(eye.Alpha, target, dt / 0.2f * (flashing ? 1f : 0.4f));
                if (flashing) eye.Alpha = 0.75f + 0.25f * Mathf.Sin(now * 14f);
                eye.Sprite.color = new Color(1, 1, 1, eye.Alpha);
                float scale = flashing ? 1.3f : 1f;
                eye.Go.transform.localScale = new Vector3(scale, scale, 1);
                eye.Go.SetActive(eye.Alpha > 0.01f);
                var pc = Game.Player(id);
                if (pc != null) Place(pc, eye);
            }
            foreach (var (id, bang) in Bangs)
            {
                if (bang.Go == null) continue;
                bang.Go.SetActive(bang.On && s.Report);
                if (!bang.On) continue;
                float e = Mathf.Clamp01((now - bang.Since) / 0.3f);
                float k = e < 0.6f ? Mathf.Lerp(0.3f, 1.25f, e / 0.6f) : Mathf.Lerp(1.25f, 1f, (e - 0.6f) / 0.4f);
                bang.Go.transform.localScale = new Vector3(k, k, 1);
                var pc = Game.Player(id);
                if (pc != null)
                {
                    var p = pc.transform.position;
                    bang.Go.transform.position = new Vector3(p.x, p.y + 0.95f, Z - 0.02f);
                }
            }
        }

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
                    float inside = Mathf.Abs(ny) - edge * 0.95f;
                    float pupil = Mathf.Sqrt(nx * nx * 2.4f + ny * ny * 2.4f);
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
            _seen = null;
            Fade.Clear();
            Rings.Clear();
            Bangs.Clear();
            Eyes.Clear();
            WasInVent.Clear();
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
