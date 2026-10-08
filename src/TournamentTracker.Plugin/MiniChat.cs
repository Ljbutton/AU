using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// The referee's mini chat: the last few chat messages, always shown small in the bottom-left
    /// corner of the host's screen, so the chat never has to be opened. It goes away in meetings
    /// (the meeting screen and its own chat take the screen) and turns see-through while a
    /// living player is behind it, so it never hides the game. The chat button still opens the
    /// full chat to type in.
    /// </summary>
    internal static class MiniChat
    {
        private const int Keep = 7;
        private const float Width = 3.4f;

        private static readonly List<string> Lines = new List<string>();
        private static readonly StringBuilder Text = new StringBuilder(1024);
        private static TextMeshPro? _text;
        private static bool _dirty = true;
        private static float _alpha = -1, _nextLook;
        private static bool _behind;

        /// <summary>A message shown in this game's chat (from the AddChat hook).</summary>
        public static void Add(PlayerControl? from, string? message)
        {
            if (string.IsNullOrWhiteSpace(message) || !Game.IsHost) return;
            var data = from != null ? from.Data : null;
            string name = data?.PlayerName ?? "?";
            string shown = TournamentPlugin.Session?.DisplayName(PlayerSnapshot.MakeKey(data?.FriendCode, name)) ?? name;
            string colour = "FFFFFF";
            if (data != null && data.DefaultOutfit != null)
            {
                int id = data.DefaultOutfit.ColorId;
                var colors = Palette.PlayerColors;
                if (id >= 0 && id < colors.Length)
                {
                    Color c = colors[id];
                    // Dark colours lightened so they read on the map.
                    float light = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                    if (light < 0.35f) c = Color.Lerp(c, Color.white, 0.45f);
                    colour = ColorUtility.ToHtmlStringRGB(c);
                }
            }
            string ghost = data != null && data.IsDead ? " <color=#9AA6B2>(dead)</color>" : "";
            Lines.Add($"<color=#{colour}><b>{Clean(shown)}</b></color>{ghost}: {Clean(message!)}");
            while (Lines.Count > Keep) Lines.RemoveAt(0);
            _dirty = true;
        }

        /// <summary>No rich-text tricks from players' messages.</summary>
        private static string Clean(string s) => s.Replace("<", "‹").Replace(">", "›");

        /// <summary>Kept out of the player camera's pictures (it's drawn in the world, near the host's view).</summary>
        public static void Hide(bool hide)
        {
            if (_text == null) return;
            if (hide)
            {
                _hiddenForCam = _text.gameObject.activeSelf;
                if (_hiddenForCam) _text.gameObject.SetActive(false);
            }
            else if (_hiddenForCam)
            {
                _text.gameObject.SetActive(true);
                _hiddenForCam = false;
            }
        }
        private static bool _hiddenForCam;

        public static void Update()
        {
            var camera = Camera.main;
            bool show = camera != null && RefSlot.LocalIsRefereeGhost() && MeetingHud.Instance == null && ExileController.Instance == null;
            if (!show)
            {
                if (_text != null && _text.gameObject.activeSelf) _text.gameObject.SetActive(false);
                return;
            }
            if (_text == null && !Make(camera!)) return;
            var text = _text!;
            if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
            if (_dirty)
            {
                _dirty = false;
                Text.Clear();
                if (Lines.Count == 0) Text.Append("<color=#9AA6B2>Chat shows here.</color>");
                for (int i = 0; i < Lines.Count; i++) { if (i > 0) Text.Append('\n'); Text.Append(Lines[i]); }
                text.text = Text.ToString();
            }

            // The bottom-left corner, the same size whatever the zoom.
            float size = camera!.orthographicSize, scale = size / 3f;
            text.transform.localScale = new Vector3(scale, scale, 1);
            float left = -size * camera.aspect + 0.25f * scale, bottom = -size + 0.3f * scale;
            text.transform.localPosition = new Vector3(left, bottom, 5f);

            // See-through while a living player is behind it (checked a few times a second).
            if (Time.unscaledTime >= _nextLook)
            {
                _nextLook = Time.unscaledTime + 0.15f;
                var origin = camera.transform.position;
                float x0 = origin.x + left, y0 = origin.y + bottom;
                float x1 = x0 + Width * scale, y1 = y0 + Math.Max(0.4f, text.preferredHeight) * scale;
                byte? referee = RefSlot.RefereeId();
                _behind = false;
                foreach (var p in Frame.Players)
                {
                    if (p.Dead || p.Disconnected || p.Id == referee) continue;
                    if (p.Pos.x >= x0 - 0.4f && p.Pos.x <= x1 + 0.4f && p.Pos.y >= y0 - 0.4f && p.Pos.y <= y1 + 0.6f) { _behind = true; break; }
                }
            }
            float target = _behind ? 0.22f : 1f;
            float a = _alpha < 0 ? target : Mathf.MoveTowards(_alpha, target, Time.unscaledDeltaTime * 4f);
            if (a != _alpha)
            {
                _alpha = a;
                text.alpha = a;
            }
        }

        private static bool Make(Camera camera)
        {
            try
            {
                var go = new GameObject("TT Mini chat");
                go.transform.SetParent(camera.transform, false);
                _text = go.AddComponent<TextMeshPro>();
                var font = Object.FindObjectOfType<TextMeshPro>();
                if (font != null && font != _text) _text.font = font.font;
                _text.alignment = TextAlignmentOptions.BottomLeft;
                _text.enableWordWrapping = true;
                _text.richText = true;
                _text.fontSize = 1.35f;
                _text.color = Color.white;
                _text.outlineWidth = 0.22f;
                _text.outlineColor = new Color32(0, 0, 0, 255);
                _text.rectTransform.pivot = new Vector2(0, 0);
                _text.rectTransform.sizeDelta = new Vector2(Width, 3.2f);
                _dirty = true;
                return true;
            }
            catch (Exception e)
            {
                TournamentPlugin.Logger.Warn("Mini chat: couldn't make the text: " + e.Message);
                _text = null;
                return false;
            }
        }
    }
}
