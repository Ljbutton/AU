using System;
using System.Text;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// The referee's screen (and so the stream's wide view): a small list down the left edge of who
    /// is alive, dead or gone, in each player's colour. Only on the referee's own screen, during
    /// play (meetings show the cards). Nothing about roles, and nothing is sent to the players.
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

        public static void Update()
        {
            bool show = RefSlot.LocalIsRefereeGhost() && MeetingHud.Instance == null && ExileController.Instance == null
                        && HudManager.InstanceExists && !ReplayTheater.Active;
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
            if (now != _last) { _last = now; text.text = now; }
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
                if (p.Disconnected) lines.Append($"<color=#6E7781><s>{shown}</s> left</color>\n");
                else if (p.Dead) lines.Append($"<color=#{colour}><alpha=#70><s>{shown}</s></color> <color=#FF5C5C>dead</color>\n");
                else { alive++; lines.Append($"<color=#{colour}>{shown}</color>\n"); }
            }
            Text.Append($"<color=#FFFFFF><b>Alive {alive}/{total}</b></color>\n").Append(lines);
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
            if (light < 0.25f) c = Color.Lerp(c, Color.white, 0.3f);
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
                _text.fontSize = 1.25f;
                _text.lineSpacing = -8f;
                _text.color = Color.white;
                _text.outlineWidth = 0.15f;
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
