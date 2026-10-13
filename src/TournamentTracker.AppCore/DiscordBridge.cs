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
using TournamentTracker.Discord;
using TournamentTracker.Setup;

namespace TournamentTracker.App
{
    /// <summary>Which bot to keep online, in which server, and whether it reads channel messages.</summary>
    public sealed record BotConfig(string Token, string GuildId, bool Listen);

    /// <summary>
    /// The mod's way to Discord (see <see cref="ButtonBridge"/>). While The Button is open the bot
    /// stays connected, so it shows online before Among Us starts. The mod asks here for what the bot
    /// heard and sends its Discord requests through here. Only Discord addresses are passed on,
    /// and only for the mod (it has the token in the file this writes into the mod's folder).
    /// </summary>
    public sealed class DiscordBridge : IDisposable
    {
        /// <summary>The events the mod uses: who's in voice, slash commands, channel messages.</summary>
        private static readonly HashSet<string> Passed = new HashSet<string> { "READY", "GUILD_CREATE", "VOICE_STATE_UPDATE", "MESSAGE_CREATE", "INTERACTION_CREATE", VoicePresenceState.TalkingEvent };
        private static readonly Regex MemberUrl = new Regex(@"/guilds/(\d+)/members/(\d+)$");
        private const int Kept = 1000;

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly HttpClient _discord;
        private readonly Func<BotConfig?> _fromSetup;
        private readonly Func<string?> _modDir;
        private readonly Func<BotConfig, IBotGateway> _connect;
        private readonly object _lock = new object();
        private readonly List<(long Seq, string Type, JsonElement Data)> _events = new List<(long, string, JsonElement)>();
        private TaskCompletionSource<bool> _news = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private IBotGateway? _gateway;
        private BotConfig? _config;
        private string _epoch = "";
        private long _seq;
        private string? _botName;
        private BotConfig? _modWants;
        /// <summary>The voice channel the mod wants the bot in, to see who's talking (the host's).</summary>
        private string? _voiceWanted;
        private DateTime _modSeen = DateTime.MinValue;
        /// <summary>The mod's last Discord request through here.</summary>
        private DateTime _relaySeen = DateTime.MinValue;
        private string? _wroteTo;
        /// <summary>
        /// Who The Button has server-muted or deafened (and with which bot token; "" when not known,
        /// from an earlier run), to put right when the game can't any more.
        /// </summary>
        private readonly Dictionary<(string Guild, string User), string> _muted = new Dictionary<(string, string), string>();
        /// <summary>Members The Button unmutes itself (the game is gone), with when to try next.</summary>
        private readonly Dictionary<(string Guild, string User), Owed> _owed = new Dictionary<(string, string), Owed>();
        /// <summary>
        /// Members a run of The Button that didn't close properly (a crash, a power cut) had muted.
        /// Null once it's known what to do with them: a game still running from before takes them
        /// over (it sends everyone's state again when The Button is back); otherwise they're unmuted.
        /// </summary>
        private HashSet<(string Guild, string User)>? _orphans;
        private readonly DateTime _started = DateTime.UtcNow;
        private readonly DiscordRest _rest;
        private readonly SemaphoreSlim _releasing = new SemaphoreSlim(1, 1);
        private readonly object _fileLock = new object();
        private string? _savedMuted;

        private sealed class Owed
        {
            public string Token = "";
            public DateTime NextTry;
            public int Failures;
        }

        /// <summary>The file in the mod's folder listing who The Button has muted, so a crash doesn't leave them that way.</summary>
        public const string MutedFileName = "button-muted.json";

        /// <summary>How long a request for news is held open.</summary>
        public TimeSpan Wait { get; set; } = ButtonGateway.Wait;
        /// <summary>The mod hasn't asked for this long: it's closed, so the setup code's bot is used.</summary>
        public static readonly TimeSpan ModGone = TimeSpan.FromSeconds(60);

        /// <summary>
        /// The mod (Among Us) hasn't been heard from for this long: it closed or crashed, and whoever
        /// it left muted is unmuted. A running game asks again at least every <see cref="Wait"/>.
        /// </summary>
        public TimeSpan ReleaseAfter { get; set; } = ButtonGateway.Wait + TimeSpan.FromSeconds(10);

