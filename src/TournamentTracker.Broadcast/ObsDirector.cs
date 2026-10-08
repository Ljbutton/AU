using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// How The Button talks to OBS and what it builds there, in obs.json next to The Button's
    /// settings. <see cref="Sources"/> is the lobby → OBS source mapping, kept up to date by The Button.
    /// The password is stored on this PC only.
    /// </summary>
    public sealed class ObsSettings
    {
        public const string FileName = "obs.json";

        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 4455;
        public string Password { get; set; } = "";
        /// <summary>Connect when The Button starts (after the first successful connect).</summary>
        public bool AutoConnect { get; set; }
        /// <summary>The scenes The Button builds and switches between: full screen, 2-up, quad.</summary>
        public Dictionary<string, string> Scenes { get; set; } = new Dictionary<string, string> { ["full"] = "TT Full", ["2up"] = "TT 2-up", ["4up"] = "TT Quad", ["grid"] = "TT Grid", ["break"] = "TT Sponsor Break", ["intermission"] = "TT Intermission", ["slate"] = "TT Be Right Back", ["cam"] = "TT Player Cam" };
        /// <summary>Each lobby's VDO.Ninja source is called this plus the lobby name.</summary>
        public string SourcePrefix { get; set; } = "TT Lobby ";
        /// <summary>Each lobby's player camera source (only in the TT Player Cam scene) is called this plus the lobby name.</summary>
        public string CamPrefix { get; set; } = "TT Cam ";
        /// <summary>Lobby → its player camera source in OBS, as built.</summary>
        public Dictionary<string, string> CamSources { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// The bitrate OBS asks each host's VDO.Ninja video for (kbps; 0 = VDO.Ninja's own, about 2500).
        /// Higher keeps the picture sharp when a replay zooms in. Only OBS's own copy asks for it, not
        /// the small previews, so the host's upload isn't spent on those.
        /// </summary>
        public int VideoKbps { get; set; } = 8000;
        /// <summary>Space between the pictures in 2-up and quad, in canvas pixels.</summary>
        public int Gap { get; set; } = 8;
        /// <summary>Whose game sound plays: "slot1" (the full-screen lobby, or slot 1) or "none".</summary>
        public string Audio { get; set; } = "slot1";
        /// <summary>Replays: clip lengths, zoom, where clips are saved, and the replay keys.</summary>
        public ReplaySettings Replay { get; set; } = new ReplaySettings();
        /// <summary>The swoosh on every switch (Part 17).</summary>
        public SwooshSettings Swoosh { get; set; } = new SwooshSettings();
        /// <summary>The lobbies' voice (Part 11).</summary>
        public VoiceSettings Voice { get; set; } = new VoiceSettings();
        /// <summary>Lobby → OBS source, as built. The Button keeps this up to date.</summary>
        public Dictionary<string, string> Sources { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Red Alert's own sources that had to move to a fresh name ("TT Broadcast" → "TT Broadcast 2") because OBS kept a removed one.</summary>
        public Dictionary<string, string> Renamed { get; set; } = new Dictionary<string, string>();

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        };

        public static ObsSettings Load(string? path)
        {
            try
            {
                if (path != null && File.Exists(path))
                {
                    var s = JsonSerializer.Deserialize<ObsSettings>(File.ReadAllText(path), Json) ?? new ObsSettings();
                    s.Sources = new Dictionary<string, string>(s.Sources ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                    s.CamSources = new Dictionary<string, string>(s.CamSources ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                    s.Renamed ??= new Dictionary<string, string>();
                    s.Replay ??= new ReplaySettings();
                    s.Swoosh ??= new SwooshSettings();
                    s.Voice ??= new VoiceSettings();
                    s.Voice.Volume = new Dictionary<string, double>(s.Voice.Volume ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);
                    s.Voice.Offset = new Dictionary<string, int>(s.Voice.Offset ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in ReplaySettings.DefaultHotkeys()) if (!s.Replay.Hotkeys.ContainsKey(kv.Key)) s.Replay.Hotkeys[kv.Key] = kv.Value;
                    var d = new ObsSettings();
                    foreach (var kv in d.Scenes) if (!s.Scenes.ContainsKey(kv.Key)) s.Scenes[kv.Key] = kv.Value;
                    return s;
                }
            }
            catch (Exception) { }
            return new ObsSettings();
        }

        public void Save(string? path)
        {
            if (path == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
    }

    /// <summary>A rectangle on the OBS canvas.</summary>
    public readonly struct Box
    {
        public readonly double X, Y, W, H;
        public Box(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
        public double Cx => X + W / 2;
        public double Cy => Y + H / 2;
    }

    /// <summary>
    /// Builds the TT scenes in OBS (one VDO.Ninja source per lobby, in every TT scene), puts
    /// lobbies on stream by showing and placing their sources and switching scene, plays only
    /// slot 1's game sound, and reads back what's on when you switch in OBS yourself. Only
    /// touches scenes and sources it made (named TT …).
    /// </summary>
    public sealed partial class ObsDirector : IAsyncDisposable
    {
        /// <summary>A host's VDO.Ninja link as OBS opens it: asking for <see cref="ObsSettings.VideoKbps"/>.</summary>
        internal string WithBitrate(string url) =>
            Settings.VideoKbps > 0 && url.Contains("vdo.ninja", StringComparison.OrdinalIgnoreCase) && !url.Contains("videobitrate=", StringComparison.OrdinalIgnoreCase)
                ? url + "&videobitrate=" + Settings.VideoKbps : url;

        private readonly string? _path;
        private readonly CasterDesk _desk;
        private readonly Func<IReadOnlyList<(string Lobby, string Url)>> _feeds;
        private readonly SemaphoreSlim _busy = new SemaphoreSlim(1, 1);
        private readonly Func<string, int, string?, TimeSpan, Task<ObsClient>> _connect;
        private ObsClient? _obs;
        private Timer? _timer;
        // A switch Red Alert asked OBS for, until OBS says the scene changed (CurrentProgramSceneChanged).
        private sealed class Expect { public string Scene = ""; public OnAir? Air; public DateTime Asked; }
        private Expect? _expect;
        /// <summary>How long OBS gets to make a switch before the Live desk says it didn't.</summary>
        public static TimeSpan SwitchTimeout { get; set; } = TimeSpan.FromSeconds(2.5);
        /// <summary>The last switch OBS didn't make (shown on the Live desk), or null.</summary>
        public string? SwitchProblem { get; private set; }
        /// <summary>The scene Red Alert is waiting for OBS to switch to, or null.</summary>
        public string? SwitchingTo => _expect?.Scene;
        private DateTime _nextReconnect;
        private bool _wantConnected;

        public ObsSettings Settings { get; private set; }
        /// <summary>Each lobby's player camera link (only lobbies whose host sends one).</summary>
        public Func<IReadOnlyList<(string Lobby, string Url)>>? CamFeeds { get; set; }
        public double Width { get; private set; } = 1920;
        public double Height { get; private set; } = 1080;
        public string? Problem { get; private set; }
        public bool Connected => _obs?.Connected == true;
        public string? ObsVersion => _obs?.ObsVersion;
        public string? Scene { get; private set; }

        /// <param name="feeds">Each lobby's video link (with sound), as OBS should load it.</param>
        public ObsDirector(string? settingsPath, CasterDesk desk, Func<IReadOnlyList<(string Lobby, string Url)>> feeds,
            Func<string, int, string?, TimeSpan, Task<ObsClient>>? connect = null)
        {
            _path = settingsPath;
            _desk = desk;
            _feeds = feeds;
            _connect = connect ?? ObsClient.ConnectAsync;
            Settings = ObsSettings.Load(settingsPath);
            _wantConnected = Settings.AutoConnect;
        }

        /// <summary>Starts the background upkeep: reconnecting, and adding sources for new lobbies.</summary>
        public void Start() => _timer ??= new Timer(_ => _ = Task.Run(TickAsync), null, 500, 3000);

        // ---- Connecting -------------------------------------------------------------------------

        public async Task<string> ConnectAsync(string? host = null, int? port = null, string? password = null)
        {
            if (host != null) Settings.Host = host.Trim().Length > 0 ? host.Trim() : "127.0.0.1";
            if (port.HasValue && port.Value > 0) Settings.Port = port.Value;
            if (password != null) Settings.Password = password;
            _wantConnected = true;
            await DropAsync().ConfigureAwait(false);
            try
            {
                var obs = await _connect(Settings.Host, Settings.Port, Settings.Password.Length > 0 ? Settings.Password : null, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                obs.Event += OnEvent;
                obs.Closed += why => { Problem = "Lost OBS: " + why + " Reconnecting…"; };
                _obs = obs;
                Problem = null;
                Settings.AutoConnect = true;
                Save();
                var video = await obs.RequestAsync("GetVideoSettings").ConfigureAwait(false);
                Width = video.GetProperty("baseWidth").GetDouble();
                Height = video.GetProperty("baseHeight").GetDouble();
                // Fake lobbies left from a simulation (or an older version's stand-ins) go first.
                if (!_desk.Simulating) await ForgetSimAsync(CasterDesk.IsSimLobby).ConfigureAwait(false);
                await BuildAsync().ConfigureAwait(false);
                await EnsureReplaySceneAsync().ConfigureAwait(false);
                await EnsureBroadcastAsync().ConfigureAwait(false);
                await EnsureSwooshAsync().ConfigureAwait(false);
                _voiceInputs.Clear(); _voiceSet.Clear(); _duckWas = "\u0000";
                await EnsureVoiceAsync().ConfigureAwait(false);
                await ReadBackAsync(force: true).ConfigureAwait(false);
                try { await CheckSetupAsync().ConfigureAwait(false); } catch (Exception) { }
                return $"Connected to OBS{(ObsVersion != null ? " " + ObsVersion : "")}. The TT scenes are ready.";
            }
            catch (Exception e)
            {
                Problem = e.Message;
                return e.Message;
            }
        }

        public async Task DisconnectAsync()
        {
            _wantConnected = false;
            Settings.AutoConnect = false;
            Save();
            await DropAsync().ConfigureAwait(false);
            Problem = null;
        }

        private async Task DropAsync()
        {
            var old = _obs;
            _obs = null;
            if (old != null) await old.DisposeAsync().ConfigureAwait(false);
        }

        public void SaveSettings() => Save();

        private void Save()
        {
            try { Settings.Save(_path); } catch (Exception) { }
        }

        private async Task TickAsync()
        {
            try
            {
                if (_wantConnected && !Connected && DateTime.UtcNow >= _nextReconnect)
                {
                    _nextReconnect = DateTime.UtcNow.AddSeconds(10);
                    await ConnectAsync().ConfigureAwait(false);
                    return;
                }
                if (Connected)
                {
                    await AddSourcesAsync().ConfigureAwait(false);
                    await EnsureVoiceAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e) { Problem = e.Message; }
        }

        // ---- Building the TT scenes --------------------------------------------------------------

        /// <summary>The slots of a layout on the canvas: full screen, two side by side, or a 2×2 grid.</summary>
        /// <summary>The grid for a number of lobbies: 1 full, 2 side by side, 3–4 2×2, 5–6 3×2, 7–9 3×3, 10–12 4×3, 13–16 4×4.</summary>
        public static (int Cols, int Rows) GridShape(int count) => count switch
        {
            <= 1 => (1, 1),
            2 => (2, 1),
            <= 4 => (2, 2),
            <= 6 => (3, 2),
            <= 9 => (3, 3),
            <= 12 => (4, 3),
            <= 16 => (4, 4),
            _ => (5, (count + 4) / 5),
        };

        public static List<Box> Slots(string layout, double w, double h, double gap, int count = 0)
        {
            switch (layout)
            {
                case "grid":
                {
                    var (cols, rows) = GridShape(count);
                    double bw = (w - (cols + 1) * gap) / cols, bh = (h - (rows + 1) * gap) / rows;
                    var boxes = new List<Box>();
                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < cols; c++)
                            boxes.Add(new Box(gap + c * (bw + gap), gap + r * (bh + gap), bw, bh));
                    return boxes;
                }
                case "cam":
                {
                    // The player camera fills the screen (its own source); the lobby's whole map sits small in the bottom right.
                    double bw = Math.Round(w * 0.27), bh = Math.Round(bw * 9 / 16), m = Math.Round(w * 0.015);
                    return new List<Box> { new Box(w - bw - m, h - bh - m, bw, bh) };
                }
                case "break":
                {
                    // The lobby on the left two thirds; the sponsor's panel (drawn by the graphics app) on the right.
                    double bw = Math.Round(w * 0.66), bh = bw * 9 / 16;
                    return new List<Box> { new Box(Math.Round(w * 0.025), (h - bh) / 2, bw, bh) };
                }
                case "2up":
                {
                    double bw = (w - 3 * gap) / 2, bh = bw * 9 / 16;
                    double y = (h - bh) / 2;
                    return new List<Box> { new Box(gap, y, bw, bh), new Box(2 * gap + bw, y, bw, bh) };
                }
                case "4up":
                {
                    double bw = (w - 3 * gap) / 2, bh = (h - 3 * gap) / 2;
                    return new List<Box>
                    {
                        new Box(gap, gap, bw, bh), new Box(2 * gap + bw, gap, bw, bh),
                        new Box(gap, 2 * gap + bh, bw, bh), new Box(2 * gap + bw, 2 * gap + bh, bw, bh),
                    };
                }
                default:
                    return new List<Box> { new Box(0, 0, w, h) };
            }
        }

        /// <summary>Makes the three TT scenes (if missing) and a source for every lobby sending video.</summary>
        public async Task BuildAsync()
        {
            var obs = _obs ?? throw new InvalidOperationException("Not connected to OBS.");
            await _busy.WaitAsync().ConfigureAwait(false);
            try
            {
                var list = await obs.RequestAsync("GetSceneList").ConfigureAwait(false);
                var have = list.GetProperty("scenes").EnumerateArray().Select(s => s.GetProperty("sceneName").GetString()).ToHashSet();
                foreach (var scene in Settings.Scenes.Values.Where(s => !have.Contains(s)))
                    await obs.RequestAsync("CreateScene", new { sceneName = scene }).ConfigureAwait(false);
            }
            finally { _busy.Release(); }
            await AddSourcesAsync(now: true).ConfigureAwait(false);
        }

        /// <summary>A VDO.Ninja source for each lobby that has video, in every TT scene (hidden until it's on).</summary>
        /// <param name="now">Try lobbies that failed lately too (Rebuild), not only when their wait is over.</param>
        private async Task AddSourcesAsync(bool now = false)
        {
            var obs = _obs;
            if (obs == null) return;
            bool newSources = false;
            await _busy.WaitAsync().ConfigureAwait(false);
            try
            {
                var inputs = (await obs.RequestAsync("GetInputList").ConfigureAwait(false)).GetProperty("inputs").EnumerateArray()
                    .Select(i => i.GetProperty("inputName").GetString() ?? "").ToHashSet();
                bool changed = false;
                foreach (var (lobby, feedUrl) in _feeds())
                {
                    string url = WithBitrate(feedUrl);
                    // Simulation's lobbies only while it runs (a late tick mustn't build them again after it stopped).
                    if (!_desk.Simulating && CasterDesk.IsSimLobby(lobby)) continue;
                    if (!now && _sourceProblems.TryGetValue(lobby, out var failed) && DateTime.UtcNow < failed.RetryAt) continue;
                    try
                    {
                        string root = Settings.SourcePrefix + lobby;
                        string current = Settings.Sources.TryGetValue(lobby, out var m) && m.Length > 0 ? m : root;
                        // Stays connected while hidden, so switching is instant. "Control audio via OBS" on.
                        var (name, _, added) = await EnsureInputAsync(obs, root, current, Settings.Scenes.Values, inputs, "browser_source",
                            new { url, width = 1920, height = 1080, reroute_audio = true, shutdown = false, restart_when_active = false, fps_custom = false },
                            enabled: false,
                            existing: async n =>
                            {
                                var cur = await obs.RequestAsync("GetInputSettings", new { inputName = n }).ConfigureAwait(false);
                                string? was = cur.TryGetProperty("inputSettings", out var st) && st.TryGetProperty("url", out var u) ? u.GetString() : null;
                                if (was != url) await obs.RequestAsync("SetInputSettings", new { inputName = n, inputSettings = new { url } }).ConfigureAwait(false);
                            }).ConfigureAwait(false);
                        if (added) newSources = true;
                        if (!Settings.Sources.TryGetValue(lobby, out var mapped) || mapped != name) { Settings.Sources[lobby] = name; changed = true; newSources = true; }
                        _sourceProblems.TryRemove(lobby, out _);
                        await EnsureReplayFilterAsync(obs, lobby, name).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        // One lobby's trouble doesn't hold up the others; it's tried again later (or by Rebuild).
                        _sourceProblems[lobby] = (e.Message, DateTime.UtcNow + SourceRetry);
                    }
                }
                if (await AddCamSourcesAsync(obs, inputs).ConfigureAwait(false)) changed = newSources = true;
                if (changed) Save();
            }
            finally { _busy.Release(); }
            // New lobby pictures go on top: put the graphics back above them.
            if (newSources)
            {
                await EnsureBroadcastAsync().ConfigureAwait(false);
                await EnsureSwooshAsync().ConfigureAwait(false);
            }
        }

        private sealed class Item
        {
            public int Id;
            public string Source = "";
            public bool Enabled;
            public double X, Y, W, H;
        }

        private static async Task<List<Item>> ItemsAsync(ObsClient obs, string scene)
        {
            var r = await obs.RequestAsync("GetSceneItemList", new { sceneName = scene }).ConfigureAwait(false);
            return r.GetProperty("sceneItems").EnumerateArray().Select(i =>
            {
                var t = i.TryGetProperty("sceneItemTransform", out var tr) ? tr : default;
                double Num(string p) => t.ValueKind == JsonValueKind.Object && t.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                double w = Num("boundsWidth") > 0 ? Num("boundsWidth") : Num("width");
                double h = Num("boundsHeight") > 0 ? Num("boundsHeight") : Num("height");
                return new Item
                {
                    Id = i.GetProperty("sceneItemId").GetInt32(),
                    Source = i.GetProperty("sourceName").GetString() ?? "",
                    Enabled = i.GetProperty("sceneItemEnabled").GetBoolean(),
                    X = Num("positionX"), Y = Num("positionY"), W = w, H = h,
                };
            }).ToList();
        }

        // ---- Putting lobbies on stream ----------------------------------------------------------

        /// <summary>Shows the on-air lobbies in their slots in the layout's scene, hides the rest, switches to it and sets the sound.</summary>
        public async Task ApplyAsync(OnAir air)
        {
            var obs = _obs;
            if (obs == null || !Settings.Scenes.TryGetValue(air.Layout, out var scene)) return;
            // From here until OBS confirms, its item events are ours (arranging the scene), not a switch by hand.
            var ex = new Expect { Scene = scene, Air = air, Asked = DateTime.UtcNow };
            _expect = ex;
            await AddSourcesAsync().ConfigureAwait(false);
            // A swoosh when what's on stream changes (a new scene, or pictures moving in this one).
            string key = AirKey(air);
            // No swoosh to, from or within the player camera: game to player to player is a straight cut.
            bool camCut = air.Layout == "cam" || Scene == Settings.Scenes.GetValueOrDefault("cam");
            if (!camCut && (key != _airKey || Scene != scene)) await SwooshAsync(Scene != scene).ConfigureAwait(false);
            _airKey = key;
            await _busy.WaitAsync().ConfigureAwait(false);
            try
            {
                var boxes = Slots(air.Layout, Width, Height, air.Layout == "full" ? 0 : Settings.Gap, air.Slots.Count);
                var items = await ItemsAsync(obs, scene).ConfigureAwait(false);
                foreach (var item in items)
                {
                    // The player camera scene: only the on-air lobby's camera shows, full screen, under everything.
                    if (CamLobbyOf(item.Source) is string camLobby)
                    {
                        bool showCam = air.Layout == "cam" && string.Equals(camLobby, air.Slots.FirstOrDefault(), StringComparison.OrdinalIgnoreCase);
                        if (showCam)
                            await obs.RequestAsync("SetSceneItemTransform", new
                            {
                                sceneName = scene,
                                sceneItemId = item.Id,
                                sceneItemTransform = new
                                {
                                    positionX = 0.0, positionY = 0.0, alignment = 5, rotation = 0.0,
                                    boundsType = "OBS_BOUNDS_SCALE_INNER", boundsAlignment = 0, boundsWidth = Width, boundsHeight = Height,
                                    cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0,
                                },
                            }).ConfigureAwait(false);
                        if (item.Enabled != showCam)
                            await obs.RequestAsync("SetSceneItemEnabled", new { sceneName = scene, sceneItemId = item.Id, sceneItemEnabled = showCam }).ConfigureAwait(false);
                        continue;
                    }
                    string? lobby = LobbyOf(item.Source);
                    if (lobby == null) continue;                     // not ours: leave it alone
                    int slot = air.Slots.FindIndex(s => string.Equals(s, lobby, StringComparison.OrdinalIgnoreCase));
                    if (slot >= 0 && slot < boxes.Count)
                    {
                        var b = boxes[slot];
                        await obs.RequestAsync("SetSceneItemTransform", new
                        {
                            sceneName = scene,
                            sceneItemId = item.Id,
                            sceneItemTransform = new
                            {
                                positionX = b.X, positionY = b.Y, alignment = 5, rotation = 0.0,
                                boundsType = "OBS_BOUNDS_SCALE_INNER", boundsAlignment = 0, boundsWidth = b.W, boundsHeight = b.H,
                                cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0,
                            },
                        }).ConfigureAwait(false);
                    }
                    bool show = slot >= 0 && slot < boxes.Count;
                    if (item.Enabled != show)
                        await obs.RequestAsync("SetSceneItemEnabled", new { sceneName = scene, sceneItemId = item.Id, sceneItemEnabled = show }).ConfigureAwait(false);
                }
                // Only one lobby's game sound at a time: the full-screen one, or slot 1.
                string? loud = Settings.Audio == "slot1" ? air.Slots.FirstOrDefault(s => s != null) : null;
                foreach (var (lobby, source) in Settings.Sources.ToList())
                {
                    bool muted = !string.Equals(lobby, loud, StringComparison.OrdinalIgnoreCase);
                    try { await obs.RequestAsync("SetInputMute", new { inputName = source, inputMuted = muted }).ConfigureAwait(false); }
                    catch (ObsException) { /* the source was removed in OBS */ }
                }
                ex.Asked = DateTime.UtcNow;
                await obs.RequestAsync("SetCurrentProgramScene", new { sceneName = scene }).ConfigureAwait(false);
                Problem = null;
                // Already on that scene (the pictures moved within it): OBS sends no scene change, so check.
                var cur = await obs.RequestAsync("GetCurrentProgramScene").ConfigureAwait(false);
                if (ProgramName(cur) == scene) Confirm(ex);
                else _ = Task.Delay(SwitchTimeout).ContinueWith(_ => TimedOut(ex));
            }
            catch (Exception e)
            {
                Problem = "OBS: " + e.Message;
                if (_expect == ex) { _expect = null; SwitchProblem = $"OBS didn't switch to {scene}: {e.Message}"; }
                _ = Task.Run(async () => { try { await ReadBackAsync().ConfigureAwait(false); } catch (Exception) { } });
            }
            finally { _busy.Release(); }
            // The voice follows the picture.
            await SetVoicesAsync(air).ConfigureAwait(false);
        }

        /// <summary>
        /// A player camera source for each lobby whose host sends one, in the TT Player Cam scene only,
        /// at the bottom (the lobby's own picture and the graphics go over it). True if any changed.
        /// </summary>
        private async Task<bool> AddCamSourcesAsync(ObsClient obs, HashSet<string> inputs)
        {
            var feeds = CamFeeds?.Invoke();
            if (feeds == null || !Settings.Scenes.TryGetValue("cam", out var scene)) return false;
            bool changed = false;
            foreach (var (lobby, feedUrl) in feeds)
            {
                if (!_desk.Simulating && CasterDesk.IsSimLobby(lobby)) continue;
                try
                {
                    string url = WithBitrate(feedUrl), root = Settings.CamPrefix + lobby;
                    string current = Settings.CamSources.TryGetValue(lobby, out var m) && m.Length > 0 ? m : root;
                    var (name, ids, added) = await EnsureInputAsync(obs, root, current, new[] { scene }, inputs, "browser_source",
                        new { url, width = 1920, height = 1080, reroute_audio = true, shutdown = false, restart_when_active = false, fps_custom = false },
                        enabled: false,
                        existing: async n =>
                        {
                            var cur = await obs.RequestAsync("GetInputSettings", new { inputName = n }).ConfigureAwait(false);
                            string? was = cur.TryGetProperty("inputSettings", out var st) && st.TryGetProperty("url", out var u) ? u.GetString() : null;
                            if (was != url) await obs.RequestAsync("SetInputSettings", new { inputName = n, inputSettings = new { url } }).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    await EnsureReplayFilterAsync(obs, "Cam " + lobby, name).ConfigureAwait(false);
                    if (added && ids.TryGetValue(scene, out var id))
                        await obs.RequestAsync("SetSceneItemIndex", new { sceneName = scene, sceneItemId = id, sceneItemIndex = 0 }).ConfigureAwait(false);
                    if (!Settings.CamSources.TryGetValue(lobby, out var mapped) || mapped != name) { Settings.CamSources[lobby] = name; changed = true; }
                    if (added) changed = true;
                    try { await obs.RequestAsync("SetInputMute", new { inputName = name, inputMuted = true }).ConfigureAwait(false); } catch (ObsException) { }
                }
                catch (Exception e) { CamProblem = $"Player camera for {lobby}: {e.Message}"; }
            }
            return changed;
        }

        /// <summary>Why a player camera source couldn't be made (shown on the OBS page), or null.</summary>
        public string? CamProblem { get; private set; }

        private string? CamLobbyOf(string source)
        {
            if (_dead.Contains(source)) return null;
            foreach (var (lobby, name) in Settings.CamSources) if (name == source) return lobby;
            return source.StartsWith(Settings.CamPrefix, StringComparison.Ordinal) ? source.Substring(Settings.CamPrefix.Length) : null;
        }

        private string? LobbyOf(string source)
        {
            if (_dead.Contains(source)) return null;
            foreach (var (lobby, name) in Settings.Sources) if (name == source) return lobby;
            return source.StartsWith(Settings.SourcePrefix, StringComparison.Ordinal) ? source.Substring(Settings.SourcePrefix.Length) : null;
        }

        // ---- When you switch in OBS yourself ------------------------------------------------------

        private static string ProgramName(JsonElement cur) =>
            cur.TryGetProperty("currentProgramSceneName", out var s) ? s.GetString() ?? "" : cur.TryGetProperty("sceneName", out var n) ? n.GetString() ?? "" : "";

        /// <summary>OBS made the switch we asked for: what's on stream is now what the desk thinks.</summary>
        private void Confirm(Expect ex)
        {
            if (_expect != ex) return;
            _expect = null;
            SwitchProblem = null;
            Scene = ex.Scene;
            if (ex.Air != null) _desk.ObsConfirmed(ex.Air, ex.Scene);
        }

        /// <summary>OBS didn't switch in time: say so, and show what OBS really has.</summary>
        private void TimedOut(Expect ex)
        {
            if (_expect != ex) return;
            _expect = null;
            SwitchProblem = $"OBS didn't switch to {ex.Scene} (still on {Scene ?? "another scene"}). Check OBS, then try again.";
            _ = Task.Run(async () => { try { await ReadBackAsync(force: true).ConfigureAwait(false); } catch (Exception) { } });
        }

        /// <summary>A replay going on: OBS's switch to the replay scene is ours too.</summary>
        private Expect ExpectReplay() => _expect = new Expect { Scene = Settings.Replay.Scene, Asked = DateTime.UtcNow };

        private void OnEvent(string type, JsonElement data)
        {
            if (type == "InputVolumeMeters") { OnMeters(data); return; }      // 20 times a second: nothing else
            if (type == "VendorEvent") { OnVendorEvent(data); return; }
            if (type == "InputMuteStateChanged") { OnMuteChanged(data); return; }
            if (type == "CurrentProgramSceneChanged")
            {
                string name = data.TryGetProperty("sceneName", out var sn) ? sn.GetString() ?? "" : "";
                var ex = _expect;
                if (ex != null && name == ex.Scene) { Confirm(ex); if (ex.Air == null) Scene = name; return; }
                Scene = name;
                if (name == Settings.Replay.Scene) return;           // the replay manager keeps track of replays
                // Someone switched in OBS (or OBS went elsewhere while we waited): OBS is right.
                if (ex != null) { _expect = null; SwitchProblem = null; }
                _ = Task.Run(async () => { try { await ReadBackAsync().ConfigureAwait(false); } catch (Exception) { } });
                return;
            }
            if (type != "SceneItemEnableStateChanged" && type != "SceneItemTransformChanged") return;
            // While a switch of ours is under way these are us arranging the scene.
            if (_expect != null || _busy.CurrentCount == 0) return;
            // A picture shown, hidden or moved by hand in the live scene.
            if (data.TryGetProperty("sceneName", out var sc) && sc.GetString() != Scene) return;
            QueueReadBack();
        }

        // Dragging a picture in OBS sends a burst of events: one read a moment after they stop.
        private int _readQueued;
        private void QueueReadBack()
        {
            if (System.Threading.Interlocked.Exchange(ref _readQueued, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(300).ConfigureAwait(false);
                System.Threading.Interlocked.Exchange(ref _readQueued, 0);
                try { await ReadBackAsync().ConfigureAwait(false); } catch (Exception) { }
            });
        }

        /// <summary>Works out what's on stream from OBS's program scene and tells the desk (on-air labels follow).</summary>
        public async Task ReadBackAsync(bool force = false)
        {
            var obs = _obs;
            if (obs == null || _expect != null && !force) return;
            var cur = await obs.RequestAsync("GetCurrentProgramScene").ConfigureAwait(false);
            string scene = ProgramName(cur);
            Scene = scene;
            if (scene == Settings.Replay.Scene) return;           // the replay manager keeps track of replays
            string? layout = Settings.Scenes.FirstOrDefault(kv => kv.Value == scene).Key;
            if (layout == null)
            {
                _desk.ObsChanged(new OnAir { Layout = "none", Slots = new List<string?>(), Scene = scene });
                return;
            }
            var shown = (await ItemsAsync(obs, scene).ConfigureAwait(false)).Where(i => i.Enabled && LobbyOf(i.Source) != null).ToList();
            var boxes = Slots(layout, Width, Height, layout == "full" ? 0 : Settings.Gap, shown.Count);
            var slots = new List<string?>(new string?[boxes.Count]);
            foreach (var item in shown)
            {
                string? lobby = LobbyOf(item.Source);
                if (lobby == null) continue;
                // Whichever slot its picture's middle is nearest.
                double cx = item.X + item.W / 2, cy = item.Y + item.H / 2;
                int best = Enumerable.Range(0, boxes.Count).OrderBy(i => Math.Pow(boxes[i].Cx - cx, 2) + Math.Pow(boxes[i].Cy - cy, 2)).First();
                if (slots[best] == null) slots[best] = lobby;
                else { int free = slots.IndexOf(null); if (free >= 0) slots[free] = lobby; }
            }
            // The same as the desk has (our own switch, read again): just note the scene.
            var now = _desk.OnAir;
            if (now.Layout == layout && now.Slots.Count == slots.Count && now.Slots.Zip(slots).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase)))
            {
                _desk.ObsConfirmed(now, scene);
                return;
            }
            _desk.ObsChanged(new OnAir { Layout = layout, Slots = slots, Scene = scene });
        }

        public object Status() => new
        {
            Connected,
            Version = ObsVersion,
            Problem,
            Scene,
            Settings.Host,
            Settings.Port,
            HasPassword = Settings.Password.Length > 0,
            Scenes = Settings.Scenes,
            Sources = Settings.Sources,
            Canvas = $"{Width}×{Height}",
            Voice = VoiceStatus(),
            Swoosh = new { Settings.Swoosh.On, Stinger = _stinger, Problem = SwooshProblem, File = SwooshFile, Count = Swooshes },
            SwitchingTo, SwitchProblem,
            Checks = Connected ? Checks : new List<SetupCheck>(),
            CheckedAt = CheckedAt?.ToString("o"),
            DesktopAudioOn = Connected ? DesktopAudioOn : new List<string>(),
            SourceRecord, ReplayProblem, CamProblem,
            // Lobbies on stream whose picture isn't in OBS, and why (the Live desk warns, with Fix).
            Missing = Connected ? _desk.OnAir.Slots.Where(l => l != null).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(l => (Lobby: l!, Why: SourceIssue(l!))).Where(x => x.Why != null).ToDictionary(x => x.Lobby, x => x.Why!) : new Dictionary<string, string>(),
            SourceProblems,
        };

        public async ValueTask DisposeAsync()
        {
            _timer?.Dispose();
            _timer = null;
            await DropAsync().ConfigureAwait(false);
        }
    }
}
