using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TournamentTracker.Control;
using TournamentTracker.Setup;
using TournamentTracker.Stats;

using TournamentTracker.App.Broadcast;

namespace TournamentTracker.App
{
    /// <summary>Where the app keeps its own settings (the Among Us folder).</summary>
    public sealed class AppSettings
    {
        public string? GamePath { get; set; }

        /// <summary>Download and install new versions of The Button by itself.</summary>
        public bool AutoUpdateApp { get; set; } = true;

        /// <summary>Install new versions of the mod in Among Us by themselves (while the game is closed).</summary>
        public bool AutoUpdateMod { get; set; } = true;

        /// <summary>The administration code that unlocks the organiser's view (null: locked).</summary>
        public string? AdminCode { get; set; }

        /// <summary>
        /// A copy of the host's setup code. The mod reads it from the Among Us folder, where a
        /// reinstall or a file check can remove it; from here it's put back.
        /// </summary>
        public string? SetupCode { get; set; }

        /// <summary>Lobby voice for the broadcast (when sending the game to the caster): levels, and whether the referee's own microphone goes in.</summary>
        public double VoiceLevel { get; set; } = 1.0;
        public double GameSoundLevel { get; set; } = 0.5;
        public bool VoiceIncludeMic { get; set; }
        public bool VoiceOff { get; set; }

        public static AppSettings Load(string file)
        {
            try { if (File.Exists(file)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? new AppSettings(); }
            catch (Exception) { }
            return new AppSettings();
        }

        public void Save(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(this));
        }
    }

    /// <summary>What the Windows window needs to give the app: where things are, and how to open files and links.</summary>
    public sealed class AppEnvironment
    {
        public string SettingsFile { get; set; } = "";
        public string? SteamRoot { get; set; }
        public string EpicManifests { get; set; } = GameLocator.EpicManifests;
        public IReadOnlyList<string> Fallbacks { get; set; } = GameLocator.Fallbacks;
        public string Downloads { get; set; } = "";
        /// <summary>Opens a folder, file or web link with Windows.</summary>
        public Action<string> Open { get; set; } = _ => { };
        public string Version { get; set; } = "";

        /// <summary>The running TheButton.exe, so it can update itself. Null: no self-update (tests, other platforms).</summary>
        public string? ExePath { get; set; }

        /// <summary>The caster overlay's port (OBS points at it); 0 picks any free one (tests).</summary>
        public int CasterPort { get; set; } = Organizer.CasterPort;

        /// <summary>Starts the new version and closes this one.</summary>
        public Action Restart { get; set; } = () => { };
    }

    /// <summary>
    /// The app's screens and everything behind them, served on this computer only. Every
    /// /app/ call needs the per-launch token the page was given, so no website can drive it.
    /// </summary>
    public sealed class AppServer : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private static readonly Regex ReplayName = new Regex(@"^tt-replay-[\w.-]+\.json(\.gz)?$");

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly AppEnvironment _env;
        private readonly AppSettings _settings;
        private readonly ModInstaller _installer;
        private readonly ModClient _mod;
        private readonly HttpClient _http;
        private Release? _latest;
        private Organizer? _organizer;
        private CasterDesk? _desk;
        private ObsDirector? _obs;
        private ReplayManager? _replays;
        private BroadcastApp? _broadcast;
        private MontageManager? _montages;
        private Voice.VoiceCapture? _voice;

        /// <summary>The lobby's voice and game sound for the send page (started the first time it asks).</summary>
        private Voice.VoiceCapture VoiceNow()
        {
            if (_voice == null)
            {
                _voice = new Voice.VoiceCapture { VoiceLevel = _settings.VoiceLevel, GameLevel = _settings.GameSoundLevel };
                if (!_settings.VoiceOff) _voice.Start();
            }
            return _voice;
        }

        /// <summary>Starts (or stops) the organiser's view for the saved administration code.</summary>
        private void StartOrganizer()
        {
            _organizer?.Dispose();
            _organizer = null;
            _desk?.Dispose();
            _desk = null;
            _replays?.Dispose();
            _replays = null;
            var oldObs = _obs;
            _obs = null;
            if (oldObs != null) _ = oldObs.DisposeAsync().AsTask();
            if (_settings.AdminCode != null && SetupCode.TryParse(_settings.AdminCode, out var code, out _) && code.IsAdmin)
            {
                _organizer = new Organizer(code, _http, casterPort: _env.CasterPort);
                var organizer = _organizer;
                var desk = _desk = new CasterDesk(DeskConfigPath, rosterPath: SideFile(Roster.FileName), dataFolder: SideFolder())
                {
                    ExternalGames = () => organizer.Games,
                    Advance = () => organizer.Advance,
                    GamesPerRound = () => organizer.GamesPerRound,
                };
                var obs = _obs = new ObsDirector(SideFile(ObsSettings.FileName), desk, () => ObsFeeds(organizer, desk)) { VoiceFeeds = () => VoiceFeeds(organizer, desk) };
                // The overlay and video page follow the lobby in the first slot; OBS (when connected) shows the whole layout.
                desk.Switch = air =>
                {
                    var first = air.Slots.FirstOrDefault(x => x != null);
                    if (first != null) organizer.Cast(first);
                    if (obs.Connected) _ = obs.ApplyAsync(air);
                };
                if (organizer.CasterUrl != null) obs.TagUrl = organizer.CasterUrl + "replaytag";
                var broadcast = _broadcast = new BroadcastApp(desk, () => _obs, SideFile(BroadcastSettings.FileName));
                if (organizer.CasterUrl != null) obs.BroadcastUrl = organizer.CasterUrl + "broadcast";
                organizer.MorePages = path => BroadcastPage(broadcast, path) ?? SponsorPage(desk, path);
                var builder = new MontageBuilder(() => obs.Settings.Replay, () => obs.ClipFolder, SideFolder() is { } side ? Path.Combine(side, "tools") : null);
                obs.MakeSwoosh = builder.SwooshAsync;
                var replays = _replays = new ReplayManager(desk, obs) { TagChanged = json => organizer.ReplayNow = json, Builder = builder, Sponsors = desk.Sponsors };
                _montages = new MontageManager(desk, replays, builder, desk.Sponsors, () => obs.Settings.Replay);
                obs.Start();
            }
        }

        /// <summary>The on-stream graphics app and what it reads, on the caster port (OBS loads it from there).</summary>
        private static (string Type, byte[] Body)? BroadcastPage(BroadcastApp app, string path)
        {
            string route = path.Split('?')[0];
            if (route == "/broadcast") return ("text/html; charset=utf-8", Encoding.UTF8.GetBytes(Resource("ui/broadcast.html")));
            if (route == "/broadcast/state") return ("application/json", JsonSerializer.SerializeToUtf8Bytes(app.State(), Json));
            if (route == "/broadcast/logo") return app.Logo();
            // Players' crewmate heads, for every name on stream.
            var head = Regex.Match(route, @"^/crew/(\d{1,2})\.png$");
            if (head.Success) { var png = ResourceBytes($"ui/crew/{head.Groups[1].Value}.png"); return png.Length > 0 ? ("image/png", png) : null; }
            var font = Regex.Match(route, @"^/fonts/([a-z0-9-]+\.woff2)$");
            if (font.Success) { var b = ResourceBytes("ui/fonts/" + font.Groups[1].Value); return b.Length > 0 ? ("font/woff2", b) : null; }
            return null;
        }

        /// <summary>A sponsor's logo or video (files on this PC) for the graphics app and the replay tag.</summary>
        private static (string Type, byte[] Body)? SponsorPage(CasterDesk desk, string path)
        {
            if (path.Split('?')[0] != "/sponsorfile") return null;
            string q = path.Contains('?') ? path.Substring(path.IndexOf('?') + 1) : "";
            return desk.Sponsors.MediaFile(HttpRequest.Query(q, "name"), HttpRequest.Query(q, "what"));
        }