        /// <summary>
        /// News that arrives while a request waits is held this long before the answer goes back,
        /// so a burst (several people starting to talk at once, a move's two voice events) is one
        /// round trip for the mod instead of one each.
        /// </summary>
        public TimeSpan Batch { get; set; } = TimeSpan.FromMilliseconds(100);

        public DiscordBridge(Func<BotConfig?> fromSetup, Func<string?> modDir, HttpClient? discord = null, Func<BotConfig, IBotGateway>? connect = null)
        {
            _fromSetup = fromSetup;
            _modDir = modDir;
            _discord = discord ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _rest = new DiscordRest(_discord, NullLog.Instance);
            _connect = connect ?? (c => new VoiceGateway(c.Token, c.GuildId, NullLog.Instance, listenToMessages: c.Listen));
            _orphans = LoadMuted();
            Token = LocalServer.NewToken();
            _listener = LocalServer.Listen();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(() => LocalServer.AcceptLoop(_listener, _cts.Token, Serve));
            Task.Run(KeepLoop);
        }

        public int Port { get; }
        public string Token { get; }

        /// <summary>The bot from a host setup code, or the host's own (an administration code has none for this).</summary>
        public static BotConfig? FromSetupCode(string? text, HostDiscord? host = null)
        {
            if (!SetupCode.TryParse(text, out var code, out _) || code.IsAdmin) return null;
            var s = new TrackerSettings();
            code.ApplyTo(s);
            host?.ApplyTo(s);          // the host's own bots, set in The Button, come first
            if (!s.AutoMute.IsConfigured) return null;
            return new BotConfig(s.AutoMute.BotTokens[0], s.AutoMute.GuildId, s.ResultsChannelId.Length > 0 && s.Mode == TrackerMode.Tournament);
        }

        /// <summary>For The Button's screens: is the bot online, and if not, why.</summary>
        public object Status()
        {
            lock (_lock)
            {
                var state = _gateway?.State;
                return new
                {
                    Configured = _config != null,
                    Connected = state?.Connected ?? false,
                    Name = _botName,
                    Problem = state?.Problem,
                    Warning = state?.Warning,
                    Game = ModConnected,
                };
            }
        }

        /// <summary>The mod in Among Us is asking for news.</summary>
        public bool ModConnected { get { lock (_lock) return DateTime.UtcNow - _modSeen < ModGone; } }

        // ---- Keeping the bot connected -------------------------------------------------------

