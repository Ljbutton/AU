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

        /// <summary>The host's own bots and channels (Settings → Your Discord), checked with Discord when saved. Copied to the Among Us folder for the mod.</summary>
        public HostDiscord? HostDiscord { get; set; }
        public List<string> HostBotNames { get; set; } = new List<string>();
        public string HostPublicName { get; set; } = "";
        public string HostStatusName { get; set; } = "";

        /// <summary>Lobby voice for the broadcast (when sending the game to the caster): levels, and whether the referee's own microphone goes in.</summary>
        public double VoiceLevel { get; set; } = 1.0;
        public double GameSoundLevel { get; set; } = 0.5;
        public bool VoiceIncludeMic { get; set; }
        public bool VoiceOff { get; set; }

        /// <summary>BepInEx's console window (the mod's log) shows when Among Us starts. Off: no window; the log file is still written.</summary>
        public bool ShowModConsole { get; set; }

        /// <summary>The host's Twitch channel: shown on the tournament stream with their lobby (sent with their game).</summary>
        public string? Twitch { get; set; }

        /// <summary>
        /// The Button's own token and port, kept across restarts (an update, say), so an open "Send my game
        /// to the caster" tab keeps reaching it. Made the first time.
        /// </summary>
        public string? AppToken { get; set; }
        public int AppPort { get; set; }

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

        /// <summary>Starts the new version and closes this one.</summary>
        public Action Restart { get; set; } = () => { };

        /// <summary>Connects the Discord bot (a pretend one in tests). Null: the real gateway.</summary>
        public Func<BotConfig, TournamentTracker.Discord.IBotGateway>? Bots { get; set; }
    }

    /// <summary>
    /// The app's screens and everything behind them, served on this computer only. Every
    /// /app/ call needs the per-launch token the page was given, so no website can drive it.
    /// </summary>
    public sealed class AppServer : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly AppEnvironment _env;
        private readonly AppSettings _settings;
        private readonly ModInstaller _installer;
        private readonly ModClient _mod;
        private readonly HttpClient _http;
        private Release? _latest;
        private Organizer? _organizer;
        private Voice.VoiceCapture? _voice;
        private readonly DiscordBridge _bridge;

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
            if (_settings.AdminCode != null && SetupCode.TryParse(_settings.AdminCode, out var code, out _) && code.IsAdmin)
                _organizer = new Organizer(code, _http);
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
            // The same token and port as last time when possible, so the send page (open in the browser)
            // keeps working after The Button restarts.
            if (string.IsNullOrEmpty(_settings.AppToken) || _settings.AppToken!.Length < 16)
            {
                var bytes = new byte[18];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                _settings.AppToken = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
            }
            Token = _settings.AppToken!;
            _listener = Listen(_settings.AppPort);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            if (_settings.AppPort != Port) _settings.AppPort = Port;
            TrySave();
            Task.Run(AcceptLoop);
            StartOrganizer();
            // The bot stays online while The Button is open; the mod reaches Discord through it.
            _bridge = new DiscordBridge(() => DiscordBridge.FromSetupCode(_settings.SetupCode, _settings.HostDiscord),
                () => GamePath == null ? null : ModInstaller.DataDir(GamePath), connect: env.Bots);
        }

        public string? GamePath => _settings.GamePath;

        /// <summary>The port used last time if it's free, else any.</summary>
        private static TcpListener Listen(int port)
        {
            if (port > 0 && port < 65536)
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                try { l.Start(); return l; }
                catch (SocketException) { }
            }
            var any = new TcpListener(IPAddress.Loopback, 0);
            any.Start();
            return any;
        }

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
            if (method == "GET" && route == "/send")
                return Text(200, "text/html; charset=utf-8", SendPage.Html);
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
                case ("POST", "/app/open"): return Ok(Open(Arg("what")));
                case ("POST", "/app/feed"): return Ok(await FeedAsync(Arg("on") == "true").ConfigureAwait(false));
                case ("POST", "/app/admin/code"): return Ok(SetAdminCode(Arg("code")));
                case ("GET", "/app/admin"): return _organizer == null ? Text(404, "application/json", "{\"error\":\"locked\"}") : Ok(_organizer.State());
                case ("POST", "/app/names"):
                {
                    // From the caster, over VDO.Ninja (this host's send page): roster names for this lobby's players.
                    if (GamePath == null) return Ok(new { ok = false });
                    string json = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("names", out var nm) && nm.ValueKind == JsonValueKind.Object ? nm.GetRawText() : "{}";
                    bool sent = await _mod.NamesAsync(GamePath, json).ConfigureAwait(false) != null;
                    return Ok(new { ok = sent });
                }
                case ("GET", "/app/sendinfo"):
                {
                    string? push = null;
                    bool cam = false;
                    if (GamePath != null)
                    {
                        string? status = await _mod.StatusAsync(GamePath).ConfigureAwait(false);
                        if (status != null && JsonDocument.Parse(status).RootElement.TryGetProperty("feed", out var feed))
                        {
                            if (feed.TryGetProperty("pushUrl", out var pu) && pu.ValueKind == JsonValueKind.String) push = pu.GetString();
                            cam = feed.TryGetProperty("cam", out var c) && c.ValueKind == JsonValueKind.True;
                        }
                    }
                    return Ok(new { pushUrl = push != null && push.StartsWith(TournamentSession.VdoNinja, StringComparison.Ordinal) ? push : null, twitch = _settings.Twitch, cam });
                }
                case ("GET", "/app/cam"):
                {
                    // The player camera's next picture, for the send page (which sends it to the caster).
                    byte[]? frame = GamePath == null ? null
                        : await _mod.PlayerCamAsync(GamePath, long.TryParse(HttpRequest.Query(query, "after"), out var after) ? after : 0).ConfigureAwait(false);
                    return frame == null ? (204, "application/octet-stream", Array.Empty<byte>()) : (200, "application/octet-stream", frame);
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
                case ("POST", "/app/updateall"): return Ok(UpdateAll());
                case ("POST", "/app/restart"):
                    if (_appReady == null) return Ok(new { ok = false, message = "No update is waiting." });
                    _ = Task.Run(async () => { await Task.Delay(300).ConfigureAwait(false); _env.Restart(); });
                    return Ok(new { ok = true, message = "Restarting…" });
                case ("POST", "/app/modauto"):
                    _settings.AutoUpdateMod = Arg("on") == "true";
                    TrySave();
                    _nextAutoModUpdate = DateTime.MinValue;
                    return Ok(new { ok = true, message = _settings.AutoUpdateMod ? "The mod updates itself while Among Us is closed." : "Automatic mod updates are off: Settings shows when a new version is out." });
                case ("POST", "/app/modconsole"):
                {
                    _settings.ShowModConsole = Arg("on") == "true";
                    _settings.Save(_env.SettingsFile);
                    if (GamePath != null) try { ModInstaller.SetConsole(GamePath, _settings.ShowModConsole); } catch (Exception) { }
                    return Ok(new { ok = true, message = _settings.ShowModConsole ? "The mod's console window shows from the next start of Among Us." : "No console window from the next start of Among Us (the log is still in Settings → Mod and updates → Open log)." });
                }
                case ("POST", "/app/hostdiscord"): return Ok(await SaveHostDiscordAsync(input).ConfigureAwait(false));
                case ("POST", "/app/twitch"):
                {
                    string typed = Arg("name").Trim();
                    string? handle = TournamentTracker.Broadcast.FeedProtocol.TwitchHandle(typed);
                    if (typed.Length > 0 && handle == null) return Ok(new { ok = false, message = "That isn't a Twitch channel name (letters, numbers and _, like your twitch.tv/ link)." });
                    _settings.Twitch = handle;
                    _settings.Save(_env.SettingsFile);
                    return Ok(new { ok = true, message = handle == null ? "Twitch channel removed." : $"Your Twitch: {handle}. It shows on the tournament stream with your lobby." });
                }
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
            KeepConsoleSetting(mod);
            string? status = mod.Installed && GamePath != null ? await _mod.StatusAsync(GamePath).ConfigureAwait(false) : null;
            return new
            {
                App = _env.Version,
                Twitch = _settings.Twitch,
                ModConsole = _settings.ShowModConsole,
                Game = new { Path = GamePath, Found = mod.GameFound, Candidates = Candidates() },
                Mod = new
                {
                    mod.Installed, mod.InstalledVersion, mod.GameArch, mod.LoaderArch, mod.LoaderMatchesGame,
                    Latest = _latest?.Tag,
                    UpdateAvailable = ModUpdateAvailable(mod),
                    Auto = _settings.AutoUpdateMod,
                    Installing = _installing,
                    InstallResult = _installResult,
                    Pending = _modWanted,
                },
                Setup = SetupView(),
                HostDiscord = HostDiscordView(),
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
                Bot = _bridge.Status(),
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
                    _settings.HostDiscord?.Save(dir);
                    string game = GamePath;
                    _ = Task.Run(() => _mod.CommandAsync(game, "setup reload"));
                }
                if (_settings.HostDiscord != null && !_settings.HostDiscord.IsEmpty && !File.Exists(Path.Combine(dir, HostDiscord.FileName)))
                    _settings.HostDiscord.Save(dir);
            }
            catch (Exception) { }
        }

        /// <summary>The host's Discord for Settings: names and the end of each token, never the tokens.</summary>
        private object HostDiscordView()
        {
            var h = _settings.HostDiscord ?? new HostDiscord();
            string codeGuild = SetupCode.TryParse(_settings.SetupCode, out var code, out _) ? code.GuildId ?? "" : "";
            return new
            {
                h.GuildId,
                CodeGuild = codeGuild,
                Bots = h.BotTokens.Select((t, i) => new { Name = i < _settings.HostBotNames.Count ? _settings.HostBotNames[i] : $"Bot {i + 1}", Tail = t.Length > 4 ? t.Substring(t.Length - 4) : "" }).ToList(),
                Public = h.PublicWebhook.Length > 0 ? (_settings.HostPublicName.Length > 0 ? _settings.HostPublicName : "set") : "",
                Status = h.StatusWebhook.Length > 0 ? (_settings.HostStatusName.Length > 0 ? _settings.HostStatusName : "set") : "",
                CodeBots = code?.BotTokens?.Count ?? 0,
                Prelim = code != null && !code.IsTournament && !code.IsAdmin,
            };
        }

        private async Task<object> SaveHostDiscordAsync(JsonElement input)
        {
            string? Field(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var typed = new HostDiscord
            {
                GuildId = Field("guild") ?? _settings.HostDiscord?.GuildId ?? "",
                BotTokens = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToList() : new List<string>(),
                PublicWebhook = Field("public")!,
                StatusWebhook = Field("status")!,
            };
            var remove = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("remove", out var rm) && rm.ValueKind == JsonValueKind.Array
                ? rm.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList() : new List<int>();
            string? codeGuild = SetupCode.TryParse(_settings.SetupCode, out var code, out _) ? code.GuildId : null;
            var result = await HostDiscordCheck.CheckAsync(_http, typed, _settings.HostDiscord, remove, codeGuild).ConfigureAwait(false);

            _settings.HostDiscord = result.Saved.IsEmpty ? null : result.Saved;
            _settings.HostBotNames = result.BotNames;
            _settings.HostPublicName = result.PublicName;
            _settings.HostStatusName = result.StatusName;
            TrySave();
            bool live = false;
            if (GamePath != null)
            {
                string dir = ModInstaller.DataDir(GamePath);
                try { result.Saved.Save(dir); } catch (Exception) { }
                live = await _mod.CommandAsync(GamePath, "setup reload").ConfigureAwait(false) != null;
            }
            int bots = result.Saved.BotTokens.Count;
            string saved = bots > 0 ? $"Saved: {string.Join(", ", result.BotNames)} online while The Button is open." : "Saved.";
            if (bots > 0 && _settings.SetupCode == null) saved += " Paste your setup code too: the bots start with it.";
            else if (live) saved += " The mod in Among Us uses it now (after the game, if one is running).";
            return new { ok = result.Ok, message = result.Ok ? saved : string.Join(" ", result.Problems) + (bots > 0 || result.Saved.PublicWebhook.Length > 0 ? " Everything else was saved." : "") };
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
                    code.Lobby.TaskBarMode, code.Lobby.GhostsDoTasks,
                    Roles = code.Lobby.Roles == null ? null : string.Join(", ", code.Lobby.Roles.Select(r => $"{LobbySettings.RoleName(r.Key)} {(r.Value.Length > 0 ? r.Value[0] : 0)} at {(r.Value.Length > 1 ? r.Value[1] : 0)}%")),
                    RoleOptions = code.Lobby.Options == null ? null : string.Join(", ", code.Lobby.Options.Select(o => $"{LobbySettings.OptionName(o.Key)} {o.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}")),
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

        /// <summary>Update was pressed while Among Us was running: the mod updates as soon as it's closed.</summary>
        private volatile bool _modWanted;

        /// <summary>
        /// Settings' one Update button: whatever is out, The Button and the mod. The mod's files are in
        /// use while Among Us runs, so then it waits for Among Us to close.
        /// </summary>
        private object UpdateAll()
        {
            var mod = ModInstaller.State(GamePath);
            bool app = AppUpdateAvailable, modNew = ModUpdateAvailable(mod) && mod.LoaderMatchesGame;
            if (!app && !modNew) return new { ok = false, message = "Everything is up to date." };
            if (app) _ = Task.Run(UpdateAppAsync);
            string modMessage = "";
            if (modNew)
            {
                if (ModInstaller.GameRunning()) { _modWanted = true; modMessage = "The mod updates as soon as Among Us is closed."; }
                else { StartInstall(); modMessage = "Updating the mod…"; }
            }
            return new { ok = true, message = (app ? "Downloading the new version of The Button (restart it when it's ready). " : "") + modMessage };
        }

        /// <summary>BepInEx's console window as set here (hidden by default): put back after installs, repairs and BepInEx's first run.</summary>
        private void KeepConsoleSetting(ModState mod)
        {
            if (!mod.Installed || GamePath == null || _installing.Length > 0) return;
            try { if (ModInstaller.ConsoleShown(GamePath) != _settings.ShowModConsole) ModInstaller.SetConsole(GamePath, _settings.ShowModConsole); }
            catch (Exception) { /* tried again on the next refresh */ }
        }

        /// <summary>A new version of the mod is out: install it by itself once Among Us is closed (unless switched off, and Update wasn't pressed).</summary>
        private void AutoUpdateMod(ModState mod)
        {
            if (!ModUpdateAvailable(mod)) _modWanted = false;
            if (_modWanted && mod.LoaderMatchesGame && GamePath != null && _installing.Length == 0 && !ModInstaller.GameRunning())
            {
                _modWanted = false;
                StartInstall();
                return;
            }
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
                "broadcast" => $"https://github.com/{ModInstaller.Repo}/releases?q={Uri.EscapeDataString("broadcast-v")}&expanded=true",
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
            _bridge.Dispose();
            _organizer?.Dispose();
            _voice?.Dispose();
            try { _listener.Stop(); } catch (Exception) { }
        }
    }
}
