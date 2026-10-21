using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TournamentTracker.Control;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>Someone signed in to the Watch page: their name, where from, and when they were last seen.</summary>
    public sealed class Watcher
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Ip { get; set; } = "";
        public DateTime SignedIn { get; set; }
        public DateTime LastSeen { get; set; }
        internal string Token = "";
    }

    /// <summary>
    /// The Watch page for the production team (Settings → Team watch): every lobby at once, on any PC or
    /// tablet on the same network. Red Alert's only server that listens beyond this PC, and only while
    /// Team watch is on (off by default); on its own port (8780, else the next free one), with a plain
    /// TcpListener like the others (no URL reservation or admin rights needed).
    /// <para>
    /// Nothing but the sign-in page (and its fonts and look) answers without signing in. Signing in takes
    /// the watch code shown in Settings (and a name), and gives a token: kept by the page and sent with
    /// every request, and as an HttpOnly cookie for the pictures. Every other route checks it; there's
    /// no trust in the Host header or where a request comes from. Wrong codes are limited per address
    /// (and overall), so the code can't be guessed. A new code (or Team watch off) signs everyone out.
    /// The code is never logged and is taken out of support bundles.
    /// </para>
    /// Bounded: <see cref="MaxWatchers"/> people at once, <see cref="MaxConnections"/> requests at once,
    /// each read within <see cref="ReadTimeout"/>, small bodies only.
    /// </summary>
    public sealed class WatchServer : IDisposable
    {
        public const int DefaultPort = 8780;
        public const int MaxWatchers = 12;
        public const int MaxConnections = 96;
        public const int MaxName = 24;
        public const int CodeLength = 8;
        /// <summary>Wrong codes from one address in a minute before it has to wait; and from everyone together.</summary>
        public const int WrongPerMinute = 5, WrongPerMinuteAll = 40;
        /// <summary>Easy to read out and type: no 0/O, 1/I/L.</summary>
        public const string CodeLetters = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        public const string Cookie = "ttwatch";
        /// <summary>Seen this recently: watching now (the pictures are taken only while someone is).</summary>
        public static TimeSpan WatchingFor { get; set; } = TimeSpan.FromSeconds(10);
        internal static TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(10);

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly object _lock = new object();
        private readonly Func<DateTime> _clock;
        private readonly int _wantPort;
        private readonly Dictionary<string, Watcher> _byToken = new Dictionary<string, Watcher>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DateTime>> _wrong = new Dictionary<string, List<DateTime>>();
        private readonly List<DateTime> _wrongAll = new List<DateTime>();
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private string _code;
        private int _open;
        private long _seq;

        /// <summary>What the page shows once signed in: every lobby (the same for everyone).</summary>
        public Func<object>? State { get; set; }
        /// <summary>A lobby's latest picture (a small JPEG), or null.</summary>
        public Func<string, byte[]?>? Frame { get; set; }
        /// <summary>A "Show this" from a watcher: the HTTP status and what to say.</summary>
        public Func<Watcher, JsonElement, (int Status, string Message)>? Suggest { get; set; }
        /// <summary>Said in Red Alert's log (sign-ins, wrong codes: never the code itself).</summary>
        public Action<string>? Log { get; set; }

        /// <summary>The port it listens on while on (0 when off).</summary>
        public int Port { get; private set; }
        public bool Listening => _listener != null;
        /// <summary>Why it isn't listening although it should be (every port taken), or null.</summary>
        public string? Problem { get; private set; }

        /// <param name="port">The port to listen on (8780, else the next free one up to 8789); 0: any free one (tests).</param>
        /// <param name="code">The code kept from before; a new one when there's none (or it isn't one).</param>
        public WatchServer(int port = DefaultPort, string? code = null, Func<DateTime>? clock = null)
        {
            _wantPort = port;
            _clock = clock ?? (() => DateTime.UtcNow);
            _code = IsCode(code) ? Normal(code!) : MakeCode();
        }

        /// <summary>The watch code (8 easy letters and numbers).</summary>
        public string Code { get { lock (_lock) return _code; } }

        /// <summary>A code as typed ("k7mp-q4xr", " K7MP Q4XR ") the way it's kept: capitals, no spaces or dashes.</summary>
        public static string Normal(string code) => new string((code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());

        public static bool IsCode(string? code) => code != null && Normal(code) is { Length: CodeLength } n && n.All(c => CodeLetters.Contains(c));

        public static string MakeCode()
        {
            var chars = new char[CodeLength];
            for (int i = 0; i < CodeLength; i++) chars[i] = CodeLetters[RandomNumberGenerator.GetInt32(CodeLetters.Length)];
            return new string(chars);
        }

        /// <summary>A new code: everyone signed in is signed out (and has to type the new one).</summary>
        public string NewCode()
        {
            lock (_lock)
            {
                _code = MakeCode();
                _byToken.Clear();
                return _code;
            }
        }

        // ---- On and off ---------------------------------------------------------------------------------

        /// <summary>Listens on every network (Team watch on). False (with <see cref="Problem"/>) when no port was free.</summary>
        public bool Start()
        {
            lock (_lock)
            {
                if (_listener != null) return true;
                var ports = _wantPort == 0 ? new[] { 0 } : Enumerable.Range(_wantPort, 10).ToArray();
                foreach (int port in ports)
                {
                    var listener = new TcpListener(IPAddress.Any, port);
                    try { listener.Start(); }
                    catch (SocketException) { continue; }
                    _listener = listener;
                    Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    Problem = null;
                    var cts = _cts = new CancellationTokenSource();
                    _ = Task.Run(() => LocalServer.AcceptLoop(listener, cts.Token, client => ServeAsync(client)));
                    return true;
                }
                Problem = $"Ports {_wantPort}–{_wantPort + 9} are all taken: Team watch can't start.";
                return false;
            }
        }

        /// <summary>Stops listening (Team watch off): everyone is signed out.</summary>
        public void Stop()
        {
            lock (_lock)
            {
                _cts?.Cancel();
                _cts = null;
                try { _listener?.Stop(); } catch (Exception) { }
                _listener = null;
                Port = 0;
                Problem = null;
                _byToken.Clear();
            }
        }

        public void Dispose() => Stop();

        // ---- Who's watching ---------------------------------------------------------------------------------

        /// <summary>Everyone signed in, the most recently seen first.</summary>
        public List<Watcher> Watchers()
        {
            lock (_lock) return _byToken.Values.OrderByDescending(w => w.LastSeen).Select(w => new Watcher { Id = w.Id, Name = w.Name, Ip = w.Ip, SignedIn = w.SignedIn, LastSeen = w.LastSeen }).ToList();
        }

        /// <summary>Someone has the page open now (seen within <see cref="WatchingFor"/>): the pictures are worth taking.</summary>
        public bool AnyoneWatching
        {
            get
            {
                var now = _clock();
                lock (_lock) return _listener != null && _byToken.Values.Any(w => now - w.LastSeen < WatchingFor);
            }
        }

        /// <summary>Signs one person out (Settings → Team watch). False when they weren't signed in.</summary>
        public bool SignOut(string id)
        {
            lock (_lock)
            {
                var hit = _byToken.FirstOrDefault(kv => kv.Value.Id == id);
                if (hit.Value == null) return false;
                _byToken.Remove(hit.Key);
                return true;
            }
        }

        /// <summary>
        /// Signing in from <paramref name="ip"/>: the code and a name. 200 with a token; 401 for a wrong code;
        /// 429 when this address (or everyone together) has had too many wrong codes in the last minute (even the
        /// right code waits, so the code can't be guessed); 400 without a name; 409 when <see cref="MaxWatchers"/>
        /// people are watching already.
        /// </summary>
        public (int Status, string Message, Watcher? Who) SignIn(string ip, string code, string name)
        {
            var now = _clock();
            name = SuggestionBox.Clean(name);
            if (name.Length > MaxName) name = name.Substring(0, MaxName).Trim();
            lock (_lock)
            {
                if (!_wrong.TryGetValue(ip, out var mine)) mine = new List<DateTime>();
                mine.RemoveAll(t => (now - t).TotalSeconds >= 60);
                _wrongAll.RemoveAll(t => (now - t).TotalSeconds >= 60);
                if (mine.Count >= WrongPerMinute || _wrongAll.Count >= WrongPerMinuteAll)
                    return (429, "Too many wrong codes. Wait a minute, then try again.", null);
                if (!SameCode(code, _code))
                {
                    mine.Add(now);
                    _wrongAll.Add(now);
                    _wrong[ip] = mine;
                    if (_wrong.Count > 1000) foreach (var k in _wrong.Where(kv => kv.Value.All(t => (now - t).TotalSeconds >= 60)).Select(kv => kv.Key).ToList()) _wrong.Remove(k);
                    Log?.Invoke($"Team watch: a wrong code from {ip}.");
                    return (401, "That code isn't right. It's in Red Alert: Settings → Team watch.", null);
                }
                if (name.Length == 0) return (400, "Type your name, so the caster knows who's suggesting.", null);
                if (_byToken.Count >= MaxWatchers)
                {
                    // Room is made from whoever has been gone longest (not seen for a while).
                    var gone = _byToken.Where(kv => now - kv.Value.LastSeen > TimeSpan.FromMinutes(2)).OrderBy(kv => kv.Value.LastSeen).Select(kv => kv.Key).FirstOrDefault();
                    if (gone == null) return (409, $"{MaxWatchers} people are watching already. Ask the caster to sign someone out.", null);
                    _byToken.Remove(gone);
                }
                var w = new Watcher { Id = "w" + (++_seq), Name = name, Ip = ip, SignedIn = now, LastSeen = now, Token = LocalServer.NewToken(24) };
                _byToken[w.Token] = w;
                Log?.Invoke($"Team watch: {name} signed in from {ip}.");
                return (200, $"Signed in as {name}.", Copy(w, withToken: true));
            }
        }

        /// <summary>The watcher with this token (noted as seen now), or null.</summary>
        public Watcher? Check(string? token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            var now = _clock();
            lock (_lock)
            {
                if (!_byToken.TryGetValue(token, out var w)) return null;
                w.LastSeen = now;
                return Copy(w);
            }
        }

        private static Watcher Copy(Watcher w, bool withToken = false) =>
            new Watcher { Id = w.Id, Name = w.Name, Ip = w.Ip, SignedIn = w.SignedIn, LastSeen = w.LastSeen, Token = withToken ? w.Token : "" };

        /// <summary>The same code, compared without giving away how much of it was right.</summary>
        private static bool SameCode(string typed, string code)
        {
            var a = Encoding.ASCII.GetBytes(Normal(typed ?? ""));
            var b = Encoding.ASCII.GetBytes(code);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        /// <summary>This PC's addresses on its networks (IPv4), for the links Settings shows the team.</summary>
        public static List<string> LocalAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var a in nic.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                            list.Add(a.Address.ToString());
                }
            }
            catch (Exception) { }
            // Home and venue networks (192.168.…, 10.…, 172.16–31.…) first.
            return list.Distinct().OrderBy(a => a.StartsWith("192.168.", StringComparison.Ordinal) ? 0 : a.StartsWith("10.", StringComparison.Ordinal) ? 1 : a.StartsWith("172.", StringComparison.Ordinal) ? 2 : 3).ToList();
        }

        // ---- Serving ----------------------------------------------------------------------------------------

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                if (Interlocked.Increment(ref _open) > MaxConnections) { Interlocked.Decrement(ref _open); return; }
                try
                {
                    string ip = client.Client.RemoteEndPoint is IPEndPoint ep ? (ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address).ToString() : "?";
                    var stream = client.GetStream();
                    // A request that doesn't arrive in time is dropped (nobody holds a connection open for nothing).
                    var read = HttpRequest.ReadRawAsync(stream, 4096);
                    if (await Task.WhenAny(read, Task.Delay(ReadTimeout)).ConfigureAwait(false) != read) return;
                    var (method, path, headers, body) = await read.ConfigureAwait(false);
                    var answer = await Task.Run(() => Route(method, path, headers, Encoding.UTF8.GetString(body), ip)).ConfigureAwait(false);
                    await WriteAsync(stream, answer).ConfigureAwait(false);
                }
                catch (Exception) { }
                finally { Interlocked.Decrement(ref _open); }
            }
        }

        /// <summary>An answer: status, type, body, and a cookie to set (or null).</summary>
        internal readonly record struct Answer(int Status, string Type, byte[] Body, string? SetCookie = null);

        private static async Task WriteAsync(NetworkStream stream, Answer a)
        {
            string reason = a.Status switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 409 => "Conflict", 429 => "Too Many Requests", _ => "Error" };
            var head = new StringBuilder();
            head.Append($"HTTP/1.1 {a.Status} {reason}\r\nContent-Type: {a.Type}\r\nContent-Length: {a.Body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n");
            // Not framed by another site (the sign-in can't be dressed up), nothing sniffed, no address passed on to the hosts' video.
            head.Append("X-Content-Type-Options: nosniff\r\nX-Frame-Options: SAMEORIGIN\r\nReferrer-Policy: no-referrer\r\n");
            if (a.Type.StartsWith("text/html", StringComparison.Ordinal))
                head.Append("Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; frame-src 'self' https:; connect-src 'self'; frame-ancestors 'self'\r\n");
            if (a.SetCookie != null) head.Append("Set-Cookie: ").Append(a.SetCookie).Append("\r\n");
            head.Append("\r\n");
            var bytes = Encoding.ASCII.GetBytes(head.ToString());
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(a.Body, 0, a.Body.Length).ConfigureAwait(false);
        }

        private static Answer Reply(int status, object value) => new Answer(status, "application/json", JsonSerializer.SerializeToUtf8Bytes(value, Json));
        private static Answer Say(int status, string message) => Reply(status, new { ok = status is >= 200 and < 300, message });

        /// <summary>The token a request carries: its X-Watch-Token header, else the page's cookie.</summary>
        private static string? TokenOf(Dictionary<string, string> headers, bool headerOnly)
        {
            if (headers.TryGetValue("x-watch-token", out var t) && t.Length > 0) return t;
            if (headerOnly || !headers.TryGetValue("cookie", out var cookies)) return null;
            foreach (var part in cookies.Split(';'))
            {
                var kv = part.Trim();
                if (kv.StartsWith(Cookie + "=", StringComparison.Ordinal)) return kv.Substring(Cookie.Length + 1);
            }
            return null;
        }

        /// <summary>One request, answered (public routes, then everything that needs the token).</summary>
        internal Answer Route(string method, string path, Dictionary<string, string> headers, string body, string ip)
        {
            string route = path.Split('?')[0];
            string query = path.Contains('?') ? path.Substring(path.IndexOf('?') + 1) : "";
            var asm = typeof(WatchServer).Assembly;
            // The page (it opens on the sign-in), its look and fonts: nothing about the games.
            if (method == "GET" && (route == "/" || route == "/watch" || route == "/index.html"))
                return new Answer(200, "text/html; charset=utf-8", UiFiles.Bytes(asm, "ui/watch.html"));
            if (method == "GET" && UiFiles.Base(asm, route) is { } shared) return new Answer(200, shared.Type, shared.Body);
            if (method == "GET" && UiFiles.Asset(asm, route) is { } asset) return new Answer(200, asset.Type, asset.Body);
            if (method == "POST" && route == "/watch/signin")
            {
                JsonElement input = default;
                try { if (body.Length > 0) input = JsonDocument.Parse(body).RootElement.Clone(); } catch (Exception) { }
                string Arg(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var (status, message, who) = SignIn(ip, Arg("code"), Arg("name"));
                if (who == null) return Say(status, message);
                var a = Reply(200, new { ok = true, message, token = who.Token, name = who.Name });
                return a with { SetCookie = $"{Cookie}={who.Token}; Path=/; HttpOnly; SameSite=Strict" };
            }
            if (!route.StartsWith("/watch/", StringComparison.Ordinal)) return Say(404, "Not found.");

            // Everything else: signed in. Changes need the token in the header (a page elsewhere can't send it).
            var watcher = Check(TokenOf(headers, headerOnly: method != "GET"));
            if (watcher == null) return Say(401, "Sign in again: the watch code changed, or Team watch was turned off.");
            switch ((method, route))
            {
                case ("GET", "/watch/me"):
                    lock (_lock) return Reply(200, new { ok = true, name = watcher.Name, token = _byToken.FirstOrDefault(kv => kv.Value.Id == watcher.Id).Key });
                case ("GET", "/watch/state"):
                    return new Answer(200, "application/json", State?.Invoke() is { } st ? (st as byte[] ?? JsonSerializer.SerializeToUtf8Bytes(st, Json)) : Encoding.UTF8.GetBytes("{}"));
                case ("GET", "/watch/frame"):
                {
                    var jpg = Frame?.Invoke(HttpRequest.Query(query, "lobby"));
                    return jpg == null ? new Answer(204, "image/jpeg", Array.Empty<byte>()) : new Answer(200, "image/jpeg", jpg);
                }
                case ("GET", "/watch/sim"):
                    // The simulation's stand-in picture (without OBS), as OBS shows it.
                    return new Answer(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(CasterPages.SimFeed));
                case ("POST", "/watch/suggest"):
                {
                    if (Suggest == null) return Say(423, "Red Alert is locked.");
                    JsonElement input = default;
                    try { if (body.Length > 0) input = JsonDocument.Parse(body).RootElement.Clone(); } catch (Exception) { return Say(400, "That wasn't a suggestion."); }
                    if (input.ValueKind != JsonValueKind.Object) return Say(400, "That wasn't a suggestion.");
                    var (status, message) = Suggest(watcher, input);
                    return Say(status, message);
                }
                case ("POST", "/watch/signout"):
                    SignOut(watcher.Id);
                    return Say(200, "Signed out.") with { SetCookie = $"{Cookie}=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0" };
                default:
                    return Say(404, "Not found.");
            }
        }
    }
}