        private async Task KeepLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try { Reconcile(); WriteFile(); CheckReleases(); }
                catch (Exception) { }
                try { await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Connects the bot the mod wants (while it's running), else the setup code's; reconnects when that changes.</summary>
        internal void Reconcile()
        {
            BotConfig? want;
            lock (_lock) want = _modWants != null && DateTime.UtcNow - _modSeen < ModGone ? _modWants : null;
            want ??= _fromSetup();
            IBotGateway? old = null, started = null;
            lock (_lock)
            {
                // Out of voice once the mod stops asking (Among Us closed, or no longer hosting).
                string? voice = DateTime.UtcNow - _modSeen < ModGone ? _voiceWanted : null;
                if (want == _config) { _gateway?.JoinVoice(voice); return; }
                old = _gateway;
                _gateway = null;
                _config = want;
                _botName = null;
                _events.Clear();
                _epoch = Guid.NewGuid().ToString("N").Substring(0, 12);
                if (want != null)
                {
                    started = _connect(want);
                    string epoch = _epoch;
                    started.Dispatched += (type, data) => Heard(epoch, type, data);
                    _gateway = started;
                }
                Wake();
            }
            old?.Dispose();
            started?.Start();
            string? wanted;
            lock (_lock) wanted = DateTime.UtcNow - _modSeen < ModGone ? _voiceWanted : null;
            started?.JoinVoice(wanted);
        }

        private void Heard(string epoch, string type, JsonElement data)
        {
            if (!Passed.Contains(type)) return;
            if (type == "VOICE_STATE_UPDATE") BackInVoice(data);
            lock (_lock)
            {
                if (epoch != _epoch) return;
                if (type == "READY" && data.TryGetProperty("user", out var u))
                    _botName = u.TryGetProperty("global_name", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString()
                        : u.TryGetProperty("username", out var n) ? n.GetString() : null;
                _events.Add((++_seq, type, data));
                if (_events.Count > Kept) _events.RemoveRange(0, _events.Count - Kept);
                Wake();
            }
        }

        private void Wake()
        {
            var news = _news;
            _news = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            news.TrySetResult(true);
        }

        /// <summary>Tells the mod where The Button is (a file in its folder), once Among Us has the mod.</summary>
        private void WriteFile()
        {
            string? dir = _modDir();
            if (_wroteTo != null && _wroteTo != dir) { ButtonBridge.Remove(_wroteTo, Token); _wroteTo = null; }
            if (dir == null || !Directory.Exists(Path.GetDirectoryName(dir))) return;
            if (ButtonBridge.Read(dir) is { } link && link.Port == Port && link.Token == Token) { _wroteTo = dir; return; }
            ButtonBridge.Write(dir, Port, Token);
            _wroteTo = dir;
        }

        // ---- Serving the mod -------------------------------------------------------------------

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var (method, path, headers, body) = await HttpRequest.ReadRawAsync(stream, 30_000_000).ConfigureAwait(false);
                    var answer = await RouteAsync(method, path, headers, body).ConfigureAwait(false);
                    await WriteAsync(stream, answer).ConfigureAwait(false);
                }
                catch (Exception) { /* the game closed mid-request */ }
            }
        }

        internal sealed class Answer
        {
            public int Status { get; set; } = 200;
            public string Type { get; set; } = "application/json";
            public byte[] Body { get; set; } = Array.Empty<byte>();
            public List<(string, string)> Headers { get; } = new List<(string, string)>();
            public static Answer Json(int status, object value) => new Answer { Status = status, Body = JsonSerializer.SerializeToUtf8Bytes(value) };
        }

        internal async Task<Answer> RouteAsync(string method, string path, Dictionary<string, string> headers, byte[] body)
        {
            // Only the mod: it has the token from the file in its folder.
            if (!headers.TryGetValue(ButtonBridge.TokenHeader.ToLowerInvariant(), out var token) || token != Token)
                return Answer.Json(403, new { message = "Forbidden" });
            string route = path.Split('?')[0];
            if (route == "/gateway" && method == "POST") return await GatewayAsync(body).ConfigureAwait(false);
            if (route == "/discord") return await RelayAsync(method, headers, body).ConfigureAwait(false);
            return Answer.Json(404, new { message = "Not found" });
        }

