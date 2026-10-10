using System;
using System.Collections.Generic;
using InnerNet;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// On the host's screen (and so on stream): who is talking on Discord, for players linked to
    /// it. In meetings their card's banner is outlined in their colour; in the lobby a speaker in
    /// their colour shows by their name.
    /// The bot sits muted in the host's voice channel to see it (it never records anyone).
    /// Only drawn in this one game; nothing is sent to the players.
    /// </summary>
    internal static class TalkingLights
    {
        private const float Check = 0.1f, Fade = 0.15f;

        private static float _next;
        private static Sprite? _ring, _speaker;
        private static readonly Dictionary<IntPtr, Light> Cards = new Dictionary<IntPtr, Light>();
        private static readonly Dictionary<byte, Light> Icons = new Dictionary<byte, Light>();
        private static readonly Dictionary<string, (string Key, Color Colour)> KeyByName = new Dictionary<string, (string, Color)>();
        private static MeetingHud? _meeting;
        private static bool _told;

        private sealed class Light
        {
            public GameObject Go = null!;
            public SpriteRenderer Sprite = null!;
            public float Alpha = -1;
            public bool On;
            public Color Colour = Color.white;
            public Transform? Follow;
            /// <summary>A lobby speaker: it follows a player around.</summary>
            public bool Wanders;
            public Vector3 Offset;
        }

        public static void Update()
        {
            var session = TournamentPlugin.Session;
            var client = AmongUsClient.Instance;
            bool hosting = session != null && Game.IsHost && client != null;
            var meeting = hosting ? MeetingHud.Instance : null;
            bool lobby = hosting && client!.GameState == InnerNetClient.GameStates.Joined;
            if (meeting != _meeting) { ClearCards(); _meeting = meeting; _told = false; }
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
            // Names on the cards → players (their game name, or the roster name the referee's nameplates show).
            KeyByName.Clear();
            foreach (var p in Frame.Players)
            {
                if (p.Data == null) continue;
                string real = p.Data.PlayerName ?? "";
                string key = PlayerSnapshot.MakeKey(p.Data.FriendCode, real);
                var colour = ColourOf(p.Data);
                KeyByName[real.Trim()] = (key, colour);
                if (session.DisplayName(key) is string shown) KeyByName[shown.Trim()] = (key, colour);
            }
            // The map open over the meeting: no outlines on top of it.
            bool mapOpen = MapBehaviour.Instance != null && MapBehaviour.Instance.IsOpen;
            foreach (var area in meeting.playerStates)
            {
                if (area == null || area.NameText == null) continue;
                bool talking = KeyByName.TryGetValue(Plain(area.NameText.text), out var who) && session.IsTalking(who.Key)
                    && area.gameObject.activeInHierarchy && area.transform.localScale != Vector3.zero && !mapOpen;
                if (!Cards.TryGetValue(area.Pointer, out var light) || light.Go == null)
                {
                    if (!talking) continue;
                    light = Cards[area.Pointer] = CardLight(area);
                }
                if (talking) light.Colour = who.Colour;
                light.On = talking;
            }
            if (!_told)
            {
                // Once a meeting, for the log: how many cards were matched to players.
                _told = true;
                int cards = 0, known = 0;
                foreach (var area in meeting.playerStates)
                    if (area != null && area.NameText != null && area.transform.localScale != Vector3.zero) { cards++; if (KeyByName.ContainsKey(Plain(area.NameText.text))) known++; }
                TournamentPlugin.Logger.Info($"Who's talking: meeting with {cards} cards, {known} matched to players.");
            }
        }

        /// <summary>A card's name without any colour tags, trimmed.</summary>
        private static string Plain(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text!.IndexOf('<') < 0) return text.Trim();
            var sb = new System.Text.StringBuilder(text.Length);
            bool tag = false;
            foreach (char c in text)
            {
                if (c == '<') tag = true;
                else if (c == '>') tag = false;
                else if (!tag) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// A ring in the player's colour around a meeting card's banner. It hangs off the banner
        /// picture itself, in its own space, so it fits the banner exactly and moves with it (the
        /// cards slide in when the meeting opens, so measuring them on screen goes wrong).
        /// </summary>
        private static Light CardLight(PlayerVoteArea area)
        {
            var bg = area.Background;
            var go = new GameObject("TT Talking");
            // On the card's own layer: the meeting screen is drawn by the screen-overlay camera,
            // which leaves out the default layer.
            go.layer = area.gameObject.layer;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Ring();
            sr.color = new Color(1, 1, 1, 0);
            // Just above the banner, below everything drawn over the meeting (the map).
            sr.sortingOrder = (bg != null ? bg.sortingOrder : 0) + 1;

            if (bg != null && bg.sprite != null)
            {
                sr.sortingLayerID = bg.sortingLayerID;
                go.transform.SetParent(bg.transform, false);
                var size = bg.drawMode != SpriteDrawMode.Simple ? (Vector3)bg.size : bg.sprite.bounds.size;
                var centre = bg.drawMode != SpriteDrawMode.Simple ? Vector3.zero : bg.sprite.bounds.center;
                go.transform.localPosition = new Vector3(centre.x, centre.y, -0.05f);
                // The ring sprite is 1 unit wide and RingHigh high, its outline inset by the glow; a touch
                // larger than the banner so its corners sit inside the outline.
                go.transform.localScale = new Vector3(size.x / (1 - 2 * RingGlow / RingW) * 1.02f, size.y / (1 - 2 * RingGlow / RingH) / RingHigh * 1.06f, 1);
                go.transform.localRotation = Quaternion.identity;
            }
            else
            {
                // No banner picture (shouldn't happen): fall back to the card's own position and the usual banner size.
                go.transform.SetParent(area.transform, false);
                go.transform.localPosition = new Vector3(0, 0, -0.05f);
                go.transform.localScale = new Vector3(2.7f / (1 - 2 * RingGlow / RingW) * 1.02f, 0.65f / (1 - 2 * RingGlow / RingH) / RingHigh * 1.06f, 1);
            }
            return new Light { Go = go, Sprite = sr };
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
                // The player's body can be swapped (back in the lobby after a game, a rejoin): always follow the current one.
                light.Follow = p.Pc.transform;
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
            return new Light { Go = go, Sprite = sr, Follow = p.Pc.transform, Wanders = true };
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
                    var body = p.Pc.transform.position;
                    x = np.x - name.preferredWidth * name.transform.lossyScale.x / 2 - 0.22f - body.x;
                    y = np.y - body.y;
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
                }
                if (light.Wanders && light.Follow == null && light.Alpha > 0)
                {
                    // The body it followed is gone: hide it rather than leave it hanging in place.
                    light.On = false;
                    light.Alpha = 0;
                    light.Go.SetActive(false);
                    continue;
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

        // The ring picture: banner-shaped (about 4.3 : 1, like a meeting card), small corners.
        private const int RingW = 260, RingH = 60;
        private const float RingGlow = 6f, RingHigh = (float)RingH / RingW;

        /// <summary>A rounded outline with a soft glow, 1 unit wide and <see cref="RingHigh"/> high, empty inside.</summary>
        private static Sprite Ring()
        {
            if (_ring != null) return _ring;
            const int w = RingW, h = RingH;
            float radius = 7, edge = 3.5f, glow = RingGlow;
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
