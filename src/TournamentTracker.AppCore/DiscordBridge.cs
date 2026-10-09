using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
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
        private string? _wroteTo;
        /// <summary>Who The Button has server-muted or deafened (and with which bot), to put right when it closes.</summary>
        private readonly Dictionary<(string Guild, string User), string> _muted = new Dictionary<(string, string), string>();

        /// <summary>How long a request for news is held open.</summary>
        public TimeSpan Wait { get; set; } = ButtonGateway.Wait;
        /// <summary>The mod hasn't asked for this long: it's closed, so the setup code's bot is used.</summary>
        public static readonly TimeSpan ModGone = TimeSpan.FromSeconds(60);

        public DiscordBridge(Func<BotConfig?> fromSetup, Func<string?> modDir, HttpClient? discord = null, Func<BotConfig, IBotGateway>? connect = null)
        {
            _fromSetup = fromSetup;
            _modDir = modDir;
            _discord = discord ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _connect = connect ?? (c => new VoiceGateway(c.Token, c.GuildId, NullLog.Instance, listenToMessages: c.Listen));
            var bytes = new byte[18];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            Token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(AcceptLoop);
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
                try { Reconcile(); WriteFile(); }
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
            lock (_lock) { _modWants = wants; _modSeen = DateTime.UtcNow; _voiceWanted = voice; }
            Reconcile();

            var until = DateTime.UtcNow + Wait;
            while (true)
            {
                Task news;
                lock (_lock)
                {
                    var state = _gateway?.State;
                    List<object>? events = null;
                    long first = _events.Count > 0 ? _events[0].Seq : _seq + 1;
                    if (epoch != _epoch || since > _seq || since < first - 1) events = Snapshot();
                    else if (_seq > since) events = _events.Where(x => x.Seq > since).Select(x => (object)new { t = x.Type, d = x.Data }).ToList();
                    if (events != null || DateTime.UtcNow >= until)
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
                var left = until - DateTime.UtcNow;
                if (left > TimeSpan.Zero) await Task.WhenAny(news, Task.Delay(left, _cts.Token)).ConfigureAwait(false);
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
        private static readonly string[] Returned = { "X-RateLimit-Remaining", "X-RateLimit-Reset-After", "X-RateLimit-Bucket", "Retry-After" };

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
            catch (Exception e) when (!_cts.IsCancellationRequested)
            {
                answer = Answer.Json(502, new { message = "Discord didn't answer: " + e.Message });
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
                lock (_lock)
                {
                    if (on) _muted[key] = auth.First();
                    else _muted.Remove(key);
                }
            }
            catch (JsonException) { }
        }

        /// <summary>Unmutes everyone The Button left muted (the game can't any more once it's closed).</summary>
        internal async Task ReleaseAllAsync(TimeSpan timeout)
        {
            List<KeyValuePair<(string Guild, string User), string>> muted;
            lock (_lock) { muted = _muted.ToList(); _muted.Clear(); }
            if (muted.Count == 0) return;
            using var cts = new CancellationTokenSource(timeout);
            await Task.WhenAll(muted.Select(async x =>
            {
                try
                {
                    using var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{DiscordRest.DefaultApiBase}/guilds/{x.Key.Guild}/members/{x.Key.User}")
                    {
                        Content = new StringContent("{\"mute\":false,\"deaf\":false}", Encoding.UTF8, "application/json"),
                    };
                    req.Headers.TryAddWithoutValidation("Authorization", x.Value);
                    req.Headers.TryAddWithoutValidation("X-Audit-Log-Reason", "The Button closed");
                    using var _ = await _discord.SendAsync(req, cts.Token).ConfigureAwait(false);
                }
                catch (Exception) { }
            })).ConfigureAwait(false);
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