        /// <summary>The mod asks what the bot heard since last time (waiting up to <see cref="Wait"/> for news).</summary>
        private async Task<Answer> GatewayAsync(byte[] body)
        {
            BotConfig? wants = null;
            string? voice = null;
            long since = 0;
            string epoch = "";
            try
            {
                var j = JsonDocument.Parse(body).RootElement;
                string t = j.GetProperty("token").GetString() ?? "", g = j.GetProperty("guild").GetString() ?? "";
                if (t.Length > 0 && g.Length > 0) wants = new BotConfig(t, g, j.TryGetProperty("listen", out var l) && l.ValueKind == JsonValueKind.True);
                since = j.TryGetProperty("since", out var s) && s.TryGetInt64(out var n) ? n : 0;
                epoch = j.TryGetProperty("epoch", out var e) ? e.GetString() ?? "" : "";
                voice = j.TryGetProperty("voice", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } vc ? vc : null;
            }
            catch (Exception) { return Answer.Json(400, new { message = "Bad request" }); }
            bool decided;
            lock (_lock)
            {
                _modWants = wants;
                _modSeen = DateTime.UtcNow;
                _voiceWanted = voice;
                decided = _orphans != null;
                if (decided) DecideOrphans(survivor: epoch.Length > 0 && epoch != _epoch);
            }
            if (decided)
            {
                SaveMuted();
                _ = ReleaseAsync(all: false, CancellationToken.None);
            }
            Reconcile();

            var until = DateTime.UtcNow + Wait;
            bool waited = false, batched = false;
            while (true)
            {
                Task news;
                bool batch = false;
                lock (_lock)
                {
                    var state = _gateway?.State;
                    List<object>? events = null;
                    long first = _events.Count > 0 ? _events[0].Seq : _seq + 1;
                    if (epoch != _epoch || since > _seq || since < first - 1) events = Snapshot();
                    else if (_seq > since)
                    {
                        // News came while this waited: the rest of a burst gets a moment to arrive.
                        if (waited && !batched && Batch > TimeSpan.Zero) batch = true;
                        else events = _events.Where(x => x.Seq > since).Select(x => (object)new { t = x.Type, d = x.Data }).ToList();
                    }
                    if (!batch && (events != null || DateTime.UtcNow >= until))
                        return Answer.Json(200, new
                        {
                            epoch = _epoch,
                            last = _seq,
                            connected = state?.Connected ?? false,
                            problem = state?.Problem,
                            warning = state?.Warning,
                            events = events ?? new List<object>(),
                        });
                    news = _news.Task;
                }
                if (batch)
                {
                    batched = true;
                    try { await Task.Delay(Batch, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return Answer.Json(503, new { message = "Closing" }); }
                    continue;
                }
                var left = until - DateTime.UtcNow;
                if (left > TimeSpan.Zero)
                {
                    // The timer is stopped as soon as news comes, rather than left running for the rest of the wait.
                    using var stop = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    await Task.WhenAny(news, Task.Delay(left, stop.Token)).ConfigureAwait(false);
                    stop.Cancel();
                }
                waited = true;
                if (_cts.IsCancellationRequested) return Answer.Json(503, new { message = "Closing" });
            }
        }

        /// <summary>What a newly started mod needs to catch up: the bot's identity and who's in voice now.</summary>
        private List<object> Snapshot()
        {
            var list = new List<object>();
            var state = _gateway?.State;
            if (state?.BotUserId == null || _config == null) return list;
            list.Add(new { t = "READY", d = new { user = new { id = state.BotUserId }, application = new { id = state.ApplicationId } } });
            var members = state.Members;
            list.Add(new
            {
                t = "GUILD_CREATE",
                d = new
                {
                    id = _config.GuildId,
                    voice_states = members.Select(m => new { user_id = m.UserId, channel_id = m.ChannelId }).ToList(),
                    members = members.Select(m => new { nick = m.Nick, user = new { id = m.UserId, username = m.Username, global_name = m.GlobalName, bot = m.IsBot } }).ToList(),
                },
            });
            return list;
        }

        private static readonly string[] Copied = { "Authorization", "X-Audit-Log-Reason" };
        private static readonly string[] Returned = { "X-RateLimit-Remaining", "X-RateLimit-Reset-After", "X-RateLimit-Bucket", "X-RateLimit-Global", "X-RateLimit-Scope", "Retry-After" };

        /// <summary>Sends one of the mod's requests on to Discord, and Discord's answer back.</summary>
        private async Task<Answer> RelayAsync(string method, Dictionary<string, string> headers, byte[] body)
        {
            if (!headers.TryGetValue(ButtonBridge.TargetHeader.ToLowerInvariant(), out var target) || !Uri.TryCreate(target, UriKind.Absolute, out var url) || !ButtonBridge.IsDiscord(url))
                return Answer.Json(400, new { message = "Only Discord addresses" });
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            foreach (var name in Copied)
                if (headers.TryGetValue(name.ToLowerInvariant(), out var v)) request.Headers.TryAddWithoutValidation(name, v);
            request.Headers.TryAddWithoutValidation("User-Agent", headers.TryGetValue("user-agent", out var ua) ? ua : "DiscordBot (https://github.com/Ljbutton/AU, 1.0)");
            if (body.Length > 0 || method is "POST" or "PUT" or "PATCH")
            {
                request.Content = new ByteArrayContent(body);
                if (headers.TryGetValue("content-type", out var ct)) request.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
            }
            Answer answer;
            lock (_lock) _relaySeen = DateTime.UtcNow;
            try
            {
                using var response = await _discord.SendAsync(request, _cts.Token).ConfigureAwait(false);
                answer = new Answer
                {
                    Status = (int)response.StatusCode,
                    Type = response.Content.Headers.ContentType?.ToString() ?? "application/json",
                    Body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false),
                };
                foreach (var name in Returned)
                    if (response.Headers.TryGetValues(name, out var values)) answer.Headers.Add((name, values.First()));
                if (response.IsSuccessStatusCode && method == "PATCH") NoteMute(url, request, body);
            }
            catch (Exception e) when (!_cts.IsCancellationRequested && e is HttpRequestException && e.InnerException is SocketException)
            {
                // No connection to Discord at all: nothing was sent, so the mod may safely send it again.
                answer = Answer.Json(503, new { message = "Couldn't reach Discord: " + e.Message });
                answer.Headers.Add((DiscordRest.NotSentHeader, "1"));
            }
            catch (Exception e) when (!_cts.IsCancellationRequested)
            {
                // Sent, but no answer (a timeout): it may have gone through, which the mod has to know
                // before it posts a report again.
                answer = Answer.Json(504, new { message = "Discord didn't answer: " + e.Message });
            }
            answer.Headers.Add((ButtonBridge.RelayedHeader, "1"));
            return answer;
        }

