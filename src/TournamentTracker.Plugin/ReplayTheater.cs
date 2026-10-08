using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using TournamentTracker.Stats;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// Watches a replay inside Among Us, on the real map with the real characters. Open
    /// Freeplay on the replay's map and press F8: pick a replay with 1–9. The players are
    /// drawn with their colour, hat, skin, visor and name; bodies appear where the kills were.
    /// Keys: Space play/pause · ←/→ 5 s · ↑/↓ speed · 1–0 follow a player · Tab next player ·
    /// F free camera (WASD) · mouse wheel zoom · R roles · G ghosts · F8 leave.
    /// </summary>
    internal static class ReplayTheater
    {
        private static readonly float[] Speeds = { 0.5f, 1f, 2f, 4f, 8f };
        private static readonly string[] MapNames = { "The Skeld", "MIRA HQ", "Polus", "dlekS ehT", "Airship", "The Fungle" };

        private static bool _menu;
        private static List<FileInfo> _files = new List<FileInfo>();
        private static string _notice = "";
        private static ReplayPlayback? _replay;
        private static double _t;
        private static int _speed = 1;
        private static bool _playing;
        private static int _follow = -1;
        private static bool _free, _roles = true, _ghosts;
        private static Vector3 _cam;
        private static float _zoom = 3f;

        private static readonly List<GameObject> Actors = new List<GameObject>();
        private static readonly List<CosmeticsLayer?> Looks = new List<CosmeticsLayer?>();
        private static readonly Dictionary<int, GameObject> Bodies = new Dictionary<int, GameObject>();
        private static TextMeshPro? _hud;
        private static readonly HashSet<string> Failed = new HashSet<string>();

        public static bool Active => _replay != null || _menu;

        public static void Update()
        {
            var client = AmongUsClient.Instance;
            bool freeplay = client != null && client.NetworkMode.ToString() == "FreePlay" && ShipStatus.Instance != null && PlayerControl.LocalPlayer != null;
            if (!freeplay)
            {
                if (Active) Stop();
                return;
            }
            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (Active) Stop();
                else OpenMenu();
                return;
            }
            if (_menu) MenuUpdate();
            else if (_replay != null) PlayUpdate();
        }

        // ---- Picking a replay ----------------------------------------------------------

        private static void OpenMenu()
        {
            _menu = true;
            _notice = "";
            _files = ReplayLibrary.Find(new[]
            {
                Path.Combine(TournamentPlugin.DataDir, "games"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            });
            ShowMenu();
        }

        private static void ShowMenu()
        {
            var lines = new List<string> { "<b>Replays</b>  (press 1–9 · F8 to close)" };
            if (_files.Count == 0) lines.Add("No replays yet. They're saved with each game, or download one from Discord into Downloads.");
            for (int i = 0; i < _files.Count; i++)
            {
                var age = DateTime.UtcNow - _files[i].LastWriteTimeUtc;
                string when = age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago" : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago" : _files[i].LastWriteTime.ToString("d MMM");
                lines.Add($"{i + 1}. Game {ReplayLibrary.Label(_files[i])} · {when}");
            }
            if (_notice.Length > 0) lines.Add("<color=#ffcc55>" + _notice + "</color>");
            Hud(string.Join("\n", lines));
        }

        private static void MenuUpdate()
        {
            for (int i = 0; i < _files.Count && i < 9; i++)
            {
                if (!Input.GetKeyDown(KeyCode.Alpha1 + i) && !Input.GetKeyDown(KeyCode.Keypad1 + i)) continue;
                try
                {
                    var replay = ReplayPlayback.Load(File.ReadAllBytes(_files[i].FullName));
                    int here = Game.Options()?.MapId ?? -1;
                    if (replay.MapId >= 0 && here >= 0 && replay.MapId != here)
                    {
                        string map = replay.MapId < MapNames.Length ? MapNames[replay.MapId] : replay.Map;
                        _notice = $"Game {replay.Name} was on {map}. Leave Freeplay, open Freeplay on {map}, then press F8.";
                        ShowMenu();
                        return;
                    }
                    Begin(replay);
                }
                catch (Exception e)
                {
                    _notice = "Couldn't open that replay: " + e.Message;
                    ShowMenu();
                }
                return;
            }
        }

        // ---- Playing ---------------------------------------------------------------------

        private static void Begin(ReplayPlayback replay)
        {
            _menu = false;
            _replay = replay;
            _t = 0;
            _playing = true;
            _speed = 1;
            _follow = -1;
            _free = true;
            _zoom = 6f;
            _cam = Camera.main != null ? Camera.main.transform.position : Vector3.zero;

            var local = PlayerControl.LocalPlayer;
            var source = local.cosmetics;
            foreach (var p in replay.Players)
            {
                GameObject actor;
                CosmeticsLayer? look = null;
                try
                {
                    actor = Object.Instantiate(source.gameObject);
                    actor.name = "Replay " + p.Name;
                    actor.SetActive(true);
                    look = actor.GetComponent<CosmeticsLayer>();
                    Dress(look, p);
                }
                catch (Exception e)
                {
                    Warn("actor", e);
                    actor = new GameObject("Replay " + p.Name);
                }
                Actors.Add(actor);
                Looks.Add(look);
            }

            // Hide the Freeplay players and stop the local one walking; the camera is ours now.
            var all = PlayerControl.AllPlayerControls;
            for (int i = 0; i < all.Count; i++)
                Try("hide players", () => all[i].Visible = false);
            Try("freeze", () => local.moveable = false);
            if (HudManager.InstanceExists) Try("camera", () => HudManager.Instance.PlayerCam.enabled = false);
            PlayUpdate();
        }

        private static void Dress(CosmeticsLayer? look, PlaybackPlayer p)
        {
            if (look == null) return;
            Try("colour", () => look.SetColor(p.Color));
            Try("hat", () => look.SetHat(p.Outfit.Hat, p.Color));
            Try("skin", () => look.SetSkin(p.Outfit.Skin, p.Color));
            Try("visor", () => look.SetVisor(p.Outfit.Visor, p.Color));
            Try("visible", () => look.Visible = true);
            Try("name", () => look.nameText.text = p.Name);
        }

        private static void PlayUpdate()
        {
            var replay = _replay!;
            Controls(replay);
            if (_playing)
            {
                _t += Time.deltaTime * Speeds[_speed];
                if (_t >= replay.Duration) { _t = replay.Duration; _playing = false; }
            }

            var states = replay.At(_t);
            for (int i = 0; i < states.Length && i < Actors.Count; i++)
            {
                var s = states[i];
                var actor = Actors[i];
                bool show = !s.Gone && !s.InVent && (!s.Dead || _ghosts);
                if (actor.activeSelf != show) actor.SetActive(show);
                if (!show) continue;
                actor.transform.position = new Vector3(s.X, s.Y, s.Y / 1000f);
                var look = Looks[i];
                if (look == null) continue;
                var p = replay.Players[i];
                Try("flip", () => look.SetFlipX(s.FacingLeft));
                Try("name colour", () =>
                {
                    look.nameText.text = p.Name + (s.Dead ? " (ghost)" : "");
                    look.nameText.color = _roles && p.Impostor ? new Color(1f, 0.3f, 0.25f) : Color.white;
                });
            }
            UpdateBodies(replay);
            UpdateCamera(states);

            string following = _follow >= 0 && _follow < replay.Players.Count ? " · following " + replay.Players[_follow].Name : _free ? " · free camera" : "";
            var events = replay.EventsUpTo(_t, 3).Select(e => $"{Clock(e.T)}  {e.Text}");
            Hud($"<b>Game {replay.Name}</b>{(replay.Round > 0 ? $" · round {replay.Round}" : "")} · {replay.Map}{(replay.InMeeting(_t) ? " · <color=#ffcc55>MEETING</color>" : "")}\n" +
                $"{Clock(_t)} / {Clock(replay.Duration)} · {(_playing ? "playing" : "paused")} {Speeds[_speed]}×{following}\n" +
                string.Join("\n", events) +
                "\n<size=70%>Space play · ←/→ 5s · ↑/↓ speed · 1–0 follow · Tab next · F free cam (WASD) · wheel zoom · R roles · G ghosts · F8 leave</size>");
        }

        private static void Controls(ReplayPlayback replay)
        {
            if (Input.GetKeyDown(KeyCode.Space)) { if (_t >= replay.Duration) _t = 0; _playing = !_playing; }
            if (Input.GetKeyDown(KeyCode.LeftArrow)) _t = Math.Max(0, _t - 5);
            if (Input.GetKeyDown(KeyCode.RightArrow)) _t = Math.Min(replay.Duration, _t + 5);
            if (Input.GetKeyDown(KeyCode.UpArrow)) _speed = Math.Min(Speeds.Length - 1, _speed + 1);
            if (Input.GetKeyDown(KeyCode.DownArrow)) _speed = Math.Max(0, _speed - 1);
            if (Input.GetKeyDown(KeyCode.R)) _roles = !_roles;
            if (Input.GetKeyDown(KeyCode.G)) _ghosts = !_ghosts;
            if (Input.GetKeyDown(KeyCode.F)) { _free = true; _follow = -1; }
            if (Input.GetKeyDown(KeyCode.Tab)) { _free = false; _follow = (_follow + 1) % Math.Max(1, replay.Players.Count); }
            for (int i = 0; i < 10 && i < replay.Players.Count; i++)
            {
                var key = i == 9 ? KeyCode.Alpha0 : KeyCode.Alpha1 + i;
                if (Input.GetKeyDown(key)) { _follow = i; _free = false; }
            }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0) _zoom = Mathf.Clamp(_zoom - wheel, 2f, 16f);
            if (_free)
            {
                float speed = _zoom * 1.5f * Time.deltaTime;
                if (Input.GetKey(KeyCode.W)) _cam.y += speed;
                if (Input.GetKey(KeyCode.S)) _cam.y -= speed;
                if (Input.GetKey(KeyCode.A)) _cam.x -= speed;
                if (Input.GetKey(KeyCode.D)) _cam.x += speed;
            }
        }

        private static void UpdateCamera(PlaybackState[] states)
        {
            var camera = Camera.main;
            if (camera == null) return;
            if (!_free && _follow >= 0 && _follow < states.Length)
            {
                var target = new Vector3(states[_follow].X, states[_follow].Y, _cam.z);
                _cam = Vector3.Lerp(_cam, target, Math.Min(1f, Time.deltaTime * 8f));
            }
            camera.transform.position = new Vector3(_cam.x, _cam.y, camera.transform.position.z);
            camera.orthographicSize = _zoom;
        }

        private static void UpdateBodies(ReplayPlayback replay)
        {
            for (int b = 0; b < replay.Bodies.Count; b++)
            {
                var body = replay.Bodies[b];
                bool show = _t >= body.From && _t < body.Until;
                if (!Bodies.TryGetValue(b, out var go))
                {
                    if (!show) continue;
                    go = MakeBody(replay.Players[body.Player].Color);
                    if (go == null) continue;
                    go.transform.position = new Vector3(body.X, body.Y, body.Y / 1000f + 0.0005f);
                    Bodies[b] = go;
                }
                if (go.activeSelf != show) go.SetActive(show);
            }
        }

        private static GameObject? MakeBody(int color)
        {
            try
            {
                var prefab = BodyPrefab();
                if (prefab == null) return null;
                var body = Object.Instantiate(prefab);
                body.enabled = false;   // a replay body can't be reported
                foreach (var renderer in body.bodyRenderers) PlayerMaterial.SetColors(color, renderer);
                return body.gameObject;
            }
            catch (Exception e)
            {
                Warn("body", e);
                return null;
            }
        }

        private static DeadBody? _bodyPrefab;

        /// <summary>
        /// The game's dead body model. Game versions keep it in different places
        /// (DeadBodyPrefab, an array of them, or GetDeadBody(role)), so it's looked up by name.
        /// </summary>
        private static DeadBody? BodyPrefab()
        {
            if (_bodyPrefab != null) return _bodyPrefab;
            var manager = GameManager.Instance;
            if (manager == null) return null;
            var type = manager.GetType();
            foreach (var name in new[] { "DeadBodyPrefab", "deadBodyPrefab", "DeadBodyPrefabs", "deadBodyPrefabs" })
            {
                object? value = type.GetProperty(name)?.GetValue(manager) ?? type.GetField(name)?.GetValue(manager);
                if (value is DeadBody single) return _bodyPrefab = single;
                if (value is System.Collections.IEnumerable list)
                    foreach (var item in list)
                        if (item is DeadBody first) return _bodyPrefab = first;
            }
            var getter = type.GetMethods().FirstOrDefault(m => m.Name == "GetDeadBody" && m.GetParameters().Length == 1);
            var role = PlayerControl.LocalPlayer?.Data?.Role;
            if (getter != null && role != null && getter.Invoke(manager, new object[] { role }) is DeadBody fromRole) return _bodyPrefab = fromRole;
            return null;
        }

        public static void Stop()
        {
            _menu = false;
            _replay = null;
            foreach (var a in Actors) if (a != null) Object.Destroy(a);
            foreach (var b in Bodies.Values) if (b != null) Object.Destroy(b);
            Actors.Clear();
            Looks.Clear();
            Bodies.Clear();
            if (_hud != null) Object.Destroy(_hud.gameObject);
            _hud = null;

            var local = PlayerControl.LocalPlayer;
            var all = PlayerControl.AllPlayerControls;
            for (int i = 0; i < all.Count; i++)
                Try("show players", () => all[i].Visible = true);
            if (local != null) Try("unfreeze", () => local.moveable = true);
            if (HudManager.InstanceExists) Try("camera back", () => HudManager.Instance.PlayerCam.enabled = true);
            if (Camera.main != null) Camera.main.orthographicSize = 3f;
        }

        // ---- On-screen text --------------------------------------------------------------

        private static void Hud(string text)
        {
            var camera = Camera.main;
            if (camera == null) return;
            if (_hud == null)
            {
                try
                {
                    var go = new GameObject("ReplayHud");
                    go.transform.SetParent(camera.transform, false);
                    _hud = go.AddComponent<TextMeshPro>();
                    var font = Object.FindObjectOfType<TextMeshPro>();
                    if (font != null && font != _hud) _hud.font = font.font;
                    _hud.alignment = TextAlignmentOptions.TopLeft;
                    _hud.enableWordWrapping = false;
                    _hud.color = Color.white;
                    _hud.outlineWidth = 0.2f;
                    _hud.outlineColor = new Color32(0, 0, 0, 255);
                }
                catch (Exception e)
                {
                    Warn("text", e);
                    return;
                }
            }
            // Stay in the top-left corner at the same size whatever the zoom.
            float size = camera.orthographicSize;
            float scale = size / 3f;
            _hud.transform.localScale = new Vector3(scale, scale, 1);
            _hud.fontSize = 2f;
            _hud.rectTransform.sizeDelta = new Vector2(12f, 4f);
            _hud.rectTransform.pivot = new Vector2(0, 1);
            _hud.transform.localPosition = new Vector3(-size * camera.aspect + 0.25f * scale, size - 0.2f * scale, 5f);
            _hud.text = text;
        }

        private static string Clock(double seconds) => $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";

        private static void Try(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Warn(what, e); }
        }

        private static void Warn(string what, Exception e)
        {
            if (Failed.Add(what)) TournamentPlugin.Logger.Warn($"Replay theatre: {what} failed ({e.Message})");
        }
    }
}
