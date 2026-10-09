using System;
using System.Runtime.InteropServices;
using InnerNet;
using TournamentTracker.PlayerCam;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// The player camera, on the host's game: a second camera, never shown on the host's screen,
    /// that follows one player up close (the caster's pick, or one it picks itself) and is read
    /// back 30 times a second for The Button to send to the caster as its own stream. Only drawn
    /// while Red Alert has it on and The Button's send page is asking for pictures.
    /// </summary>
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

        public static void LateUpdate()
        {
            var session = TournamentPlugin.Session;
            var main = Camera.main;
            bool on = session != null && Game.IsHost && main != null && session.Spectator.Cam && session.Cam.Wanted
                      && AmongUsClient.Instance != null && AmongUsClient.Instance.GameState == InnerNetClient.GameStates.Started;
            if (!on) { _placed = false; Pending.Clear(); return; }

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

            Collect(session!);
            if (now < _nextShot) return;
            _nextShot = now + 1f / PlayerCamFeed.Fps;
            bool async = UseAsync();
            if (async && Pending.Count >= 3) return;     // the graphics card is behind: this frame is skipped
            if (!Ready(main!)) return;

            var cam = _cam!;
            cam.transform.position = new Vector3(_at.x, _at.y, main!.transform.position.z);
            MiniChat.Hide(true);
            try { cam.Render(); }
            finally { MiniChat.Hide(false); }

            if (async)
            {
                // Read back a few frames later, without waiting for the graphics card (waiting
                // for it here cost 17–35 ms a picture and made the game stutter).
                try { Pending.Enqueue(AsyncGPUReadback.Request(_rt, 0, TextureFormat.RGBA32)); return; }
                catch (Exception e) { NoAsync(e); }
            }
            var buffer = session!.Cam.Take(PlayerCamFeed.Width * PlayerCamFeed.Height * 4);
            if (buffer == null) return;     // still making the last one: this frame is skipped
            var before = RenderTexture.active;
            RenderTexture.active = _rt;
            _tex!.ReadPixels(new Rect(0, 0, PlayerCamFeed.Width, PlayerCamFeed.Height), 0, 0, false);
            RenderTexture.active = before;
            var raw = _tex.GetRawTextureData();
            int bytes = Math.Min(buffer.Length, raw.Length);
            // The pixels start after the array's header (4 pointers in IL2CPP).
            Marshal.Copy(IntPtr.Add(raw.Pointer, 4 * IntPtr.Size), buffer, 0, bytes);
            session.Cam.Submit(PlayerCamFeed.Width, PlayerCamFeed.Height, bottomUp: true);
        }

        private static readonly System.Collections.Generic.Queue<AsyncGPUReadbackRequest> Pending = new System.Collections.Generic.Queue<AsyncGPUReadbackRequest>();
        private static bool? _async;

        private static bool UseAsync()
        {
            if (_async == null)
            {
                try { _async = SystemInfo.supportsAsyncGPUReadback; }
                catch (Exception e) { NoAsync(e); }
            }
            return _async == true;
        }

        private static void NoAsync(Exception e)
        {
            if (_async != false) TournamentPlugin.Logger.Warn("Player camera: reading pictures back the slow way (" + e.Message + ").");
            _async = false;
            Pending.Clear();
        }

        /// <summary>Pictures the graphics card has finished reading back go to The Button.</summary>
        private static void Collect(TournamentSession session)
        {
            while (Pending.Count > 0)
            {
                var request = Pending.Peek();
                try
                {
                    if (!request.done) return;
                    Pending.Dequeue();
                    if (request.hasError) continue;
                    var buffer = session.Cam.Take(PlayerCamFeed.Width * PlayerCamFeed.Height * 4);
                    if (buffer == null) continue;     // still making the last one: this picture is skipped
                    var data = request.GetDataRaw(0);
                    if (data == IntPtr.Zero) continue;
                    Marshal.Copy(data, buffer, 0, buffer.Length);
                    session.Cam.Submit(PlayerCamFeed.Width, PlayerCamFeed.Height, bottomUp: true);
                }
                catch (Exception e) { NoAsync(e); return; }
            }
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
            if (_rt != null) { _rt.Release(); Object.Destroy(_rt); }
            if (_tex != null) Object.Destroy(_tex);
            _go = null;
            _cam = null;
            _rt = null;
            _tex = null;
        }
    }
}
