using System;
using System.Collections.Generic;
using System.Globalization;
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

namespace TournamentTracker.App.Broadcast
{
    /// <summary>Red Alert's own settings (settings.json in its folder).</summary>
    public sealed class BroadcastAppSettings
    {
        /// <summary>The administration code that unlocks everything (null: locked).</summary>
        public string? AdminCode { get; set; }
        /// <summary>Mute every lobby voice from any window (Red Alert doesn't need to be in front).</summary>
        public string MuteHotkey { get; set; } = Hotkey.DefaultMuteAll;
        /// <summary>The Stream Deck links' key (Settings → Stream Deck), kept so the buttons keep working.</summary>
        public string? DeckKey { get; set; }
        /// <summary>Player cameras off for every lobby (on unless turned off).</summary>
        public bool CamOff { get; set; }
        /// <summary>Hold and the desk's automation (auto grid, the countdown…), as left (null: never saved: the defaults).</summary>
        public DeskAutomation? Desk { get; set; }
        /// <summary>Team watch: the Watch page listens on the network (off unless turned on in Settings).</summary>
        public bool TeamWatch { get; set; }
        /// <summary>The Watch page's code (a secret: taken out of support bundles, never logged).</summary>
        public string? WatchCode { get; set; }

        public static BroadcastAppSettings Load(string file) => Files.ReadJson<BroadcastAppSettings>(file) ?? new BroadcastAppSettings();

        public void Save(string file) => Files.WriteJson(file, this);
    }

    /// <summary>What the Windows window gives the broadcast app: where things are, and how to open files and links.</summary>
    public sealed class BroadcastEnvironment
    {
        /// <summary>settings.json in the app's folder; every other file (obs.json, caster-priority.json…) goes next to it.</summary>
        public string SettingsFile { get; set; } = "";
        /// <summary>Opens a folder, file or web link with Windows.</summary>
        public Action<string> Open { get; set; } = _ => { };
        public string Version { get; set; } = "";
        /// <summary>The running RedAlert.exe, so Settings can update it. Null: no updating (tests, other platforms).</summary>
        public string? ExePath { get; set; }
        /// <summary>Starts the new version and closes this one.</summary>
        public Action Restart { get; set; } = () => { };
        /// <summary>The caster pages' port (OBS points at it); 0 picks any free one (tests).</summary>
        public int CasterPort { get; set; } = CasterServer.DefaultPort;
        /// <summary>This app's own screen; 0 picks any free one. Fixed, so a producer's browser can find it later.</summary>
        public int Port { get; set; } = BroadcastServer.DefaultPort;
        /// <summary>Where Settings → Save support bundle puts its zip; null: the Windows Desktop (else next to the settings).</summary>
        public string? SupportFolder { get; set; }
        /// <summary>OBS's own folder, for its newest log and crash report in the support bundle; null: %APPDATA%\obs-studio.</summary>
        public string? ObsFolder { get; set; }
        /// <summary>The Watch page's port while Team watch is on (8780, else the next free one); 0 picks any free one (tests).</summary>
        public int WatchPort { get; set; } = WatchServer.DefaultPort;
        /// <summary>The Watch page's pictures are taken by their own loop (false: tests step it themselves).</summary>
        public bool WatchCapture { get; set; } = true;
    }

