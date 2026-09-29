using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TournamentTracker.Stats;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// Takes a top-down picture of the real map from the host's own game (no players, no
    /// darkness) for the web replay viewer. Taken once per map and game version, then reused.
    /// </summary>
    internal static class MapPhoto
    {
        private const float PixelsPerUnit = 28f;
        private const int MaxSize = 2400;

        public static ReplayBackground? For(int mapId, ReplayMap map)
        {
            if (mapId < 0 || map.Walls.Count == 0) return null;
            string dir = Path.Combine(TournamentPlugin.DataDir, "maps");
            string name = $"map-{mapId}-{Clean(Application.version)}";
            string meta = Path.Combine(dir, name + ".json");
            if (File.Exists(meta))
            {
                try { return JsonSerializer.Deserialize<ReplayBackground>(File.ReadAllText(meta)); }
                catch (Exception) { /* take a new one */ }
            }

            var points = map.Walls.SelectMany(w => w).ToArray();
            var xs = points.Where((_, i) => i % 2 == 0).ToArray();
            var ys = points.Where((_, i) => i % 2 == 1).ToArray();
            float x0 = xs.Min() - 1, x1 = xs.Max() + 1, y0 = ys.Min() - 1, y1 = ys.Max() + 1;
            byte[]? jpg = Render(x0, y0, x1, y1);
            if (jpg == null) return null;

            var background = new ReplayBackground { Image = "data:image/jpeg;base64," + Convert.ToBase64String(jpg), X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 };
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(meta, JsonSerializer.Serialize(background));
            }
            catch (Exception e) { TournamentPlugin.Logger.Warn("Map picture not saved: " + e.Message); }
            return background;
        }

        private static byte[]? Render(float x0, float y0, float x1, float y1)
        {
            var main = Camera.main;
            if (main == null) return null;
            float w = x1 - x0, h = y1 - y0;
            float ppu = Math.Min(PixelsPerUnit, MaxSize / Math.Max(w, h));
            int pw = Math.Max(64, (int)(w * ppu)), ph = Math.Max(64, (int)(h * ppu));

            GameObject? go = null;
            RenderTexture? target = null;
            Texture2D? texture = null;
            var previous = RenderTexture.active;
            try
            {
                go = new GameObject("TournamentTracker map photo");
                var camera = go.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = h / 2f;
                camera.aspect = w / h;
                camera.transform.position = new Vector3((x0 + x1) / 2f, (y0 + y1) / 2f, main.transform.position.z);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                int mask = main.cullingMask;
                foreach (var layer in new[] { "Players", "Ghost", "UI", "Shadow" })
                {
                    int n = LayerMask.NameToLayer(layer);
                    if (n >= 0) mask &= ~(1 << n);
                }
                camera.cullingMask = mask;
                camera.enabled = false;

                target = new RenderTexture(pw, ph, 24);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                texture = new Texture2D(pw, ph, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, pw, ph), 0, 0);
                texture.Apply();
                return ImageConversion.EncodeToJPG(texture, 82);
            }
            catch (Exception e)
            {
                TournamentPlugin.Logger.Warn("Couldn't take the map picture: " + e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (go != null) Object.Destroy(go);
                if (target != null) Object.Destroy(target);
                if (texture != null) Object.Destroy(texture);
            }
        }

        private static string Clean(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
    }
}
