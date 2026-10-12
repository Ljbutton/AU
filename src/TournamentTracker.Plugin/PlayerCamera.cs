using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using InnerNet;
using TournamentTracker.PlayerCam;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// The player camera, on the host's game: a second camera, never shown on the host's screen,
    /// that follows one player up close (the caster's pick, or one it picks itself) and is read
    /// back for The Button to send to the caster as its own stream. Only drawn while Red Alert has
    /// it on and The Button's send page is asking for pictures.
    /// </summary>
    /// <remarks>
    /// Read back with ReadPixels: the graphics card's asynchronous read-back (AsyncGPUReadback)
    /// isn't in the game's interop assemblies, and merely naming it made the whole class fail to
    /// load. A picture is drawn one frame and read at the start of the next, once the graphics card
    /// has finished it (read straight after drawing, the game waited for the card every time). Not
    /// drawn in meetings (Red Alert goes back to the whole map then), and only a few times a second
    /// while the camera isn't on stream. A failure turns the camera off for the session (logged
    /// once); the host's main video is separate and carries on.
    /// </remarks>
    internal static class PlayerCamera
    {
        /// <summary>How much of the map it shows: a little wider than a player's own screen.</summary>
        private const float Size = 3.4f;

        private static GameObject? _go;
        private static Camera? _cam;
        private static RenderTexture? _rt;
        private static Texture2D? _tex;
        private static float _nextShot, _nextPick;
        private static byte? _target;
        /// <summary>Who the camera is following while it's drawn (null when it isn't).</summary>
        public static byte? Following => _placed ? _target : null;
        private static Vector2 _at;
        private static bool _placed, _logged;

        /// <summary>Pictures a second while the camera is on stream (Red Alert has its sound on), and while it's only ready.</summary>
        private const float OnAirFps = 30f, ReadyFps = 3f;
        private static bool _broken, _readPending;

        public static void Tick()
        {
            if (_broken) return;
            try { Draw(); }
            catch (Exception e)
            {
                _broken = true;
                _placed = false;
                if (_shadeGo != null) _shadeGo.SetActive(false);
                TournamentPlugin.Logger.Error("Player camera turned off for this session (the main video carries on): " + e);
            }
        }

        /// <summary>
        /// Drawn at the start of a frame, before anything moves: the last frame is then finished
        /// (skins, hats and pets have caught up with the body), where drawn after this frame's own
        /// updates some of them were a frame behind and looked broken.
        /// </summary>
        private static void Draw()
        {
            var session = TournamentPlugin.Session;
            var main = Camera.main;
            bool on = session != null && Game.IsHost && main != null && session.Spectator.Cam && session.Cam.Wanted
                      && AmongUsClient.Instance != null && AmongUsClient.Instance.GameState == InnerNetClient.GameStates.Started
                      && MeetingHud.Instance == null && ExileController.Instance == null;
            if (!on) { _placed = false; _readPending = false; if (_shadeGo != null && _shadeGo.activeSelf) _shadeGo.SetActive(false); return; }
            // Last frame's picture, finished by now.
            if (_readPending) { _readPending = false; Read(session!); }

            float now = Time.unscaledTime;
            if (now >= _nextPick)
            {
                _nextPick = now + 0.1f;
                _target = session!.CamPlayer();
            }
            var who = _target.HasValue ? Frame.Get(_target.Value) : null;
            if (who == null) return;

            // Follows smoothly; a jump to another player is a cut.
            var to = who.Pc != null ? (Vector2)who.Pc.transform.position : who.Pos;
            if (!_placed || (to - _at).sqrMagnitude > 36f) _at = to;
            else _at = Vector2.Lerp(_at, to, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 9f));
            _placed = true;

            // Evenly spaced: a picture is due within half a game frame of its time (else a 60 Hz game
            // took one every 2 or 3 frames at random, and the camera looked jerky), and the next time
            // counts on from this one's, not from whenever this frame happened to come.
            if (now + Time.unscaledDeltaTime * 0.5f < _nextShot) return;
            float interval = 1f / Math.Min(session!.Spectator.CamAudio ? OnAirFps : ReadyFps, PlayerCamFeed.Fps);
            _nextShot = Math.Max(_nextShot + interval, now);
            if (!Ready(main!)) return;

            var cam = _cam!;
            cam.transform.position = new Vector3(_at.x, _at.y, main!.transform.position.z);
            MiniChat.Hide(true);
            MiniVitals.Hide(true);
            bool shade = Shade(who, cam);
            try { cam.Render(); }
            finally
            {
                MiniChat.Hide(false);
                MiniVitals.Hide(false);
                if (shade) _shadeGo!.SetActive(false);
            }
            _readPending = true;
        }

        /// <summary>The picture drawn last frame, from the graphics card to The Button's encoder.</summary>
        private static void Read(TournamentSession session)
        {
            if (_rt == null || _tex == null) return;
            int size = PlayerCamFeed.Width * PlayerCamFeed.Height * 4;
            var buffer = session.Cam.Take(size);
            if (buffer == null) return;     // still making the last one: this frame is skipped
            var before = RenderTexture.active;
            RenderTexture.active = _rt;
            _tex.ReadPixels(new Rect(0, 0, PlayerCamFeed.Width, PlayerCamFeed.Height), 0, 0, false);
            RenderTexture.active = before;
            // Straight from the texture's own pixels where the game allows it (no copy made in between).
            IntPtr pixels = Pixels(_tex);
            if (pixels != IntPtr.Zero)
            {
                Marshal.Copy(pixels, buffer, 0, Math.Min(buffer.Length, size));
            }
            else
            {
                var raw = _tex.GetRawTextureData();
                int bytes = Math.Min(buffer.Length, raw.Length);
                // The pixels start after the array's header (4 pointers in IL2CPP).
                Marshal.Copy(IntPtr.Add(raw.Pointer, 4 * IntPtr.Size), buffer, 0, bytes);
            }
            session.Cam.Submit(PlayerCamFeed.Width, PlayerCamFeed.Height, bottomUp: true);
        }

        // ---- The texture's pixels without a copy ----------------------------------------------
        // GetRawTextureData() makes a new 2 MB array in the game's memory for every picture (60 MB a
        // second on stream), and the game's garbage collector then stops the game for each clear-up:
        // a regular stutter while the camera is on. Unity's own GetRawTextureData<T>() reads the
        // texture's pixels in place, through its native Texture2D::GetWritableImageData(frame); the
        // camera asks that directly (looked up once by name; Unity 2022.3's signature: the texture
        // object, the frame number, a pointer back). Anything wrong with it and the camera carries on
        // the old way, logged once: it never turns the camera off.

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr WritableImageData(IntPtr texture, int frame);
        private static WritableImageData? _pixels;
        private static bool _pixelsTried, _pixelsFailed;

        /// <summary>Where the texture's pixels are (just written by ReadPixels), or zero to copy them the old way.</summary>
        private static IntPtr Pixels(Texture2D tex)
        {
            if (_pixelsFailed) return IntPtr.Zero;
            try
            {
                if (!_pixelsTried)
                {
                    _pixelsTried = true;
                    _pixels = Il2CppInterop.Runtime.IL2CPP.ResolveICall<WritableImageData>("UnityEngine.Texture2D::GetWritableImageData");
                }
                IntPtr at = _pixels != null ? _pixels(tex.Pointer, 0) : IntPtr.Zero;
                if (at == IntPtr.Zero) PixelsFailed("no pixels came back");
                return at;
            }
            catch (Exception e)
            {
                PixelsFailed(e.Message);
                return IntPtr.Zero;
            }
        }

        private static void PixelsFailed(string why)
        {
            _pixelsFailed = true;
            _pixels = null;
            TournamentPlugin.Logger.Warn("Player camera: reading the pixels the slower way (" + why + ").");
        }

        // ---- The followed player's sight ------------------------------------------------------
        // Outside what they can see is darker, as on their own screen: walls stop it, with a soft
        // edge. Only switched on while the player camera draws (the host's own view never has it).

        private const int Rays = 96;
        private const float Soft = 0.45f, Far = 30f, Dark = 0.72f;
        /// <summary>Not cast again while the player stands still (to this much) and their sight is unchanged, at most this long (doors).</summary>
        private const float Still = 0.001f, CastAnyway = 0.25f;
        private static GameObject? _shadeGo;
        private static Mesh? _shadeMesh;
        private static Il2CppStructArray<Vector3>? _verts;
        private static int _wallMask = -1;
        // The rays' directions, worked out once with plain .NET maths: Unity's crosses into the game's
        // native code on every call (sine, cosine, max, each new vector), about a thousand times a picture.
        private static readonly float[] RayCos = new float[Rays], RaySin = new float[Rays];
        private static bool _raysReady;
        private static float _castX = float.NaN, _castY, _castR, _castAt;

        private static bool Shade(Frame.Player who, Camera cam)
        {
            try
            {
                if (who.Dead || who.InVent || who.Data == null || ShipStatus.Instance == null) return false;
                if (_shadeGo == null && !MakeShade()) return false;
                float r = ShipStatus.Instance.CalculateLightRadius(who.Data);
                if (r <= 0.05f) return false;
                if (_wallMask < 0) _wallMask = 1 << LayerMask.NameToLayer("Shadow");
                if (!_raysReady)
                {
                    for (int i = 0; i < Rays; i++)
                    {
                        float a = MathF.PI * 2f * i / Rays;
                        RayCos[i] = MathF.Cos(a);
                        RaySin[i] = MathF.Sin(a);
                    }
                    _raysReady = true;
                }
                var origin = who.Pos;
                float now = Time.unscaledTime;
                // Standing still with the same sight (doing a task): last picture's shape still fits.
                bool same = MathF.Abs(origin.x - _castX) < Still && MathF.Abs(origin.y - _castY) < Still && r == _castR && now - _castAt < CastAnyway;
                if (!same)
                {
                    var verts = _verts!;
                    for (int i = 0; i < Rays; i++)
                    {
                        float cx = RayCos[i], cy = RaySin[i];
                        Vector2 dir = default;
                        dir.x = cx;
                        dir.y = cy;
                        var hit = Physics2D.Raycast(origin, dir, r, _wallMask);
                        // No wall: a zero distance (nothing hit).
                        float hd = hit.m_Distance;
                        float d = hd > 0f ? hd : r;
                        float inner = MathF.Max(d - Soft * 0.5f, 0.1f), edge = d + Soft * 0.5f, far = d + Far;
                        Vector3 v = default;
                        v.x = origin.x + cx * inner; v.y = origin.y + cy * inner;
                        verts[i * 3] = v;
                        v.x = origin.x + cx * edge; v.y = origin.y + cy * edge;
                        verts[i * 3 + 1] = v;
                        v.x = origin.x + cx * far; v.y = origin.y + cy * far;
                        verts[i * 3 + 2] = v;
                    }
                    _shadeMesh!.vertices = verts;
                    _shadeMesh.RecalculateBounds();
                    _castX = origin.x;
                    _castY = origin.y;
                    _castR = r;
                    _castAt = now;
                }
                // In front of everything the camera draws, but not so close it's cut off.
                Vector3 at = default;
                at.z = cam.transform.position.z + MathF.Max(cam.nearClipPlane + 0.3f, 1f);
                _shadeGo!.transform.position = at;
                _shadeGo.SetActive(true);
                return true;
            }
            catch (Exception e)
            {
                if (!_shadeFailed) TournamentPlugin.Logger.Warn("Player camera: no sight shading (" + e.Message + ").");
                _shadeFailed = true;
                if (_shadeGo != null) _shadeGo.SetActive(false);
                return false;
            }
        }
        private static bool _shadeFailed;

        private static bool MakeShade()
        {
            if (_shadeFailed) return false;
            _shadeGo = new GameObject("TT Player camera sight");
            Object.DontDestroyOnLoad(_shadeGo);
            var local = PlayerControl.LocalPlayer;
            if (local != null) _shadeGo.layer = local.gameObject.layer;
            _shadeMesh = new Mesh();
            _verts = new Il2CppStructArray<Vector3>(Rays * 3);
            _castX = float.NaN;            // a new mesh: cast for the first picture
            var colours = new Il2CppStructArray<Color>(Rays * 3);
            var tris = new Il2CppStructArray<int>(Rays * 12);
            var clear = new Color(0, 0, 0, 0);
            var dark = new Color(0, 0, 0, Dark);
            for (int i = 0; i < Rays; i++)
            {
                colours[i * 3] = clear;
                colours[i * 3 + 1] = dark;
                colours[i * 3 + 2] = dark;
                int a = i * 3, b = ((i + 1) % Rays) * 3, t = i * 12;
                // The soft edge (inner → edge), then the dark (edge → far).
                tris[t] = a; tris[t + 1] = b; tris[t + 2] = a + 1;
                tris[t + 3] = b; tris[t + 4] = b + 1; tris[t + 5] = a + 1;
                tris[t + 6] = a + 1; tris[t + 7] = b + 1; tris[t + 8] = a + 2;
                tris[t + 9] = b + 1; tris[t + 10] = b + 2; tris[t + 11] = a + 2;
            }
            _shadeMesh.vertices = _verts;
            _shadeMesh.colors = colours;
            _shadeMesh.triangles = tris;
            _shadeGo.AddComponent<MeshFilter>().mesh = _shadeMesh;
            var renderer = _shadeGo.AddComponent<MeshRenderer>();
            renderer.material = new Material(Shader.Find("Sprites/Default"));
            renderer.sortingOrder = 1000;
            _shadeGo.SetActive(false);
            return true;
        }

        /// <summary>The camera, made once: like the host's, but world only (no screen overlays, no ghosts).</summary>
        private static bool Ready(Camera main)
        {
            if (_cam != null && _rt != null && _tex != null) return true;
            try
            {
                _go = new GameObject("TT Player camera");
                Object.DontDestroyOnLoad(_go);
                _cam = _go.AddComponent<Camera>();
                _cam.CopyFrom(main);
                _cam.enabled = false;
                _rt = new RenderTexture(PlayerCamFeed.Width, PlayerCamFeed.Height, 24, RenderTextureFormat.ARGB32);
                _rt.Create();
                _cam.targetTexture = _rt;
                _cam.orthographic = true;
                _cam.orthographicSize = Size;
                _cam.aspect = (float)PlayerCamFeed.Width / PlayerCamFeed.Height;
                int mask = main.cullingMask & ~(1 << 5);   // not the UI layer
                int ghost = LayerMask.NameToLayer("Ghost");
                if (ghost >= 0) mask &= ~(1 << ghost);
                _cam.cullingMask = mask;
                _tex = new Texture2D(PlayerCamFeed.Width, PlayerCamFeed.Height, TextureFormat.RGBA32, false);
                TournamentPlugin.Logger.Info($"Player camera: ready ({PlayerCamFeed.Width}×{PlayerCamFeed.Height}, {PlayerCamFeed.Fps} a second).");
                return true;
            }
            catch (Exception e)
            {
                if (!_logged) TournamentPlugin.Logger.Error("Player camera: couldn't make it: " + e);
                _logged = true;
                Clear();
                return false;
            }
        }

        private static void Clear()
        {
            if (_go != null) Object.Destroy(_go);
            if (_shadeGo != null) Object.Destroy(_shadeGo);
            _shadeGo = null;
            if (_rt != null) { _rt.Release(); Object.Destroy(_rt); }
            if (_tex != null) Object.Destroy(_tex);
            _go = null;
            _cam = null;
            _rt = null;
            _tex = null;
        }
    }
}