        /// <summary>Remembers who's server-muted or deafened, so closing The Button doesn't leave them that way.</summary>
        private void NoteMute(Uri url, HttpRequestMessage request, byte[] body)
        {
            var m = MemberUrl.Match(url.AbsolutePath);
            if (!m.Success || !request.Headers.TryGetValues("Authorization", out var auth)) return;
            try
            {
                var j = JsonDocument.Parse(body).RootElement;
                bool on = j.TryGetProperty("mute", out var mu) && mu.ValueKind == JsonValueKind.True
                    || j.TryGetProperty("deaf", out var de) && de.ValueKind == JsonValueKind.True;
                var key = (m.Groups[1].Value, m.Groups[2].Value);
                string token = auth.First();
                if (token.StartsWith("Bot ", StringComparison.Ordinal)) token = token.Substring(4);
                lock (_lock)
                {
                    // The game decides about this member again: anything owed from before is dropped.
                    _owed.Remove(key);
                    _orphans?.Remove(key);
                    if (on) _muted[key] = token;
                    else _muted.Remove(key);
                }
                SaveMuted();
            }
            catch (JsonException) { }
        }

        /// <summary>
        /// The first word from the mod since The Button started decides about members an earlier run
        /// left muted: a game that was running before (it carries the old run's epoch) sends
        /// everyone's state again itself, so they're kept as muted; a game that started afresh
        /// doesn't know them, so they're unmuted. Called with <see cref="_lock"/> held.
        /// </summary>
        private void DecideOrphans(bool survivor)
        {
            if (_orphans == null) return;
            foreach (var key in _orphans)
            {
                if (survivor) _muted.TryAdd(key, "");
                else Owe(key, "");
            }
            _orphans = null;
        }

        /// <summary>This member is to be unmuted by The Button (with <see cref="_lock"/> held).</summary>
        private void Owe((string Guild, string User) key, string token)
        {
            if (!_owed.TryGetValue(key, out var owed)) _owed[key] = owed = new Owed();
            if (token.Length > 0) owed.Token = token;
            owed.NextTry = DateTime.MinValue;
        }

        /// <summary>
        /// Every few seconds: the game has gone quiet (Among Us closed or crashed while The Button
        /// stays open), or no game came back after a crash. Whoever it left muted is unmuted.
        /// </summary>
        internal void CheckReleases()
        {
            bool changed = false;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (_orphans != null && now - _started >= ReleaseAfter)
                {
                    DecideOrphans(survivor: false);
                    changed = true;
                }
                var heard = _modSeen > _relaySeen ? _modSeen : _relaySeen;
                if (_muted.Count > 0 && now - heard >= ReleaseAfter)
                {
                    foreach (var kv in _muted) Owe(kv.Key, kv.Value);
                    _muted.Clear();
                    changed = true;
                }
                if (_owed.Count == 0 && !changed) return;
            }
            if (changed) SaveMuted();
            _ = ReleaseAsync(all: false, CancellationToken.None);
        }

