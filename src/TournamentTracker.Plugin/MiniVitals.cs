using System;
using System.Text;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// The referee's screen (and so the stream's wide view): a small list down the left edge of the
    /// players, in each one's colour, with who is dead or gone and a red IMP by the impostors (the
    /// referee's screen already shows them). Only on the referee's own screen, during play (meetings
    /// show the cards), and nothing is sent to the players.
    /// </summary>
    internal static class MiniVitals
    {
        private const float Every = 0.25f;
        private static TextMeshPro? _text;
        private static Camera? _on;
        private static float _next;
        private static string _last = "";
        private static readonly StringBuilder Text = new StringBuilder(1024);

        /// <summary>Kept out of the player camera's pictures (it hangs off a camera near the host's view).</summary>
        public static void Hide(bool hide)
        {
            if (_text == null) return;
            if (hide) { _hidden = _text.gameObject.activeSelf; if (_hidden) _text.gameObject.SetActive(false); }
            else if (_hidden) { _text.gameObject.SetActive(true); _hidden = false; }
        }
        private static bool _hidden;

        private static bool _intro;
        private static float _introAt;

        /// <summary>The role intro (or the black screen before it) is up: checked four times a second.</summary>
        private static bool Intro()
        {
            if (Time.unscaledTime < _introAt) return _intro;
            _introAt = Time.unscaledTime + 0.25f;
            _intro = Object.FindObjectOfType<IntroCutscene>() != null || ShipStatus.Instance == null;
            return _intro;
        }

        public static void Update()
        {
            bool show = RefSlot.LocalIsRefereeGhost() && MeetingHud.Instance == null && ExileController.Instance == null
                        && HudManager.InstanceExists && !ReplayTheater.Active && !Intro();
            if (!show)
            {
                if (_text != null && _text.gameObject.activeSelf) _text.gameObject.SetActive(false);
                return;
            }
            var camera = HudCamera();
            if (camera == null) return;
            if (_text != null && _on != camera) { Object.Destroy(_text.gameObject); _text = null; }
            if (_text == null && !Make(camera, HudManager.Instance.gameObject.layer)) return;
            var text = _text!;
            if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);

            // Down the left edge, the same size whatever the zoom.
            float size = camera.orthographicSize, scale = size / 3f;
            text.transform.localScale = new Vector3(scale, scale, 1);
            text.rectTransform.sizeDelta = new Vector2(2.4f, 5f);
            text.transform.localPosition = new Vector3(-size * camera.aspect + 0.12f * scale, 0.1f * size, camera.nearClipPlane + 1f);

            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Every;
            string now = Build();
            if (now != _last) { _last = now; text.text = now; FitPanel(text); }
        }

        // ---- The panel behind the list, in The Button's look: a dark see-through card with rounded
        // corners and a faint light edge (its --panel and --line colours).

        private static SpriteRenderer? _panel;
        private static Sprite? _panelSprite;
        private const float Pad = 0.12f;

        private static void FitPanel(TextMeshPro text)
        {
            try
            {
                if (_panel == null) MakePanel(text);
                if (_panel == null) return;
                text.ForceMeshUpdate();
                var b = text.textBounds;
                _panel.transform.localPosition = new Vector3(b.center.x, b.center.y, 0.01f);
                _panel.size = new Vector2(b.size.x + Pad * 2, b.size.y + Pad * 2);
            }
            catch (Exception e)
            {
                if (!_panelFailed) TournamentPlugin.Logger.Warn("Mini vitals: no panel behind the list (" + e.Message + ").");
                _panelFailed = true;
            }
        }
        private static bool _panelFailed;

        private static void MakePanel(TextMeshPro text)
        {
            if (_panelFailed) return;
            if (_panelSprite == null)
            {
                const int n = 32, r = 9;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var fill = new Color(14 / 255f, 17 / 255f, 24 / 255f, 0.82f);
                var edge = new Color(1f, 1f, 1f, 0.16f);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        // Distance outside the rounded rectangle's inner corner circles (0 inside the straight parts).
                        float dx = Math.Max(Math.Max(r - x - 0.5f, x + 0.5f - (n - r)), 0), dy = Math.Max(Math.Max(r - y - 0.5f, y + 0.5f - (n - r)), 0);
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        var c = d > r ? new Color(0, 0, 0, 0) : d > r - 1.2f ? edge : fill;
                        if (d > r - 1f && d <= r) c.a *= r - d;        // soft outer edge
                        tex.SetPixel(x, y, c);
                    }
                tex.Apply();
                _panelSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r + 1, r + 1, r + 1, r + 1));
                _panelSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }
            var go = new GameObject("TT Mini vitals panel");
            go.layer = text.gameObject.layer;
            go.transform.SetParent(text.transform, false);
            _panel = go.AddComponent<SpriteRenderer>();
            _panel.sprite = _panelSprite;
            _panel.drawMode = SpriteDrawMode.Sliced;
            _panel.sortingOrder = text.sortingOrder - 1;
        }

        private static string Build()
        {
            Text.Clear();
            var referee = RefSlot.RefereeId();
            var session = TournamentPlugin.Session;
            int alive = 0, total = 0;
            var lines = new StringBuilder(512);
            foreach (var p in Frame.Players)
            {
                if (p.Data == null || p.Id == referee) continue;
                total++;
                string name = p.Data.PlayerName ?? "?";
                string shown = session?.DisplayName(PlayerSnapshot.MakeKey(p.Data.FriendCode, name)) ?? name;
                shown = shown.Replace("<", "‹").Replace(">", "›");
                string colour = Colour(p.Data);
                // The Button's colours: its pink-red for impostors and deaths, its grey for the gone.
                string imp = p.Impostor ? " <size=75%><color=#FF2D55><b>IMP</b></color></size>" : "";
                if (p.Disconnected) lines.Append($"<color=#9AA4B2><s>{shown}</s></color>{imp} <size=75%><color=#9AA4B2>LEFT</color></size>\n");
                else if (p.Dead) lines.Append($"<color=#{colour}><alpha=#80><s>{shown}</s></color>{imp} <size=75%><color=#FF2D55>DEAD</color></size>\n");
                else { alive++; lines.Append($"<color=#{colour}>{shown}</color>{imp}\n"); }
            }
            Text.Append($"<size=80%><color=#9AA4B2>PLAYERS</color></size>  <color=#46C28B><b>{alive}</b></color><color=#9AA4B2>/{total} alive</color>\n").Append(lines);
            return Text.ToString().TrimEnd('\n');
        }

        /// <summary>The player's colour, the darkest ones lifted a little so they read on the map.</summary>
        private static string Colour(NetworkedPlayerInfo data)
        {
            int id = data.DefaultOutfit != null ? data.DefaultOutfit.ColorId : -1;
            var colors = Palette.PlayerColors;
            if (id < 0 || id >= colors.Length) return "FFFFFF";
            Color c = colors[id];
            float light = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            // Lifted so every colour reads on the dark panel (black, brown, purple, dark green).
            if (light < 0.45f) c = Color.Lerp(c, Color.white, 0.45f - light + 0.15f);
            return ColorUtility.ToHtmlStringRGB(c);
        }

        /// <summary>The camera that draws the HUD (so the list stays put while the map camera moves and zooms).</summary>
        private static Camera? HudCamera()
        {
            if (_on != null) return _on;
            int bit = 1 << HudManager.Instance.gameObject.layer;
            Camera? best = null;
            foreach (var c in Camera.allCameras)
                if (c != null && c.orthographic && (c.cullingMask & bit) != 0 && (best == null || c.depth > best.depth)) best = c;
            // Usually the main camera draws the HUD too; the list is sized to whichever does.
            _on = best ?? Camera.main;
            return _on;
        }

        private static bool Make(Camera camera, int layer)
        {
            try
            {
                var go = new GameObject("TT Mini vitals");
                go.layer = layer;
                go.transform.SetParent(camera.transform, false);
                _text = go.AddComponent<TextMeshPro>();
                var font = Object.FindObjectOfType<TextMeshPro>();
                if (font != null && font != _text) _text.font = font.font;
                _text.alignment = TextAlignmentOptions.MidlineLeft;
                _text.enableWordWrapping = false;
                _text.richText = true;
                _text.fontSize = 1.5f;
                _text.lineSpacing = -8f;
                _text.color = Color.white;
                _text.outlineWidth = 0.1f;
                _text.outlineColor = new Color32(0, 0, 0, 255);
                _text.sortingOrder = 500;
                _text.rectTransform.pivot = new Vector2(0, 0.5f);
                _last = "";
                return true;
            }
            catch (Exception e)
            {
                TournamentPlugin.Logger.Warn("Mini vitals: couldn't make the text: " + e.Message);
                _text = null;
                return false;
            }
        }
    }
}
