using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.Discord
{
    /// <summary>
    /// The mod reaches Discord only through The Button. While The Button is open it keeps the
    /// bot connected (online even before Among Us starts), and the mod's Discord requests and
    /// the bot's events go through it. Without The Button the mod doesn't touch Discord at all,
    /// so Among Us can be played normally.
    /// The Button says where it is in <see cref="FileName"/> in the mod's data folder.
    /// </summary>
    public static class ButtonBridge
    {
        public const string FileName = "button.json";
        public const string TokenHeader = "X-TT-Token";
        /// <summary>The Discord address the request was for.</summary>
        public const string TargetHeader = "X-TT-Target";
        /// <summary>On a made-up answer: The Button isn't open, so nothing was sent.</summary>
        public const string OfflineHeader = "X-TT-Offline";
        /// <summary>On every answer The Button passes back from Discord.</summary>
        public const string RelayedHeader = "X-TT-Relayed";

        private static readonly string[] Hosts = { "discord.com", "discordapp.com", "cdn.discordapp.com", "media.discordapp.net" };

        /// <summary>A Discord address (the API, webhooks, attachments): the only places the bridge goes.</summary>
        public static bool IsDiscord(Uri? url) =>
            url != null && url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps
            && Hosts.Any(h => url.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

        public sealed class Link
        {
            public int Port { get; set; }
            public string Token { get; set; } = "";
            public string Base => $"http://127.0.0.1:{Port}/";
        }

        /// <summary>Where The Button is, or null when it isn't open.</summary>
        public static Link? Read(string dataDir)
        {
            try
            {
                string file = Path.Combine(dataDir, FileName);
                if (!File.Exists(file)) return null;
                var json = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
                var link = new Link { Port = json.GetProperty("port").GetInt32(), Token = json.GetProperty("token").GetString() ?? "" };
                return link.Port > 0 && link.Token.Length > 0 ? link : null;
            }
            catch (Exception) { return null; }
        }

        public static void Write(string dataDir, int port, string token)
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, FileName), JsonSerializer.Serialize(new { port, token }));
        }

        /// <summary>Removes the file, if it's this Button's (another one may have taken over).</summary>
        public static void Remove(string dataDir, string token)
        {
            try { if (Read(dataDir)?.Token == token) File.Delete(Path.Combine(dataDir, FileName)); }
            catch (Exception) { }
        }

        /// <summary>The mod's HTTP client for Discord: every Discord request goes through The Button.</summary>
        public static HttpClient CreateClient(string dataDir)
        {
            var http = new HttpClient(new RelayHandler(dataDir)) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://github.com/Ljbutton/AU, 1.0)");
            return http;
        }

        /// <summary>The answer when The Button isn't open: nothing went to Discord.</summary>
        public static HttpResponseMessage Offline(HttpRequestMessage request)
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"message\":\"The Button isn't open, so Discord is off.\"}", Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
            response.Headers.TryAddWithoutValidation(OfflineHeader, "1");
            return response;
        }

        /// <summary>Sends Discord requests to The Button, which sends them on; others go straight out.</summary>
        public sealed class RelayHandler : DelegatingHandler
        {
            private readonly string _dataDir;
            private static readonly string[] Forward = { "Authorization", "X-Audit-Log-Reason", "User-Agent" };

            public RelayHandler(string dataDir, HttpMessageHandler? inner = null) : base(inner ?? new HttpClientHandler())
            {
                _dataDir = dataDir;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (!IsDiscord(request.RequestUri)) return await base.SendAsync(request, ct).ConfigureAwait(false);
                var link = Read(_dataDir);
                if (link == null) return Offline(request);

                using var relay = new HttpRequestMessage(request.Method, link.Base + "discord");
                relay.Headers.TryAddWithoutValidation(TokenHeader, link.Token);
                relay.Headers.TryAddWithoutValidation(TargetHeader, request.RequestUri!.AbsoluteUri);
                foreach (var name in Forward)
                    if (request.Headers.TryGetValues(name, out var values)) relay.Headers.TryAddWithoutValidation(name, string.Join(" ", values));
                if (request.Content != null)
                {
                    var body = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                    if (request.Content.Headers.ContentType != null) body.Headers.TryAddWithoutValidation("Content-Type", request.Content.Headers.ContentType.ToString());
                    relay.Content = body;
                }
                HttpResponseMessage response;
                try { response = await base.SendAsync(relay, ct).ConfigureAwait(false); }
                catch (HttpRequestException) { return Offline(request); }   // The Button closed without tidying up
                // Not The Button's relay (an old file, someone else on that port): treat The Button as closed.
                if (!response.Headers.Contains(RelayedHeader))
                {
                    response.Dispose();
                    return Offline(request);
                }
                response.RequestMessage = request;
                return response;
            }
        }
    }

    /// <summary>
    /// The bot's gateway, as the mod sees it: The Button keeps the connection, and this asks it
    /// for what happened (who's in voice, slash commands, channel messages). It also tells The
    /// Button which bot and server the mod wants, so a settings file without a setup code works.
    /// </summary>
    public sealed class ButtonGateway : IVoicePresence, IDisposable
    {
        private readonly string _dataDir, _token, _guildId;
        private readonly bool _listen;
        private readonly ILog _log;
        private readonly HttpClient _http;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _run;
        private long _since;
        private string _epoch = "";
        private string? _readyFor;

        /// <summary>How long The Button holds a request open waiting for news.</summary>
        public static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

        public ButtonGateway(string dataDir, string token, string guildId, bool listen, ILog log, HttpMessageHandler? handler = null)
        {
            _dataDir = dataDir;
            _token = token;
            _guildId = guildId;
            _listen = listen;
            _log = log;
            _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = Wait + TimeSpan.FromSeconds(15) };
            State = new VoicePresenceState(guildId);
        }

        public VoicePresenceState State { get; }
        public bool Connected => State.Connected;
        public IReadOnlyList<VoiceMember> Members => State.Members;

        /// <summary>The Button is open and answering.</summary>
        public bool ButtonOpen { get; private set; }

        /// <summary>The Button opened or closed.</summary>
        public event Action<bool>? ButtonChanged;

        public void Start() => _run ??= Task.Run(() => RunAsync(_cts.Token));

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                bool ok = false;
                try { ok = await PollAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception e) { _log.Warn("The Button didn't answer: " + e.Message); }
                if (!ok)
                {
                    SetOpen(false);
                    try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        /// <summary>One request to The Button: false when it isn't there.</summary>
        internal async Task<bool> PollAsync(CancellationToken ct)
        {
            var link = ButtonBridge.Read(_dataDir);
            if (link == null) return false;
            using var request = new HttpRequestMessage(HttpMethod.Post, link.Base + "gateway")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { token = _token, guild = _guildId, listen = _listen, since = _since, epoch = _epoch }), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(ButtonBridge.TokenHeader, link.Token);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, ct).ConfigureAwait(false); }
            catch (HttpRequestException) { return false; }
            using (response)
            {
                if (!response.IsSuccessStatusCode) return false;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                Apply(doc.RootElement);
            }
            SetOpen(true);
            return true;
        }

        /// <summary>Takes in The Button's answer: the bot's state and the events since last time.</summary>
        internal void Apply(JsonElement answer)
        {
            string epoch = answer.TryGetProperty("epoch", out var e) ? e.GetString() ?? "" : "";
            if (epoch != _epoch) _readyFor = null;
            _epoch = epoch;
            _since = answer.TryGetProperty("last", out var l) && l.TryGetInt64(out var last) ? last : 0;
            if (answer.TryGetProperty("events", out var events))
                foreach (var ev in events.EnumerateArray())
                {
                    string type = ev.GetProperty("t").GetString() ?? "";
                    var data = ev.GetProperty("d");
                    if (type == "READY")
                    {
                        // The same bot again (The Button catching the mod up): slash commands are already set.
                        string who = data.TryGetProperty("user", out var u) && u.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                        if (_readyFor == who) continue;
                        _readyFor = who;
                    }
                    try { State.Dispatch(type, data); }
                    catch (Exception x) { _log.Warn($"Discord event {type} failed: {x.Message}"); }
                }
            State.Connected = answer.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True;
            State.Problem = Str(answer, "problem");
            State.Warning = Str(answer, "warning");
        }

        private void SetOpen(bool open)
        {
            if (!open) State.Connected = false;
            if (open == ButtonOpen) return;
            ButtonOpen = open;
            _log.Info(open ? "The Button is open: Discord goes through it." : "The Button isn't open: Discord is off.");
            ButtonChanged?.Invoke(open);
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public void Dispose()
        {
            _cts.Cancel();
            try { _run?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _http.Dispose();
        }
    }
}
