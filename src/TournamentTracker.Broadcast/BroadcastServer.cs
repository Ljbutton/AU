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

namespace TournamentTracker.App.Broadcast
{
    /// <summary>Red Alert's own settings (settings.json in its folder).</summary>
    public sealed class BroadcastAppSettings
    {
        /// <summary>The administration code that unlocks everything (null: locked).</summary>
        public string? AdminCode { get; set; }
        /// <summary>Mute every lobby voice from any window (Red Alert doesn't need to be in front).</summary>
        public string MuteHotkey { get; set; } = Hotkey.DefaultMuteAll;

        public static BroadcastAppSettings Load(string file)
        {
            try { if (File.Exists(file)) return JsonSerializer.Deserialize<BroadcastAppSettings>(File.ReadAllText(file)) ?? new BroadcastAppSettings(); }
            catch (Exception) { }
            return new BroadcastAppSettings();
        }

        public void Save(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(this));
        }
    }

    /// <summary>What the Windows window gives the broadcast app: where things are, and how to open files and links.</summary>
    public sealed class BroadcastEnvironment
    {
        /// <summary>settings.json in the app's folder; every other file (obs.json, caster-priority.json…) goes next to it.</summary>
        public string SettingsFile { get; set; } = "";
        /// <summary>Opens a folder, file or web link with Windows.</summary>
        public Action<string> Open { get; set; } = _ => { };
        public string Version { get; set; } = "";
        /// <summary>The caster pages' port (OBS points at it); 0 picks any free one (tests).</summary>
        public int CasterPort { get; set; } = CasterServer.DefaultPort;
        /// <summary>This app's own screen; 0 picks any free one. Fixed, so a producer's browser can find it later.</summary>
        public int Port { get; set; } = BroadcastServer.DefaultPort;
    }

    /// <summary>
    /// Red Alert's screen and everything behind it, served on this computer only: the caster desk
    /// (the RedZone-style tab that used to be in The Button), OBS, replays, the caster pages and
    /// Twitch. Every /app/ call needs the per-launch token the page was given, so no website can
    /// drive it. Unlocked with the tournament's administration code.
    /// </summary>
    public sealed class BroadcastServer : IDisposable
    {
        public const int DefaultPort = 8768;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly BroadcastEnvironment _env;
        private readonly BroadcastAppSettings _settings;
        private readonly HttpClient _http;
        private Organizer? _organizer;
        private CasterServer? _caster;
        private CasterDesk? _desk;
        private ObsDirector? _obs;
        private ReplayManager? _replays;
        private BroadcastApp? _broadcast;
        private TwitchDirector? _twitch;
        private Timer? _twitchTimer;
        private MontageManager? _montages;

        public int Port { get; }
        public string Token { get; }

        /// <summary>The Mute all key, as the window should register it; raised when it changes.</summary>
        public string MuteHotkey => Hotkey.TryParse(_settings.MuteHotkey, out _, out _, out var n) ? n : Hotkey.DefaultMuteAll;
        public event Action<string>? MuteHotkeyChanged;
        /// <summary>Set by the window when Windows wouldn't give it the key (another app has it).</summary>
        public string? HotkeyProblem { get; set; }

        /// <summary>The Mute all key was pressed (anywhere in Windows): every lobby voice off, or back on.</summary>
        public void MuteHotkeyPressed()
        {
            var obs = _obs;
            if (obs == null) return;
            obs.Settings.Voice.MuteAll = !obs.Settings.Voice.MuteAll;
            _ = obs.VoiceChangedAsync();
        }
        public string Url => $"http://127.0.0.1:{Port}/";
        public CasterDesk? Desk => _desk;
        public string? CasterUrl => _caster?.Url;

        public BroadcastServer(BroadcastEnvironment env, HttpClient http)
        {
            _env = env;
            _http = http;
            _settings = BroadcastAppSettings.Load(env.SettingsFile);
            var bytes = new byte[18];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            Token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
            try { _listener = new TcpListener(IPAddress.Loopback, env.Port); _listener.Start(); }
            catch (SocketException) { _listener = new TcpListener(IPAddress.Loopback, 0); _listener.Start(); }     // the usual port is taken
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(AcceptLoop);
            StartDesk();
        }

        private void TrySave()
        {
            try { _settings.Save(_env.SettingsFile); } catch (Exception) { }
        }

        private string? SideFolder() => string.IsNullOrEmpty(_env.SettingsFile) ? null : Path.GetDirectoryName(_env.SettingsFile);

        private string? SideFile(string name) =>
            string.IsNullOrEmpty(_env.SettingsFile) ? null : Path.Combine(Path.GetDirectoryName(_env.SettingsFile) ?? ".", name);

        /// <summary>Starts (or stops) the desk, OBS, replays, the caster pages and Twitch for the saved administration code.</summary>
        private void StartDesk()
        {
            _caster?.Dispose();
            _caster = null;
            _organizer?.Dispose();
            _organizer = null;
            _desk?.Dispose();
            _desk = null;
            _replays?.Dispose();
            _replays = null;
            _twitchTimer?.Dispose();
            _twitchTimer = null;
            _twitch?.Dispose();
            _twitch = null;
            var oldObs = _obs;
            _obs = null;
            if (oldObs != null) _ = oldObs.DisposeAsync().AsTask();
            if (_settings.AdminCode != null && SetupCode.TryParse(_settings.AdminCode, out var code, out _) && code.IsAdmin)
            {
                _organizer = new Organizer(code, _http);
                var organizer = _organizer;
                var caster = _caster = new CasterServer(organizer, _env.CasterPort);
                var desk = _desk = new CasterDesk(DeskConfigPath, rosterPath: SideFile(Roster.FileName), dataFolder: SideFolder())
                {
                    ExternalGames = () => organizer.Games,
                    Advance = () => organizer.Advance,
                    GamesPerRound = () => organizer.GamesPerRound,
                };
                // The overlay, video and multiview pages follow what's on stream (unless pinned).
                caster.Follow = () => desk.OnAir.Slots.FirstOrDefault(x => x != null);
                desk.AirChangedInObs += _ => caster.Refresh();
                var obs = _obs = new ObsDirector(SideFile(ObsSettings.FileName), desk, () => ObsFeeds(caster, desk)) { VoiceFeeds = () => VoiceFeeds(caster, desk), MuteHotkey = MuteHotkey };
                obs.ImpostorTagsOn = () => _broadcast?.Settings.Current.Elements.GetValueOrDefault("impostorTags") == true;
                // The overlay and video page follow the lobby in the first slot; OBS (when connected) shows the whole layout.
                desk.Switch = air =>
                {
                    caster.Refresh();
                    if (obs.Connected) _ = obs.ApplyAsync(air);
                };
                if (caster.Url != null) obs.TagUrl = caster.Url + "replaytag";
                var broadcast = _broadcast = new BroadcastApp(desk, () => _obs, SideFile(BroadcastSettings.FileName));
                if (caster.Url != null) obs.BroadcastUrl = caster.Url + "broadcast";
                caster.MorePages = path => BroadcastPage(broadcast, path) ?? SponsorPage(desk, path);
                var builder = new MontageBuilder(() => obs.Settings.Replay, () => obs.ClipFolder, SideFolder() is { } side ? Path.Combine(side, "tools") : null);
                // The swoosh in the tournament's colours, with its logo; made again when they change.
                builder.SwooshTheme = () => { var t = broadcast.Settings.Current.Theme; return (t.Primary, t.Accent, string.IsNullOrEmpty(t.Logo) || t.Logo.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? null : t.Logo); };
                obs.SwooshKey = () =>
                {
                    var t = broadcast.Settings.Current.Theme;
                    string look = $"v2|{t.Primary}|{t.Accent}|{t.Logo}|{(File.Exists(t.Logo) ? File.GetLastWriteTimeUtc(t.Logo).Ticks : 0)}";
                    return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(look)))[..8].ToLowerInvariant();
                };
                obs.MakeSwoosh = builder.SwooshAsync;
                var replays = _replays = new ReplayManager(desk, obs) { TagChanged = json => caster.ReplayNow = json, Builder = builder, Sponsors = desk.Sponsors };
                _montages = new MontageManager(desk, replays, builder, desk.Sponsors, () => obs.Settings.Replay, archivePath: SideFile(MontageManager.ArchiveFile));
                // Part 23: Twitch (predictions, polls, !sus, channel points), shown on stream by the graphics app.
                var twitch = _twitch = new TwitchDirector(desk, SideFolder(), _http) { Clips = replays.ReadyClips, PlayReplay = replays.PlayAsync };
                broadcast.Extras["twitch"] = twitch.Overlay;
                int ticking = 0;
                _twitchTimer = new Timer(_ =>
                {
                    if (Interlocked.Exchange(ref ticking, 1) == 1) return;
                    twitch.TickAsync().ContinueWith(_ => Interlocked.Exchange(ref ticking, 0));
                }, null, 1000, 1000);
                if (!twitch.Settings.Off && (twitch.Settings.TestMode || twitch.Auth.Token != null)) _ = twitch.ConnectAsync();
                // Simulation stopped: the rest of its fake lobbies' things go (the desk has cleared its own).
                var montages = _montages;
                desk.SimStopped += which => _ = Task.Run(async () =>
                {
                    try
                    {
                        await twitch.ForgetAsync(which).ConfigureAwait(false);
                        var clips = await replays.ForgetAsync(which).ConfigureAwait(false);
                        await replays.StopIfAsync(montages.Forget(which, clips)).ConfigureAwait(false);
                        await obs.ForgetSimAsync(which).ConfigureAwait(false);
                    }
                    catch (Exception) { }
                });
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

        /// <summary>caster-priority.json, in the app's folder.</summary>
        private string? DeskConfigPath => SideFile(PriorityConfig.FileName);

        private string? _ffmpegNote;

        /// <summary>Games finished so far (for sponsor reads "every N games").</summary>
        private int PlayedGames() => _desk?.Archive.Games.Count ?? 0;

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
                Reads = _desk.Sponsors.ReadsDue(PlayedGames(), _desk.Tables.CurrentRound),
                List = list.Select(x => new
                {
                    x.Name, x.Tagline, x.Placements, x.BreakSeconds, Logo = x.Logo.Length > 0,
                    Read = x.HasRead ? (x.ReadEveryMinutes > 0 ? $"read every {x.ReadEveryMinutes} min" : x.ReadEveryGames > 0 ? $"read every {x.ReadEveryGames} games" : $"read every {x.ReadEveryRounds} rounds") : null,
                    Reads = log.Count(a => a.Sponsor == x.Name && a.Placement == "read"),
                    Shown = log.Count(a => a.Sponsor == x.Name && a.Placement != "read"),
                    Seconds = Math.Round(log.Where(a => a.Sponsor == x.Name).Sum(a => a.Seconds)),
                }).ToList(),
            };
        }


        /// <summary>What each lobby's OBS source shows: its VDO.Ninja video, or a stand-in page in simulation mode.</summary>
        /// <summary>A small live picture of each lobby sending its game, for the Multiview card (VDO.Ninja asked for a low resolution).</summary>
        private object Previews() => _caster == null || _desk == null ? new List<object>()
            : ObsFeeds(_caster, _desk).OrderBy(f => _desk.NumberOf(f.Lobby)).Select(f => new { lobby = f.Lobby, no = _desk.NumberOf(f.Lobby), url = f.Url.Contains("vdo.ninja", StringComparison.OrdinalIgnoreCase) ? f.Url + "&scale=25&noaudio" : f.Url }).ToList<object>();

        private static IReadOnlyList<(string Lobby, string Url)> ObsFeeds(CasterServer caster, CasterDesk desk)
        {
            var list = caster.ObsLinks();
            if (desk.Simulating && caster.Url != null)
                foreach (var r in desk.Board.Ranking())
                    if (!list.Any(l => string.Equals(l.Lobby, r.Lobby, StringComparison.OrdinalIgnoreCase)))
                        list.Add((r.Lobby, caster.Url + "sim?lobby=" + Uri.EscapeDataString(r.Lobby)));
            return list;
        }

        /// <summary>Each lobby's voice stream for OBS; simulated lobbies get a stand-in that blips.</summary>
        private static IReadOnlyList<(string Lobby, string Url)> VoiceFeeds(CasterServer caster, CasterDesk desk)
        {
            var list = caster.VoiceLinks();
            if (desk.Simulating && caster.Url != null)
                foreach (var r in desk.Board.Ranking())
                    if (!list.Any(l => string.Equals(l.Lobby, r.Lobby, StringComparison.OrdinalIgnoreCase)))
                        list.Add((r.Lobby, caster.Url + "simvoice?lobby=" + Uri.EscapeDataString(r.Lobby)));
            return list;
        }


        private object SetAdminCode(string text)
        {
            if (text.Trim().Length == 0)
            {
                _settings.AdminCode = null;
                TrySave();
                StartDesk();
                return new { ok = true, message = "Locked." };
            }
            if (!SetupCode.TryParse(text, out var code, out var error)) return new { ok = false, message = error };
            if (!code.IsAdmin) return new { ok = false, message = "That's a host setup code. Red Alert needs the administration code from the organiser." };
            _settings.AdminCode = code.Encode();
            TrySave();
            StartDesk();
            return new { ok = true, message = $"Unlocked for {code.TournamentName}." };
        }

        private object StateNow()
        {
            return new
            {
                App = _env.Version,
                Admin = _organizer == null ? null : new { _organizer.Tournament },
                HotkeyProblem,
            };
        }

        // ---- Serving --------------------------------------------------------------------------------

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

        /// <summary>Rebuild TT scenes (and the Live desk's Fix): every scene and source again, failed lobbies tried now.</summary>
        internal async Task<object> RebuildAsync()
        {
            var obs = _obs!;
            await obs.BuildAsync().ConfigureAwait(false);
            await obs.EnsureReplaySceneAsync().ConfigureAwait(false);
            await obs.EnsureBroadcastAsync().ConfigureAwait(false);
            await obs.EnsureSwooshAsync().ConfigureAwait(false);
            await obs.EnsureVoiceAsync().ConfigureAwait(false);
            if (_desk != null && _desk.OnAir.Layout != "none") await obs.ApplyAsync(_desk.OnAir).ConfigureAwait(false);
            var problems = obs.SourceProblems;
            if (problems.Count > 0)
                return new { ok = false, message = "Not in OBS: " + string.Join("; ", problems.Select(p => $"{p.Key} ({p.Value})")) };
            var other = new[] { obs.Problem, obs.ReplayProblem, obs.SwooshProblem, obs.VoiceProblem }.FirstOrDefault(p => p != null && !p.StartsWith("Install", StringComparison.Ordinal) && !p.StartsWith("No swoosh", StringComparison.Ordinal));
            return other != null ? new { ok = false, message = "The TT scenes are rebuilt, but: " + other } : (object)new { ok = true, message = "The TT scenes are up to date." };
        }

        /// <summary>Every request, answered: an action that throws says what went wrong instead of leaving its button silent.</summary>
        internal async Task<(int Status, string Type, byte[] Body)> Route(string method, string path, Dictionary<string, string> headers, string body)
        {
            try { return await RouteCore(method, path, headers, body).ConfigureAwait(false); }
            catch (Exception e) when (path.StartsWith("/app/", StringComparison.Ordinal))
            {
                return Ok(new { ok = false, message = "That didn't work: " + e.Message });
            }
        }

        private async Task<(int Status, string Type, byte[] Body)> RouteCore(string method, string path, Dictionary<string, string> headers, string body)
        {
            // Only this computer, by name: stops a web page reaching us through DNS tricks.
            headers.TryGetValue("host", out var host);
            if (host != null && !Regex.IsMatch(host, @"^(127\.0\.0\.1|localhost)(:\d+)?$")) return Text(403, "text/plain", "Forbidden");

            string route = path.Split('?')[0];
            string query = path.Contains('?') ? path.Substring(path.IndexOf('?') + 1) : "";
            if (method == "GET" && (route == "/" || route == "/index.html"))
                return Text(200, "text/html; charset=utf-8", Resource("ui/caster.html").Replace("__APP_TOKEN__", Token));
            var font = Regex.Match(route, @"^/fonts/([a-z0-9-]+\.woff2)$");
            if (method == "GET" && font.Success)
            {
                var bytes = ResourceBytes("ui/fonts/" + font.Groups[1].Value);
                return bytes.Length > 0 ? (200, "font/woff2", bytes) : Text(404, "text/plain", "Not found");
            }
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
                case ("GET", "/app/state"): return Ok(StateNow());
                case ("POST", "/app/admin/code"): return Ok(SetAdminCode(Arg("code")));
                case ("GET", "/app/admin"):
                {
                    // The tournament's lobbies (for casting the overlay), with what the caster pages show.
                    if (_organizer == null) return Text(404, "application/json", "{\"error\":\"locked\"}");
                    var view = JsonSerializer.SerializeToNode(_organizer.State(), Json)!.AsObject();
                    view["casterUrl"] = _caster?.Url;
                    view["casterProblem"] = _caster?.Problem;
                    view["cast"] = _caster?.Casting;
                    view["pinned"] = _caster?.Pinned;
                    view["following"] = _desk?.OnAir.Slots.FirstOrDefault(x => x != null);
                    return Ok(view);
                }
                case ("POST", "/app/admin/cast"):
                    if (_caster == null) return Ok(new { ok = false, message = "Administration is locked." });
                    _caster.Pin(Arg("pin") == "false" ? null : Arg("lobby"));
                    return Ok(new { ok = true, message = _caster.Pinned is { } p ? $"Overlay pinned to {p}." : "Overlay follows what's on stream." });
                case ("GET", "/app/admin/desk"):
                    if (_desk == null || _organizer == null) return Text(404, "application/json", "{\"error\":\"locked\"}");
                    return Ok(new { desk = _desk.State(), obs = _obs?.Status(), replay = _replays?.State(), roster = _desk.RosterState(), story = _desk.StoryState(),
                        montages = _montages?.State(), moments = _replays?.Moments(), sponsors = SponsorState(),
                        voice = new { status = _desk.VoiceState(), obs = _obs?.VoiceStatus() },
                        twitch = _twitch?.State(),
                        broadcast = _broadcast == null ? null : new { url = _caster?.Url == null ? null : _caster.Url + "broadcast", elements = _broadcast.Settings.Refresh().Elements, names = BroadcastSettings.ElementNames, problem = _broadcast.Settings.Problem, alerts = _desk.Alerts.Settings, alertsWaiting = _desk.Alerts.Waiting, queue = _desk.Graphics.State(), afterGame = _desk.PromptState(), afterGameAuto = _broadcast.Settings.Current.AfterGameAuto, hold = _broadcast.Settings.Current.GraphicSeconds },
                        names = _caster!.DataLinks().ToDictionary(d => d.Lobby, d => _desk.NamesFor(d.Lobby)), receivers = _caster.DataLinks().Select(d => new { lobby = d.Lobby, url = d.Url }).ToList(),
                        previews = Previews(), twitchHandles = _desk.Board.Ranking().ToDictionary(r => r.Lobby, r => _desk.TwitchOf(r.Lobby)) });
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
                        case "reconnect" when !(_desk.Simulating && CasterDesk.IsSimLobby(lobby)):
                            // A real lobby: the page reloads its data link now (see the Live desk's receivers).
                            return Ok(new { ok = true, message = $"{lobby}: reconnecting its data link." });
                        default:
                            if (!_desk.Simulating) return Ok(new { ok = false, message = "Those buttons are for simulation mode." });
                            return Ok(new { ok = _desk.SimFail(lobby, what), message = what == "reconnect" ? $"{lobby}: reconnecting." : $"{lobby}: simulated {what} problem." });
                    }
                }
                case ("POST", "/app/admin/twitch"):
                {
                    var tw = _twitch;
                    if (tw == null) return Ok(new { ok = false, message = "Administration is locked." });
                    var st = tw.Settings;
                    bool On() => Arg("on") == "true";
                    int Int(int fallback) => int.TryParse(Arg("value"), out var n) ? n : fallback;
                    string message;
                    try
                    {
                    switch (Arg("action"))
                    {
                        case "connect": message = await tw.ConnectAsync().ConfigureAwait(false); break;
                        case "signin": message = await tw.StartSignInAsync().ConfigureAwait(false); break;
                        case "signout": tw.SignOut(); message = "Signed out of Twitch."; break;
                        case "off": st.Off = On(); tw.Save(); message = st.Off ? "Twitch is off: nothing goes to Twitch." : "Twitch is on."; break;
                        case "test":
                            st.TestMode = On(); tw.Save();
                            message = await tw.ConnectAsync().ConfigureAwait(false);
                            break;
                        case "clientId": st.ClientId = Arg("value").Trim(); tw.Save(); message = "Client ID saved."; break;
                        case "delay": st.DelaySeconds = Math.Max(0, Int(0)); tw.Save(); message = $"Stream delay: {st.DelaySeconds:0} s."; break;
                        case "featured": st.Featured = Arg("value"); tw.Save(); message = st.Featured.Length > 0 ? $"Predictions follow {st.Featured}." : "Predictions follow the lobby on stream."; break;
                        case "meetingMode": st.MeetingMode = Arg("value") is "poll" or "chat" or "both" ? Arg("value") : "auto"; tw.Save(); message = "Meeting votes: " + st.MeetingMode + "."; break;
                        case "revealAtEject": st.RevealAtEject = On(); tw.Save(); message = st.RevealAtEject ? "Whether chat was right is said at the ejection (only if your lobbies confirm ejects)." : "Whether chat was right is said when the game ends."; break;
                        case "predictionSeconds": st.PredictionSeconds = TwitchLimits.Clamp(Int(90), TwitchLimits.PredictionMinSeconds, TwitchLimits.PredictionMaxSeconds); tw.Save(); message = $"Predictions lock after {st.PredictionSeconds} s."; break;
                        case "meetingSeconds": st.MeetingSeconds = TwitchLimits.Clamp(Int(40), TwitchLimits.PollMinSeconds, TwitchLimits.PollMaxSeconds); tw.Save(); message = $"Meeting votes run {st.MeetingSeconds} s."; break;
                        case "feature": st.Features[Arg("name")] = On(); tw.Save(); message = $"{TwitchSettings.FeatureNames.GetValueOrDefault(Arg("name"), Arg("name"))}: {(On() ? "on" : "off")}."; break;
                        case "auto": st.Auto[Arg("name")] = On(); tw.Save(); message = $"Automatic {Arg("name")}: {(On() ? "on" : "off")}."; break;
                        case "start":
                            message = Arg("kind") switch
                            {
                                "prediction" => await tw.StartGamePredictionAsync(Arg("lobby").Length > 0 ? Arg("lobby") : null).ConfigureAwait(false),
                                "first" or "moreWins" => await tw.StartRoundPredictionAsync(Arg("kind")).ConfigureAwait(false),
                                "meetingPoll" => await tw.StartMeetingFromTabAsync("poll").ConfigureAwait(false),
                                "meetingChat" => await tw.StartMeetingFromTabAsync("chat").ConfigureAwait(false),
                                "lobbyPoll" => await tw.StartLobbyPollAsync().ConfigureAwait(false),
                                "mvpPoll" => await tw.StartMvpPollAsync().ConfigureAwait(false),
                                _ => "Unknown.",
                            };
                            break;
                        case "prediction": message = await tw.PredictionActionAsync(Arg("do"), Arg("outcome")).ConfigureAwait(false); break;
                        case "endpoll": message = await tw.EndPollAsync().ConfigureAwait(false); break;
                        case "redemption": message = await tw.DecideAsync(Arg("id"), On(), Arg("clip").Length > 0 ? Arg("clip") : null).ConfigureAwait(false); break;
                        default: message = "Unknown."; break;
                    }
                    }
                    catch (Exception e) { message = "Twitch: " + e.Message; }
                    return Ok(new { ok = true, message });
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
                        case "check":
                            if (!_obs.Connected) return Ok(new { ok = false, message = "Connect to OBS first." });
                            var found = await _obs.CheckSetupAsync().ConfigureAwait(false);
                            return Ok(new { ok = true, message = found.All(c => c.Ok) ? "OBS setup: all good." : $"OBS setup: {found.Count(c => !c.Ok)} to look at." });
                        case "fix":
                            if (!_obs.Connected) return Ok(new { ok = false, message = "Connect to OBS first." });
                            try { return Ok(new { ok = true, message = await _obs.FixAsync(Arg("id")).ConfigureAwait(false) }); }
                            catch (Exception e) { return Ok(new { ok = false, message = "OBS: " + e.Message }); }
                        case "disconnect":
                            await _obs.DisconnectAsync().ConfigureAwait(false);
                            return Ok(new { ok = true, message = "Disconnected from OBS." });
                        case "build":
                            if (!_obs.Connected) return Ok(new { ok = false, message = "Connect to OBS first." });
                            try { return Ok(await RebuildAsync().ConfigureAwait(false)); }
                            catch (Exception e) { return Ok(new { ok = false, message = "Rebuilding the TT scenes didn't work: " + e.Message }); }
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
                        // The archive: played montages.
                        case "archivePlay": return Ok(new { ok = true, message = await _montages.ReplayArchivedAsync(id).ConfigureAwait(false) });
                        case "archiveRebuild":
                        {
                            if (_montages.Builder.Ffmpeg == null) return Ok(new { ok = false, message = "Get ffmpeg first." });
                            var a = _montages.FindArchived(id);
                            if (a == null) return Ok(new { ok = false, message = "That montage isn't in the archive." });
                            _ = Task.Run(() => _montages.RebuildAsync(id));
                            return Ok(new { ok = true, message = $"Rebuilding {NameTag.Plain(a.Title)}: it shows under Montages when it's ready." });
                        }
                        case "archiveOpen":
                        case "archiveFolder":
                        {
                            var a = _montages.FindArchived(id);
                            if (a?.File == null || !File.Exists(a.File)) return Ok(new { ok = false, message = "Its video file is gone." });
                            try { _env.Open(Arg("action") == "archiveOpen" ? a.File : Path.GetDirectoryName(a.File)!); } catch (Exception) { }
                            return Ok(new { ok = true, message = Arg("action") == "archiveOpen" ? "Opening the video." : "Opening its folder." });
                        }
                        case "archiveDelete":
                            // Only with the page's second click ("Sure? Delete for good").
                            if (Arg("sure") != "true") return Ok(new { ok = false, message = "Click Delete again to delete it for good." });
                            return Ok(new { ok = true, message = _montages.DeleteArchived(id) });
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
                    string? file = kind == "montage" ? _montages?.Find(id)?.File ?? _montages?.FindArchived(id)?.File : kind == "thumb" ? _replays?.Find(id)?.Thumbnail : _replays?.Find(id)?.File;
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
                        case "readDone":
                            return Ok(new { ok = true, message = _desk.Sponsors.ReadDone(Arg("name"), PlayedGames(), _desk.Tables.CurrentRound) });
                        case "readSnooze":
                            return Ok(new { ok = true, message = _desk.Sponsors.ReadSnooze(Arg("name")) });
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
                    // Impostor tags without a stream delay is a setup warning: check again.
                    if (element == "impostorTags" && _obs?.Connected == true) _ = Task.Run(_obs.CheckSetupAsync);
                    return Ok(new { ok = true, message = $"{BroadcastSettings.ElementNames[element]} {(Arg("on") == "true" ? "on" : "off")}." });
                }
                case ("POST", "/app/admin/graphics"):
                {
                    // The big-graphics queue: skip, clear, hold time, and the after-game prompts.
                    if (_desk == null || _broadcast == null) return Ok(new { ok = false, message = "Administration is locked." });
                    switch (Arg("action"))
                    {
                        case "skip": _desk.Graphics.Skip(); return Ok(new { ok = true, message = "Skipped: the next graphic comes up." });
                        case "clear":
                            _desk.Graphics.Clear();
                            if (_broadcast.Settings.Current.Elements.GetValueOrDefault("standings")) _broadcast.Settings.Set("standings", false);
                            return Ok(new { ok = true, message = "Graphics queue cleared." });
                        case "hold":
                            _broadcast.Settings.SetQueue(double.TryParse(Arg("value"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec) ? sec : 8, null);
                            return Ok(new { ok = true, message = $"Each graphic stays {_broadcast.Settings.Current.GraphicSeconds} s." });
                        case "afterGameAuto":
                            _broadcast.Settings.SetQueue(null, Arg("on") == "true");
                            return Ok(new { ok = true, message = _broadcast.Settings.Current.AfterGameAuto ? "After a game, its table goes up by itself." : "After a game, the Live desk asks first." });
                        case "prompt": return Ok(new { ok = true, message = _desk.AnswerPrompt(Arg("id"), Arg("answer")) });
                        default: return Ok(new { ok = false, message = "Unknown graphics action." });
                    }
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
                        var g = _desk.Graphics.Add("storyline", null, NameTag.Plain(note.Text), note.Text);
                        return Ok(new { ok = true, message = (_desk.Graphics.Current()?.Id == g.Id ? "On stream: " : "Queued (after what's showing): ") + NameTag.Plain(note.Text) });
                    }
                    _desk.Storylines.Mark(id, action);
                    return Ok(new { ok = true, message = action switch { "pin" => "Pinned.", "unpin" => "Unpinned.", "used" => "Marked used.", "dismiss" => "Dismissed.", _ => "Done." } });
                }
                case ("POST", "/app/admin/standings"):
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    if (Arg("scope").Length > 0) _desk.StandingsScope = Arg("scope") is "overall" or "round" ? Arg("scope") : "lobby";
                    if (Arg("show").Length > 0) _broadcast?.Settings.Set("standings", Arg("show") == "true");
                    return Ok(new { ok = true, message = Arg("show") == "true" ? "Standings on stream." : Arg("show") == "false" ? "Standings off stream." : $"Standings: {(_desk.StandingsScope == "overall" ? "whole tournament" : _desk.StandingsScope == "round" ? "the round, every lobby" : "the lobby on stream")}." });
                case ("POST", "/app/admin/multiview"):
                {
                    if (_desk == null) return Ok(new { ok = false, message = "Administration is locked." });
                    var pick = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("lobbies", out var pl) && pl.ValueKind == JsonValueKind.Array
                        ? pl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList() : new List<string>();
                    if (pick.Count == 0) return Ok(new { ok = false, message = "Pick one or more lobbies first." });
                    var air = _desk.ShowPicked(pick);
                    string how = air.Layout switch { "full" => "Full screen", "2up" => "2-up", "4up" => "Quad", "grid" => "Grid", _ => air.Layout };
                    return Ok(new { ok = true, message = $"On stream ({how}): {string.Join(", ", air.Slots.Where(x => x != null))}." });
                }
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
                            v.DuckPicked = true;
                            duck = true;
                            said = v.DuckUnder.Length > 0 ? $"Lobby voice ducks under {v.DuckUnder}." : "No ducking.";
                            break;
                        case "hotkey":
                        {
                            if (!Hotkey.TryParse(Arg("value"), out _, out _, out var key))
                                return Ok(new { ok = false, message = "That key can't be used: hold Ctrl, Shift or Alt with a letter, number or F key (or use an F key or Pause alone)." });
                            _settings.MuteHotkey = key;
                            TrySave();
                            _obs.MuteHotkey = key;
                            HotkeyProblem = null;
                            MuteHotkeyChanged?.Invoke(key);
                            return Ok(new { ok = true, message = $"Mute all key: {key}." });
                        }
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
                    return Ok(new { ok = true, message = _desk.Simulating ? "Simulation on: four fake lobbies (SIM-1 to SIM-4) are playing." : "Simulation off: every fake lobby, game, clip and OBS source is gone." });
                default: return Text(404, "application/json", "{\"error\":\"not found\"}");
            }
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
            _twitchTimer?.Dispose();
            _twitch?.Dispose();
            _caster?.Dispose();
            _organizer?.Dispose();
            _desk?.Dispose();
            _replays?.Dispose();
            if (_obs != null) try { _obs.DisposeAsync().AsTask().Wait(1500); } catch (Exception) { }
            try { _listener.Stop(); } catch (Exception) { }
        }
    }
}
