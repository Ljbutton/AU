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

        public VoicePresenceState(string guildId)
        {
            _guildId = guildId;
        }

        public bool Connected { get; set; }
        public string? BotUserId { get; private set; }
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

                case "VOICE_STATE_UPDATE":
                    if (Str(data, "guild_id") != _guildId) return;
                    string? who = Str(data, "user_id");
                    if (who == null) return;
                    string? now = Str(data, "channel_id");
                    lock (_lock)
                    {
                        if (now == null) _members.Remove(who);
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

    /// <summary>
    /// A minimal Discord gateway connection: identify with the GUILDS and GUILD_VOICE_STATES
    /// intents (neither is privileged), heartbeat, and reconnect when dropped. It only listens.
    /// </summary>
    public sealed class VoiceGateway : IVoicePresence, IDisposable
    {
        public const string DefaultUrl = "wss://gateway.discord.gg/?v=10&encoding=json";
        private const int BaseIntents = (1 << 0) | (1 << 7);
        /// <summary>GUILD_MESSAGES and MESSAGE_CONTENT (privileged: turned on for the bot in the developer portal).</summary>
        private const int MessageIntents = (1 << 9) | (1 << 15);
        private int _intents = BaseIntents;

        private readonly string _token;
        private readonly Uri _url;
        private readonly ILog _log;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _run;
        private int? _sequence;

        public VoiceGateway(string token, string guildId, ILog log, string url = DefaultUrl, bool listenToMessages = false)
        {
            if (listenToMessages) _intents |= MessageIntents;
            _token = token;
            _url = new Uri(url);
            _log = log;
            State = new VoicePresenceState(guildId);
        }

        public VoicePresenceState State { get; }
        public bool Connected => State.Connected;
        public IReadOnlyList<VoiceMember> Members => State.Members;

        public void Start() => _run ??= Task.Run(() => RunAsync(_cts.Token));

        private async Task RunAsync(CancellationToken ct)
        {
            int failures = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ConnectOnceAsync(ct).ConfigureAwait(false);
                    failures = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (FatalGatewayException e)
                {
                    _log.Error("Discord gateway refused the bot: " + e.Message + " Spectator muting and auto-link are off.");
                    return;
                }
                catch (Exception e)
                {
                    failures++;
                    _log.Warn($"Discord gateway disconnected ({e.Message}); reconnecting");
                }
                State.Connected = false;
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Max(1, failures))), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task ConnectOnceAsync(CancellationToken ct)
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(_url, ct).ConfigureAwait(false);
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sendLock = new SemaphoreSlim(1, 1);
            Task? heartbeat = null;
            bool acked = true;

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
                    if (root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number) _sequence = s.GetInt32();

                    switch (op)
                    {
                        case 10: // Hello
                            int interval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                            heartbeat = Task.Run(async () =>
                            {
                                await Task.Delay(new Random().Next(interval), connection.Token).ConfigureAwait(false);
                                while (!connection.IsCancellationRequested)
                                {
                                    if (!acked)
                                    {
                                        // No ack since the last beat: the connection is dead.
                                        connection.Cancel();
                                        return;
                                    }
                                    acked = false;
                                    await Send(new { op = 1, d = _sequence }).ConfigureAwait(false);
                                    await Task.Delay(interval, connection.Token).ConfigureAwait(false);
                                }
                            });
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
                            break;
                        case 11: // Heartbeat ack
                            acked = true;
                            break;
                        case 1: // Heartbeat request
                            await Send(new { op = 1, d = _sequence }).ConfigureAwait(false);
                            break;
                        case 7: // Reconnect
                        case 9: // Invalid session
                            return;
                        case 0:
                            string type = root.GetProperty("t").GetString() ?? "";
                            State.Dispatch(type, root.GetProperty("d"));
                            if (type == "READY") State.Connected = true;
                            break;
                    }
                }
            }
            finally
            {
                connection.Cancel();
                if (heartbeat != null) { try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { } }
                var closeStatus = socket.CloseStatus;
                if (closeStatus.HasValue && (int)closeStatus.Value == 4014 && (_intents & MessageIntents) != 0)
                {
                    // The Message Content intent isn't turned on: keep voice working without channel commands.
                    _intents = BaseIntents;
                    _log.Warn("The bot's Message Content Intent is off, so results-channel commands (!lobbies, !start…) won't be heard. Voice features carry on.");
                    throw new IOException("reconnecting without message intents");
                }
                if (closeStatus.HasValue && IsFatal((int)closeStatus.Value))
                    throw new FatalGatewayException($"close code {(int)closeStatus.Value} {socket.CloseStatusDescription}");
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
            _cts.Cancel();
            try { _run?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
        }

        private sealed class FatalGatewayException : Exception
        {
            public FatalGatewayException(string message) : base(message) { }
        }
    }
}