    /// <summary>
    /// Red Alert's screen and everything behind it, served on this computer only: the caster desk
    /// (the RedZone-style tab that used to be in The Button), OBS, replays, the caster pages and
    /// Twitch. Every /app/ call needs the per-launch token the page was given, so no website can
    /// drive it. Unlocked with the tournament's administration code.
    /// </summary>
    public sealed partial class BroadcastServer : IDisposable
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
            DeckPoke();
        }
        public string Url => $"http://127.0.0.1:{Port}/";
        public CasterDesk? Desk => _desk;
        public string? CasterUrl => _caster?.Url;
        /// <summary>OBS and the replays (for tests).</summary>
        internal ObsDirector? Obs => _obs;
        internal ReplayManager? Replays => _replays;

        public BroadcastServer(BroadcastEnvironment env, HttpClient http)
        {
            _env = env;
            _http = http;
            // A file of Red Alert's that couldn't be read (and was kept as name.corrupt-…) is said in its log.
            if (!string.IsNullOrEmpty(env.SettingsFile)) Files.Warn = m => Log.Warn(m);
            _settings = BroadcastAppSettings.Load(env.SettingsFile);
            Token = LocalServer.NewToken();
            _listener = Listen(env.Port);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(() => LocalServer.Serve(_listener, _cts.Token, Route));
            StartDesk();
            StartWatch();
        }

        /// <summary>
        /// Listens on the usual port, else the next three (8769–8771: the Stream Deck plugin, set to any of
        /// them, looks for it on the others by asking /deck/hello without the key), else any free one.
        /// Settings and the Stream Deck's state say when it isn't the usual one: links copied before point
        /// at 8768.
        /// </summary>
        private static TcpListener Listen(int port) =>
            LocalServer.Listen(port == DefaultPort ? Enumerable.Range(DefaultPort, 4).ToArray() : new[] { port });

        private void TrySave()
        {
            try { _settings.Save(_env.SettingsFile); } catch (Exception) { }
        }

        /// <summary>Opens a file or folder with Windows (nothing when there's none, or Windows can't).</summary>
        private void Open(string? path)
        {
            if (path != null) try { _env.Open(path); } catch (Exception) { }
        }

        private FileLog? _logFile;
        /// <summary>Red Alert's own log (red-alert.log next to its settings; the one before is kept as red-alert.log.1).</summary>
        public FileLog Log => _logFile ??= new FileLog(SideFolder(), "red-alert.log", keepPrevious: true);

        private string? SideFolder() => string.IsNullOrEmpty(_env.SettingsFile) ? null : Path.GetDirectoryName(_env.SettingsFile);

        private string? SideFile(string name) => SideFolder() is { } folder ? Path.Combine(folder, name) : null;

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
            _panic = null;
            if (_settings.AdminCode != null && SetupCode.TryParse(_settings.AdminCode, out var code, out _) && code.IsAdmin)
            {
                // The lobbies and their links last known are kept on disk, so they're back at once after a restart (marked cached until the channel confirms them).
                var organizer = _organizer = new Organizer(code, _http, log: Log, cacheFile: SideFile(Organizer.CacheFileName));
                var caster = _caster = new CasterServer(organizer, _env.CasterPort);
                var desk = _desk = new CasterDesk(DeskConfigPath, rosterPath: SideFile(Roster.FileName), dataFolder: SideFolder())
                {
                    ExternalGames = () => organizer.Games,
                    Advance = () => organizer.Advance,
                    GamesPerRound = () => organizer.GamesPerRound,
                };
                RestoreDesk(desk);
                // The overlay, video and multiview pages follow what's on stream (unless pinned).
                caster.Follow = () => desk.OnAir.Slots.FirstOrDefault(x => x != null);
                desk.AirChangedInObs += _ => caster.Refresh();
                desk.HostCommand += SpecOut;
                desk.CamsOn = () => !_settings.CamOff;
                desk.PanicOn = () => _panic is { } inPanic && PanicInEffect(desk, inPanic);
                // A panic put right by hand is forgotten as soon as it is (before the Stream Deck hears of it).
                desk.Changed += PanicCheck;
                desk.Changed += DeckPoke;
                desk.Suggestions.Changed += DeckPoke;
                var obs = _obs = new ObsDirector(SideFile(ObsSettings.FileName), desk, () => ObsFeeds(caster, desk)) { VoiceFeeds = () => VoiceFeeds(caster, desk), MuteHotkey = MuteHotkey, CamFeeds = () => caster.CamLinks() };
                obs.ImpostorTagsOn = () => _broadcast?.Settings.Current.Elements.GetValueOrDefault("impostorTags") == true;
                obs.OutputChanged += DeckPoke;
                // The overlay and video page follow the lobby in the first slot; OBS (when connected) shows the
                // whole layout. OBS first: the caster pages' refresh can't hold up (or, throwing, stop) the switch.
                desk.Switch = air =>
                {
                    if (obs.Connected) _ = obs.ApplyAsync(air, air.Transition);
                    try { caster.Refresh(); } catch (Exception) { }
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
                var montages = _montages = new MontageManager(desk, replays, builder, desk.Sponsors, () => obs.Settings.Replay, archivePath: SideFile(MontageManager.ArchiveFile));
                replays.Protected = montages.ClipFiles;
                replays.Saved += _ => DeckPoke();
                replays.OnChanged += _ => DeckPoke();
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
            // With the broadcast delay, its game data as it was that long ago (in step with the lobbies' pictures).
            if (route == "/broadcast/state") return ("application/json", JsonSerializer.SerializeToUtf8Bytes(app.OnStream(Json), Json));
            if (route == "/broadcast/logo") return app.Logo();
            // Players' crewmate heads, for every name on stream, and the fonts.
            return UiFiles.Asset(typeof(BroadcastServer).Assembly, route);
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
            var tally = _desk.Sponsors.Tallies();
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
                    tally.GetValueOrDefault(x.Name).Reads,
                    tally.GetValueOrDefault(x.Name).Shown,
                    Seconds = Math.Round(tally.GetValueOrDefault(x.Name).Seconds),
                }).ToList(),
            };
        }

        /// <summary>A small live picture of each lobby sending its game, for the Multiview card (VDO.Ninja asked for a low resolution).</summary>
        private object Previews() => _caster == null || _desk == null ? new List<object>()
            : ObsFeeds(_caster, _desk).OrderBy(f => _desk.NumberOf(f.Lobby)).Select(f => new { lobby = f.Lobby, no = _desk.NumberOf(f.Lobby), url = f.Url.Contains("vdo.ninja", StringComparison.OrdinalIgnoreCase) ? f.Url + "&scale=25&noaudio" : f.Url }).ToList<object>();

        /// <summary>What each lobby's OBS source shows: its VDO.Ninja video, or a stand-in page in simulation mode.</summary>
        private static IReadOnlyList<(string Lobby, string Url)> ObsFeeds(CasterServer caster, CasterDesk desk) => WithSim(caster.ObsLinks(), caster, desk, "sim");

        /// <summary>Each lobby's voice stream for OBS; simulated lobbies get a stand-in that blips.</summary>
        private static IReadOnlyList<(string Lobby, string Url)> VoiceFeeds(CasterServer caster, CasterDesk desk) => WithSim(caster.VoiceLinks(), caster, desk, "simvoice");

        private static IReadOnlyList<(string Lobby, string Url)> WithSim(List<(string Lobby, string Url)> list, CasterServer caster, CasterDesk desk, string page)
        {
            if (desk.Simulating && caster.Url != null)
                foreach (var r in desk.Board.Ranking())
                    if (!list.Any(l => string.Equals(l.Lobby, r.Lobby, StringComparison.OrdinalIgnoreCase)))
                        list.Add((r.Lobby, caster.Url + page + "?lobby=" + Uri.EscapeDataString(r.Lobby)));
            return list;
        }

        /// <summary>
        /// A host's setup code against the administration code: does the host's live data go to the
        /// channel Red Alert reads, and can Red Alert's bot read what the host's bot posts?
        /// </summary>
        public object CheckHostCode(string text)
        {
            if (_organizer == null) return new { ok = false, message = "Unlock Red Alert with the administration code first." };
            if (!SetupCode.TryParse(text, out var host, out var error)) return new { ok = false, message = error };
            if (host.IsAdmin) return new { ok = false, message = "That's an administration code. Paste a host's setup code." };
            var warnings = new List<string>();
            string mine = _organizer.ResultsChannelId ?? "";
            if (string.IsNullOrEmpty(host.ResultsChannelId))
                warnings.Add(host.Mode == "prelim"
                    ? "This preliminary code has no Red Alert channel: its lobby won't show in Red Alert. Make the code with the Red Alert channel ID filled in."
                    : "This code has no results channel: its lobby won't show in Red Alert.");
            else if (host.ResultsChannelId != mine)
                warnings.Add($"This host's live data goes to channel {host.ResultsChannelId}, but Red Alert reads channel {mine}. Make the host's code (or the administration code) with the same channel.");
            if (!string.IsNullOrEmpty(host.GuildId) && !string.IsNullOrEmpty(_organizer.GuildId) && host.GuildId != _organizer.GuildId)
                warnings.Add($"This code is for server {host.GuildId}, the administration code for server {_organizer.GuildId}.");
            var hostBots = (host.BotTokens ?? new List<string>()).Select(SetupCode.BotIdOf).Where(id => id.Length > 0).ToList();
            if (hostBots.Count > 0 && !hostBots.Contains(_organizer.BotId))
                warnings.Add("The host's bot isn't the administration bot. That works only if the administration bot has Message Content Intent on (Discord Developer Portal → Bot) and can read the channel.");
            else if (hostBots.Count == 0 && !string.IsNullOrEmpty(host.ResultsChannelId))
                warnings.Add("This code has no bot: the host adds their own in The Button. Their live data reaches Red Alert only if the administration bot has Message Content Intent on.");
            string ok = $"Matches: this {(host.Mode == "prelim" ? "preliminary" : "host")} code's live data goes to the channel Red Alert reads, with the same bot.";
            return new { ok = true, warnings, message = warnings.Count == 0 ? ok : null };
        }

        private object SetAdminCode(string text)
        {
            if (text.Trim().Length == 0)
            {
                _settings.AdminCode = null;
                TrySave();
                StartDesk();
                // The remembered lobbies' links carry the hosts' VDO.Ninja passwords: not kept once locked.
                Files.Delete(SideFile(Organizer.CacheFileName));
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
                Update = UpdateState(),
                Cam = _desk == null ? null : CamState(),
                Port, PortWarning,
            };
        }

        // ---- Serving --------------------------------------------------------------------------------

        private static (int, string, byte[]) Text(int status, string type, string text) => (status, type, Encoding.UTF8.GetBytes(text));
        private static (int, string, byte[]) Ok(object value) => Text(200, "application/json", JsonSerializer.Serialize(value, Json));
        /// <summary>A button's answer: {ok, message}.</summary>
        private static (int, string, byte[]) Say(bool ok, string? message) => Ok(new { ok, message });
        private const string Locked = "Administration is locked.";

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
                return Say(false, "That didn't work: " + e.Message);
            }
            // A Stream Deck key shows the error instead of finding nothing there (OFFLINE).
            catch (Exception e) when (path.StartsWith("/deck/", StringComparison.Ordinal))
            {
                return DeckReply(500, false, "That didn't work: " + e.Message);
            }
            // Hold and the desk's automation, kept for the next start when an action changed them (DeskMemory).
            finally { if (method == "POST" || path.StartsWith("/deck/", StringComparison.Ordinal)) KeepDesk(); }
        }

        private async Task<(int Status, string Type, byte[] Body)> RouteCore(string method, string path, Dictionary<string, string> headers, string body)
        {
            // Only this computer, by name: stops a web page reaching us through DNS tricks.
            headers.TryGetValue("host", out var host);
            if (host != null && !Regex.IsMatch(host, @"^(127\.0\.0\.1|localhost)(:\d+)?$")) return Text(403, "text/plain", "Forbidden");

            string route = path.Split('?')[0];
            string query = path.Contains('?') ? path.Substring(path.IndexOf('?') + 1) : "";
            if (method == "GET" && (route == "/" || route == "/index.html"))
            {
                PageOpened();
                return Text(200, "text/html; charset=utf-8", Resource("ui/caster.html").Replace("__APP_TOKEN__", Token));
            }
            // The replay viewer (the same as The Button's): it loads a kept game with ?src=.
            if (method == "GET" && route == "/viewer")
                return Text(200, "text/html; charset=utf-8",
                    "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Replay</title></head><body style=\"margin:0\">"
                    + Resource("ui/viewer-body.html") + "</body></html>");
            if (method == "GET" && UiFiles.Asset(typeof(BroadcastServer).Assembly, route) is { } asset) return (200, asset.Type, asset.Body);
            if (method == "GET" && UiFiles.Base(typeof(BroadcastServer).Assembly, route) is { } shared) return (200, shared.Type, shared.Body);
            // Stream Deck links: their own key (they can't send the app's header, and must survive restarts).
            if (route.StartsWith("/deck/", StringComparison.Ordinal) && (method == "GET" || method == "POST")) return await Deck(route, query).ConfigureAwait(false);
            if (!route.StartsWith("/app/", StringComparison.Ordinal)) return Text(404, "text/plain", "Not found");

            headers.TryGetValue("x-app-token", out var token);
            if (token != Token && HttpRequest.Query(query, "token") != Token) return Text(401, "application/json", "{\"error\":\"bad token\"}");
            PageAsked();
            // An action: the desk's next answer is new (it shows what the click did).
            if (method == "POST") Interlocked.Increment(ref _postVersion);

            JsonElement input = default;
            if (body.Length > 0) { try { input = JsonDocument.Parse(body).RootElement; } catch (Exception) { } }
            JsonElement Prop(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) ? v : default;
            string Arg(string name) => Prop(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";
            double Num(string name) => Prop(name) is { ValueKind: not JsonValueKind.Undefined } n
                ? n.ValueKind == JsonValueKind.Number ? n.GetDouble() : double.TryParse(n.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0 : 0;
            IEnumerable<JsonElement> Items(string name) => Prop(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

            switch ((method, route))
            {
                case ("GET", "/app/state"): return Ok(StateNow());
                case ("POST", "/app/update"): return Ok(StartUpdate(Arg("check") == "true"));
                case ("POST", "/app/restart"):
                    if (_updateReady == null) return Say(false, "No update is waiting.");
                    _ = Task.Run(async () => { await Task.Delay(300).ConfigureAwait(false); _env.Restart(); });
                    return Say(true, "Restarting Red Alert…");
                case ("POST", "/app/support"):
                {
                    // Settings → Save support bundle (also on the lock screen: it works before the code is in).
                    var said = Items("warnings").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Take(50).ToList();
                    return Ok(await SaveSupportBundleAsync(said, Arg("window")).ConfigureAwait(false));
                }
                case ("POST", "/app/admin/code"): return Ok(SetAdminCode(Arg("code")));
                case ("POST", "/app/admin/checkhost"): return Ok(CheckHostCode(Arg("code")));
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
                    if (_caster == null) return Say(false, Locked);
                    _caster.Pin(Arg("pin") == "false" ? null : Arg("lobby"));
                    return Say(true, _caster.Pinned is { } p ? $"Overlay pinned to {p}." : "Overlay follows what's on stream.");
                case ("GET", "/app/admin/desk"):
                    if (_desk == null || _organizer == null) return Text(404, "application/json", "{\"error\":\"locked\"}");
                    return Json200(await DeskAnswerAsync().ConfigureAwait(false));
                case ("GET", "/app/admin/live"):
                    if (_desk == null || _organizer == null) return Text(404, "application/json", "{\"error\":\"locked\"}");
                    return Json200(await LiveAnswerAsync().ConfigureAwait(false));
                case ("GET", "/app/admin/program.jpg"):
                {
                    // What's on stream, as OBS draws it (a small picture, about twice a second at most).
                    var jpg = _obs == null ? null : await _obs.ProgramShotAsync(int.TryParse(HttpRequest.Query(query, "w"), out var w) ? w : 448).ConfigureAwait(false);
                    return jpg == null ? (204, "image/jpeg", Array.Empty<byte>()) : (200, "image/jpeg", jpg);
                }
                case ("POST", "/app/admin/do"):
                {
                    // Any Stream Deck action from the desk page (Preview, Take, hold, panic, replays…), with its answer.
                    // Unlike the Stream Deck's, its switches go through without OBS (Red Alert's pages follow them).
                    string doPath = Arg("path").Trim('/');
                    string doQuery = Prop("query") is { ValueKind: JsonValueKind.Object } dq
                        ? string.Join("&", dq.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value.GetString() ?? ""))) : "";
                    var (status, message) = await DeckActionAsync(doPath.Split('/', StringSplitOptions.RemoveEmptyEntries), doQuery, requireObs: false).ConfigureAwait(false);
                    DeckPoke();
                    return Ok(new { ok = status is >= 200 and < 300, status, message });
                }
                case ("POST", "/app/admin/feedin"):
                    if (_desk == null) return Ok(new { ok = false });
                {
                    var from = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in Items("items"))
                    {
                        _desk.Apply(item);
                        if (item.TryGetProperty("lobby", out var il) && il.ValueKind == JsonValueKind.String && item.TryGetProperty("src", out _)) from.Add(il.GetString()!);
                    }
                    // Part 22: tells each referee's page what arrived, so it stops sending those again.
                    return Ok(new { ok = true, acks = from.ToDictionary(l => l, l => _desk.Acks(l)) });
                }
                case ("POST", "/app/admin/health"):
                {
                    if (_desk == null) return Say(false, Locked);
                    string lobby = Arg("lobby"), what = Arg("what");
                    switch (what)
                    {
                        case "forget": _desk.ForgetHealth(lobby); return Say(true, $"{lobby} taken off the health list.");
                        case "autoSwitch": return Say(true, _desk.SetAutoSwitch(Arg("on") == "true"));
                        case "slate": _desk.HealthConfig.Slate = Arg("on") == "true"; _desk.HealthConfig.Save(_desk.HealthPath); return Say(true, _desk.HealthConfig.Slate ? "Be-right-back screen when every lobby is down: on." : "Every lobby down: intermission instead of the be-right-back screen.");
                        case "open": _desk.HealthConfig.Save(_desk.HealthPath); Open(_desk.HealthPath); return Say(true, "Opened health.json.");
                        case "reconnect" when !(_desk.Simulating && CasterDesk.IsSimLobby(lobby)):
                            // A real lobby: the page reloads its data link now (see the Live desk's receivers).
                            return Say(true, $"{lobby}: reconnecting its data link.");
                        default:
                            if (!_desk.Simulating) return Say(false, "Those buttons are for simulation mode.");
                            return Say(_desk.SimFail(lobby, what), what == "reconnect" ? $"{lobby}: reconnecting." : $"{lobby}: simulated {what} problem.");
                    }
                }
                case ("POST", "/app/admin/twitch"):
                {
                    var tw = _twitch;
                    if (tw == null) return Say(false, Locked);
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
                    return Say(true, message);
                }
                case ("POST", "/app/admin/interrupted"):
                {
                    if (_desk == null) return Say(false, Locked);
                    var (command, message) = _desk.Decide(Arg("id"), Arg("decision"));
                    if (command != null)
                    {
                        if (_desk.Simulating || _organizer == null) message += " (Simulation: nothing posted to Discord.)";
                        else message += " " + await _organizer.CommandAsync(command).ConfigureAwait(false);
                    }
                    return Say(true, message);
                }
                case ("POST", "/app/admin/show"):
                {
                    if (_desk == null) return Say(false, Locked);
                    string layout = Arg("layout").Length > 0 ? Arg("layout") : "full";
                    int? slot = int.TryParse(Arg("slot"), out var sl) ? sl : (int?)null;
                    List<string>? slots = Prop("slots") is { ValueKind: JsonValueKind.Array } ss
                        ? ss.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToList() : null;
                    // A sponsor break isn't replaced by another whole-screen break: End break first.
                    if (layout is "intermission" or "slate" or "soon" && _desk.BreakRefusal() is { } inBreak) return Say(false, inBreak);
                    var air = _desk.Show(Arg("lobby"), layout, slot, slots);
                    return Ok(new { ok = true, message = _desk.BreakSaid(air) ?? air.Layout switch
                    {
                        "full" => $"{air.Slots.FirstOrDefault() ?? Arg("lobby")} is on stream.",
                        "slate" => "On stream: Be right back.",
                        "intermission" => "On stream: Intermission.",
                        "soon" => "On stream: Starting soon.",
                        "break" => $"On stream: sponsor break with {air.Slots.FirstOrDefault()}.",
                        _ => air.Slots.Any(x => x != null) ? $"On stream: {string.Join(", ", air.Slots.Select(x => x ?? "empty"))}." : "Nothing on stream.",
                    } + _desk.DownNote(air.Slots), down = _desk.DownNote(air.Slots).Trim() });
                }
                case ("POST", "/app/admin/watch"):
                {
                    var card = _desk?.Find(Arg("id"));
                    if (_desk == null || card == null) return Say(false, "That card is gone.");
                    // A card with a saved replay plays it; otherwise its lobby comes back up.
                    if (_replays != null && card.ClipId != null && _replays.Find(card.ClipId)?.State == "ready")
                    {
                        if (_desk.BreakRefusal() is { } inBreak) return Say(false, inBreak);
                        return Say(true, await _replays.PlayAsync(card.ClipId).ConfigureAwait(false));
                    }
                    var watched = _desk.Show(card.Lobby, _desk.OnAir.Layout == "none" ? "full" : _desk.OnAir.Layout, 1);
                    return Say(true, _desk.BreakSaid(watched) ?? $"Back to {card.Lobby}: {NameTag.Plain(card.Text)}.");
                }
                case ("POST", "/app/admin/obs"):
                {
                    if (_obs == null) return Say(false, Locked);
                    string action = Arg("action");
                    if (action is "check" or "fix" or "build" && !_obs.Connected) return Say(false, "Connect to OBS first.");
                    switch (action)
                    {
                        case "swoosh":
                            _obs.Settings.Swoosh.On = Arg("on") == "true";
                            _obs.SaveSettings();
                            if (_obs.Settings.Swoosh.On && _obs.Connected) await _obs.EnsureSwooshAsync().ConfigureAwait(false);
                            return Say(true, _obs.Settings.Swoosh.On ? "Swoosh on every switch." : "Swoosh off: straight cuts.");
                        case "delay":
                            // Red Alert's broadcast delay (seconds): every lobby source in OBS gets it now.
                            return Say(true, await _obs.SetDelayAsync(Num("value")).ConfigureAwait(false));
                        case "check":
                            var found = await _obs.CheckSetupAsync().ConfigureAwait(false);
                            return Say(true, found.All(c => c.Ok) ? "OBS setup: all good." : $"OBS setup: {found.Count(c => !c.Ok)} to look at.");
                        case "fix":
                            try { return Say(true, await _obs.FixAsync(Arg("id")).ConfigureAwait(false)); }
                            catch (Exception e) { return Say(false, "OBS: " + e.Message); }
                        case "disconnect":
                            await _obs.DisconnectAsync().ConfigureAwait(false);
                            return Say(true, "Disconnected from OBS.");
                        case "build":
                            try { return Ok(await RebuildAsync().ConfigureAwait(false)); }
                            catch (Exception e) { return Say(false, "Rebuilding the TT scenes didn't work: " + e.Message); }
                        default:
                        {
                            int? port = int.TryParse(Arg("port"), out var pt) ? pt : (int?)null;
                            string? password = Prop("password") is { ValueKind: JsonValueKind.String } pw ? pw.GetString() : null;
                            string message = await _obs.ConnectAsync(Arg("host").Length > 0 ? Arg("host") : null, port, password).ConfigureAwait(false);
                            if (_obs.Connected && _desk != null && _desk.OnAir.Layout != "none" && _desk.OnAir.By is "button" or "deck") await _obs.ApplyAsync(_desk.OnAir).ConfigureAwait(false);
                            return Say(_obs.Connected, message);
                        }
                    }
                }
                case ("POST", "/app/admin/replay"):
                {
                    if (_replays == null || _desk == null || _obs == null) return Say(false, Locked);
                    string action = Arg("action");
                    switch (action)
                    {
                        case "save":
                        {
                            var card = _desk.Find(Arg("id"));
                            if (card == null) return Say(false, "That card is gone.");
                            if (!_obs.Connected) return Say(false, "Connect OBS first: replays come from its replay buffer.");
                            _ = Task.Run(() => _replays.SaveAsync(card));
                            return Say(true, $"Saving a replay of {card.Lobby}: {NameTag.Plain(card.Text)}.");
                        }
                        case "play":
                            // A card's id plays its clip (not over a sponsor break).
                            if (_desk.BreakRefusal() is { } inBreak) return Say(false, inBreak);
                            return Say(true, await _replays.PlayAsync(_desk.Find(Arg("id"))?.ClipId ?? Arg("id")).ConfigureAwait(false));
                        case "hotkeys":
                        {
                            if (Prop("map") is { ValueKind: JsonValueKind.Object } map)
                            {
                                foreach (var kv in map.EnumerateObject())
                                    if (kv.Value.ValueKind == JsonValueKind.String && _obs.Settings.Replay.Hotkeys.ContainsKey(kv.Name)) _obs.Settings.Replay.Hotkeys[kv.Name] = kv.Value.GetString() ?? "";
                                _obs.SaveSettings();
                            }
                            return Say(true, "Replay keys saved.");
                        }
                        default:
                        {
                            string? said = await _replays.ControlAsync(action, Num("value"), Num("value2")).ConfigureAwait(false);
                            return Say(said == null || action == "live" || action == "angle" && said.EndsWith(" angle.", StringComparison.Ordinal), said ?? "");
                        }
                    }
                }
                case ("POST", "/app/admin/montage"):
                {
                    if (_montages == null || _desk == null) return Say(false, Locked);
                    string id = Arg("id"), action = Arg("action");
                    // Building one (again) needs ffmpeg.
                    if (action is "archiveRebuild" or "game" or "round" or "custom" && _montages.Builder.Ffmpeg == null) return Say(false, "Get ffmpeg first.");
                    // Not over a sponsor break.
                    if (action is "play" or "archivePlay" && _desk.BreakRefusal() is { } inBreak) return Say(false, inBreak);
                    switch (action)
                    {
                        case "play": return Say(true, await _montages.PlayAsync(id).ConfigureAwait(false));
                        case "discard": return Say(true, _montages.Discard(id));
                        // The archive: played montages.
                        case "archivePlay": return Say(true, await _montages.ReplayArchivedAsync(id).ConfigureAwait(false));
                        case "archiveRebuild":
                        {
                            var a = _montages.FindArchived(id);
                            if (a == null) return Say(false, "That montage isn't in the archive.");
                            _ = Task.Run(() => _montages.RebuildAsync(id));
                            return Say(true, $"Rebuilding {NameTag.Plain(a.Title)}: it shows under Montages when it's ready.");
                        }
                        case "archiveOpen":
                        case "archiveFolder":
                        {
                            var a = _montages.FindArchived(id);
                            if (a?.File == null || !File.Exists(a.File)) return Say(false, "Its video file is gone.");
                            Open(action == "archiveOpen" ? a.File : Path.GetDirectoryName(a.File));
                            return Say(true, action == "archiveOpen" ? "Opening the video." : "Opening its folder.");
                        }
                        case "archiveDelete":
                            // Only with the page's second click ("Sure? Delete for good").
                            if (Arg("sure") != "true") return Say(false, "Click Delete again to delete it for good.");
                            return Say(true, _montages.DeleteArchived(id));
                        case "ffmpeg":
                            if (_montages.Builder.Ffmpeg != null) return Say(true, "ffmpeg is already here.");
                            if (!OperatingSystem.IsWindows()) return Say(false, "Install ffmpeg with your package manager.");
                            _ = Task.Run(async () => { try { _ffmpegNote = "Downloading ffmpeg…"; _ffmpegNote = await _montages.Builder.DownloadFfmpegAsync(_http).ConfigureAwait(false); } catch (Exception e) { _ffmpegNote = "ffmpeg download failed: " + e.Message; } });
                            return Say(true, "Downloading ffmpeg (about 100 MB)…");
                        case "game":
                        {
                            var game = _desk.Archive.Games.LastOrDefault(g => id.Length == 0 || string.Equals(g.Lobby, id, StringComparison.OrdinalIgnoreCase));
                            if (game == null) return Say(false, "No finished game yet.");
                            _ = Task.Run(() => _montages.GameAsync(game));
                            return Say(true, $"Building a montage of {game.Lobby}'s last game…");
                        }
                        case "round":
                        {
                            int round = int.TryParse(id, out var r) ? r : _desk.Archive.Games.Select(g => g.Round).DefaultIfEmpty(0).Max();
                            if (round <= 0) return Say(false, "No round played yet.");
                            _ = Task.Run(() => _montages.RoundAsync(round, again: true));
                            return Say(true, $"Building the round {round} montage…");
                        }
                        case "custom":
                        {
                            var ids = Items("clips").Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
                            if (ids.Count == 0) return Say(false, "Pick some moments first.");
                            _ = Task.Run(() => _montages.CustomAsync(ids, Arg("title")));
                            return Say(true, $"Building a montage of {ids.Count} moment{(ids.Count == 1 ? "" : "s")}…");
                        }
                        default: return Say(false, "Unknown montage action.");
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
                    if (_desk == null) return Say(false, Locked);
                    switch (Arg("action"))
                    {
                        case "break":
                        {
                            if (_desk.BreakRefusal() is { } inBreak) return Say(false, inBreak);
                            string said = _desk.StartBreak(Arg("name").Length > 0 ? Arg("name") : null);
                            return Say(_desk.Break != null, said);
                        }
                        case "end":
                            if (_desk.Break == null || _desk.OnAir.Layout != "break") return Say(false, "No sponsor break is on.");
                            _desk.EndBreak("button");
                            return Say(true, "Back to full screen.");
                        case "open":
                            _desk.Sponsors.Refresh(force: true);
                            Open(_desk.Sponsors.Path);
                            return Say(true, "Opening sponsors.json.");
                        case "export":
                        {
                            var folder = SideFolder();
                            if (folder == null) return Say(false, "Nowhere to save it.");
                            var (csv, summary) = _desk.Sponsors.Export();
                            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm");
                            string dir = Path.Combine(folder, "Sponsor reports");
                            SafeFile.WriteAllText(Path.Combine(dir, $"sponsor appearances {stamp}.csv"), csv);
                            SafeFile.WriteAllText(Path.Combine(dir, $"sponsor summary {stamp}.txt"), summary);
                            Open(dir);
                            return Ok(new { ok = true, message = "Saved the appearance log (CSV) and summary in \"Sponsor reports\".", summary });
                        }
                        case "readDone":
                            return Say(true, _desk.Sponsors.ReadDone(Arg("name"), PlayedGames(), _desk.Tables.CurrentRound));
                        case "readSnooze":
                            return Say(true, _desk.Sponsors.ReadSnooze(Arg("name")));
                        default: return Say(false, "Unknown sponsor action.");
                    }
                }
                case ("POST", "/app/admin/spec"):
                {
                    // Real lobbies get the command from the tab, over their VDO.Ninja link; simulated ones here.
                    string cmd = Arg("command");
                    if (_desk == null || !cmd.StartsWith("spec ", StringComparison.Ordinal)) return Ok(new { ok = false });
                    return Ok(new { ok = _desk.SimSpec(Arg("lobby"), cmd) });
                }
                case ("GET", "/app/admin/specout"): return Ok(TakeSpecOut());
                case ("GET", "/app/admin/deck"): return Ok(DeckLinks());
                case ("POST", "/app/admin/numbers"):
                {
                    // Lobby numbers (the Stream Deck's lobby keys, 1–9 on the desk): set by hand, or the online
                    // lobbies numbered 1…N afresh. Kept in lobby-numbers.json.
                    if (_desk == null) return Say(false, Locked);
                    string said;
                    if (Arg("action") == "renumber")
                    {
                        var order = _desk.RenumberOnline();
                        said = order.Count == 0 ? "No lobby is sending: nothing to number." : $"Lobby numbers: {string.Join(", ", order.Select((l, i) => $"{i + 1} {l}"))}. Every other lobby gave its number up.";
                    }
                    else
                    {
                        var want = new List<(string, int)>();
                        if (Prop("numbers") is { ValueKind: JsonValueKind.Object } nums)
                            foreach (var entry in nums.EnumerateObject())
                                if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetInt32(out int n) || entry.Value.ValueKind == JsonValueKind.String && int.TryParse(entry.Value.GetString(), out n))
                                {
                                    if (n < 1 || n > CasterDesk.MaxNumber) return Say(false, $"Lobby numbers go from 1 to {CasterDesk.MaxNumber}.");
                                    want.Add((entry.Name, n));
                                }
                        if (want.Count == 0) return Say(false, "Give a lobby and its number.");
                        _desk.SetNumbers(want);
                        said = string.Join(", ", want.Select(w => $"{w.Item1} is lobby {w.Item2}")) + ". Its Stream Deck keys follow (a lobby that had the number takes the old one).";
                    }
                    DeckPoke();
                    return Ok(new { ok = true, message = said, numbers = NumberList() });
                }
                case ("POST", "/app/admin/deck"):
                    _settings.DeckKey = null;
                    DeckKey();
                    return Say(true, "New Stream Deck key: copy the links again (the old ones stop working).");
                case ("POST", "/app/admin/cam"):
                {
                    if (_desk == null) return Say(false, Locked);
                    if (Arg("all") is "on" or "off") return Say(true, CamAll(Arg("all") == "on"));
                    var (ok, message) = Cam(Arg("lobby"), Arg("follow"));
                    return Say(ok, message);
                }
                case ("POST", "/app/admin/roster"):
                {
                    if (_desk == null) return Say(false, Locked);
                    if (Arg("action") == "open")
                    {
                        Open(_desk.Roster.Path);
                        return Say(true, "Opening the roster file.");
                    }
                    string key = Arg("key");
                    if (key.Length == 0) return Say(false, "Which player?");
                    // "auto" goes back to automatic matching; "" means nobody on the roster.
                    string pick = Arg("name");
                    _desk.Roster.Override(key, pick == "auto" ? null : pick);
                    return Say(true, pick == "auto" ? "Matched automatically again." : pick.Length == 0 ? "Not on the roster." : $"Now {pick}.");
                }
                case ("POST", "/app/admin/broadcast"):
                {
                    if (_broadcast == null) return Say(false, Locked);
                    if (Arg("action") == "alerts" && _desk != null)
                    {
                        // Off-screen alerts: pause all, or one kind on/off.
                        if (Arg("kind").Length == 0)
                        {
                            bool pause = Arg("on").Length > 0 ? Arg("on") != "true" : !_desk.Alerts.Settings.Paused;
                            _desk.Alerts.SetPaused(pause);
                            return Say(true, pause ? "Off-screen alerts paused." : "Off-screen alerts back on.");
                        }
                        if (!AlertQueue.Kinds.Contains(Arg("kind"))) return Say(false, "Unknown alert.");
                        _desk.Alerts.SetType(Arg("kind"), Arg("on") == "true");
                        return Say(true, $"{Arg("kind")} alerts {(Arg("on") == "true" ? "on" : "off")}.");
                    }
                    if (Arg("action") == "theme")
                    {
                        Open(_broadcast.Settings.Path);
                        return Say(true, "Opening the theme (broadcast.json).");
                    }
                    string element = Arg("element");
                    if (!BroadcastSettings.DefaultElements().ContainsKey(element)) return Say(false, "Unknown element.");
                    _broadcast.Settings.Set(element, Arg("on") == "true");
                    // Impostor tags without a stream delay is a setup warning: check again.
                    if (element == "impostorTags" && _obs?.Connected == true) _ = Task.Run(_obs.CheckSetupAsync);
                    return Say(true, $"{BroadcastSettings.ElementNames[element]} {(Arg("on") == "true" ? "on" : "off")}.");
                }
                case ("POST", "/app/admin/graphics"):
                {
                    // The big-graphics queue: skip, clear, hold time, and the after-game prompts.
                    if (_desk == null || _broadcast == null) return Say(false, Locked);
                    switch (Arg("action"))
                    {
                        case "skip": _desk.Graphics.Skip(); return Say(true, "Skipped: the next graphic comes up.");
                        case "clear": return Say(true, ClearGraphics(_desk, _broadcast));
                        case "hold":
                            _broadcast.Settings.SetQueue(double.TryParse(Arg("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? sec : 8, null);
                            return Say(true, $"Each graphic stays {_broadcast.Settings.Current.GraphicSeconds} s.");
                        case "afterGameAuto":
                            _broadcast.Settings.SetQueue(null, Arg("on") == "true");
                            return Say(true, _broadcast.Settings.Current.AfterGameAuto ? "After a game, its table goes up by itself." : "After a game, the Live desk asks first.");
                        case "prompt": return Say(true, _desk.AnswerPrompt(Arg("id"), Arg("answer")));
                        default: return Say(false, "Unknown graphics action.");
                    }
                }
                case ("GET", "/app/admin/teamwatch"): return Ok(TeamWatchState());
                case ("POST", "/app/admin/teamwatch"): return Ok(TeamWatchAction(Arg("action"), Arg("id")));
                case ("POST", "/app/admin/suggestion"): return SuggestionAction(Arg("id"), Arg("action"), Arg("t"));
                case ("POST", "/app/admin/dismiss"):
                    _desk?.Dismiss(Arg("id"));
                    return Ok(new { ok = true });
                case ("POST", "/app/admin/note"):
                {
                    if (_desk == null) return Say(false, Locked);
                    string id = Arg("id"), action = Arg("action");
                    if (action == "show")
                    {
                        var note = _desk.Storylines.Notes(_desk.FocusKeys(), 200).FirstOrDefault(n => n.Id == id);
                        if (note == null) return Say(false, "That note is gone.");
                        _desk.ShownNote = (note.Text, DateTime.UtcNow);
                        _broadcast?.Settings.Set("storyline", true);
                        _desk.Storylines.Mark(id, "used");
                        var g = _desk.Graphics.Add("storyline", null, NameTag.Plain(note.Text), note.Text);
                        return Say(true, (_desk.Graphics.Current()?.Id == g.Id ? "On stream: " : "Queued (after what's showing): ") + NameTag.Plain(note.Text));
                    }
                    _desk.Storylines.Mark(id, action);
                    return Say(true, action switch { "pin" => "Pinned.", "unpin" => "Unpinned.", "used" => "Marked used.", "dismiss" => "Dismissed.", _ => "Done." });
                }
                case ("POST", "/app/admin/standings"):
                    if (_desk == null) return Say(false, Locked);
                    if (Arg("scope").Length > 0) _desk.StandingsScope = Arg("scope") is "overall" or "round" ? Arg("scope") : "lobby";
                    if (Arg("show").Length > 0) _broadcast?.Settings.Set("standings", Arg("show") == "true");
                    return Say(true, Arg("show") == "true" ? "Standings on stream." : Arg("show") == "false" ? "Standings off stream." : $"Standings: {(_desk.StandingsScope == "overall" ? "whole tournament" : _desk.StandingsScope == "round" ? "the round, every lobby" : "the lobby on stream")}.");
                case ("GET", "/app/admin/gamereplay"):
                {
                    var file = _desk?.Replays.File(HttpRequest.Query(query, "id"));
                    return file == null ? Text(404, "text/plain", "Not found") : (200, "application/octet-stream", file);
                }
                case ("POST", "/app/admin/multiview"):
                {
                    if (_desk == null) return Say(false, Locked);
                    var pick = Items("lobbies").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList();
                    if (pick.Count == 0) return Say(false, "Pick one or more lobbies first.");
                    var air = _desk.ShowPicked(pick, "button", Arg("layout").Length > 0 ? Arg("layout") : null);
                    string how = air.Layout switch { "full" => "Full screen", "2up" => "2-up", "4up" => "Quad", "grid" => "Grid", "cam" => "Player cam", "cams" => "Player cams", _ => air.Layout };
                    if (_desk.BreakSaid(air) is { } inBreak) return Ok(new { ok = true, message = inBreak + _desk.DownNote(air.Slots), down = _desk.DownNote(air.Slots).Trim() });
                    return Ok(new { ok = true, message = $"On stream ({how}): {string.Join(", ", air.Slots.Where(x => x != null))}." + _desk.DownNote(air.Slots), down = _desk.DownNote(air.Slots).Trim() });
                }
                case ("POST", "/app/admin/autoswitch"):
                {
                    if (_desk == null) return Say(false, Locked);
                    if (Arg("action") == "dismiss") { _desk.DismissAutoSwitch(); return Say(true, "OK."); }
                    var note = _desk.AutoSwitched;
                    var back = _desk.UndoAutoSwitch();
                    return back == null ? Say(false, "Nothing to undo.")
                        : Say(true, $"Back to what you had. Auto switch leaves {note!.Lobby} alone until it comes back and drops again.");
                }
                case ("POST", "/app/admin/voice"):
                {
                    if (_obs == null) return Say(false, Locked);
                    var v = _obs.Settings.Voice;
                    string lobby = Arg("lobby");
                    string said;
                    bool duck = false, rebuild = false;
                    switch (Arg("action"))
                    {
                        case "mute":
                            v.MuteAll = Arg("on").Length > 0 ? Arg("on") == "true" : !v.MuteAll;
                            said = v.MuteAll ? "Every lobby voice muted." : "Lobby voice back on: it follows the picture.";
                            break;
                        case "cue":
                            v.HearPreview = Arg("on").Length > 0 ? Arg("on") == "true" : !v.HearPreview;
                            said = v.HearPreview ? "Hear Preview on: the lobby in Preview plays in your headphones (OBS's monitoring device), not on stream." : "Hear Preview off.";
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
                                return Say(false, "That key can't be used: hold Ctrl, Shift or Alt with a letter, number or F key (or use an F key or Pause alone).");
                            _settings.MuteHotkey = key;
                            TrySave();
                            _obs.MuteHotkey = key;
                            HotkeyProblem = null;
                            MuteHotkeyChanged?.Invoke(key);
                            return Say(true, $"Mute all key: {key}.");
                        }
                        case "on":
                            v.On = Arg("on") == "true";
                            said = v.On ? "Lobby voice on stream." : "Lobby voice off stream.";
                            rebuild = true;
                            break;
                        default: return Say(false, "Unknown voice action.");
                    }
                    await _obs.VoiceChangedAsync(duck, rebuild).ConfigureAwait(false);

                    return Say(true, said);
                }
                case ("POST", "/app/admin/intermission"):
                {
                    if (_desk == null) return Say(false, Locked);
                    string action = Arg("action");
                    switch (action)
                    {
                        case "on" or "off" or "toggle":
                            if (_desk.BreakRefusal() is { } inBreak) return Say(false, inBreak);
                            if (action == "on" || action == "toggle" && _desk.OnAir.Layout != "intermission") { _desk.ShowIntermission(); return Say(true, "Intermission on stream."); }
                            return Say(true, $"Back to {string.Join(", ", _desk.EndIntermission().Slots.Where(x => x != null))}.");
                        case "auto":
                            // Auto intermission was removed: people switch the stream.
                            return Say(false, AutoRemoved);
                        case "dismiss": _desk.DismissIntermissionOffer(); return Say(true, "OK, not now.");
                        case "next":
                        {
                            // "20" (minutes from now), "19:30" (today, local time), or "" to clear.
                            string v = Arg("value").Trim();
                            if (v.Length == 0) { _desk.NextRoundAt = null; return Say(true, "Countdown cleared."); }
                            DateTime at;
                            if (int.TryParse(v, out int mins)) at = DateTime.UtcNow.AddMinutes(mins);
                            else if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var tod))
                            {
                                var local = DateTime.Now.Date + tod;
                                if (local < DateTime.Now) local = local.AddDays(1);
                                at = local.ToUniversalTime();
                            }
                            else return Say(false, "Type minutes (20) or a time (19:30).");
                            _desk.NextRoundAt = at;
                            return Say(true, $"Next round at {at.ToLocalTime():HH:mm}.");
                        }
                        case "montage":
                        {
                            var q = _desk.QueuedMontage;
                            if (q == null || _montages == null) return Say(false, "No montage waiting.");
                            if (_desk.BreakRefusal() is { } onBreak) return Say(false, onBreak);
                            return Say(true, await _montages.PlayAsync(q.Value.Id).ConfigureAwait(false));
                        }
                        default: return Say(false, "Unknown intermission action.");
                    }
                }
                case ("POST", "/app/admin/card"):
                {
                    if (_desk == null) return Say(false, Locked);
                    if (Arg("action") == "hide") { _desk.HidePlayerCard(); return Say(true, "Card off."); }
                    if (Arg("key").Length == 0) return Say(false, "Which player?");
                    if (_broadcast != null && !_broadcast.Settings.Current.Elements.GetValueOrDefault("playerCards")) _broadcast.Settings.Set("playerCards", true);
                    string said = _desk.ShowPlayerCard(Arg("key"), Arg("lobby").Length > 0 ? Arg("lobby") : null);
                    return Say(!said.StartsWith("Skipped", StringComparison.Ordinal), said);
                }
                case ("POST", "/app/admin/autogrid"):
                    if (_desk == null) return Say(false, Locked);
                    _desk.AutoGrid = Arg("on") == "true";
                    return Say(true, _desk.AutoGrid ? "Auto grid on: when no lobby is mid-game, the grid goes into Preview. Take shows it." : "Auto grid off.");
                case ("POST", "/app/admin/sim"):
                    if (_desk == null) return Say(false, Locked);
                    _desk.Simulate(Arg("on") == "true");
                    return Say(true, _desk.Simulating ? "Simulation on: four fake lobbies (SIM-1 to SIM-4) are playing." : "Simulation off: every fake lobby, game, clip and OBS source is gone.");
                default: return Text(404, "application/json", "{\"error\":\"not found\"}");
            }
        }

        private static string Resource(string name) => UiFiles.Text(typeof(BroadcastServer).Assembly, name);
        private static byte[] ResourceBytes(string name) => UiFiles.Bytes(typeof(BroadcastServer).Assembly, name);

        public void Dispose()
        {
            _cts.Cancel();
            _watch?.Dispose();
            _capture?.Dispose();
            _twitchTimer?.Dispose();
            _twitch?.Dispose();
            _caster?.Dispose();
            _organizer?.Dispose();
            try { _desk?.Replays.Flush(); } catch (Exception) { }
            _desk?.Dispose();
            _replays?.Dispose();
            if (_obs != null) try { _obs.DisposeAsync().AsTask().Wait(1500); } catch (Exception) { }
            try { _listener.Stop(); } catch (Exception) { }
        }
    }
}
