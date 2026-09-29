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
                case ("GET", "/app/replay"):
                {
                    string name = HttpRequest.Query(query, "name");
                    var file = ReplayFiles().FirstOrDefault(f => f.Name == name);
                    return file == null || !ReplayName.IsMatch(name) ? Text(404, "text/plain", "Not found") : (200, "application/octet-stream", File.ReadAllBytes(file.FullName));
                }
                case ("POST", "/app/open"): return Ok(Open(Arg("what")));
                case ("POST", "/app/update"):
                    if (!AppUpdateAvailable) return Ok(new { ok = false, message = "The Button is up to date." });
                    _ = Task.Run(UpdateAppAsync);
                    return Ok(new { ok = true, message = "Downloading the new version of The Button…" });
                case ("POST", "/app/restart"):
                    if (_appReady == null) return Ok(new { ok = false, message = "No update is waiting." });
                    _ = Task.Run(async () => { await Task.Delay(300).ConfigureAwait(false); _env.Restart(); });
                    return Ok(new { ok = true, message = "Restarting…" });
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
            string? status = mod.Installed && GamePath != null ? await _mod.StatusAsync(GamePath).ConfigureAwait(false) : null;
            return new
            {
                App = _env.Version,
                Game = new { Path = GamePath, Found = mod.GameFound, Candidates = Candidates() },
                Mod = new
                {
                    mod.Installed, mod.InstalledVersion, mod.LoaderIs32Bit, mod.NeedsRepair,
                    Latest = _latest?.Tag,
                    UpdateAvailable = _latest != null && mod.Installed && Newer(_latest.Tag, mod.InstalledVersion),
                    Installing = _installing,
                    InstallResult = _installResult,
                },
                Setup = SetupView(),
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

        private object? SetupView()
        {
            if (GamePath == null) return null;
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

        private object StartInstall()
        {
            if (_installing.Length > 0) return new { ok = false, message = "Already installing." };
            if (GamePath == null) return new { ok = false, message = "Find Among Us first." };
            string game = GamePath;
            _installing = "Checking for the latest version…";
            _installResult = "";
            _ = Task.Run(async () =>
            {
                try
                {
                    var release = await _installer.LatestAsync().ConfigureAwait(false);
                    if (release == null) { _installResult = "Couldn't reach GitHub to download the mod. Check your internet connection."; return; }
                    _latest = release;
                    string error = await _installer.InstallAsync(game, release, m => _installing = m).ConfigureAwait(false);
                    _installResult = error.Length > 0 ? error : $"Installed {release.Tag}. Start Among Us: the first start takes a few minutes while BepInEx sets itself up.";
                }
                finally { _installing = ""; }
            });
            return new { ok = true, message = "Installing…" };
        }

        private async Task<object> SaveSetupAsync(string text)
        {
            if (GamePath == null) return new { ok = false, message = "Find Among Us first." };
            if (!SetupCode.TryParse(text, out var code, out var error)) return new { ok = false, message = error };
            SetupCode.Save(ModInstaller.DataDir(GamePath), code.Encode());
            bool live = await _mod.CommandAsync(GamePath, "setup reload").ConfigureAwait(false) != null;
            return new { ok = true, message = $"Setup saved: {code.Describe()}." + (live ? " The mod in Among Us picked it up." : " It's used next time Among Us starts.") };
        }

        private async Task<object> ClearSetupAsync()
        {
            if (GamePath == null) return new { ok = false, message = "Find Among Us first." };
            SetupCode.Clear(ModInstaller.DataDir(GamePath));
            await _mod.CommandAsync(GamePath, "setup reload").ConfigureAwait(false);
            return new { ok = true, message = "Setup code removed: the mod uses its settings file." };
        }

        private async Task<object> CommandAsync(string command)
        {
            if (GamePath == null || command.Trim().Length == 0) return new { ok = false, replies = new[] { "Find Among Us first." } };
            string? answer = await _mod.CommandAsync(GamePath, command).ConfigureAwait(false);
            if (answer == null) return new { ok = false, replies = new[] { "Among Us isn't running with the mod, so that can't be done right now." } };
            return JsonDocument.Parse(answer).RootElement;
        }

        private List<FileInfo> ReplayFiles()
        {
            var folders = new List<string> { _env.Downloads };
            if (GamePath != null) folders.Add(Path.Combine(ModInstaller.DataDir(GamePath), "games"));
            return ReplayLibrary.Find(folders, 50);
        }

        private object Replays() => ReplayFiles().Select(f => new
        {
            f.Name, Label = ReplayLibrary.Label(f), When = f.LastWriteTimeUtc.ToString("o"), Size = f.Length,
            Downloaded = f.FullName.StartsWith(_env.Downloads, StringComparison.OrdinalIgnoreCase),
        }).ToList();

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
                "generator" => $"https://github.com/{ModInstaller.Repo}/blob/main/docs/setup-codes.html",
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
            try { _listener.Stop(); } catch (Exception) { }
        }
    }
}
