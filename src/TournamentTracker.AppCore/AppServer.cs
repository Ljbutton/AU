using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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

        /// <summary>What the send page shares: "window" (the Among Us window, the default) or "screen" (the entire screen).</summary>
        public string ShareMode { get; set; } = SendPage.ShareWindow;

        /// <summary>Send the game at 720p, 30 fps, at most 2.5 Mbps: for a slow upload.</summary>
        public bool LowQuality { get; set; }

        /// <summary>
        /// The Button's own token and port, kept across restarts (an update, say), so an open "Send my game
        /// to the caster" tab keeps reaching it. Made the first time.
        /// </summary>
        public string? AppToken { get; set; }
        public int AppPort { get; set; }

        /// <summary>The saved settings, or the defaults. A file that can't be read is kept as app.json.corrupt-… (and <paramref name="warn"/> told) before the defaults are saved over it.</summary>
        public static AppSettings Load(string file, Action<string>? warn = null)
        {
            try { return SafeFile.ReadOrRecover(file, text => JsonSerializer.Deserialize<AppSettings>(text), warn) ?? new AppSettings(); }
            catch (Exception) { return new AppSettings(); }
        }

        public void Save(string file) => SafeFile.WriteAllText(file, JsonSerializer.Serialize(this));
    }

    /// <summary>What the Windows window needs to give the app: where things are, and how to open files and links.</summary>
    public sealed class AppEnvironment
    {
        public string SettingsFile { get; set; } = "";
        public string? SteamRoot { get; set; }
        public string EpicManifests { get; set; } = GameLocator.EpicManifests;
        public IReadOnlyList<string> Fallbacks { get; set; } = GameLocator.Fallbacks;
        public string Downloads { get; set; } = "";
        /// <summary>Where a support bundle is saved (Downloads when there's no Desktop).</summary>
        public string Desktop { get; set; } = "";
        /// <summary>Among Us's own log folder (…\AppData\LocalLow\Innersloth\Among Us), for the support bundle. Null: none.</summary>
        public string? UnityLogs { get; set; }
        /// <summary>Check my connection's servers, sizes and timings (shorter, and pretend servers, in tests).</summary>
        public ConnectionCheck.Options CheckOptions { get; set; } = new ConnectionCheck.Options();
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
        private readonly object _voiceLock = new object();
        /// <summary>the-button.log, next to the settings (the organiser's view writes to it too).</summary>
        private readonly FileLog _log;
        /// <summary>Every 10 s: the lobby sound stops when the send page went away, and the backup catches up.</summary>
        private readonly Timer _tick;
        private readonly DiscordBridge _bridge;
        /// <summary>The copy of the mod's games, links and rounds in The Button's own folder (null: no settings folder).</summary>
        private readonly ModBackup? _backup;
        /// <summary>Copies kept of damaged files lately (The Button's and the mod's), as the last look found them.</summary>
        private volatile List<DamagedFile> _damaged = new List<DamagedFile>();
        private string? _backupProblem;

        /// <summary>The send page last asked about the lobby sound this long ago (closed): capturing stops until it asks again.</summary>
        private static readonly TimeSpan VoiceIdle = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The lobby's voice and game sound for the send page: started the first time it asks, and
        /// again after it stopped because the page was closed.
        /// </summary>
        private Voice.VoiceCapture VoiceNow()
        {
            lock (_voiceLock)
            {
                _voice ??= new Voice.VoiceCapture { VoiceLevel = _settings.VoiceLevel, GameLevel = _settings.GameSoundLevel };
                _voice.Touch();
                if (!_settings.VoiceOff && !_voice.Running) _voice.Start();
                return _voice;
            }
        }

        /// <summary>Starts (or stops) the organiser's view for the saved administration code.</summary>
        private void StartOrganizer()
        {
            _organizer?.Dispose();
            _organizer = null;
            if (_settings.AdminCode != null && SetupCode.TryParse(_settings.AdminCode, out var code, out _) && code.IsAdmin)
                _organizer = new Organizer(code, _http, log: _log);
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
            _env.ExePath != null && _latest?.App != null && _appReady == null && AppUpdater.Newer(_latest.Tag, _env.Version);

        private async Task UpdateAppAsync()
        {
            var release = _latest;
            string? exe = _env.ExePath;
            if (_appUpdating || release?.App == null || exe == null) return;
            _appUpdating = true;
            _appUpdateError = null;
            try
            {
                string error = await AppUpdater.InstallAsync(_http, release.App, exe, log: _log).ConfigureAwait(false);
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
            _log = new FileLog(string.IsNullOrEmpty(env.SettingsFile) ? null : Path.GetDirectoryName(env.SettingsFile), "the-button.log");
            _settings = AppSettings.Load(env.SettingsFile, m => _log.Warn(m + " The Button started with its default settings (the setup code comes back from the Among Us folder)."));
            _installer = new ModInstaller(http, _log);
            _mod = new ModClient(http);
            if (!GameLocator.IsGameFolder(_settings.GamePath))
            {
                var found = Candidates().FirstOrDefault();
                if (found != null) { _settings.GamePath = found.Path; TrySave(); }
            }
            // The same token and port as last time when possible, so the send page (open in the browser)
            // keeps working after The Button restarts.
            if (string.IsNullOrEmpty(_settings.AppToken) || _settings.AppToken!.Length < 16)
                _settings.AppToken = LocalServer.NewToken();
            Token = _settings.AppToken!;
            _listener = LocalServer.Listen(_settings.AppPort);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            if (_settings.AppPort != Port) _settings.AppPort = Port;
            TrySave();
            Task.Run(() => LocalServer.Serve(_listener, _cts.Token, Route));
            StartOrganizer();
            if (AppFolder is { } folder) _backup = new ModBackup(Path.Combine(folder, ModBackup.FolderName));
            _tick = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            // The bot stays online while The Button is open; the mod reaches Discord through it.
            _bridge = new DiscordBridge(() => DiscordBridge.FromSetupCode(_settings.SetupCode, _settings.HostDiscord),
                () => GamePath == null ? null : ModInstaller.DataDir(GamePath), connect: env.Bots);
        }

        public string? GamePath => _settings.GamePath;

        /// <summary>
        /// The Among Us folder when it's really there, for anything that writes into it (null: none found, or the
        /// one found before is gone). An organiser's PC has none, and nothing is made in a folder that isn't there.
        /// </summary>
        private string? GameFolder => GameLocator.IsGameFolder(GamePath) ? GamePath : null;

        /// <summary>Where The Button keeps its settings, log and backup (%LOCALAPPDATA%\TheButton); null when it has none.</summary>
        private string? AppFolder => string.IsNullOrEmpty(_env.SettingsFile) ? null : Path.GetDirectoryName(_env.SettingsFile);

        /// <summary>Every 10 s, on the timer: the lobby sound, then the backup.</summary>
        private void Tick()
        {
            try { _voice?.StopIfIdle(VoiceIdle); } catch (Exception) { }
            try { BackupNow(); } catch (Exception) { }
        }

        // ---- The backup of the mod's data, and damaged files -----------------------------------------

        /// <summary>
        /// Brings the backup up to date with the mod's folder when anything there changed (a game saved or submitted,
        /// links or the waiting list changed), and looks for copies kept of damaged files. On the 10-second timer.
        /// </summary>
        internal void BackupNow()
        {
            string? data = GameFolder == null ? null : ModInstaller.DataDir(GameFolder);
            if (_backup != null)
            {
                _backup.Mirror(data);
                string? problem = _backup.State.Problem;
                if (problem != null && problem != _backupProblem) _log.Warn("Backup: " + problem);
                _backupProblem = problem;
            }
            _damaged = ModBackup.Damaged(AppFolder, data, DateTime.UtcNow);
        }

        private object? BackupView()
        {
            if (_backup == null) return null;
            var s = _backup.State;
            return new { s.Files, s.Missing, s.CopiedAt, s.Problem };
        }

        /// <summary>Settings → Help → Restore backup (and Home's notice): puts back what's missing or older in the mod's folder, with Among Us closed.</summary>
        private object RestoreBackup()
        {
            if (_backup == null) return new { ok = false, message = "There's no backup on this PC." };
            if (GameFolder == null) return new { ok = false, message = FindGameFirst };
            if (ModInstaller.GameRunning()) return new { ok = false, message = "Close Among Us first: the mod would save over what's put back." };
            var r = _backup.Restore(ModInstaller.DataDir(GameFolder));
            _log.Info("Restore backup: " + r.Message);
            BackupNow();
            return new { ok = r.Ok, message = r.Message };
        }

        /// <summary>Home's notice → Forget them: the games only the backup has were removed from the mod's folder on purpose.</summary>
        private object ForgetBackup()
        {
            if (_backup == null || GameFolder == null) return new { ok = false, message = "There's no backup on this PC." };
            int gone = _backup.Forget(ModInstaller.DataDir(GameFolder));
            _log.Info($"Backup: forgot {gone} game(s) that aren't in the mod's folder.");
            BackupNow();
            return new { ok = true, message = gone == 1 ? "The backup no longer keeps that game." : $"The backup no longer keeps those {gone} games." };
        }

        private List<GameInstall> Candidates() => GameLocator.Find(_env.SteamRoot, _env.EpicManifests, _env.Fallbacks);

        private readonly object _saveLock = new object();

        /// <summary>Saves the settings, one save at a time (requests arrive on several threads at once).</summary>
        private void TrySave()
        {
            lock (_saveLock)
                try { _settings.Save(_env.SettingsFile); } catch (Exception) { }
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
            if (method == "GET" && UiFiles.Asset(typeof(AppServer).Assembly, route) is { } asset) return (200, asset.Type, asset.Body);
            if (method == "GET" && route == "/logo.png") return (200, "image/png", ResourceBytes("ui/logo.png"));
            if (method == "GET" && UiFiles.Base(typeof(AppServer).Assembly, route) is { } shared) return (200, shared.Type, shared.Body);
            if (!route.StartsWith("/app/", StringComparison.Ordinal)) return Text(404, "text/plain", "Not found");

            headers.TryGetValue("x-app-token", out var token);
            if (token != Token && HttpRequest.Query(query, "token") != Token) return Text(401, "application/json", "{\"error\":\"bad token\"}");

            JsonElement input = default;
            if (body.Length > 0) { try { input = JsonDocument.Parse(body).RootElement; } catch (Exception) { } }
            string Arg(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            // A send page that was replaced (Open again) or switched off is told so, and closes itself down.
            if ((route is "/app/sendinfo" or "/app/sendfeed" or "/app/cam" or "/app/voice/pcm" or "/app/voice" or "/app/names" or "/app/command" or "/app/share") && StalePage(query))
                return Text(409, "application/json", "{\"error\":\"replaced\"}");

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
                    string json = Has(input, "names", JsonValueKind.Object, out var nm) ? nm.GetRawText() : "{}";
                    bool sent = await _mod.NamesAsync(GamePath, json).ConfigureAwait(false) != null;
                    return Ok(new { ok = sent });
                }
                case ("GET", "/app/sendinfo"):
                {
                    var status = GamePath == null ? null : await StatusElementAsync(GamePath).ConfigureAwait(false);
                    string? push = PushUrl(status);
                    bool cam = Feed(status) is JsonElement feed && Has(feed, "cam", JsonValueKind.True, out _);
                    // The send page shares through shareUrl (what to share, the quality) and reads its stream name and password from pushUrl.
                    return Ok(new
                    {
                        pushUrl = push,
                        shareUrl = push == null ? null : SendPage.ShareUrl(push, ShareMode, _settings.LowQuality),
                        share = new { mode = ShareMode, low = _settings.LowQuality },
                        twitch = _settings.Twitch,
                        cam,
                    });
                }
                case ("POST", "/app/share"): return Ok(SetShare(Arg("mode"), Arg("low")));
                case ("POST", "/app/conncheck"): return Ok(ConnectionCheckCommand(Arg("action")));
                case ("POST", "/app/support"): return Ok(await SaveSupportBundleAsync().ConfigureAwait(false));
                case ("POST", "/app/backup/restore"): return Ok(await Task.Run(RestoreBackup).ConfigureAwait(false));
                case ("POST", "/app/backup/forget"): return Ok(await Task.Run(ForgetBackup).ConfigureAwait(false));
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
                    return Ok(new { on = !_settings.VoiceOff, mic = _settings.VoiceIncludeMic, state = VoiceNow().State() });
                case ("POST", "/app/voice"):
                {
                    var v = VoiceNow();
                    double Level(string name, double was) => Has(input, name, JsonValueKind.Number, out var n) ? Math.Max(0, Math.Min(1.5, n.GetDouble())) : was;
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
                    // The send page is open and says whether VDO.Ninja is still sending its picture.
                    _sendSeen = DateTime.UtcNow;
                    _sendVideo = HttpRequest.Query(query, "video");
                    if (_sendVideo == "ok") _sendVideoOkAt = _sendSeen;
                    string? feed = GamePath == null ? null : await _mod.FeedAsync(GamePath, long.TryParse(HttpRequest.Query(query, "since"), out var since) ? since : 0).ConfigureAwait(false);
                    return Text(200, "application/json", feed ?? "{\"last\":-1,\"items\":[]}");
                }
                case ("POST", "/app/admin/command"):
                {
                    // One change from the Referee or Organiser tab's fields ({kind, game, player…}); never free text.
                    if (_organizer == null) return Ok(new { ok = false, message = "Administration is locked." });
                    var (ok, message) = await _organizer.PostChangeAsync(Arg("kind"), Arg).ConfigureAwait(false);
                    return Ok(new { ok, message });
                }
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
                    return Ok(Switch(Arg("on") == "true", on => { _settings.AutoUpdateMod = on; _nextAutoModUpdate = DateTime.MinValue; },
                        "The mod updates itself while Among Us is closed.", "Automatic mod updates are off: Settings shows when a new version is out."));
                case ("POST", "/app/modconsole"):
                    return Ok(Switch(Arg("on") == "true", on =>
                    {
                        _settings.ShowModConsole = on;
                        if (GameFolder is { } game) try { ModInstaller.SetConsole(game, on); } catch (Exception) { }
                    }, "The mod's console window shows from the next start of Among Us.", "No console window from the next start of Among Us (the log is still in Settings → Mod and updates → Open log)."));
                case ("POST", "/app/hostdiscord"): return Ok(await SaveHostDiscordAsync(input).ConfigureAwait(false));
                case ("POST", "/app/twitch"):
                {
                    string typed = Arg("name").Trim();
                    string? handle = TournamentTracker.Broadcast.FeedProtocol.TwitchHandle(typed);
                    if (typed.Length > 0 && handle == null) return Ok(new { ok = false, message = "That isn't a Twitch channel name (letters, numbers and _, like your twitch.tv/ link)." });
                    _settings.Twitch = handle;
                    TrySave();
                    return Ok(new { ok = true, message = handle == null ? "Twitch channel removed." : $"Your Twitch: {handle}. It shows on the tournament stream with your lobby." });
                }
                case ("POST", "/app/autoupdate"):
                    return Ok(Switch(Arg("on") == "true", on => { _settings.AutoUpdateApp = on; if (on && AppUpdateAvailable) _ = Task.Run(UpdateAppAsync); },
                        "The Button updates itself.", "Automatic updates are off: Settings shows when a new version is out."));
                default: return Text(404, "application/json", "{\"error\":\"not found\"}");
            }
        }

        /// <summary>One of Settings' on/off switches: set, saved, and said in words.</summary>
        private object Switch(bool on, Action<bool> set, string yes, string no)
        {
            set(on);
            TrySave();
            return new { ok = true, message = on ? yes : no };
        }

        /// <summary>A property of a JSON object, when it's of the given kind.</summary>
        private static bool Has(JsonElement e, string name, JsonValueKind kind, out JsonElement value)
        {
            value = default;
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value) && value.ValueKind == kind;
        }

        /// <summary>The feed part of the mod's status (null: no status, or a mod without a feed).</summary>
        private static JsonElement? Feed(JsonElement? status) => status is JsonElement s && Has(s, "feed", JsonValueKind.Object, out var feed) ? feed : null;

        /// <summary>The mod's VDO.Ninja push link from its status (null: none, or not a VDO.Ninja link).</summary>
        private static string? PushUrl(JsonElement? status)
        {
            string? url = Feed(status) is JsonElement feed && Has(feed, "pushUrl", JsonValueKind.String, out var push) ? push.GetString() : null;
            return url != null && url.StartsWith(TournamentSession.VdoNinja, StringComparison.Ordinal) ? url : null;
        }

        // ---- State ------------------------------------------------------------------------

        /// <summary>The mod's status, read once per question and let go (null: not running, or no answer).</summary>
        private async Task<JsonElement?> StatusElementAsync(string game)
        {
            string? text = await _mod.StatusAsync(game).ConfigureAwait(false);
            if (text == null) return null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }
            catch (JsonException) { return null; }
        }

        /// <summary>The mod's files as last looked at: read again after a few seconds, or as soon as an install starts, moves on or ends.</summary>
        private sealed record ModLook(string Key, DateTime At, ModState State);
        private volatile ModLook? _modLook;
        private static readonly TimeSpan ModLookFresh = TimeSpan.FromSeconds(3);

        private ModState ModStateLately()
        {
            string key = $"{GamePath}|{_installing}|{_installResult}";
            var now = DateTime.UtcNow;
            var look = _modLook;
            if (look != null && look.Key == key && now - look.At < ModLookFresh) return look.State;
            var state = ModInstaller.State(GamePath);
            _modLook = new ModLook(key, now, state);
            return state;
        }

        // The game folders The Button could use, only needed (and looked for) while Among Us isn't found.
        private List<GameInstall>? _candidates;
        private DateTime _candidatesAt = DateTime.MinValue;

        private List<GameInstall> CandidatesLately()
        {
            var now = DateTime.UtcNow;
            var list = _candidates;
            if (list != null && now - _candidatesAt < TimeSpan.FromSeconds(10)) return list;
            list = Candidates();
            _candidates = list;
            _candidatesAt = now;
            return list;
        }

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
            var mod = ModStateLately();
            AutoRepair(mod);
            AutoUpdateMod(mod);
            KeepConsoleSetting(mod);
            JsonElement? status = mod.Installed && GamePath != null ? await StatusElementAsync(GamePath).ConfigureAwait(false) : null;
            if (await WatchSendPageAsync(status).ConfigureAwait(false) && GamePath != null)
                status = await StatusElementAsync(GamePath).ConfigureAwait(false) ?? status;
            var organizer = _organizer;          // read once: locking or a new code replaces it on another thread
            return new
            {
                SendPage = new
                {
                    Open = SendPageOpen,
                    Video = _sendVideo,
                    Note = _feedNote,
                },
                App = _env.Version,
                Twitch = _settings.Twitch,
                ModConsole = _settings.ShowModConsole,
                Game = new { Path = GamePath, Found = mod.GameFound, Candidates = mod.GameFound ? new List<GameInstall>() : CandidatesLately() },
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
                Admin = organizer == null ? null : new { organizer.Tournament },
                // An administration code and no Among Us: the organiser's (or a referee's) PC, where Home doesn't ask for the game or the mod.
                OrganiserPc = organizer != null && !mod.GameFound,
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
                Status = status,
                Bot = _bridge.Status(),
                Share = new { Mode = ShareMode, Low = _settings.LowQuality, VoiceOn = !_settings.VoiceOff, Instructions = SendPage.Instructions(ShareMode, !_settings.VoiceOff) },
                Health = HealthView(status),
                ConnCheck = ConnectionCheckView(),
                Backup = BackupView(),
                Damaged = _damaged,
            };
        }

        // ---- The mod's health: safe mode and the last disconnect (Home) ---------------------------

        private string? _seenDisconnect;
        private bool? _seenSafeMode;

        /// <summary>
        /// Safe mode and the last disconnect, as Home shows them (null for a mod from before them). Each new
        /// disconnect and each change of safe mode is written to the-button.log once, for support bundles.
        /// </summary>
        private object HealthView(JsonElement? status)
        {
            var safe = ModHealth.SafeMode(status);
            var gone = ModHealth.LastDisconnect(status, DateTime.UtcNow);
            if (gone != null && _seenDisconnect != gone.At.ToString("o"))
            {
                _seenDisconnect = gone.At.ToString("o");
                _log.Info($"The mod reports a disconnect at {gone.At:yyyy-MM-dd HH:mm:ss} UTC: {gone.Reason}{(gone.Detail != null ? " (" + gone.Detail + ")" : "")}{(gone.InGame ? ", during a game" : "")}");
            }
            if (safe != null && _seenSafeMode != safe.On)
            {
                _seenSafeMode = safe.On;
                _log.Info(safe.On ? $"The mod is in safe mode: {safe.Reason ?? "no reason given"} (Among Us {safe.GameVersion ?? "?"}, built for {safe.BuiltFor ?? "?"})."
                    : $"The mod is running normally{(safe.Overridden ? " (safe mode overridden by the host)" : "")}.");
            }
            return new { SafeMode = safe, LastDisconnect = gone };
        }

        // ---- What the send page shares ----------------------------------------------------------

        private string ShareMode => _settings.ShareMode == SendPage.ShareScreen ? SendPage.ShareScreen : SendPage.ShareWindow;

        /// <summary>The Among Us window or the entire screen, and lower quality: from Settings or the send page (which then asks again what to share).</summary>
        private object SetShare(string mode, string low)
        {
            var said = new List<string>();
            if ((mode == SendPage.ShareWindow || mode == SendPage.ShareScreen) && mode != ShareMode)
            {
                _settings.ShareMode = mode;
                said.Add(mode == SendPage.ShareWindow ? "Sharing the Among Us window." : "Sharing the entire screen.");
            }
            if (low.Length > 0 && (low == "true") != _settings.LowQuality)
            {
                _settings.LowQuality = low == "true";
                said.Add(_settings.LowQuality ? "Lower quality: 720p at 30 fps, at most 2.5 Mbps." : "Full quality: 1080p at up to 60 fps.");
            }
            if (said.Count == 0) return new { ok = true, message = "No change." };
            TrySave();
            // The send page starts VDO.Ninja again and waits for the host to pick: the "stopped sending"
            // watch only counts again once the new share has sent a picture.
            _sendVideoOkAt = DateTime.MinValue;
            return new { ok = true, message = string.Join(" ", said) + (SendPageOpen
                ? " The send page in your browser starts again: pick what to share there (the caster's picture is off until you do)."
                : " Used the next time you send your game.") };
        }

        // ---- Check my connection ------------------------------------------------------------------

        private readonly object _checkLock = new object();
        private CancellationTokenSource? _checkCts;
        private bool _checkStopped;
        private volatile string _checkPhase = "";
        private double _checkProgress;
        private volatile ConnectionResult? _checkResult;
        private volatile string? _checkError;

        /// <summary>A whole run never takes longer than this (a server that hangs, say).</summary>
        private static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(75);

        /// <summary>"start" runs the check in the background (one at a time); "cancel" stops it.</summary>
        private object ConnectionCheckCommand(string action)
        {
            lock (_checkLock)
            {
                if (action == "cancel")
                {
                    if (_checkCts == null) return new { ok = false, message = "No check is running." };
                    _checkStopped = true;
                    _checkCts.Cancel();
                    return new { ok = true, message = "Stopping the check." };
                }
                if (_checkCts != null) return new { ok = false, message = "The check is already running." };
                var cts = _checkCts = new CancellationTokenSource(CheckLimit);
                _checkStopped = false;
                _checkError = null;
                _checkPhase = "Starting…";
                _checkProgress = 0;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var r = await ConnectionCheck.RunAsync(_http, _env.CheckOptions, (phase, done) => { _checkPhase = phase; _checkProgress = done; }, cts.Token).ConfigureAwait(false);
                        _checkResult = r;
                        _log.Info($"Connection check: {r.Score.Level}. {string.Join("; ", r.Score.Facts)}; sent {SupportBundle.Size(r.BytesSent)}.");
                    }
                    catch (OperationCanceledException)
                    {
                        _checkError = _checkStopped ? "Check stopped." : "The check took too long and was stopped: your connection may be very slow, or something is blocking it.";
                    }
                    catch (Exception e)
                    {
                        _checkError = "The check didn't finish: " + e.Message;
                    }
                    finally
                    {
                        lock (_checkLock) _checkCts = null;
                        cts.Dispose();
                    }
                });
                return new { ok = true, message = "Checking your connection (about 15 seconds)…" };
            }
        }

        private object ConnectionCheckView()
        {
            bool running;
            lock (_checkLock) running = _checkCts != null;
            var r = _checkResult;
            return new
            {
                Running = running,
                Phase = running ? _checkPhase : "",
                Progress = running ? Math.Round(_checkProgress, 2) : 0,
                Error = running ? null : _checkError,
                Needs = ConnectionCheck.Needs,
                Result = r == null ? null : new
                {
                    r.At,
                    r.Score.Level,
                    r.Score.Headline,
                    r.Score.Facts,
                    r.Score.Advice,
                    Upload = r.UploadMbps == null ? null : (double?)Math.Round(r.UploadMbps.Value, 1),
                    UploadText = r.UploadMbps == null ? null : ConnectionCheck.Mbps(r.UploadMbps.Value),
                    Sent = SupportBundle.Size(r.BytesSent),
                },
            };
        }

        // ---- Support bundle -----------------------------------------------------------------------

        /// <summary>
        /// Settings → Help → Save support bundle: a zip on the Desktop (or in Downloads) with the logs, settings
        /// and state that explain a problem, secrets taken out, and its folder opened so it can be sent.
        /// </summary>
        private async Task<object> SaveSupportBundleAsync()
        {
            TrySave();
            var mod = ModInstaller.State(GamePath);
            string? status = mod.Installed && GamePath != null ? await _mod.StatusAsync(GamePath).ConfigureAwait(false) : null;
            var notes = new List<string>();
            var r = _checkResult;
            notes.Add(r == null ? "Connection check: not run since The Button started."
                : $"Connection check ({r.At:yyyy-MM-dd HH:mm} UTC): {r.Score.Level}. {r.Score.Headline} {string.Join("; ", r.Score.Facts)}." + (r.Score.Advice.Count > 0 ? " Advice given: " + string.Join(" ", r.Score.Advice) : ""));
            notes.Add($"Sharing: {(ShareMode == SendPage.ShareWindow ? "the Among Us window" : "the entire screen")}{(_settings.LowQuality ? ", lower quality" : "")}; lobby voice {(_settings.VoiceOff ? "off" : "on")}; send page {(SendPageOpen ? "open (video: " + (_sendVideo.Length > 0 ? _sendVideo : "unknown") + ")" : "not open")}.");
            if (_feedNote != null) notes.Add("Send page note: " + _feedNote);
            notes.Add("Discord bot (The Button's): " + JsonSerializer.Serialize(_bridge.Status(), Json));
            notes.Add("Setup code: " + (SetupCode.TryParse(_settings.SetupCode, out var code, out _) ? code.Describe() : "none") + (_settings.AdminCode != null ? "; administration code saved" : ""));
            if (_organizer is { } organizer)
                notes.Add($"Organiser view: {organizer.Tournament}{(organizer.Problem is { } problem ? "; problem: " + problem : "")}{(mod.GameFound ? "" : "; organiser PC (no Among Us here, and none needed)")}.");
            notes.Add($"Updates: The Button automatic {(_settings.AutoUpdateApp ? "on" : "off")}, mod automatic {(_settings.AutoUpdateMod ? "on" : "off")}; newest release seen {_latest?.Tag ?? "not checked yet"}.");
            // Only counts: the backup's files themselves never go in the bundle.
            if (_backup?.State is { } bk)
                notes.Add($"Backup (The Button's copy of the mod's games, links and rounds): {bk.Files} files, last copied {(bk.CopiedAt is DateTime at ? at.ToString("yyyy-MM-dd HH:mm") + " UTC" : "not since The Button started")}; {bk.Missing} saved game(s) only in the backup{(bk.Problem != null ? "; problem: " + bk.Problem : "")}.");
            foreach (var d in _damaged) notes.Add($"Damaged file: {d.Whose} {d.Name} couldn't be read ({d.At:yyyy-MM-dd HH:mm} UTC); a copy is kept as {d.Kept}.");
            var inputs = new ButtonSupport.Inputs
            {
                AppVersion = _env.Version, SettingsFile = _env.SettingsFile, GamePath = GamePath, Mod = mod,
                StatusJson = status, UnityLogFolder = _env.UnityLogs, Notes = notes,
            };
            try
            {
                string folder = SupportBundle.Folder(_env.Desktop, _env.Downloads);
                string path = await Task.Run(() => ButtonSupport.Build(inputs).Save(folder, DateTime.Now)).ConfigureAwait(false);
                _log.Info("Support bundle saved: " + Path.GetFileName(path));
                try { _env.Open(folder); } catch (Exception) { }
                string where = folder == _env.Desktop ? "your Desktop" : folder == _env.Downloads ? "Downloads" : folder;
                return new { ok = true, path, message = $"Saved {Path.GetFileName(path)} to {where}. Send it to the organiser: bot tokens, webhooks, codes and passwords were taken out." };
            }
            catch (Exception e)
            {
                return new { ok = false, message = "Couldn't save the support bundle: " + e.Message };
            }
        }

        /// <summary>
        /// Keeps The Button's copy of the setup code and the one in the Among Us folder the same:
        /// a code put in some other way is copied here, and one that went missing (the mod
        /// reinstalled, Among Us reinstalled or its files checked) is put back.
        /// </summary>
        private void KeepSetupCode()
        {
            if (GameFolder is not { } game) return;
            string dir = ModInstaller.DataDir(game);
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
            string? Field(string name) => Has(input, name, JsonValueKind.String, out var v) ? v.GetString() : null;
            var typed = new HostDiscord
            {
                GuildId = Field("guild") ?? _settings.HostDiscord?.GuildId ?? "",
                BotTokens = Has(input, "tokens", JsonValueKind.Array, out var t)
                    ? t.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToList() : new List<string>(),
                PublicWebhook = Field("public")!,
                StatusWebhook = Field("status")!,
            };
            var remove = Has(input, "remove", JsonValueKind.Array, out var rm)
                ? rm.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList() : new List<int>();
            string? codeGuild = SetupCode.TryParse(_settings.SetupCode, out var code, out _) ? code.GuildId : null;
            var result = await HostDiscordCheck.CheckAsync(_http, typed, _settings.HostDiscord, remove, codeGuild).ConfigureAwait(false);

            _settings.HostDiscord = result.Saved.IsEmpty ? null : result.Saved;
            _settings.HostBotNames = result.BotNames;
            _settings.HostPublicName = result.PublicName;
            _settings.HostStatusName = result.StatusName;
            TrySave();
            bool live = false;
            if (GameFolder is { } game)
            {
                string dir = ModInstaller.DataDir(game);
                try { result.Saved.Save(dir); } catch (Exception) { }
                live = await _mod.CommandAsync(game, "setup reload").ConfigureAwait(false) != null;
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

        private bool ModUpdateAvailable(ModState mod) => _latest != null && mod.Installed && AppUpdater.Newer(_latest.Tag, mod.InstalledVersion);

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

        private DateTime _nextConsoleCheck = DateTime.MinValue;

        /// <summary>
        /// BepInEx's console window as set here (hidden by default): put back after installs, repairs and
        /// BepInEx's first run. Its settings file is read every 10 s, not on every refresh.
        /// </summary>
        private void KeepConsoleSetting(ModState mod)
        {
            if (!mod.Installed || GamePath == null || _installing.Length > 0) return;
            var now = DateTime.UtcNow;
            if (now < _nextConsoleCheck) return;
            _nextConsoleCheck = now + TimeSpan.FromSeconds(10);
            try { if (ModInstaller.ConsoleShown(GamePath) != _settings.ShowModConsole) ModInstaller.SetConsole(GamePath, _settings.ShowModConsole); }
            catch (Exception) { _nextConsoleCheck = DateTime.MinValue; /* tried again on the next refresh */ }
        }

        /// <summary>
        /// Among Us is closed. Asked on every refresh while an update or repair waits for that, so the
        /// look from the last few seconds is used while the game runs, and a fresh one confirms it closed.
        /// </summary>
        private static bool GameClosed() => !ModInstaller.GameRunningLately() && !ModInstaller.GameRunning();

        /// <summary>A new version of the mod is out: install it by itself once Among Us is closed (unless switched off, and Update wasn't pressed).</summary>
        private void AutoUpdateMod(ModState mod)
        {
            if (!ModUpdateAvailable(mod)) _modWanted = false;
            if (_modWanted && mod.LoaderMatchesGame && GamePath != null && _installing.Length == 0 && GameClosed())
            {
                _modWanted = false;
                StartInstall();
                return;
            }
            if (!_settings.AutoUpdateMod || !ModUpdateAvailable(mod) || !mod.LoaderMatchesGame || GamePath == null || _installing.Length > 0) return;
            if (DateTime.UtcNow < _nextAutoModUpdate || !GameClosed()) return;
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
            if (DateTime.UtcNow < _nextAutoRepair || !GameClosed()) return;
            _nextAutoRepair = DateTime.UtcNow + AutoRepairRetry;
            StartInstall(repair: true);
        }

        private object StartInstall(bool repair = false, bool update = false)
        {
            if (_installing.Length > 0) return new { ok = false, message = "Already installing." };
            if (GameFolder is not { } game) return new { ok = false, message = FindGameFirst };
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
            if (!SetupCode.TryParse(text, out var code, out var error)) return new { ok = false, message = error };
            // The organiser's own code unlocks the Organiser tab; nobody else sees that it exists. It's kept in
            // The Button's own settings, so an organiser's PC needn't have Among Us.
            if (code.IsAdmin)
            {
                SetAdminCode(text);
                return new { ok = true, admin = true, message = $"Organiser view unlocked for {code.TournamentName}." };
            }
            // A host's code goes in the mod's folder, inside Among Us's.
            if (GameFolder is not { } game) return new { ok = false, message = FindGameFirst };
            SetupCode.Save(ModInstaller.DataDir(game), code.Encode());
            _settings.SetupCode = code.Encode();
            TrySave();
            bool live = await _mod.CommandAsync(game, "setup reload").ConfigureAwait(false) != null;
            return new { ok = true, message = $"Setup saved: {code.Describe()}." + (live ? " The mod in Among Us picked it up." : " It's used next time Among Us starts.") };
        }

        private async Task<object> ClearSetupAsync()
        {
            if (GameFolder is not { } game) return new { ok = false, message = FindGameFirst };
            SetupCode.Clear(ModInstaller.DataDir(game));
            _settings.SetupCode = null;
            TrySave();
            await _mod.CommandAsync(game, "setup reload").ConfigureAwait(false);
            return new { ok = true, message = "Setup code removed: the mod uses its settings file." };
        }

        // The VDO.Ninja send page: when it last asked for the feed, and whether its video is going out.
        private DateTime _sendSeen = DateTime.MinValue, _sendVideoOkAt = DateTime.MinValue;
        private string _sendVideo = "";
        private string? _feedNote;
        private static readonly TimeSpan SendPageGone = TimeSpan.FromSeconds(15), VideoGone = TimeSpan.FromSeconds(20);

        private bool SendPageOpen => _sendSeen != DateTime.MinValue && DateTime.UtcNow - _sendSeen < SendPageGone;

        /// <summary>
        /// Sending video is on but the send page was closed (no asks for 15 s) or VDO.Ninja stopped
        /// sending its picture (20 s): turn it off, so The Button never says "Sending" when nothing is.
        /// </summary>
        private async Task<bool> WatchSendPageAsync(JsonElement? status)
        {
            if (_sendSeen == DateTime.MinValue || !(Feed(status) is JsonElement feed && Has(feed, "on", JsonValueKind.True, out _))) return false;
            string? why = SendPageStopped(DateTime.UtcNow, _sendSeen, _sendVideoOkAt, _sendVideo);
            if (why == null) return false;
            _sendSeen = DateTime.MinValue;
            _sendVideoOkAt = DateTime.MinValue;
            await FeedAsync(false).ConfigureAwait(false);
            _feedNote = $"Sending video turned off: {why}. Turn it on again to send.";
            return true;
        }

        /// <summary>
        /// Why sending should be turned off, or null: the page stopped asking for 15 s, or a picture that
        /// was going out has stalled (the page says "lost") for 20 s. "unknown" isn't a stall: it's the
        /// share being picked again (What to share, Lower quality or Pick again start VDO.Ninja over and
        /// it waits for the host's click) or no viewer connected right now, so it never times out.
        /// </summary>
        internal static string? SendPageStopped(DateTime now, DateTime seen, DateTime videoOkAt, string video) =>
            now - seen > SendPageGone ? "the VDO.Ninja page was closed"
            : videoOkAt != DateTime.MinValue && video == "lost" && now - videoOkAt > VideoGone ? "VDO.Ninja stopped sending the picture"
            : null;

        // Each send page The Button opens has its own id (in its link). Only the newest is served: an older
        // tab (after Open again) or the tab of a feed that was switched off gets 409 for everything and
        // stops sharing the screen and the lobby voice, instead of two tabs splitting the sound between them.
        private readonly object _sendPageLock = new object();
        private string? _sendPageId;

        private static string NewSendPageId() => Guid.NewGuid().ToString("N").Substring(0, 10);

        /// <summary>
        /// A request from a send page that isn't the current one. Pages without an id (from before ids)
        /// are served as always. After The Button restarts (an update) no page has been opened yet: the
        /// first page that asks is taken as the current one, so an open tab keeps working.
        /// </summary>
        private bool StalePage(string query)
        {
            string page = HttpRequest.Query(query, "page");
            if (page.Length == 0) return false;
            lock (_sendPageLock)
            {
                _sendPageId ??= page;
                return _sendPageId != page;
            }
        }

        /// <summary>Every send page now open is told to close down (the feed was switched off).</summary>
        private void RetireSendPages()
        {
            lock (_sendPageLock) _sendPageId = NewSendPageId();
        }

        /// <summary>
        /// "Send my game to the caster": switches it in the mod, then opens the private VDO.Ninja
        /// page in the host's browser, where they pick the Among Us window.
        /// </summary>
        private async Task<object> FeedAsync(bool on)
        {
            _sendSeen = DateTime.MinValue;
            _sendVideoOkAt = DateTime.MinValue;
            _sendVideo = "";
            if (on) _feedNote = null;
            else RetireSendPages();
            if (GamePath == null) return Replies(false, FindGameFirst);
            string? answer = await _mod.CommandAsync(GamePath, on ? "feed on" : "feed off").ConfigureAwait(false);
            if (answer == null) return Replies(false, ModNotRunning);
            // The Button's own page shares the screen through VDO.Ninja and sends the lobby's live data with it.
            // A new tab every time (Open again is for when the old one picked the wrong screen or stalled): the old one is retired.
            if (on && PushUrl(await StatusElementAsync(GamePath).ConfigureAwait(false)) != null)
            {
                string id = NewSendPageId();
                lock (_sendPageLock) _sendPageId = id;
                try { _env.Open($"{Url}send?token={Token}&page={id}"); } catch (Exception) { }
                // What to pick is The Button's to say (the window or the whole screen, and where the sound comes from).
                return Replies(true, "Sending your game to the caster. " + SendPage.Instructions(ShareMode, !_settings.VoiceOff));
            }
            return JsonDocument.Parse(answer).RootElement;
        }

        private async Task<object> CommandAsync(string command)
        {
            if (GamePath == null || command.Trim().Length == 0) return Replies(false, FindGameFirst);
            string? answer = await _mod.CommandAsync(GamePath, command).ConfigureAwait(false);
            return answer == null ? Replies(false, ModNotRunning) : JsonDocument.Parse(answer).RootElement;
        }

        private const string FindGameFirst = "Find Among Us first.", ModNotRunning = "Among Us isn't running with the mod, so that can't be done right now.";

        /// <summary>An answer in the shape of the mod's own (ok, and the lines to show).</summary>
        private static object Replies(bool ok, string text) => new { ok, replies = new[] { text } };

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
                "backup" => _backup?.Folder,
                _ => null,
            };
            if (target == null) return new { ok = false };
            if (what == "backup") try { Directory.CreateDirectory(target); } catch (Exception) { }
            try { _env.Open(target); return new { ok = true }; }
            catch (Exception e) { return new { ok = false, message = e.Message }; }
        }

        private static string Resource(string name) => UiFiles.Text(typeof(AppServer).Assembly, name);
        private static byte[] ResourceBytes(string name) => UiFiles.Bytes(typeof(AppServer).Assembly, name);

        public void Dispose()
        {
            _cts.Cancel();
            _tick.Dispose();
            _bridge.Dispose();
            _organizer?.Dispose();
            _voice?.Dispose();
            try { _listener.Stop(); } catch (Exception) { }
        }
    }
}