        /// <summary>caster-priority.json, next to The Button's settings.</summary>
        private string? DeskConfigPath => SideFile(PriorityConfig.FileName);

        private string? _ffmpegNote;

        private object? SponsorState()
        {
            if (_desk == null) return null;
            var list = _desk.Sponsors.Sponsors;
            var log = _desk.Sponsors.Appearances();
            return new
            {
                Path = _desk.Sponsors.Path,
                Problem = _desk.Sponsors.Problem,
                FfmpegNote = _ffmpegNote,
                List = list.Select(x => new
                {
                    x.Name, x.Tagline, x.Placements, x.BreakSeconds, Logo = x.Logo.Length > 0,
                    Shown = log.Count(a => a.Sponsor == x.Name),
                    Seconds = Math.Round(log.Where(a => a.Sponsor == x.Name).Sum(a => a.Seconds)),
                }).ToList(),
            };
        }

        private string? SideFolder() => string.IsNullOrEmpty(_env.SettingsFile) ? null : Path.GetDirectoryName(_env.SettingsFile);

        private string? SideFile(string name) =>
            string.IsNullOrEmpty(_env.SettingsFile) ? null : Path.Combine(Path.GetDirectoryName(_env.SettingsFile) ?? ".", name);

        /// <summary>What each lobby's OBS source shows: its VDO.Ninja video, or a stand-in page in simulation mode.</summary>
        private static IReadOnlyList<(string Lobby, string Url)> ObsFeeds(Organizer organizer, CasterDesk desk)
        {
            var list = organizer.ObsLinks();
            if (desk.Simulating && organizer.CasterUrl != null)
                foreach (var r in desk.Board.Ranking())
                    if (!list.Any(l => string.Equals(l.Lobby, r.Lobby, StringComparison.OrdinalIgnoreCase)))
                        list.Add((r.Lobby, organizer.CasterUrl + "sim?lobby=" + Uri.EscapeDataString(r.Lobby)));
            return list;
        }

        /// <summary>Each lobby's voice stream for OBS; simulated lobbies get a stand-in that blips.</summary>
        private static IReadOnlyList<(string Lobby, string Url)> VoiceFeeds(Organizer organizer, CasterDesk desk)
        {
            var list = organizer.VoiceLinks();
            if (desk.Simulating && organizer.CasterUrl != null)
                foreach (var r in desk.Board.Ranking())
                    if (!list.Any(l => string.Equals(l.Lobby, r.Lobby, StringComparison.OrdinalIgnoreCase)))
                        list.Add((r.Lobby, organizer.CasterUrl + "simvoice?lobby=" + Uri.EscapeDataString(r.Lobby)));
            return list;
        }

        private object SetAdminCode(string text)
        {
            if (text.Trim().Length == 0)
            {
                _settings.AdminCode = null;
                TrySave();
                StartOrganizer();
                return new { ok = true, message = "Administration is off." };
            }
            if (!SetupCode.TryParse(text, out var code, out var error)) return new { ok = false, message = error };
            if (!code.IsAdmin) return new { ok = false, message = "That's a host setup code. Administration needs an administration code from the organiser." };
            _settings.AdminCode = code.Encode();
            TrySave();
            StartOrganizer();
            return new { ok = true, message = $"Organiser view unlocked for {code.TournamentName}." };
        }

        // The Button updating itself: the new exe is put in place next to the running one
        // (Windows lets a running program be renamed, not overwritten) and used from the next start.
        private volatile string? _appReady;
        private volatile bool _appUpdating;
        private volatile string? _appUpdateError;

        private bool AppUpdateAvailable =>
            _env.ExePath != null && _latest?.AppUrl != null && _appReady == null && Newer(_latest.Tag, _env.Version);

        private async Task UpdateAppAsync()
        {
            var release = _latest;
            string? exe = _env.ExePath;
            if (_appUpdating || release?.AppUrl == null || exe == null) return;
            _appUpdating = true;
            _appUpdateError = null;
            try
            {
                string error = await AppUpdater.InstallAsync(_http, release.AppUrl, exe).ConfigureAwait(false);
                if (error.Length > 0) _appUpdateError = error;
                else _appReady = release.Tag;
            }
            finally { _appUpdating = false; }
        }
        private DateTime _latestChecked = DateTime.MinValue;
        private string _installing = "";
        private string _installResult = "";

        public int Port { get; }
        public string Token { get; }
        public string Url => $"http://127.0.0.1:{Port}/";

        public AppServer(AppEnvironment env, HttpClient http)
        {
            _env = env;
            _http = http;
            _settings = AppSettings.Load(env.SettingsFile);
            _installer = new ModInstaller(http);
            _mod = new ModClient(http);
            if (!GameLocator.IsGameFolder(_settings.GamePath))
            {
                var found = Candidates().FirstOrDefault();
                if (found != null) { _settings.GamePath = found.Path; TrySave(); }
            }
            var bytes = new byte[18];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            Token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(AcceptLoop);
            StartOrganizer();
        }

        public string? GamePath => _settings.GamePath;

        private List<GameInstall> Candidates() => GameLocator.Find(_env.SteamRoot, _env.EpicManifests, _env.Fallbacks);

