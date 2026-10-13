using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TournamentTracker.Plugin
{
    /// <summary>What the mod draws on the host's screen: its own text over the game, and small pictures made once.</summary>
    internal static class Drawing
    {
        /// <summary>The camera that draws this layer (the main one if it draws everything).</summary>
        public static Camera? CameraFor(int layer)
        {
            int bit = 1 << layer;
            Camera? best = null;
            foreach (var c in Camera.allCameras)
                if (c != null && c.orthographic && (c.cullingMask & bit) != 0 && (best == null || c.depth > best.depth)) best = c;
            return best ?? Camera.main;
        }

        /// <summary>White text with a black outline, in the game's own font, on top, carried by the camera.</summary>
        public static TextMeshPro MakeText(string name, Camera camera, int layer)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(camera.transform, false);
            var text = go.AddComponent<TextMeshPro>();
            var font = Object.FindObjectOfType<TextMeshPro>();
            if (font != null && font != text) text.font = font.font;
            text.color = Color.white;
            text.outlineColor = new Color32(0, 0, 0, 255);
            text.sortingOrder = 500;
            return text;
        }

        /// <summary>A picture from its pixels, smoothed when stretched, kept for the whole session.</summary>
        public static Sprite MakeSprite(Color32[] px, int w, int h, float ppu)
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
