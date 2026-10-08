using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// The referee's mini chat: in meetings, the newest chat messages along the bottom edge of the
    /// host's screen, below the name plates, so the chat never has to be opened to follow the
    /// vote. Not shown during play. The chat button still opens the full chat to type in.
    /// </summary>
    internal static class MiniChat
    {
        private const int Keep = 7;
        /// <summary>Lines shown at once (the newest), so it stays below the name plates.</summary>
        private const int Shown = 3;

        private static readonly List<string> Lines = new List<string>();
        private static readonly StringBuilder Text = new StringBuilder(1024);
        private static TextMeshPro? _text;
        private static bool _dirty = true;

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
            // Only in meetings (the vote is when the chat matters); never during play.
            var meeting = MeetingHud.Instance;
            bool show = meeting != null && RefSlot.LocalIsRefereeGhost();
            if (!show)
            {
                if (_text != null && _text.gameObject.activeSelf) _text.gameObject.SetActive(false);
                return;
            }
            // Drawn by the camera that draws the meeting screen, so it's on top of it.
            var camera = MeetingCamera(meeting!);
            if (camera == null) return;
            if (_text != null && _on != camera) { Object.Destroy(_text.gameObject); _text = null; }
            if (_text == null && !Make(camera, meeting!.gameObject.layer)) return;
            var text = _text!;
            if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
            if (_dirty)
            {
                _dirty = false;
                Text.Clear();
                int from = Math.Max(0, Lines.Count - Shown);
                if (Lines.Count == 0) Text.Append("<color=#9AA6B2>Chat shows here.</color>");
                for (int i = from; i < Lines.Count; i++) { if (i > from) Text.Append('\n'); Text.Append(Lines[i]); }
                text.text = Text.ToString();
            }

            // Along the bottom edge, below the name plates, the same size whatever the camera's zoom.
            float size = camera.orthographicSize, scale = size / 3f;
            text.transform.localScale = new Vector3(scale, scale, 1);
            text.rectTransform.sizeDelta = new Vector2(3f * camera.aspect * 2f * 0.72f, 1.2f);
            // In front of the meeting screen, but not so close the camera cuts it off.
            float z = Math.Max(camera.nearClipPlane + 0.2f, meeting!.transform.position.z - camera.transform.position.z - 3f);
            text.transform.localPosition = new Vector3(0, -size + 0.08f * scale, z);
        }

        /// <summary>The camera that draws the meeting screen (the HUD's; the game camera if it draws both).</summary>
        private static Camera? MeetingCamera(MeetingHud meeting)
        {
            if (_on != null && _onFor == meeting) return _on;
            int bit = 1 << meeting.gameObject.layer;
            Camera? best = null;
            foreach (var c in Camera.allCameras)
                if (c != null && c.orthographic && (c.cullingMask & bit) != 0 && (best == null || c.depth > best.depth)) best = c;
            _on = best ?? Camera.main;
            _onFor = meeting;
            return _on;
        }
        private static Camera? _on;
        private static MeetingHud? _onFor;

        private static bool Make(Camera camera, int layer)
        {
            try
            {
                var go = new GameObject("TT Mini chat");
                go.layer = layer;
                go.transform.SetParent(camera.transform, false);
                _text = go.AddComponent<TextMeshPro>();
                var font = Object.FindObjectOfType<TextMeshPro>();
                if (font != null && font != _text) _text.font = font.font;
                _text.alignment = TextAlignmentOptions.Bottom;
                _text.enableWordWrapping = true;
                _text.richText = true;
                _text.fontSize = 1.15f;
                _text.color = Color.white;
                _text.outlineWidth = 0.22f;
                _text.outlineColor = new Color32(0, 0, 0, 255);
                _text.sortingOrder = 500;
                _text.rectTransform.pivot = new Vector2(0.5f, 0);
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
