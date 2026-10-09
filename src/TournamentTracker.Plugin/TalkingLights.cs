using System;
using System.Collections.Generic;
using InnerNet;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// On the host's screen (and so on stream): who is talking on Discord, for players linked to
    /// it. In meetings their card lights up in their colour, with a speaker on the outline; in the
    /// lobby a speaker in their
    /// colour shows by their name.
    /// The bot sits muted in the host's voice channel to see it (it never records anyone).
    /// Only drawn in this one game; nothing is sent to the players.
    /// </summary>
    internal static class TalkingLights
    {
        private const float Check = 0.1f, Fade = 0.15f;

        private static float _next;
        private static Sprite? _ring, _speaker, _badge;
        private static readonly Dictionary<IntPtr, Light> Cards = new Dictionary<IntPtr, Light>();
        private static readonly Dictionary<byte, Light> Icons = new Dictionary<byte, Light>();
        private static readonly Dictionary<byte, (string Key, Color Colour)> KeyById = new Dictionary<byte, (string, Color)>();
        private static MeetingHud? _meeting;

        private sealed class Light
        {
            public GameObject Go = null!;
            public SpriteRenderer Sprite = null!;
            /// <summary>The speaker on a meeting card's outline (its own object: the ring is stretched).</summary>
            public SpriteRenderer? Badge;
            public float Alpha = -1;
            public bool On;
            public Color Colour = Color.white;
            public Transform? Follow;
            public Vector3 Offset;
        }

        public static void Update()
        {
            var session = TournamentPlugin.Session;
            var client = AmongUsClient.Instance;
            bool hosting = session != null && Game.IsHost && client != null;
            var meeting = hosting ? MeetingHud.Instance : null;
            bool lobby = hosting && client!.GameState == InnerNetClient.GameStates.Joined;
            if (meeting != _meeting) { ClearCards(); _meeting = meeting; }
            if (!lobby) ClearIcons();

            if (hosting && Time.unscaledTime >= _next && (meeting != null || lobby))
            {
                _next = Time.unscaledTime + Check;
                if (meeting != null) CheckCards(session!, meeting);
                if (lobby) CheckIcons(session!);
            }
            Animate(Cards.Values);
            Animate(Icons.Values);
        }

        // ---- Meetings: the talking player's card lights up ---------------------------------------

        private static void CheckCards(TournamentSession session, MeetingHud meeting)
        {
            if (meeting.playerStates == null) return;
            // Cards → players by the card's player ID (its name text can be the roster name, or
            // changed by the referee's nameplates, so it isn't matched).
            KeyById.Clear();
            foreach (var p in Frame.Players)
            {
                if (p.Data == null) continue;
                KeyById[p.Id] = (PlayerSnapshot.MakeKey(p.Data.FriendCode, p.Data.PlayerName ?? ""), ColourOf(p.Data));
            }
            foreach (var area in meeting.playerStates)
            {
                if (area == null) continue;
                bool talking = KeyById.TryGetValue(area.TargetPlayerId, out var who) && session.IsTalking(who.Key)
                    && area.gameObject.activeInHierarchy && area.transform.localScale != Vector3.zero;
                if (!Cards.TryGetValue(area.Pointer, out var light) || light.Go == null)
                {
                    if (!talking) continue;
                    light = Cards[area.Pointer] = CardLight(area);
                }
                if (talking) light.Colour = who.Colour;
                light.On = talking;
            }
        }

        /// <summary>A green ring around a meeting card, sized to the card.</summary>
        private static Light CardLight(PlayerVoteArea area)
        {
            var go = new GameObject("TT Talking");
            go.transform.SetParent(area.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Ring();
            sr.color = new Color(1, 1, 1, 0);
            // The card's size and drawing order, from its own pictures.
            Bounds? box = null;
            int layer = 0, order = 0;
            foreach (var r in area.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (r == null || r == sr || r.sprite == null) continue;
                if (box is Bounds b) { b.Encapsulate(r.bounds); box = b; } else box = r.bounds;
                layer = r.sortingLayerID;
                order = Math.Max(order, r.sortingOrder);
            }
            sr.sortingLayerID = layer;
            sr.sortingOrder = order + 1;
            var size = box?.size ?? new Vector3(2.7f, 0.65f, 0);
            var centre = box?.center ?? area.transform.position;
            var parent = area.transform.lossyScale;
            float px = Math.Abs(parent.x) < 1e-4f ? 1 : parent.x, py = Math.Abs(parent.y) < 1e-4f ? 1 : parent.y;
            // The ring sprite is 1 unit wide and 0.3 high.
            go.transform.localScale = new Vector3(size.x * 1.04f / px, size.y * 1.12f / 0.3f / py, 1);
            go.transform.position = new Vector3(centre.x, centre.y, area.transform.position.z - 0.05f);

            // A speaker sitting on the outline's top-right corner.
            var badgeGo = new GameObject("TT Talking speaker");
            badgeGo.transform.SetParent(area.transform, false);
            var badge = badgeGo.AddComponent<SpriteRenderer>();
            badge.sprite = Badge();
            badge.color = new Color(1, 1, 1, 0);
            badge.sortingLayerID = layer;
            badge.sortingOrder = order + 2;
            float across = size.y * 0.62f;   // the badge sprite is 1 unit across
            badgeGo.transform.localScale = new Vector3(across / Math.Abs(px), across / Math.Abs(py), 1);
            badgeGo.transform.position = new Vector3(centre.x + size.x * 0.52f - across * 0.35f, centre.y + size.y * 0.56f, area.transform.position.z - 0.06f);
            badgeGo.SetActive(false);
            return new Light { Go = go, Sprite = sr, Badge = badge };
        }

        // ---- The lobby: a speaker by the talking player's name -----------------------------------

        private static void CheckIcons(TournamentSession session)
        {
            foreach (var p in Frame.Players)
            {
                if (p.Data == null) continue;
                string key = PlayerSnapshot.MakeKey(p.Data.FriendCode, p.Data.PlayerName ?? "");
                bool talking = session.IsTalking(key) && !p.Disconnected;
                if (!Icons.TryGetValue(p.Id, out var light) || light.Go == null)
                {
                    if (!talking) continue;
                    light = Icons[p.Id] = IconLight(p);
                }
                if (talking) light.Colour = ColourOf(p.Data);
                light.On = talking;
                Place(p, light);
            }
            foreach (var (id, light) in Icons)
                if (Frame.Get(id) == null) light.On = false;
        }

        private static Light IconLight(Frame.Player p)
        {
            var go = new GameObject("TT Talking " + p.Id);
            UnityEngine.Object.DontDestroyOnLoad(go);
            var local = PlayerControl.LocalPlayer;
            if (local != null) go.layer = local.gameObject.layer;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Speaker();
            sr.color = new Color(1, 1, 1, 0);
            return new Light { Go = go, Sprite = sr, Follow = p.Pc.transform };
        }

        /// <summary>Just left of the player's name (measured ten times a second; followed every frame).</summary>
        private static void Place(Frame.Player p, Light light)
        {
            float x = -0.75f, y = 0.62f;
            try
            {
                var name = p.Pc.cosmetics != null ? p.Pc.cosmetics.nameText : null;
                if (name != null)
                {
                    var np = name.transform.position;
                    x = np.x - name.preferredWidth * name.transform.lossyScale.x / 2 - 0.22f - p.Pos.x;
                    y = np.y - p.Pos.y;
                }
            }
            catch (Exception) { }
            light.Offset = new Vector3(x, y, -0.5f);
        }

        // ---- Fading in and out ---------------------------------------------------------------------

        private static void Animate(IEnumerable<Light> lights)
        {
            float step = Time.unscaledDeltaTime / Fade;
            foreach (var light in lights)
            {
                if (light.Go == null) continue;
                float target = light.On ? 0.95f : 0f;
                float a = Math.Max(light.Alpha, 0f);
                a = Math.Abs(target - a) <= step ? target : a + Math.Sign(target - a) * step;
                if (a != light.Alpha)
                {
                    light.Alpha = a;
                    light.Sprite.color = new Color(light.Colour.r, light.Colour.g, light.Colour.b, a);
                    bool show = a > 0.01f;
                    if (light.Go.activeSelf != show) light.Go.SetActive(show);
                    if (light.Badge != null)
                    {
                        light.Badge.color = new Color(light.Colour.r, light.Colour.g, light.Colour.b, a);
                        if (light.Badge.gameObject.activeSelf != show) light.Badge.gameObject.SetActive(show);
                    }
                }
                if (light.Follow != null && light.Alpha > 0.01f)
                {
                    var f = light.Follow.position;
                    light.Go.transform.position = new Vector3(f.x + light.Offset.x, f.y + light.Offset.y, f.z + light.Offset.z);
                }
            }
        }

        /// <summary>
        /// The player's own colour, made lighter for the darkest ones (black, brown…) so the glow still
        /// shows against the dark meeting screen.
        /// </summary>
        private static Color ColourOf(NetworkedPlayerInfo? data)
        {
            int id = data != null && data.DefaultOutfit != null ? data.DefaultOutfit.ColorId : -1;
            var colors = Palette.PlayerColors;
            Color c = id >= 0 && id < colors.Length ? (Color)colors[id] : Color.white;
            float light = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            if (light < 0.3f)
            {
                float k = (0.3f - light) / 0.3f * 0.45f;
                c = new Color(c.r + (1 - c.r) * k, c.g + (1 - c.g) * k, c.b + (1 - c.b) * k, 1);
            }
            return c;
        }

        private static void ClearCards()
        {
            foreach (var light in Cards.Values)
            {
                if (light.Go != null) UnityEngine.Object.Destroy(light.Go);
                if (light.Badge != null) UnityEngine.Object.Destroy(light.Badge.gameObject);
            }
            Cards.Clear();
        }

        private static void ClearIcons()
        {
            if (Icons.Count == 0) return;
            foreach (var light in Icons.Values) if (light.Go != null) UnityEngine.Object.Destroy(light.Go);
            Icons.Clear();
        }

        // ---- The pictures, drawn once ----------------------------------------------------------------

        /// <summary>A rounded green outline with a soft glow, 1 unit wide and 0.3 high, empty inside.</summary>
        private static Sprite Ring()
        {
            if (_ring != null) return _ring;
            const int w = 200, h = 60;
            float radius = 14, edge = 3.5f, glow = 6;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    // Distance to the rounded rectangle's outline, inset by the glow.
                    float qx = Math.Abs(x + 0.5f - w / 2f) - (w / 2f - glow - radius);
                    float qy = Math.Abs(y + 0.5f - h / 2f) - (h / 2f - glow - radius);
                    float outside = (float)Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - radius;
                    float d = Math.Abs(outside);
                    float a = d <= edge / 2 ? 1f : Math.Max(0, 1 - (d - edge / 2) / glow) * 0.45f;
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            _ring = Make(px, w, h, w);
            return _ring;
        }

        /// <summary>A small speaker with two sound waves.</summary>
        private static Sprite Speaker()
        {
            if (_speaker != null) return _speaker;
            const int s = 48;
            var px = new Color32[s * s];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                    px[y * s + x] = IsSpeaker(x + 0.5f, y + 0.5f - s / 2f) ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
            _speaker = Make(px, s, s, 110);
            return _speaker;
        }

        /// <summary>The speaker's shape, in a 48-wide box centred on y = 0.</summary>
        private static bool IsSpeaker(float fx, float fy)
        {
            bool box = fx >= 4 && fx <= 13 && Math.Abs(fy) <= 6;
            bool cone = fx > 13 && fx <= 24 && Math.Abs(fy) <= 6 + (fx - 13) * 0.9f;
            float r = (float)Math.Sqrt((fx - 22) * (fx - 22) + fy * fy);
            bool front = fx > 26 && Math.Abs(fy) < (fx - 22) * 1.1f;
            bool wave = front && (Math.Abs(r - 10) < 1.8f || Math.Abs(r - 17) < 1.8f);
            return box || cone || wave;
        }

        /// <summary>
        /// The meeting card's speaker: a dark disc with a rim, the speaker on it, 1 unit across. Tinted
        /// in the player's colour, the rim and speaker take the colour and the disc stays dark.
        /// </summary>
        private static Sprite Badge()
        {
            if (_badge != null) return _badge;
            const int s = 64;
            var px = new Color32[s * s];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = x + 0.5f - s / 2f, dy = y + 0.5f - s / 2f;
                    float r = (float)Math.Sqrt(dx * dx + dy * dy);
                    Color32 c = new Color32(0, 0, 0, 0);
                    if (r <= 31) c = r >= 27.5f ? new Color32(255, 255, 255, 255) : new Color32(22, 22, 28, 240);
                    // The speaker, 48 wide scaled to 36 and centred on the disc.
                    if (r < 27.5f && IsSpeaker((dx + 18) * 48 / 36f, dy * 48 / 36f)) c = new Color32(255, 255, 255, 255);
                    if (r > 31 && r < 32) c = new Color32(255, 255, 255, (byte)((32 - r) * 255));
                    px[y * s + x] = c;
                }
            _badge = Make(px, s, s, s);
            return _badge;
        }

        private static Sprite Make(Color32[] px, int w, int h, float ppu)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            tex.SetPixels32(px);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), ppu);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
    }
}