        private void TrySave()
        {
            try { _settings.Save(_env.SettingsFile); } catch (Exception) { }
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var (method, path, headers, body) = await HttpRequest.ReadAsync(stream).ConfigureAwait(false);
                    var (status, type, bytes) = await Route(method, path, headers, body).ConfigureAwait(false);
                    await HttpRequest.WriteAsync(stream, status, type, bytes).ConfigureAwait(false);
                }
                catch (Exception) { /* the window closed mid-request */ }
            }
        }

        private static (int, string, byte[]) Text(int status, string type, string text) => (status, type, Encoding.UTF8.GetBytes(text));
        private static (int, string, byte[]) Ok(object value) => Text(200, "application/json", JsonSerializer.Serialize(value, Json));

        internal async Task<(int Status, string Type, byte[] Body)> Route(string method, string path, Dictionary<string, string> headers, string body)
        {
            // Only this computer, by name: stops a web page reaching us through DNS tricks.
            headers.TryGetValue("host", out var host);
            if (host != null && !Regex.IsMatch(host, @"^(127\.0\.0\.1|localhost)(:\d+)?$")) return Text(403, "text/plain", "Forbidden");

            string route = path.Split('?')[0];
            string query = path.Contains('?') ? path.Substring(path.IndexOf('?') + 1) : "";
            if (method == "GET" && (route == "/" || route == "/index.html"))
                return Text(200, "text/html; charset=utf-8", Resource("ui/index.html").Replace("__APP_TOKEN__", Token));
            if (method == "GET" && route == "/viewer")
                return Text(200, "text/html; charset=utf-8",
                    "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"></head><body style=\"margin:0\">"
                    + Resource("ui/viewer-body.html") + "</body></html>");
            if (method == "GET" && route == "/send")
                return Text(200, "text/html; charset=utf-8", CasterPages.Send);
            if (method == "GET" && route == "/generator")
                return Text(200, "text/html; charset=utf-8", Resource("docs/setup-codes.html"));
            var font = Regex.Match(route, @"^/fonts/([a-z0-9-]+\.woff2)$");
            if (method == "GET" && font.Success)
            {
                var bytes = ResourceBytes("ui/fonts/" + font.Groups[1].Value);
                return bytes.Length > 0 ? (200, "font/woff2", bytes) : Text(404, "text/plain", "Not found");
            }
            if (method == "GET" && route == "/logo.png") return (200, "image/png", ResourceBytes("ui/logo.png"));
            var crew = Regex.Match(route, @"^/crew/(\d{1,2})\.png$");
            if (method == "GET" && crew.Success)
            {
                var png = ResourceBytes($"ui/crew/{crew.Groups[1].Value}.png");
                return png.Length > 0 ? (200, "image/png", png) : Text(404, "text/plain", "Not found");
            }
            if (!route.StartsWith("/app/", StringComparison.Ordinal)) return Text(404, "text/plain", "Not found");

            headers.TryGetValue("x-app-token", out var token);
            if (token != Token && HttpRequest.Query(query, "token") != Token) return Text(401, "application/json", "{\"error\":\"bad token\"}");

            JsonElement input = default;
            if (body.Length > 0) { try { input = JsonDocument.Parse(body).RootElement; } catch (Exception) { } }
            string Arg(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            switch ((method, route))
            {
                case ("GET", "/app/state"): return Ok(await StateAsync().ConfigureAwait(false));
                case ("POST", "/app/game"): return Ok(SetGame(Arg("path")));
                case ("POST", "/app/install"): return Ok(StartInstall());
                case ("POST", "/app/setup"): return Ok(await SaveSetupAsync(Arg("code")).ConfigureAwait(false));
                case ("POST", "/app/setup/clear"): return Ok(await ClearSetupAsync().ConfigureAwait(false));
                case ("POST", "/app/command"): return Ok(await CommandAsync(Arg("command")).ConfigureAwait(false));
                case ("GET", "/app/activity"):
                {
                    string? activity = GamePath == null ? null : await _mod.ActivityAsync(GamePath, long.TryParse(HttpRequest.Query(query, "since"), out var s) ? s : 0).ConfigureAwait(false);
                    return Text(200, "application/json", activity ?? "{\"last\":0,\"lines\":[]}");
                }
                case ("GET", "/app/replays"): return Ok(Replays());
                case ("GET", "/app/games"): return Ok(Games());
                case ("GET", "/app/replay"):
                {
                    string name = HttpRequest.Query(query, "name");
                    var file = ReplayFiles().FirstOrDefault(f => f.Name == name);
                    return file == null || !ReplayName.IsMatch(name) ? Text(404, "text/plain", "Not found") : (200, "application/octet-stream", File.ReadAllBytes(file.FullName));
                }
                case ("POST", "/app/open"): return Ok(Open(Arg("what")));
                case ("POST", "/app/feed"): return Ok(await FeedAsync(Arg("on") == "true").ConfigureAwait(false));
                case ("POST", "/app/admin/code"): return Ok(SetAdminCode(Arg("code")));
                case ("GET", "/app/admin"): return _organizer == null ? Text(404, "application/json", "{\"error\":\"locked\"}") : Ok(_organizer.State());
                case ("POST", "/app/admin/cast"):
                    if (_organizer == null) return Ok(new { ok = false, message = "Administration is locked." });
                    _organizer.Cast(Arg("lobby"));
                    return Ok(new { ok = true, message = $"Casting {Arg("lobby")}." });
                case ("GET", "/app/admin/desk"):
                    if (_desk == null || _organizer == null) return Text(404, "application/json", "{\"error\":\"locked\"}");
                    return Ok(new { desk = _desk.State(), obs = _obs?.Status(), replay = _replays?.State(), roster = _desk.RosterState(), story = _desk.StoryState(),
                        montages = _montages?.State(), moments = _replays?.Moments(), sponsors = SponsorState(),
                        voice = new { status = _desk.VoiceState(), obs = _obs?.VoiceStatus() },
                        broadcast = _broadcast == null ? null : new { url = _organizer.CasterUrl == null ? null : _organizer.CasterUrl + "broadcast", elements = _broadcast.Settings.Refresh().Elements, names = BroadcastSettings.ElementNames, problem = _broadcast.Settings.Problem, alerts = _desk.Alerts.Settings, alertsWaiting = _desk.Alerts.Waiting },
                        names = _organizer.DataLinks().ToDictionary(d => d.Lobby, d => _desk.NamesFor(d.Lobby)), receivers = _organizer.DataLinks().Select(d => new { lobby = d.Lobby, url = d.Url }).ToList() });
                case ("POST", "/app/admin/feedin"):
                    if (_desk == null) return Ok(new { ok = false });
                {
                    var from = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var item in items.EnumerateArray())
                        {
                            _desk.Apply(item);
                            if (item.TryGetProperty("lobby", out var il) && il.ValueKind == JsonValueKind.String && item.TryGetProperty("src", out _)) from.Add(il.GetString()!);
                        }
                    // Part 22: tells each referee's page what arrived, so it stops sending those again.
                    return Ok(new { ok = true, acks = from.ToDictionary(l => l, l => _desk.Acks(l)) });
                }
                case ("POST", "/app/admin/health"):
                {
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    string lobby = Arg("lobby"), what = Arg("what");
                    switch (what)
                    {
                        case "forget": _desk.ForgetHealth(lobby); return Ok(new { ok = true, message = $"{lobby} taken off the health list." });
                        case "autoSwitch": _desk.HealthConfig.AutoSwitch = Arg("on") == "true"; _desk.HealthConfig.Save(_desk.HealthPath); return Ok(new { ok = true, message = _desk.HealthConfig.AutoSwitch ? "Auto switch away from a lobby that drops: on." : "Auto switch away from a lobby that drops: off." });
                        case "slate": _desk.HealthConfig.Slate = Arg("on") == "true"; _desk.HealthConfig.Save(_desk.HealthPath); return Ok(new { ok = true, message = _desk.HealthConfig.Slate ? "Be-right-back screen when every lobby is down: on." : "Every lobby down: intermission instead of the be-right-back screen." });
                        case "open": if (_desk.HealthPath != null) { _desk.HealthConfig.Save(_desk.HealthPath); try { _env.Open(_desk.HealthPath); } catch (Exception) { } } return Ok(new { ok = true, message = "Opened health.json." });
                        default:
                            if (!_desk.Simulating) return Ok(new { ok = false, message = "Those buttons are for simulation mode." });
                            return Ok(new { ok = _desk.SimFail(lobby, what), message = what == "reconnect" ? $"{lobby}: reconnecting." : $"{lobby}: simulated {what} problem." });
                    }
                }
                case ("POST", "/app/admin/interrupted"):
                {
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    var (command, message) = _desk.Decide(Arg("id"), Arg("decision"));
                    if (command != null)
                    {
                        if (_desk.Simulating || _organizer == null) message += " (Simulation: nothing posted to Discord.)";
                        else message += " " + await _organizer.CommandAsync(command).ConfigureAwait(false);
                    }
                    return Ok(new { ok = true, message });
                }
                case ("POST", "/app/admin/show"):
                {
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    string layout = Arg("layout").Length > 0 ? Arg("layout") : "full";
                    int? slot = int.TryParse(Arg("slot"), out var sl) ? sl : (int?)null;
                    List<string>? slots = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("slots", out var ss) && ss.ValueKind == JsonValueKind.Array
                        ? ss.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToList() : null;
                    var air = _desk.Show(Arg("lobby"), layout, slot, slots);
                    return Ok(new { ok = true, message = air.Layout == "full" ? $"{Arg("lobby")} is on stream." : $"On stream: {string.Join(", ", air.Slots.Select(x => x ?? "empty"))}." });
                }
                case ("POST", "/app/admin/watch"):
                {
                    var card = _desk?.Find(Arg("id"));
                    if (_desk == null || card == null) return Ok(new { ok = false, message = "That card is gone." });
                    // A card with a saved replay plays it; otherwise its lobby comes back up.
                    if (_replays != null && card.ClipId != null && _replays.Find(card.ClipId)?.State == "ready")
                    {
                        string played = await _replays.PlayAsync(card.ClipId).ConfigureAwait(false);
                        return Ok(new { ok = true, message = played });
                    }
                    _desk.Show(card.Lobby, _desk.OnAir.Layout == "none" ? "full" : _desk.OnAir.Layout, 1);
                    return Ok(new { ok = true, message = $"Back to {card.Lobby}: {NameTag.Plain(card.Text)}." });
                }
                case ("POST", "/app/admin/obs"):
                {
                    if (_obs == null) return Ok(new { ok = false, message = "Administration is locked." });
                    switch (Arg("action"))
                    {
                        case "swoosh":
                            _obs.Settings.Swoosh.On = Arg("on") == "true";
                            _obs.SaveSettings();
                            if (_obs.Settings.Swoosh.On && _obs.Connected) await _obs.EnsureSwooshAsync().ConfigureAwait(false);
                            return Ok(new { ok = true, message = _obs.Settings.Swoosh.On ? "Swoosh on every switch." : "Swoosh off: straight cuts." });
                        case "disconnect":
                            await _obs.DisconnectAsync().ConfigureAwait(false);
                            return Ok(new { ok = true, message = "Disconnected from OBS." });
                        case "build":
                            if (!_obs.Connected) return Ok(new { ok = false, message = "Connect to OBS first." });
                            await _obs.BuildAsync().ConfigureAwait(false);
                            await _obs.EnsureSwooshAsync().ConfigureAwait(false);
                            if (_desk != null && _desk.OnAir.Layout != "none") await _obs.ApplyAsync(_desk.OnAir).ConfigureAwait(false);
                            return Ok(new { ok = true, message = "The TT scenes are up to date." });
                        default:
                        {
                            int? port = int.TryParse(Arg("port"), out var pt) ? pt : (int?)null;
                            string? password = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("password", out var pw) && pw.ValueKind == JsonValueKind.String ? pw.GetString() : null;
                            string message = await _obs.ConnectAsync(Arg("host").Length > 0 ? Arg("host") : null, port, password).ConfigureAwait(false);
                            if (_obs.Connected && _desk != null && _desk.OnAir.Layout != "none" && _desk.OnAir.By == "button") await _obs.ApplyAsync(_desk.OnAir).ConfigureAwait(false);
                            return Ok(new { ok = _obs.Connected, message });
                        }
                    }
                }
                case ("POST", "/app/admin/replay"):
                {
                    if (_replays == null || _desk == null || _obs == null) return Ok(new { ok = false, message = "Administration is locked." });
                    string action = Arg("action");
                    double Num(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var n)
                        ? n.ValueKind == JsonValueKind.Number ? n.GetDouble() : double.TryParse(n.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0 : 0;
                    switch (action)
                    {
                        case "save":
                        {
                            var card = _desk.Find(Arg("id"));
                            if (card == null) return Ok(new { ok = false, message = "That card is gone." });
                            if (!_obs.Connected) return Ok(new { ok = false, message = "Connect OBS first: replays come from its replay buffer." });
                            _ = Task.Run(() => _replays.SaveAsync(card));
                            return Ok(new { ok = true, message = $"Saving a replay of {card.Lobby}: {NameTag.Plain(card.Text)}." });
                        }
                        case "play":
                        {
                            string id = Arg("id");
                            var card = _desk.Find(id);
                            if (card?.ClipId != null) id = card.ClipId;
                            return Ok(new { ok = true, message = await _replays.PlayAsync(id).ConfigureAwait(false) });
                        }
                        case "hotkeys":
                        {
                            if (input.TryGetProperty("map", out var map) && map.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var kv in map.EnumerateObject())
                                    if (kv.Value.ValueKind == JsonValueKind.String && _obs.Settings.Replay.Hotkeys.ContainsKey(kv.Name)) _obs.Settings.Replay.Hotkeys[kv.Name] = kv.Value.GetString() ?? "";
                                _obs.SaveSettings();
                            }
                            return Ok(new { ok = true, message = "Replay keys saved." });
                        }
                        default:
                        {
                            string? said = await _replays.ControlAsync(action, Num("value"), Num("value2")).ConfigureAwait(false);
                            return Ok(new { ok = said == null || action == "live", message = said ?? "" });
                        }
                    }
                }
                case ("POST", "/app/admin/montage"):
                {
                    if (_montages == null || _desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    string id = Arg("id");
                    switch (Arg("action"))
                    {
                        case "play": return Ok(new { ok = true, message = await _montages.PlayAsync(id).ConfigureAwait(false) });
                        case "discard": return Ok(new { ok = true, message = _montages.Discard(id) });
                        case "ffmpeg":
                            if (_montages.Builder.Ffmpeg != null) return Ok(new { ok = true, message = "ffmpeg is already here." });
                            if (!OperatingSystem.IsWindows()) return Ok(new { ok = false, message = "Install ffmpeg with your package manager." });
                            _ = Task.Run(async () => { try { _ffmpegNote = "Downloading ffmpeg…"; _ffmpegNote = await _montages.Builder.DownloadFfmpegAsync(_http).ConfigureAwait(false); } catch (Exception e) { _ffmpegNote = "ffmpeg download failed: " + e.Message; } });
                            return Ok(new { ok = true, message = "Downloading ffmpeg (about 100 MB)…" });
                        case "game":
                        {
                            if (_montages.Builder.Ffmpeg == null) return Ok(new { ok = false, message = "Get ffmpeg first." });
                            var game = _desk.Archive.Games.LastOrDefault(g => id.Length == 0 || string.Equals(g.Lobby, id, StringComparison.OrdinalIgnoreCase));
                            if (game == null) return Ok(new { ok = false, message = "No finished game yet." });
                            _ = Task.Run(() => _montages.GameAsync(game));
                            return Ok(new { ok = true, message = $"Building a montage of {game.Lobby}'s last game…" });
                        }
                        case "round":
                        {
                            if (_montages.Builder.Ffmpeg == null) return Ok(new { ok = false, message = "Get ffmpeg first." });
                            int round = int.TryParse(id, out var r) ? r : _desk.Archive.Games.Select(g => g.Round).DefaultIfEmpty(0).Max();
                            if (round <= 0) return Ok(new { ok = false, message = "No round played yet." });
                            _ = Task.Run(() => _montages.RoundAsync(round, again: true));
                            return Ok(new { ok = true, message = $"Building the round {round} montage…" });
                        }
                        case "custom":
                        {
                            if (_montages.Builder.Ffmpeg == null) return Ok(new { ok = false, message = "Get ffmpeg first." });
                            var ids = input.TryGetProperty("clips", out var cl) && cl.ValueKind == JsonValueKind.Array ? cl.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
                            if (ids.Count == 0) return Ok(new { ok = false, message = "Pick some moments first." });
                            _ = Task.Run(() => _montages.CustomAsync(ids, Arg("title")));
                            return Ok(new { ok = true, message = $"Building a montage of {ids.Count} moment{(ids.Count == 1 ? "" : "s")}…" });
                        }
                        default: return Ok(new { ok = false, message = "Unknown montage action." });
                    }
                }
                case ("GET", "/app/admin/media"):
                {
                    // A montage, a clip or a clip's still, for the preview and the Moments library.
                    string id = HttpRequest.Query(query, "id"), kind = HttpRequest.Query(query, "kind");
                    string? file = kind == "montage" ? _montages?.Find(id)?.File : kind == "thumb" ? _replays?.Find(id)?.Thumbnail : _replays?.Find(id)?.File;
                    if (file == null || !File.Exists(file)) return Text(404, "text/plain", "Not found");
                    return (200, SponsorBook.MediaType(file), File.ReadAllBytes(file));
                }
                case ("POST", "/app/admin/sponsor"):
                {
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    switch (Arg("action"))
                    {
                        case "break":
                        {
                            string said = _desk.StartBreak(Arg("name").Length > 0 ? Arg("name") : null);
                            return Ok(new { ok = _desk.Break != null, message = said });
                        }
                        case "end":
                            if (_desk.Break is not { } b) return Ok(new { ok = false, message = "No sponsor break is on." });
                            _desk.Show(b.Lobby ?? "");
                            return Ok(new { ok = true, message = "Back to full screen." });
                        case "open":
                            _desk.Sponsors.Refresh(force: true);
                            if (_desk.Sponsors.Path != null) try { _env.Open(_desk.Sponsors.Path); } catch (Exception) { }
                            return Ok(new { ok = true, message = "Opening sponsors.json." });
                        case "export":
                        {
                            var folder = SideFolder();
                            if (folder == null) return Ok(new { ok = false, message = "Nowhere to save it." });
                            var (csv, summary) = _desk.Sponsors.Export();
                            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm");
                            string dir = Path.Combine(folder, "Sponsor reports");
                            Directory.CreateDirectory(dir);
                            File.WriteAllText(Path.Combine(dir, $"sponsor appearances {stamp}.csv"), csv);
                            File.WriteAllText(Path.Combine(dir, $"sponsor summary {stamp}.txt"), summary);
                            try { _env.Open(dir); } catch (Exception) { }
                            return Ok(new { ok = true, message = "Saved the appearance log (CSV) and summary in \"Sponsor reports\".", summary });
                        }
                        default: return Ok(new { ok = false, message = "Unknown sponsor action." });
                    }
                }
                case ("POST", "/app/admin/spec"):
                {
                    // Real lobbies get the command from the tab, over their VDO.Ninja link; simulated ones here.
                    string cmd = Arg("command");
                    if (_desk == null || !cmd.StartsWith("spec ", StringComparison.Ordinal)) return Ok(new { ok = false });
                    return Ok(new { ok = _desk.SimSpec(Arg("lobby"), cmd) });
                }
                case ("POST", "/app/admin/roster"):
                {
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    if (Arg("action") == "open")
                    {
                        if (_desk.Roster.Path != null) try { _env.Open(_desk.Roster.Path); } catch (Exception) { }
                        return Ok(new { ok = true, message = "Opening the roster file." });
                    }
                    string key = Arg("key");
                    if (key.Length == 0) return Ok(new { ok = false, message = "Which player?" });
                    // "auto" goes back to automatic matching; "" means nobody on the roster.
                    string pick = Arg("name");
                    _desk.Roster.Override(key, pick == "auto" ? null : pick);
                    return Ok(new { ok = true, message = pick == "auto" ? "Matched automatically again." : pick.Length == 0 ? "Not on the roster." : $"Now {pick}." });
                }
                case ("POST", "/app/names"):
                {
                    // From the caster, over VDO.Ninja (this host's send page): roster names for this lobby's players.
                    if (GamePath == null) return Ok(new { ok = false });
                    string json = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("names", out var nm) && nm.ValueKind == JsonValueKind.Object ? nm.GetRawText() : "{}";
                    bool sent = await _mod.NamesAsync(GamePath, json).ConfigureAwait(false) != null;
                    return Ok(new { ok = sent });
                }
                case ("POST", "/app/admin/broadcast"):
                {
                    if (_broadcast == null) return Ok(new { ok = false, message = "Administration is locked." });
                    if (Arg("action") == "alerts" && _desk != null)
                    {
                        // Off-screen alerts: pause all, or one kind on/off.
                        if (Arg("kind").Length == 0)
                        {
                            bool pause = Arg("on").Length > 0 ? Arg("on") != "true" : !_desk.Alerts.Settings.Paused;
                            _desk.Alerts.SetPaused(pause);
                            return Ok(new { ok = true, message = pause ? "Off-screen alerts paused." : "Off-screen alerts back on." });
                        }
                        if (!AlertQueue.Kinds.Contains(Arg("kind"))) return Ok(new { ok = false, message = "Unknown alert." });
                        _desk.Alerts.SetType(Arg("kind"), Arg("on") == "true");
                        return Ok(new { ok = true, message = $"{Arg("kind")} alerts {(Arg("on") == "true" ? "on" : "off")}." });
                    }
                    if (Arg("action") == "theme")
                    {
                        if (_broadcast.Settings.Path != null) try { _env.Open(_broadcast.Settings.Path); } catch (Exception) { }
                        return Ok(new { ok = true, message = "Opening the theme (broadcast.json)." });
                    }
                    string element = Arg("element");
                    if (!BroadcastSettings.DefaultElements().ContainsKey(element)) return Ok(new { ok = false, message = "Unknown element." });
                    _broadcast.Settings.Set(element, Arg("on") == "true");
                    return Ok(new { ok = true, message = $"{BroadcastSettings.ElementNames[element]} {(Arg("on") == "true" ? "on" : "off")}." });
                }
                case ("POST", "/app/admin/dismiss"):
                    _desk?.Dismiss(Arg("id"));
                    return Ok(new { ok = true });
                case ("POST", "/app/admin/note"):
                {
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    string id = Arg("id"), action = Arg("action");
                    if (action == "show")
                    {
                        var note = _desk.Storylines.Notes(_desk.FocusKeys(), 200).FirstOrDefault(n => n.Id == id);
                        if (note == null) return Ok(new { ok = false, message = "That note is gone." });
                        _desk.ShownNote = (note.Text, DateTime.UtcNow);
                        _broadcast?.Settings.Set("storyline", true);
                        _desk.Storylines.Mark(id, "used");
                        return Ok(new { ok = true, message = "On stream: " + NameTag.Plain(note.Text) });
                    }
                    _desk.Storylines.Mark(id, action);
                    return Ok(new { ok = true, message = action switch { "pin" => "Pinned.", "unpin" => "Unpinned.", "used" => "Marked used.", "dismiss" => "Dismissed.", _ => "Done." } });
                }
                case ("POST", "/app/admin/standings"):
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    if (Arg("scope").Length > 0) _desk.StandingsScope = Arg("scope") == "overall" ? "overall" : "round";
                    if (Arg("show").Length > 0) _broadcast?.Settings.Set("standings", Arg("show") == "true");
                    return Ok(new { ok = true, message = Arg("show") == "true" ? "Standings on stream." : Arg("show") == "false" ? "Standings off stream." : $"Standings: {(_desk.StandingsScope == "overall" ? "whole tournament" : "this round")}." });
                case ("POST", "/app/admin/voice"):
                {
                    if (_obs == null) return Ok(new { ok = false, message = "Administration is locked." });
                    var v = _obs.Settings.Voice;
                    string lobby = Arg("lobby");
                    double Num(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var n)
                        ? n.ValueKind == JsonValueKind.Number ? n.GetDouble() : double.TryParse(n.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0 : 0;
                    string said;
                    bool duck = false;
                    switch (Arg("action"))
                    {
                        case "mute":
                            v.MuteAll = Arg("on").Length > 0 ? Arg("on") == "true" : !v.MuteAll;
                            said = v.MuteAll ? "Every lobby voice muted." : "Lobby voice back on: it follows the picture.";
                            break;
                        case "pin":
                            v.Pin = lobby == v.Pin ? "" : lobby;
                            said = v.Pin.Length > 0 ? $"{v.Pin}'s voice stays up whatever is on screen." : "Lobby voice follows the picture again.";
                            break;
                        case "volume":
                            v.Volume[lobby] = Math.Round(Math.Max(-30, Math.Min(12, Num("value"))), 1);
                            said = $"{lobby} voice {v.Volume[lobby]:+0.#;-0.#;0} dB.";
                            break;
                        case "offset":
                            v.Offset[lobby] = (int)Math.Max(0, Math.Min(5000, Num("value")));
                            said = $"{lobby} voice {v.Offset[lobby]} ms later.";
                            break;
                        case "duck":
                            v.DuckUnder = Arg("name").Trim();
                            duck = true;
                            said = v.DuckUnder.Length > 0 ? $"Lobby voice ducks under {v.DuckUnder}." : "No ducking.";
                            break;
                        case "on":
                            v.On = Arg("on") == "true";
                            said = v.On ? "Lobby voice on stream." : "Lobby voice off stream.";
                            break;
                        default: return Ok(new { ok = false, message = "Unknown voice action." });
                    }
                    await _obs.VoiceChangedAsync(duck).ConfigureAwait(false);
                    return Ok(new { ok = true, message = said });
                }
                case ("POST", "/app/admin/intermission"):
                {
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    switch (Arg("action"))
                    {
                        case "on": _desk.ShowIntermission(); return Ok(new { ok = true, message = "Intermission on stream." });
                        case "off": { var air = _desk.EndIntermission(); return Ok(new { ok = true, message = $"Back to {string.Join(", ", air.Slots.Where(x => x != null))}." }); }
                        case "toggle":
                            if (_desk.OnAir.Layout == "intermission") { var air = _desk.EndIntermission(); return Ok(new { ok = true, message = $"Back to {string.Join(", ", air.Slots.Where(x => x != null))}." }); }
                            _desk.ShowIntermission();
                            return Ok(new { ok = true, message = "Intermission on stream." });
                        case "auto":
                            _desk.AutoIntermission = Arg("on") == "true";
                            return Ok(new { ok = true, message = _desk.AutoIntermission ? "Auto intermission: on between rounds, back when a lobby starts." : "Auto intermission off." });
                        case "dismiss": _desk.DismissIntermissionOffer(); return Ok(new { ok = true, message = "OK, not now." });
                        case "next":
                        {
                            // "20" (minutes from now), "19:30" (today, local time), or "" to clear.
                            string v = Arg("value").Trim();
                            if (v.Length == 0) { _desk.NextRoundAt = null; return Ok(new { ok = true, message = "Countdown cleared." }); }
                            DateTime at;
                            if (int.TryParse(v, out int mins)) at = DateTime.UtcNow.AddMinutes(mins);
                            else if (TimeSpan.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var tod))
                            {
                                var local = DateTime.Now.Date + tod;
                                if (local < DateTime.Now) local = local.AddDays(1);
                                at = local.ToUniversalTime();
                            }
                            else return Ok(new { ok = false, message = "Type minutes (20) or a time (19:30)." });
                            _desk.NextRoundAt = at;
                            return Ok(new { ok = true, message = $"Next round at {at.ToLocalTime():HH:mm}." });
                        }
                        case "montage":
                        {
                            var q = _desk.QueuedMontage;
                            if (q == null || _montages == null) return Ok(new { ok = false, message = "No montage waiting." });
                            return Ok(new { ok = true, message = await _montages.PlayAsync(q.Value.Id).ConfigureAwait(false) });
                        }
                        default: return Ok(new { ok = false, message = "Unknown intermission action." });
                    }
                }
                case ("POST", "/app/admin/card"):
                {
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    if (Arg("action") == "hide") { _desk.HidePlayerCard(); return Ok(new { ok = true, message = "Card off." }); }
                    if (Arg("key").Length == 0) return Ok(new { ok = false, message = "Which player?" });
                    if (_broadcast != null && !_broadcast.Settings.Current.Elements.GetValueOrDefault("playerCards")) _broadcast.Settings.Set("playerCards", true);
                    string said = _desk.ShowPlayerCard(Arg("key"), Arg("lobby").Length > 0 ? Arg("lobby") : null);
                    return Ok(new { ok = !said.StartsWith("Skipped", StringComparison.Ordinal), message = said });
                }
                case ("POST", "/app/admin/wins"):
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    _desk.WinScope = Arg("scope") == "round" ? "round" : "today";
                    return Ok(new { ok = true, message = _desk.WinScope == "round" ? "Win counter: this round." : "Win counter: today." });
                case ("POST", "/app/admin/autogrid"):
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    _desk.AutoGrid = Arg("on") == "true";
                    return Ok(new { ok = true, message = _desk.AutoGrid ? "Auto grid on: the grid comes up whenever no lobby is mid-game." : "Auto grid off." });
                case ("POST", "/app/admin/sim"):
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    _desk.Simulate(Arg("on") == "true");
                    return Ok(new { ok = true, message = _desk.Simulating ? "Simulation on: four fake lobbies are playing." : "Simulation off." });
                case ("GET", "/app/sendinfo"):
                {
                    string? push = null;
                    if (GamePath != null)
                    {
                        string? status = await _mod.StatusAsync(GamePath).ConfigureAwait(false);
                        if (status != null && JsonDocument.Parse(status).RootElement.TryGetProperty("feed", out var feed)
                            && feed.TryGetProperty("pushUrl", out var pu) && pu.ValueKind == JsonValueKind.String) push = pu.GetString();
                    }
                    return Ok(new { pushUrl = push != null && push.StartsWith(TournamentSession.VdoNinja, StringComparison.Ordinal) ? push : null });
                }
                case ("GET", "/app/voice/pcm"):
                    // The mix of Discord's and Among Us's sound since the last call, for the send page (never played here).
                    if (_settings.VoiceOff) return (200, "application/octet-stream", Array.Empty<byte>());
                    return (200, "application/octet-stream", VoiceNow().Read());
                case ("GET", "/app/voice"):
                {
                    var v = VoiceNow();
                    return Ok(new { on = !_settings.VoiceOff, mic = _settings.VoiceIncludeMic, state = v.State() });
                }
                case ("POST", "/app/voice"):
                {
                    var v = VoiceNow();
                    double Level(string name, double was) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? Math.Max(0, Math.Min(1.5, n.GetDouble())) : was;
                    _settings.VoiceLevel = v.VoiceLevel = Level("voiceLevel", _settings.VoiceLevel);
                    _settings.GameSoundLevel = v.GameLevel = Level("gameLevel", _settings.GameSoundLevel);
                    if (Arg("mic").Length > 0) _settings.VoiceIncludeMic = Arg("mic") == "true";
                    if (Arg("on").Length > 0)
                    {
                        _settings.VoiceOff = Arg("on") != "true";
                        if (_settings.VoiceOff) v.Stop(); else v.Start();
                    }
                    TrySave();
                    return Ok(new { ok = true });
                }
                case ("GET", "/app/sendfeed"):
                {
                    string? feed = GamePath == null ? null : await _mod.FeedAsync(GamePath, long.TryParse(HttpRequest.Query(query, "since"), out var since) ? since : 0).ConfigureAwait(false);
                    return Text(200, "application/json", feed ?? "{\"last\":-1,\"items\":[]}");
                }
                case ("POST", "/app/admin/command"):
                    if (_organizer == null) return Ok(new { ok = false, message = "Administration is locked." });
                    return Ok(new { ok = true, message = await _organizer.CommandAsync(Arg("text")).ConfigureAwait(false) });
                case ("POST", "/app/update"):
                    if (!AppUpdateAvailable) return Ok(new { ok = false, message = "The Button is up to date." });
                    _ = Task.Run(UpdateAppAsync);
                    return Ok(new { ok = true, message = "Downloading the new version of The Button…" });
                case ("POST", "/app/restart"):
                    if (_appReady == null) return Ok(new { ok = false, message = "No update is waiting." });
                    _ = Task.Run(async () => { await Task.Delay(300).ConfigureAwait(false); _env.Restart(); });
                    return Ok(new { ok = true, message = "Restarting…" });
                case ("POST", "/app/modauto"):
                    _settings.AutoUpdateMod = Arg("on") == "true";
                    TrySave();
                    _nextAutoModUpdate = DateTime.MinValue;
                    return Ok(new { ok = true, message = _settings.AutoUpdateMod ? "The mod updates itself while Among Us is closed." : "Automatic mod updates are off: Settings shows when a new version is out." });
                case ("POST", "/app/autoupdate"):
                    _settings.AutoUpdateApp = Arg("on") == "true";
                    TrySave();
                    if (_settings.AutoUpdateApp && AppUpdateAvailable) _ = Task.Run(UpdateAppAsync);
                    return Ok(new { ok = true, message = _settings.AutoUpdateApp ? "The Button updates itself." : "Automatic updates are off: Settings shows when a new version is out." });
                default: return Text(404, "application/json", "{\"error\":\"not found\"}");
            }
        }

        // ---- State ------------------------------------------------------------------------

        private async Task<object> StateAsync()
        {
            if (DateTime.UtcNow - _latestChecked > TimeSpan.FromMinutes(20))
            {
                _latestChecked = DateTime.UtcNow;
                _ = Task.Run(async () =>
                {
                    _latest = await _installer.LatestAsync().ConfigureAwait(false) ?? _latest;
                    if (_settings.AutoUpdateApp && AppUpdateAvailable) await UpdateAppAsync().ConfigureAwait(false);
                });
            }
            var mod = ModInstaller.State(GamePath);
            AutoRepair(mod);
            AutoUpdateMod(mod);
            string? status = mod.Installed && GamePath != null ? await _mod.StatusAsync(GamePath).ConfigureAwait(false) : null;
            return new
            {
                App = _env.Version,
                Game = new { Path = GamePath, Found = mod.GameFound, Candidates = Candidates() },
                Mod = new
                {
                    mod.Installed, mod.InstalledVersion, mod.GameArch, mod.LoaderArch, mod.LoaderMatchesGame,
                    Latest = _latest?.Tag,
                    UpdateAvailable = ModUpdateAvailable(mod),
                    Auto = _settings.AutoUpdateMod,
                    Installing = _installing,
                    InstallResult = _installResult,
                },
                Setup = SetupView(),
                Admin = _organizer == null ? null : new { _organizer.Tournament },
                AppUpdate = new
                {
                    Supported = _env.ExePath != null,
                    Auto = _settings.AutoUpdateApp,
                    Available = AppUpdateAvailable ? _latest!.Tag : null,
                    Ready = _appReady,
                    Busy = _appUpdating,
                    Error = _appUpdateError,
                },
                Connected = status != null,
                Status = status == null ? (JsonElement?)null : JsonDocument.Parse(status).RootElement,
            };
        }

        /// <summary>"v1.3.0" is newer than "v1.2.1" (or than nothing recorded).</summary>
        public static bool Newer(string latest, string? installed)
        {
            static Version? V(string? tag) => Version.TryParse((tag ?? "").TrimStart('v', 'V'), out var v) ? v : null;
            var l = V(latest);
            var i = V(installed);
            return l != null && (i == null || l > i);
        }

        /// <summary>
        /// Keeps The Button's copy of the setup code and the one in the Among Us folder the same:
        /// a code put in some other way is copied here, and one that went missing (the mod
        /// reinstalled, Among Us reinstalled or its files checked) is put back.
        /// </summary>
        private void KeepSetupCode()
        {
            if (GamePath == null) return;
            string dir = ModInstaller.DataDir(GamePath);
            string file = Path.Combine(dir, SetupCode.FileName);
            try
            {
                if (File.Exists(file))
                {
                    string text = File.ReadAllText(file).Trim();
                    if (text != _settings.SetupCode && SetupCode.TryParse(text, out var code, out _) && !code.IsAdmin)
                    {
                        _settings.SetupCode = text;
                        TrySave();
                    }
                }
                else if (_settings.SetupCode != null && SetupCode.TryParse(_settings.SetupCode, out _, out _))
                {
                    SetupCode.Save(dir, _settings.SetupCode);
                    string game = GamePath;
                    _ = Task.Run(() => _mod.CommandAsync(game, "setup reload"));
                }
            }
            catch (Exception) { }
        }

        private object? SetupView()
        {
            if (GamePath == null) return null;
            KeepSetupCode();
            string file = Path.Combine(ModInstaller.DataDir(GamePath), SetupCode.FileName);
            if (!File.Exists(file)) return null;
            if (!SetupCode.TryParse(File.ReadAllText(file), out var code, out var error)) return new { Error = error };
            return new
            {
                Description = code.Describe(),
                Kind = code.IsTournament ? "Tournament host" : "Preliminary",
                code.TournamentName, code.TournamentId, code.Server,
                Bots = code.BotTokens?.Count ?? 0, code.GuildId, code.ResultsChannelId,
                PrelimChannels = code.PrelimChannelIds?.Count ?? 0,
                code.AdvanceCount, code.GamesPerRound,
                Rotation = code.ImpostorRotation == true, Lead = code.Lead == true,
                Settings = code.Lobby == null ? null : new
                {
                    code.Lobby.Impostors, code.Lobby.KillCooldown, code.Lobby.KillDistance, code.Lobby.PlayerSpeed,
                    code.Lobby.CrewmateVision, code.Lobby.ImpostorVision, code.Lobby.EmergencyMeetings, code.Lobby.EmergencyCooldown,
                    code.Lobby.DiscussionTime, code.Lobby.VotingTime, code.Lobby.ConfirmEjects, code.Lobby.AnonymousVotes,
                    code.Lobby.VisualTasks, code.Lobby.CommonTasks, code.Lobby.LongTasks, code.Lobby.ShortTasks, code.Lobby.RolesOff,
                },
            };
        }

        // ---- Actions ----------------------------------------------------------------------

        private object SetGame(string path)
        {
            path = path.Trim().Trim('"');
            if (path.EndsWith(GameLocator.Exe, StringComparison.OrdinalIgnoreCase)) path = Path.GetDirectoryName(path) ?? path;
            if (!GameLocator.IsGameFolder(path)) return new { ok = false, message = "That folder doesn't have Among Us.exe in it. In Steam: right-click Among Us › Manage › Browse local files, then copy that folder's path." };
            _settings.GamePath = path;
            TrySave();
            return new { ok = true, message = "Found Among Us." };
        }

        private DateTime _nextAutoRepair = DateTime.MinValue, _nextAutoModUpdate = DateTime.MinValue;

        private bool ModUpdateAvailable(ModState mod) => _latest != null && mod.Installed && Newer(_latest.Tag, mod.InstalledVersion);

        /// <summary>A new version of the mod is out: install it by itself once Among Us is closed (unless switched off).</summary>
        private void AutoUpdateMod(ModState mod)
        {
            if (!_settings.AutoUpdateMod || !ModUpdateAvailable(mod) || !mod.LoaderMatchesGame || GamePath == null || _installing.Length > 0) return;
            if (DateTime.UtcNow < _nextAutoModUpdate || ModInstaller.GameRunning()) return;
            _nextAutoModUpdate = DateTime.UtcNow + AutoRepairRetry;
            StartInstall(update: true);
        }
        /// <summary>How long to wait before trying an automatic repair again after one didn't work.</summary>
        public static readonly TimeSpan AutoRepairRetry = TimeSpan.FromMinutes(15);

        /// <summary>
        /// The mod is installed with a loader the game can't use (the 32-bit one left from before
        /// Among Us went 64-bit): put the right one in by itself, once Among Us is closed.
        /// </summary>
        private void AutoRepair(ModState mod)
        {
            if (!mod.Installed || mod.LoaderMatchesGame || GamePath == null || _installing.Length > 0) return;
            if (DateTime.UtcNow < _nextAutoRepair || ModInstaller.GameRunning()) return;
            _nextAutoRepair = DateTime.UtcNow + AutoRepairRetry;
            StartInstall(repair: true);
        }

        private object StartInstall(bool repair = false, bool update = false)
        {
            if (_installing.Length > 0) return new { ok = false, message = "Already installing." };
            if (GamePath == null) return new { ok = false, message = "Find Among Us first." };
            string game = GamePath;
            _installing = repair ? "Repairing the mod loader (Among Us needs the 64-bit one)…" : update ? "Updating the mod by itself…" : "Checking for the latest version…";
            _installResult = "";
            _ = Task.Run(async () =>
            {
                try
                {
                    var release = await _installer.LatestAsync().ConfigureAwait(false);
                    if (release == null) { _installResult = "Couldn't reach GitHub to download the mod. Check your internet connection."; return; }
                    _latest = release;
                    string error = await _installer.InstallAsync(game, release, m => _installing = m).ConfigureAwait(false);
                    _installResult = error.Length > 0 ? (repair ? "Couldn't repair the mod loader by itself: " + error + " Press Repair to try again."
                            : update ? "Couldn't update the mod by itself: " + error + " It tries again in a while, or press Update." : error)
                        : update ? $"Updated the mod to {release.Tag} by itself."
                        : repair ? $"Repaired the mod loader by itself: {release.Tag} with the 64-bit loader. The next Among Us start takes a few minutes while BepInEx sets itself up."
                        : $"Installed {release.Tag}. Start Among Us: the first start takes a few minutes while BepInEx sets itself up.";
                    KeepSetupCode();
                }
                finally { _installing = ""; }
            });
            return new { ok = true, message = "Installing…" };
        }

        private async Task<object> SaveSetupAsync(string text)
        {
            if (GamePath == null) return new { ok = false, message = "Find Among Us first." };
            if (!SetupCode.TryParse(text, out var code, out var error)) return new { ok = false, message = error };
            // The organiser's own code unlocks the Organiser tab; nobody else sees that it exists.
            if (code.IsAdmin)
            {
                SetAdminCode(text);
                return new { ok = true, admin = true, message = $"Organiser view unlocked for {code.TournamentName}." };
            }
            SetupCode.Save(ModInstaller.DataDir(GamePath), code.Encode());
            _settings.SetupCode = code.Encode();
            TrySave();
            bool live = await _mod.CommandAsync(GamePath, "setup reload").ConfigureAwait(false) != null;
            return new { ok = true, message = $"Setup saved: {code.Describe()}." + (live ? " The mod in Among Us picked it up." : " It's used next time Among Us starts.") };
        }

        private async Task<object> ClearSetupAsync()
        {
            if (GamePath == null) return new { ok = false, message = "Find Among Us first." };
            SetupCode.Clear(ModInstaller.DataDir(GamePath));
            _settings.SetupCode = null;
            TrySave();
            await _mod.CommandAsync(GamePath, "setup reload").ConfigureAwait(false);
            return new { ok = true, message = "Setup code removed: the mod uses its settings file." };
        }

        /// <summary>
        /// "Send my game to the caster": switches it in the mod, then opens the private VDO.Ninja
        /// page in the host's browser, where they pick the Among Us window.
        /// </summary>
        private async Task<object> FeedAsync(bool on)
        {
            if (GamePath == null) return new { ok = false, replies = new[] { "Find Among Us first." } };
            string? answer = await _mod.CommandAsync(GamePath, on ? "feed on" : "feed off").ConfigureAwait(false);
            if (answer == null) return new { ok = false, replies = new[] { "Among Us isn't running with the mod, so that can't be done right now." } };
            if (on)
            {
                string? status = await _mod.StatusAsync(GamePath).ConfigureAwait(false);
                string? url = null;
                if (status != null && JsonDocument.Parse(status).RootElement.TryGetProperty("feed", out var feed)
                    && feed.TryGetProperty("pushUrl", out var push) && push.ValueKind == JsonValueKind.String)
                    url = push.GetString();
                // The Button's own page shares the screen through VDO.Ninja and sends the lobby's live data with it.
                if (url != null && url.StartsWith(TournamentSession.VdoNinja, StringComparison.Ordinal))
                    try { _env.Open($"{Url}send?token={Token}"); } catch (Exception) { }
            }
            return JsonDocument.Parse(answer).RootElement;
        }

        private async Task<object> CommandAsync(string command)
        {
            if (GamePath == null || command.Trim().Length == 0) return new { ok = false, replies = new[] { "Find Among Us first." } };
            string? answer = await _mod.CommandAsync(GamePath, command).ConfigureAwait(false);
            if (answer == null) return new { ok = false, replies = new[] { "Among Us isn't running with the mod, so that can't be done right now." } };
            return JsonDocument.Parse(answer).RootElement;
        }

        private List<FileInfo> ReplayFiles(int max = 50)
        {
            var folders = new List<string> { _env.Downloads };
            if (GamePath != null) folders.Add(Path.Combine(ModInstaller.DataDir(GamePath), "games"));
            return ReplayLibrary.Find(folders, max);
        }

        private object Replays() => ReplayFiles().Select(f => new
        {
            f.Name, Label = ReplayLibrary.Label(f), When = f.LastWriteTimeUtc.ToString("o"), Size = f.Length,
            Downloaded = f.FullName.StartsWith(_env.Downloads, StringComparison.OrdinalIgnoreCase),
        }).ToList();

        /// <summary>
        /// The Games page: this PC's games (newest first) from the saved game files, each with its
        /// replay when there is one, plus replays from elsewhere (downloaded from another lobby).
        /// Read from the files, so it works with Among Us closed.
        /// </summary>
        private object Games()
        {
            var replays = ReplayFiles(200);
            var byName = replays.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var games = new List<GameRecord>();
            if (GamePath != null)
            {
                try
                {
                    var dir = new DirectoryInfo(Path.Combine(ModInstaller.DataDir(GamePath), "games"));
                    if (dir.Exists)
                        foreach (var f in dir.EnumerateFiles("game-*.json", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).Take(60))
                        {
                            try { if (JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(f.FullName)) is GameRecord g) games.Add(g); }
                            catch (Exception) { /* a damaged file */ }
                        }
                }
                catch (Exception) { }
            }
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = games.OrderByDescending(g => g.EndedUtc ?? g.StartedUtc).Select(g =>
            {
                string file = ReplayRecorder.FileNameFor(g);
                bool hasReplay = byName.ContainsKey(file);
                if (hasReplay) used.Add(file);
                var mvp = g.Counted ? g.Players.OrderByDescending(p => p.Points).FirstOrDefault() : null;
                return new
                {
                    g.Id, g.Name, g.Round, g.Map, g.Winner, g.EndReason,
                    When = (g.EndedUtc ?? g.StartedUtc).ToString("o"),
                    Minutes = g.EndedUtc == null ? (double?)null : Math.Round((g.EndedUtc.Value - g.StartedUtc).TotalMinutes, 1),
                    g.Voided, g.VoidReason, Players = g.Players.Count,
                    Mvp = mvp == null ? null : new { mvp.Name, Color = mvp.ColorId, Points = Math.Round(mvp.Points, 2) },
                    Replay = hasReplay ? file : null,
                };
            }).ToList();
            var other = replays.Where(f => !used.Contains(f.Name)).Select(f => new
            {
                f.Name, Label = ReplayLibrary.Label(f), When = f.LastWriteTimeUtc.ToString("o"),
                Downloaded = f.FullName.StartsWith(_env.Downloads, StringComparison.OrdinalIgnoreCase),
            }).Take(30).ToList();
            return new { Games = list, Other = other };
        }

        private object Open(string what)
        {
            string? target = what switch
            {
                "game" => GamePath,
                "data" => GamePath == null ? null : ModInstaller.DataDir(GamePath),
                "log" => GamePath == null ? null : Path.Combine(GamePath, "BepInEx", "LogOutput.log"),
                "config" => GamePath == null ? null : Path.Combine(GamePath, "BepInEx", "config", "com.ljbutton.tournamenttracker.cfg"),
                "overlay" => "http://localhost:8765/",
                "releases" => $"https://github.com/{ModInstaller.Repo}/releases/latest",
                "generator" => Url + "generator",
                _ => null,
            };
            if (target == null) return new { ok = false };
            try { _env.Open(target); return new { ok = true }; }
            catch (Exception e) { return new { ok = false, message = e.Message }; }
        }

        private static string Resource(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) return "";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static byte[] ResourceBytes(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null) return Array.Empty<byte>();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _organizer?.Dispose();
            _desk?.Dispose();
            _replays?.Dispose();
            _voice?.Dispose();
            if (_obs != null) try { _obs.DisposeAsync().AsTask().Wait(1500); } catch (Exception) { }
            try { _listener.Stop(); } catch (Exception) { }
        }
    }
}