        /// <summary>Someone owed an unmute came back to voice: try now (Discord can only unmute people in voice).</summary>
        private void BackInVoice(JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("user_id", out var u) || u.ValueKind != JsonValueKind.String
                || !data.TryGetProperty("channel_id", out var c) || c.ValueKind != JsonValueKind.String) return;
            string user = u.GetString() ?? "";
            string guild = data.TryGetProperty("guild_id", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : "";
            lock (_lock)
            {
                if (!_owed.TryGetValue((guild, user), out var owed)) return;
                owed.NextTry = DateTime.MinValue;
            }
            _ = ReleaseAsync(all: false, CancellationToken.None);
        }

        /// <summary>A bot token that can unmute in this server: the one that muted, else the mod's or the setup code's bot.</summary>
        private string? TokenFor(string guild, string known)
        {
            if (known.Length > 0) return known;
            lock (_lock)
            {
                if (_modWants != null && _modWants.GuildId == guild) return _modWants.Token;
                if (_config != null && _config.GuildId == guild) return _config.Token;
            }
            BotConfig? setup = null;
            try { setup = _fromSetup(); } catch (Exception) { }
            return setup != null && setup.GuildId == guild ? setup.Token : null;
        }

        /// <summary>
        /// Unmutes and undeafens who's owed it (<paramref name="all"/>: everyone, now), a few at a
        /// time, through Discord's rate limit. Whoever isn't done stays in the file in the mod's
        /// folder: tried again later, when they're back in voice, or at the next start.
        /// </summary>
        private async Task ReleaseAsync(bool all, CancellationToken ct)
        {
            if (!all && !_releasing.Wait(0)) return;              // a round is already going
            if (all && !await _releasing.WaitAsync(Timeout.Infinite, ct).ConfigureAwait(false)) return;
            try
            {
                List<((string Guild, string User) Key, Owed Owed)> due;
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    due = _owed.Where(kv => all || kv.Value.NextTry <= now).Select(kv => (kv.Key, kv.Value)).ToList();
                    // Not tried again before its time, even if this round doesn't get to it.
                    foreach (var (_, owed) in due) owed.NextTry = now + TimeSpan.FromSeconds(15);
                }
                if (due.Count == 0) return;
                using var lanes = new SemaphoreSlim(MuteLanes, MuteLanes);
                await Task.WhenAll(due.Select(async item =>
                {
                    await lanes.WaitAsync(ct).ConfigureAwait(false);
                    try { await ReleaseOneAsync(item.Key, item.Owed, ct).ConfigureAwait(false); }
                    finally { lanes.Release(); }
                })).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally
            {
                _releasing.Release();
                SaveMuted();
            }
        }

        /// <summary>Unmutes sent at once (like the mod's own, per bot): quick, and Discord's limit is still honoured.</summary>
        private const int MuteLanes = 4;

