using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.Discord
{
    public sealed class VoiceMember
    {
        public string UserId { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string Username { get; set; } = "";
        public string? GlobalName { get; set; }
        public string? Nick { get; set; }
        public bool IsBot { get; set; }

        public string DisplayName => Nick ?? GlobalName ?? Username;
        public IEnumerable<string> Names => new[] { Nick, GlobalName, Username }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!);
    }

    /// <summary>Who is in which voice channel of the server, kept current by the gateway.</summary>
    public interface IVoicePresence
    {
        bool Connected { get; }
        IReadOnlyList<VoiceMember> Members { get; }
    }

    /// <summary>
    /// Tracks voice channel membership from gateway events. Pure state: the socket code in
    /// <see cref="VoiceGateway"/> feeds it, and tests can feed it directly.
    /// </summary>
    public sealed class VoicePresenceState : IVoicePresence
    {
        private readonly string _guildId;
        private readonly object _lock = new object();
        private readonly Dictionary<string, VoiceMember> _members = new Dictionary<string, VoiceMember>();
        private readonly HashSet<string> _talking = new HashSet<string>();

        /// <summary>The bot's own event (from The Button's voice listener): someone started or stopped talking.</summary>
        public const string TalkingEvent = "TT_TALKING";

        /// <summary>This person (a Discord user ID) is talking in the voice channel the bot listens in.</summary>
        public bool IsTalking(string userId)
        {
            lock (_lock) return _talking.Contains(userId);
        }

        /// <summary>Everyone talking right now.</summary>
        public IReadOnlyCollection<string> Talking
        {
            get { lock (_lock) return _talking.ToList(); }
        }

        public VoicePresenceState(string guildId)
        {
            _guildId = guildId;
        }

        public bool Connected { get; set; }
        public string? BotUserId { get; private set; }

        /// <summary>Why Discord won't let the bot in (it has stopped trying), for the host to see. Null when fine.</summary>
        public string? Problem { get; set; }

        /// <summary>Something that works but is limited, e.g. results-channel commands unheard.</summary>
        public string? Warning { get; set; }
        public string? ApplicationId { get; private set; }

        /// <summary>The gateway said hello: the bot's user and application are known.</summary>
        public event Action? Ready;

        /// <summary>Someone used one of the bot's slash commands.</summary>
        public event Action<Interaction>? InteractionCreated;

        /// <summary>A message was posted in a channel the bot can see (only with the message intents).</summary>
        public event Action<ChannelMessage>? MessageCreated;

        public IReadOnlyList<VoiceMember> Members
        {
            get { lock (_lock) return _members.Values.ToList(); }
        }

        /// <summary>Applies one gateway dispatch (op 0) event.</summary>
        public void Dispatch(string type, JsonElement data)
        {
            switch (type)
            {
                case "READY":
                    if (data.TryGetProperty("user", out var me)) BotUserId = Str(me, "id");
                    if (data.TryGetProperty("application", out var app)) ApplicationId = Str(app, "id");
                    Ready?.Invoke();
                    break;

                case "INTERACTION_CREATE":
                    if (Interaction.Parse(data) is Interaction interaction && interaction.GuildId == _guildId)
                        InteractionCreated?.Invoke(interaction);
                    break;

                case "GUILD_CREATE":
                    if (Str(data, "id") != _guildId) return;
                    // Without the privileged members intent, "members" holds exactly the people
                    // in voice (plus the bot), which is what the voice states need for names.
                    var profiles = new Dictionary<string, JsonElement>();
                    if (data.TryGetProperty("members", out var members))
                        foreach (var m in members.EnumerateArray())
                            if (m.TryGetProperty("user", out var u) && Str(u, "id") is string id) profiles[id] = m;
                    lock (_lock)
                    {
                        _members.Clear();
                        _talking.Clear();
                        if (data.TryGetProperty("voice_states", out var states))
                        {
                            foreach (var vs in states.EnumerateArray())
                            {
                                string? userId = Str(vs, "user_id");
                                string? channel = Str(vs, "channel_id");
                                if (userId == null || channel == null) continue;
                                profiles.TryGetValue(userId, out var profile);
                                _members[userId] = Build(userId, channel, profile.ValueKind == JsonValueKind.Object ? profile : (JsonElement?)null);
                            }
                        }
                    }
                    break;

                case "MESSAGE_CREATE":
                    MessageCreated?.Invoke(ChannelMessage.Parse(data));
                    break;

                case TalkingEvent:
                    if (Str(data, "user_id") is string talker)
                        lock (_lock)
                        {
                            if (data.TryGetProperty("talking", out var t) && t.ValueKind == JsonValueKind.True) _talking.Add(talker);
                            else _talking.Remove(talker);
                        }
                    break;

                case "VOICE_STATE_UPDATE":
                    if (Str(data, "guild_id") != _guildId) return;
                    string? who = Str(data, "user_id");
                    if (who == null) return;
                    string? now = Str(data, "channel_id");
                    lock (_lock)
                    {
                        if (now == null) { _members.Remove(who); _talking.Remove(who); }
                        else _members[who] = Build(who, now, data.TryGetProperty("member", out var mem) ? mem : (JsonElement?)null, _members.GetValueOrDefault(who));
                    }
                    break;
            }
        }

        private static VoiceMember Build(string userId, string channelId, JsonElement? member, VoiceMember? previous = null)
        {
            var v = new VoiceMember
            {
                UserId = userId,
                ChannelId = channelId,
                Username = previous?.Username ?? "",
                GlobalName = previous?.GlobalName,
                Nick = previous?.Nick,
                IsBot = previous?.IsBot ?? false,
            };
            if (member is JsonElement m && m.ValueKind == JsonValueKind.Object)
            {
                v.Nick = Str(m, "nick");
                if (m.TryGetProperty("user", out var u))
                {
                    v.Username = Str(u, "username") ?? v.Username;
                    v.GlobalName = Str(u, "global_name");
                    v.IsBot = u.TryGetProperty("bot", out var b) && b.ValueKind == JsonValueKind.True;
                }
            }
            return v;
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>The bot's live connection to Discord, as The Button holds it (a pretend one in tests).</summary>
    public interface IBotGateway : IDisposable
    {
        VoicePresenceState State { get; }
        event Action<string, JsonElement>? Dispatched;
        void Start();
        /// <summary>Puts the bot in this voice channel (null: out of voice), to see who's talking.</summary>
        void JoinVoice(string? channelId);
    }

    /// <summary>
    /// A minimal Discord gateway connection: identify with the GUILDS and GUILD_VOICE_STATES
    /// intents (neither is privileged), heartbeat, and reconnect when dropped. It only listens.
    /// A dropped connection is resumed where Discord allows it, so the events in between (a /link,
    /// a !start in the results channel) arrive late instead of never.
    /// </summary>
    public sealed class VoiceGateway : IVoicePresence, IBotGateway
    {
        public const string DefaultUrl = "wss://gateway.discord.gg/?v=10&encoding=json";
        private const int BaseIntents = (1 << 0) | (1 << 7);
        /// <summary>GUILD_MESSAGES and MESSAGE_CONTENT (privileged: turned on for the bot in the developer portal).</summary>
        private const int MessageIntents = (1 << 9) | (1 << 15);
        private int _intents = BaseIntents;

        /// <summary>Discord lets a bot identify once every 5 seconds (and about 1000 times a day).</summary>
        private static readonly TimeSpan IdentifyGap = TimeSpan.FromSeconds(5.5);
        /// <summary>A connection that lasted this long starts the reconnect wait from the beginning again.</summary>
        private static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);
        /// <summary>Resumes in a row that may fail before a fresh session is started instead.</summary>
        private const int MaxResumeFailures = 3;

        private readonly string _token;
        private readonly Uri _url;
        private readonly ILog _log;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Random _random = new Random();
        private Task? _run;
        /// <summary>The last event number Discord sent (-1: none yet). Read by the heartbeat on another thread.</summary>
        private long _sequence = -1;
        private readonly string _guildId;
        private Func<object, Task>? _send;
        private readonly object _voiceLock = new object();
        private string? _voiceWanted, _voiceSession, _voiceToken, _voiceEndpoint, _voiceIn;
        private VoiceListener? _listener;

        // Where the session can be picked up after a drop (op 6 RESUME). Only the run loop touches these.
        private string? _sessionId;
        private Uri? _resumeUrl;
        private int _resumeFailures;
        private DateTime _lastIdentify = DateTime.MinValue;

        // The voice self-check: when the bot should be listening but isn't, and how often it asked again.
        private DateTime? _voiceLostAt;
        private int _rejoins;

        public VoiceGateway(string token, string guildId, ILog log, string url = DefaultUrl, bool listenToMessages = false)
        {
            if (listenToMessages) _intents |= MessageIntents;
            _token = token;
            _url = new Uri(url);
            _log = log;
            _guildId = guildId;
            State = new VoicePresenceState(guildId);
        }

        public VoicePresenceState State { get; }
        public bool Connected => State.Connected;

        /// <summary>Every event Discord sends (after <see cref="State"/> has taken it in), for The Button to pass on to the mod.</summary>
        public event Action<string, JsonElement>? Dispatched;
        public IReadOnlyList<VoiceMember> Members => State.Members;

        /// <summary>Scales every wait between connections (tests make them short).</summary>
        internal double WaitScale { get; set; } = 1;

        /// <summary>How often the bot checks it is listening where it should be.</summary>
        internal TimeSpan VoiceCheckEvery { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>How long the bot may be out of the voice channel it should be in before it asks again (doubling each time).</summary>
        internal TimeSpan RejoinAfter { get; set; } = TimeSpan.FromSeconds(15);

        public void Start() => _run ??= Task.Run(() => RunAsync(_cts.Token));

        // ---- In a voice channel, to see who's talking ------------------------------------------

        /// <summary>Puts the bot in this voice channel, muted (null: out of voice). Kept across reconnects.</summary>
        public void JoinVoice(string? channelId)
        {
            Func<object, Task>? send;
            lock (_voiceLock)
            {
                if (channelId == _voiceWanted) return;
                _voiceWanted = channelId;
                _voiceLostAt = null;
                _rejoins = 0;
                send = _send;
                if (channelId == null) StopListening();
            }
            if (send != null) _ = SendVoiceState(send, channelId);
        }

        private Task SendVoiceState(Func<object, Task> send, string? channelId) =>
            send(new { op = 4, d = new { guild_id = _guildId, channel_id = channelId, self_mute = true, self_deaf = false } });

        /// <summary>The bot's own voice state and voice server: once both are known, listen there.</summary>
        private void VoiceEvent(string type, JsonElement d)
        {
            lock (_voiceLock)
            {
                if (type == "VOICE_STATE_UPDATE")
                {
                    if (d.TryGetProperty("user_id", out var u) && u.GetString() != State.BotUserId) return;
                    if (d.TryGetProperty("guild_id", out var g) && g.GetString() != _guildId) return;
                    _voiceSession = d.TryGetProperty("session_id", out var s) ? s.GetString() : null;
                    string? channel = d.TryGetProperty("channel_id", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    if (channel != _voiceIn) StopListening();
                    _voiceIn = channel;
                    if (channel == null)
                    {
                        // Out of voice: the next join brings a new voice server and token.
                        _voiceToken = null;
                        _voiceEndpoint = null;
                        return;
                    }
                }
                else if (type == "VOICE_SERVER_UPDATE")
                {
                    if (d.TryGetProperty("guild_id", out var g) && g.GetString() != _guildId) return;
                    _voiceToken = d.TryGetProperty("token", out var t) ? t.GetString() : null;
                    _voiceEndpoint = d.TryGetProperty("endpoint", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    StopListening();          // a new server: connect again
                }
                if (_listener != null || _voiceIn == null || _voiceSession == null || _voiceToken == null || _voiceEndpoint == null || State.BotUserId == null) return;
                var listener = new VoiceListener(_voiceEndpoint, _guildId, State.BotUserId, _voiceSession, _voiceToken, _log);
                listener.Talking += (user, talking) =>
                {
                    var data = JsonSerializer.SerializeToElement(new { user_id = user, talking });
                    State.Dispatch(VoicePresenceState.TalkingEvent, data);
                    Dispatched?.Invoke(VoicePresenceState.TalkingEvent, data);
                };
                _listener = listener;
                listener.Start();
            }
        }

        private void StopListening()
        {
            var old = _listener;
            _listener = null;
            if (old != null) Task.Run(old.Dispose);
        }

        /// <summary>
        /// Every few seconds: the bot should be listening in a voice channel but isn't (its listener
        /// gave up, Discord took it out of voice, or a join went unanswered). It asks Discord again,
        /// waiting twice as long after each try, so talking lights come back by themselves.
        /// </summary>
        private async Task WatchVoiceAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(VoiceCheckEvery, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                try { CheckVoice(); }
                catch (Exception e) { _log.Warn("Voice (who's talking) check failed: " + e.Message); }
            }
        }

        internal void CheckVoice()
        {
            Func<object, Task>? send;
            string wanted;
            bool leaveFirst;
            var now = DateTime.UtcNow;
            lock (_voiceLock)
            {
                if (_listener != null && _listener.Listening) _rejoins = 0;
                bool fine = _voiceWanted == null || _send == null || (_listener != null && !_listener.Stopped);
                if (fine)
                {
                    _voiceLostAt = null;
                    return;
                }
                _voiceLostAt ??= now;
                var wait = TimeSpan.FromTicks(RejoinAfter.Ticks * (1L << Math.Min(_rejoins, 5)));
                if (now - _voiceLostAt.Value < wait) return;
                _voiceLostAt = now;
                // What the old listener had is no good any more: wait for Discord's new voice server.
                StopListening();
                _voiceToken = null;
                _voiceEndpoint = null;
                // First ask for the same channel again; if that wasn't enough, leave and come back.
                leaveFirst = _rejoins > 0 && _voiceIn != null;
                _rejoins++;
                send = _send;
                wanted = _voiceWanted!;
            }
            _log.Info("Voice (who's talking): the bot isn't listening in the host's voice channel; asking Discord again.");
            _ = RejoinAsync(send!, wanted, leaveFirst);
        }

        private async Task RejoinAsync(Func<object, Task> send, string channel, bool leaveFirst)
        {
            try
            {
                if (leaveFirst)
                {
                    await SendVoiceState(send, null).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                lock (_voiceLock) { if (_voiceWanted != channel || _send != send) return; }
                await SendVoiceState(send, channel).ConfigureAwait(false);
            }
            catch (Exception e) { _log.Warn("Voice (who's talking): couldn't ask to join again: " + e.Message); }
        }

        // ---- The connection ----------------------------------------------------------------------

        /// <summary>How one connection ended, for the run loop's next step.</summary>
        private sealed class Ending
        {
            /// <summary>READY or RESUMED came: the connection was really working.</summary>
            public bool Working;
            public DateTime Started = DateTime.UtcNow;
            public DateTime Ended;
            /// <summary>Discord said the session is invalid (op 9): it asks for a 1–5 s pause.</summary>
            public bool InvalidSession;
            public bool Resuming;
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var voiceWatch = Task.Run(() => WatchVoiceAsync(ct));
            int failures = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var ending = new Ending();
                    try
                    {
                        await ConnectOnceAsync(ending, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (FatalGatewayException e)
                    {
                        _log.Error("Discord gateway refused the bot: " + e.Message + " Spectator muting and auto-link are off.");
                        State.Problem = e.Code switch
                        {
                            4004 => "Discord turned down the bot's token: it was reset or copied wrong. Ask the organiser for a new setup code.",
                            4013 or 4014 => "Discord won't give the bot what it needs. The organiser should check the bot's settings in the Discord Developer Portal (Bot → Privileged Gateway Intents).",
                            _ => $"Discord refused the bot (code {e.Code}). Ask the organiser to check it.",
                        };
                        State.Connected = false;
                        return;
                    }
                    catch (Exception e)
                    {
                        _log.Warn($"Discord gateway disconnected ({e.Message}); reconnecting");
                    }
                    State.Connected = false;
                    if (ending.Ended == default) ending.Ended = DateTime.UtcNow;

                    if (ending.Resuming && !ending.Working && ++_resumeFailures >= MaxResumeFailures) ForgetSession();
                    if (ending.Working) _resumeFailures = 0;

                    // A connection that got going and lasted starts the count again. Anything else
                    // (refused, dropped at once, Discord closing it straight away) waits longer each
                    // time, so the bot never hammers Discord with identifies.
                    failures = ending.Working && ending.Ended - ending.Started >= StableAfter ? 0 : failures + 1;
                    var wait = ReconnectWait(failures, ending.InvalidSession);
                    try { await Task.Delay(Scaled(wait), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
            finally
            {
                try { await voiceWatch.ConfigureAwait(false); } catch (Exception) { }
            }
        }

        /// <summary>
        /// The pause before connecting again: about a second after a working connection dropped
        /// (or Discord asked to reconnect), 1–5 s after an invalid session (as Discord asks), and
        /// 2, 4, 8… up to 60 s while connections keep failing.
        /// </summary>
        internal TimeSpan ReconnectWait(int failures, bool invalidSession)
        {
            double floor;
            lock (_random) floor = invalidSession ? 1 + _random.NextDouble() * 4 : 0.5 + _random.NextDouble();
            double backoff = failures <= 1 ? 0 : Math.Min(60, 2 << Math.Min(failures - 2, 5));
            return TimeSpan.FromSeconds(Math.Max(floor, backoff));
        }

        private TimeSpan Scaled(TimeSpan t) => TimeSpan.FromTicks((long)(t.Ticks * Math.Max(0, WaitScale)));

        private void ForgetSession()
        {
            _sessionId = null;
            _resumeUrl = null;
            _resumeFailures = 0;
            Interlocked.Exchange(ref _sequence, -1);
        }

        private int? Sequence
        {
            get
            {
                long s = Interlocked.Read(ref _sequence);
                return s < 0 ? (int?)null : (int)s;
            }
        }

        /// <summary>
        /// Discord's resume address, only if it is a Discord gateway (or the same host the bot was
        /// told to use): the bot's token goes there.
        /// </summary>
        internal Uri? ResumeUri(string? url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
            if (u.Scheme != _url.Scheme) return null;
            bool discord = u.Host.Equals("discord.gg", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".discord.gg", StringComparison.OrdinalIgnoreCase)
                || u.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase);
            if (!discord && !u.Host.Equals(_url.Host, StringComparison.OrdinalIgnoreCase)) return null;
            return new Uri(u.GetLeftPart(UriPartial.Authority) + "/" + _url.Query);
        }

        private async Task ConnectOnceAsync(Ending ending, CancellationToken ct)
        {
            bool resume = _sessionId != null && _resumeUrl != null && Sequence != null;
            ending.Resuming = resume;
            if (!resume)
            {
                // Never identify faster than Discord allows, whatever made the last connection end.
                var since = DateTime.UtcNow - _lastIdentify;
                if (since < Scaled(IdentifyGap)) await Task.Delay(Scaled(IdentifyGap) - since, ct).ConfigureAwait(false);
            }

            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(resume ? _resumeUrl! : _url, ct).ConfigureAwait(false);
            ending.Started = DateTime.UtcNow;
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sendLock = new SemaphoreSlim(1, 1);
            Task? heartbeat = null;
            int acked = 1;

            async Task Send(object payload)
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                await sendLock.WaitAsync(connection.Token).ConfigureAwait(false);
                try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, connection.Token).ConfigureAwait(false); }
                finally { sendLock.Release(); }
            }

            try
            {
                while (!connection.IsCancellationRequested)
                {
                    using var doc = await ReceiveAsync(socket, connection.Token).ConfigureAwait(false);
                    if (doc == null) return;
                    var root = doc.RootElement;
                    int op = root.GetProperty("op").GetInt32();
                    if (root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number) Interlocked.Exchange(ref _sequence, s.GetInt64());

                    switch (op)
                    {
                        case 10: // Hello
                            int interval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                            int jitter;
                            lock (_random) jitter = _random.Next(Math.Max(1, interval));
                            heartbeat = Task.Run(async () =>
                            {
                                await Task.Delay(jitter, connection.Token).ConfigureAwait(false);
                                while (!connection.IsCancellationRequested)
                                {
                                    if (Interlocked.Exchange(ref acked, 0) == 0)
                                    {
                                        // No ack since the last beat: the connection is dead (resumed next).
                                        connection.Cancel();
                                        return;
                                    }
                                    await Send(new { op = 1, d = Sequence }).ConfigureAwait(false);
                                    await Task.Delay(interval, connection.Token).ConfigureAwait(false);
                                }
                            });
                            if (resume)
                            {
                                await Send(new { op = 6, d = new { token = _token, session_id = _sessionId, seq = Sequence } }).ConfigureAwait(false);
                            }
                            else
                            {
                                _lastIdentify = DateTime.UtcNow;
                                Interlocked.Exchange(ref _sequence, -1);
                                await Send(new
                                {
                                    op = 2,
                                    d = new
                                    {
                                        token = _token,
                                        intents = _intents,
                                        properties = new { os = "windows", browser = "TournamentTracker", device = "TournamentTracker" },
                                    },
                                }).ConfigureAwait(false);
                            }
                            break;
                        case 11: // Heartbeat ack
                            Interlocked.Exchange(ref acked, 1);
                            break;
                        case 1: // Heartbeat request
                            await Send(new { op = 1, d = Sequence }).ConfigureAwait(false);
                            break;
                        case 7: // Reconnect: Discord wants this session picked up on a new connection
                            return;
                        case 9: // Invalid session: d says whether it can still be resumed
                            bool resumable = root.TryGetProperty("d", out var d9) && d9.ValueKind == JsonValueKind.True;
                            if (!resumable) ForgetSession();
                            ending.InvalidSession = true;
                            return;
                        case 0:
                            string type = root.GetProperty("t").GetString() ?? "";
                            var data = root.GetProperty("d");
                            State.Dispatch(type, data);
                            if (type == "VOICE_STATE_UPDATE" || type == "VOICE_SERVER_UPDATE") VoiceEvent(type, data);
                            if (type == "READY")
                            {
                                _sessionId = data.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null;
                                _resumeUrl = ResumeUri(data.TryGetProperty("resume_gateway_url", out var ru) && ru.ValueKind == JsonValueKind.String ? ru.GetString() : null);
                                ending.Working = true;
                                State.Connected = true;
                                string? wanted;
                                lock (_voiceLock) { _send = Send; wanted = _voiceWanted; StopListening(); _voiceIn = null; }
                                if (wanted != null) await SendVoiceState(Send, wanted).ConfigureAwait(false);
                            }
                            else if (type == "RESUMED")
                            {
                                // The same session: the missed events came just before this. The voice
                                // connection is separate and carried on; only a voice change asked for
                                // while disconnected still has to be sent.
                                ending.Working = true;
                                State.Connected = true;
                                string? wanted;
                                bool differs;
                                lock (_voiceLock) { _send = Send; wanted = _voiceWanted; differs = _voiceWanted != _voiceIn; }
                                if (differs) await SendVoiceState(Send, wanted).ConfigureAwait(false);
                            }
                            Dispatched?.Invoke(type, data.Clone());
                            break;
                    }
                }
            }
            finally
            {
                ending.Ended = DateTime.UtcNow;
                lock (_voiceLock) _send = null;
                connection.Cancel();
                // The heartbeat's own failure (a send on a socket Discord just closed) must not hide
                // why Discord closed it: a refused token would otherwise reconnect forever.
                if (heartbeat != null) { try { await heartbeat.ConfigureAwait(false); } catch (Exception) { } }
                var closeStatus = socket.CloseStatus;
                if (closeStatus.HasValue)
                {
                    int code = (int)closeStatus.Value;
                    if (code == 4014 && (_intents & MessageIntents) != 0)
                    {
                        // The Message Content intent isn't turned on: keep voice working without channel commands.
                        _intents = BaseIntents;
                        ForgetSession();
                        _log.Warn("The bot's Message Content Intent is off, so results-channel commands (!lobbies, !start…) won't be heard. Voice features carry on.");
                        State.Warning = "The bot's Message Content Intent is off, so referee commands in the results channel (!adjust, !void, !start…) aren't heard. The organiser turns it on in the Discord Developer Portal.";
                        throw new IOException("reconnecting without message intents");
                    }
                    if (IsFatal(code))
                    {
                        ForgetSession();
                        throw new FatalGatewayException(code, $"close code {code} {socket.CloseStatusDescription}");
                    }
                    // Sequence out of order, or the session timed out: it can't be resumed.
                    if (code == 4007 || code == 4009) ForgetSession();
                }
            }
        }

        /// <summary>Close codes that retrying can't fix: bad token, disallowed intents, and so on.</summary>
        private static bool IsFatal(int code) => code == 4004 || code == 4010 || code == 4011 || code == 4012 || code == 4013 || code == 4014;

        private static async Task<JsonDocument?> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }
            message.Position = 0;
            return JsonDocument.Parse(message);
        }

        public void Dispose()
        {
            lock (_voiceLock) StopListening();
            _cts.Cancel();
            try { _run?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
        }

        private sealed class FatalGatewayException : Exception
        {
            public FatalGatewayException(int code, string message) : base(message) { Code = code; }
            public int Code { get; }
        }
    }
}