        private async Task ReleaseOneAsync((string Guild, string User) key, Owed owed, CancellationToken ct)
        {
            string? token = TokenFor(key.Guild, owed.Token);
            if (token == null) return;                     // no bot for this server yet: tried again later
            DiscordResult result;
            try { result = await _rest.SetVoiceStateAsync(token, key.Guild, key.User, () => (false, false), "The Button closed", ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { result = new DiscordResult { Ok = false }; }
            lock (_lock)
            {
                // The game took this member over meanwhile.
                if (!_owed.TryGetValue(key, out var current) || current != owed) return;
                if (result.Ok || (result.Status >= 400 && result.Status < 500 && result.Status != 429 && !result.IsNotInVoice))
                {
                    // Done, or never possible (they left the server, the bot lost its permission).
                    _owed.Remove(key);
                    return;
                }
                owed.Failures++;
                owed.NextTry = DateTime.UtcNow + (result.IsNotInVoice
                    ? TimeSpan.FromMinutes(5)                // or as soon as the gateway sees them back in voice
                    : TimeSpan.FromSeconds(Math.Min(300, 15 << Math.Min(owed.Failures - 1, 5))));
            }
        }

        /// <summary>Unmutes everyone The Button left muted (the game can't any more once it's closed).</summary>
        internal async Task ReleaseAllAsync(TimeSpan timeout)
        {
            lock (_lock)
            {
                // Not heard from a game since the start: whoever an earlier run left muted is let go too.
                DecideOrphans(survivor: false);
                foreach (var kv in _muted) Owe(kv.Key, kv.Value);
                _muted.Clear();
                if (_owed.Count == 0) return;
            }
            SaveMuted();
            using var cts = new CancellationTokenSource(timeout);
            await ReleaseAsync(all: true, cts.Token).ConfigureAwait(false);
        }

        // ---- The list of muted members, kept on disk ---------------------------------------------

        private string? MutedFile()
        {
            string? dir = _wroteTo;
            if (dir == null)
            {
                try { dir = _modDir(); } catch (Exception) { dir = null; }
            }
            return dir == null ? null : Path.Combine(dir, MutedFileName);
        }

        /// <summary>Who an earlier run left muted (null: nobody).</summary>
        private HashSet<(string Guild, string User)>? LoadMuted()
        {
            try
            {
                string? file = MutedFile();
                if (file == null || !File.Exists(file)) return null;
                var set = new HashSet<(string, string)>();
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("muted", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var e in list.EnumerateArray())
                        if (e.TryGetProperty("guild", out var g) && e.TryGetProperty("user", out var u)
                            && g.GetString() is { Length: > 0 } guild && u.GetString() is { Length: > 0 } user)
                            set.Add((guild, user));
                _savedMuted = null;
                return set.Count > 0 ? set : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Writes who The Button has muted (server and user IDs, no tokens) next to the mod's
        /// button.json, so the next start can unmute them if The Button or the game crashed.
        /// </summary>
        private void SaveMuted()
        {
            List<(string Guild, string User)> keys;
            lock (_lock)
            {
                keys = _muted.Keys.Concat(_owed.Keys).Concat(_orphans ?? Enumerable.Empty<(string, string)>())
                    .Distinct().OrderBy(k => k.Item1, StringComparer.Ordinal).ThenBy(k => k.Item2, StringComparer.Ordinal).ToList();
            }
            string json = JsonSerializer.Serialize(new { muted = keys.Select(k => new { guild = k.Guild, user = k.User }) });
            lock (_fileLock)
            {
                if (json == _savedMuted) return;
                try
                {
                    string? file = MutedFile();
                    if (file == null || !Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(file)))) return;
                    if (keys.Count == 0)
                    {
                        if (File.Exists(file)) File.Delete(file);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        string temp = file + ".tmp";
                        File.WriteAllText(temp, json);
                        File.Move(temp, file, overwrite: true);
                    }
                    _savedMuted = json;
                }
                catch (Exception) { }
            }
        }

        private static async Task WriteAsync(Stream stream, Answer a)
        {
            var head = new StringBuilder();
            head.Append($"HTTP/1.1 {a.Status} {(a.Status == 200 ? "OK" : "Status")}\r\nContent-Type: {a.Type}\r\nContent-Length: {a.Body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n");
            foreach (var (name, value) in a.Headers) head.Append(name).Append(": ").Append(value).Append("\r\n");
            head.Append("\r\n");
            var bytes = Encoding.ASCII.GetBytes(head.ToString());
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(a.Body, 0, a.Body.Length).ConfigureAwait(false);
        }

        public void Dispose()
        {
            try { ReleaseAllAsync(TimeSpan.FromSeconds(3)).Wait(TimeSpan.FromSeconds(4)); } catch (Exception) { }
            _cts.Cancel();
            try { _listener.Stop(); } catch (Exception) { }
            if (_wroteTo != null) ButtonBridge.Remove(_wroteTo, Token);
            IBotGateway? g;
            lock (_lock) { g = _gateway; _gateway = null; }
            g?.Dispose();
        }
    }
}
