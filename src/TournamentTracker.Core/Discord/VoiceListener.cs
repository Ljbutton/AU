using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.Discord
{
    /// <summary>
    /// The bot sitting in a voice channel, only to see who is talking: it never plays or records
    /// anything, and never decodes anyone's voice. Discord sends each person's voice as a stream
    /// of small packets while they talk (and none while they're quiet); the packets' headers say
    /// whose they are, so "packets arriving" is "talking". The voice itself stays encrypted.
    /// Talking starts and stops are reported through <see cref="Talking"/>.
    /// </summary>
    public sealed class VoiceListener : IDisposable
    {
        /// <summary>Quiet this long after the last packet: stopped talking.</summary>
        public static readonly TimeSpan QuietAfter = TimeSpan.FromMilliseconds(350);

        private readonly string _endpoint, _guildId, _userId, _sessionId, _token;
        private readonly ILog _log;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<uint, string> _users = new ConcurrentDictionary<uint, string>();
        private readonly ConcurrentDictionary<uint, long> _heard = new ConcurrentDictionary<uint, long>();
        private readonly HashSet<string> _talking = new HashSet<string>();
        private Task? _run;
        private uint _ssrc;
        private int _seq = -1;

        /// <summary>Someone (their Discord user ID) started (true) or stopped (false) talking.</summary>
        public event Action<string, bool>? Talking;

        /// <summary>Why it stopped trying, if it did.</summary>
        public string? Problem { get; private set; }

        public VoiceListener(string endpoint, string guildId, string userId, string sessionId, string token, ILog log)
        {
            _endpoint = endpoint.Replace("wss://", "").TrimEnd('/');
            _guildId = guildId;
            _userId = userId;
            _sessionId = sessionId;
            _token = token;
            _log = log;
        }

        public void Start() => _run ??= Task.Run(() => RunAsync(_cts.Token));

        private async Task RunAsync(CancellationToken ct)
        {
            var watch = Task.Run(() => WatchAsync(ct));
            // End-to-end encryption (DAVE): first as a listener that doesn't take part; if Discord
            // requires it (close 4017), again saying it does. The packet headers are readable either way.
            int dave = 0;
            for (int attempt = 0; attempt < 6 && !ct.IsCancellationRequested; attempt++)
            {
                int? closed = null;
                try { closed = await ConnectOnceAsync(dave, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception e) { _log.Warn("Voice (who's talking): " + e.Message); }
                if (closed == 4017 && dave == 0) { dave = 1; attempt--; continue; }
                // Session or token no longer good: the gateway gives a new one (the bot rejoins).
                if (closed is 4004 or 4006 or 4009 or 4011 or 4014 or 4016) { Problem = $"Discord closed the voice connection ({closed})."; break; }
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 + attempt * 2)), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            AllQuiet();
            try { await watch.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        /// <summary>One voice connection; the close code it ended with, if any.</summary>
        private async Task<int?> ConnectOnceAsync(int dave, CancellationToken ct)
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"wss://{_endpoint}/?v=8"), ct).ConfigureAwait(false);
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sendLock = new SemaphoreSlim(1, 1);
            UdpClient? udp = null;
            Task? heartbeat = null, receive = null, keepAlive = null;

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
                    var (text, binary) = await ReceiveAsync(socket, connection.Token).ConfigureAwait(false);
                    if (text == null && !binary) break;           // closed
                    if (text == null) continue;                    // end-to-end encryption's own messages: not needed to see who talks
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    int op = root.GetProperty("op").GetInt32();
                    if (root.TryGetProperty("seq", out var sq) && sq.TryGetInt32(out var seqNo)) _seq = seqNo;
                    var d = root.TryGetProperty("d", out var dd) ? dd : default;
                    switch (op)
                    {
                        case 8: // Hello
                            double interval = d.GetProperty("heartbeat_interval").GetDouble();
                            heartbeat = Task.Run(async () =>
                            {
                                while (!connection.IsCancellationRequested)
                                {
                                    await Task.Delay(TimeSpan.FromMilliseconds(interval), connection.Token).ConfigureAwait(false);
                                    await Send(new { op = 3, d = new { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), seq_ack = _seq } }).ConfigureAwait(false);
                                }
                            });
                            await Send(new { op = 0, d = new { server_id = _guildId, user_id = _userId, session_id = _sessionId, token = _token, max_dave_protocol_version = dave } }).ConfigureAwait(false);
                            break;

                        case 2: // Ready: where to send and receive the voice packets
                            _ssrc = d.GetProperty("ssrc").GetUInt32();
                            string ip = d.GetProperty("ip").GetString() ?? "";
                            int port = d.GetProperty("port").GetInt32();
                            var modes = d.TryGetProperty("modes", out var m) ? m.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
                            string mode = new[] { "aead_aes256_gcm_rtpsize", "aead_xchacha20_poly1305_rtpsize" }.FirstOrDefault(modes.Contains) ?? modes.FirstOrDefault() ?? "aead_xchacha20_poly1305_rtpsize";
                            udp = new UdpClient();
                            udp.Connect(ip, port);
                            var (myIp, myPort) = await DiscoverAsync(udp, _ssrc, connection.Token).ConfigureAwait(false);
                            await Send(new { op = 1, d = new { protocol = "udp", data = new { address = myIp, port = myPort, mode } } }).ConfigureAwait(false);
                            var u = udp;
                            receive = Task.Run(() => ReceivePacketsAsync(u, connection.Token));
                            keepAlive = Task.Run(() => KeepAliveAsync(u, connection.Token));
                            break;

                        case 4: // Session description: connected
                            _log.Info("Voice (who's talking): the bot is listening in the host's voice channel.");
                            Problem = null;
                            break;

                        case 5: // Speaking: whose packets are whose
                            if (d.TryGetProperty("ssrc", out var s5) && d.TryGetProperty("user_id", out var u5) && u5.GetString() is string who)
                            {
                                uint ssrc = s5.GetUInt32();
                                _users[ssrc] = who;
                                if (d.TryGetProperty("speaking", out var sp) && sp.TryGetInt32(out var flags) && flags != 0) Heard(ssrc);
                            }
                            break;

                        case 13: // Someone left the channel
                            if (d.TryGetProperty("user_id", out var gone) && gone.GetString() is string left)
                                foreach (var kv in _users.Where(kv => kv.Value == left).ToList()) { _users.TryRemove(kv.Key, out _); _heard.TryRemove(kv.Key, out _); }
                            break;

                        case 21: // End-to-end encryption: a change is coming. Say ready (nothing to change for a listener).
                            if (d.TryGetProperty("transition_id", out var tid))
                                await Send(new { op = 23, d = new { transition_id = tid.GetInt32() } }).ConfigureAwait(false);
                            break;
                    }
                }
            }
            finally
            {
                connection.Cancel();
                foreach (var t in new[] { heartbeat, receive, keepAlive })
                    if (t != null) { try { await t.ConfigureAwait(false); } catch (Exception) { } }
                udp?.Dispose();
            }
            return socket.CloseStatus.HasValue ? (int)socket.CloseStatus.Value : (int?)null;
        }

        /// <summary>Discord's IP discovery: asks the voice server which address our packets come from.</summary>
        private static async Task<(string Ip, int Port)> DiscoverAsync(UdpClient udp, uint ssrc, CancellationToken ct)
        {
            var packet = new byte[74];
            packet[0] = 0; packet[1] = 1;              // request
            packet[2] = 0; packet[3] = 70;             // length
            packet[4] = (byte)(ssrc >> 24); packet[5] = (byte)(ssrc >> 16); packet[6] = (byte)(ssrc >> 8); packet[7] = (byte)ssrc;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await udp.SendAsync(packet, packet.Length).ConfigureAwait(false);
                var got = udp.ReceiveAsync();
                if (await Task.WhenAny(got, Task.Delay(1000, ct)).ConfigureAwait(false) != got) continue;
                var r = (await got.ConfigureAwait(false)).Buffer;
                if (r.Length < 74 || r[1] != 2) continue;
                int end = Array.IndexOf(r, (byte)0, 8);
                string ip = Encoding.ASCII.GetString(r, 8, (end < 0 ? 72 : end) - 8);
                int port = (r[72] << 8) | r[73];
                return (ip, port);
            }
            throw new IOException("the voice server didn't answer IP discovery");
        }

        private async Task ReceivePacketsAsync(UdpClient udp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try
                {
                    var got = udp.ReceiveAsync();
                    var done = await Task.WhenAny(got, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                    if (done != got) return;
                    r = await got.ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }
                if (RtpSsrc(r.Buffer) is uint ssrc && ssrc != _ssrc) Heard(ssrc);
            }
        }

        /// <summary>The sender of a voice packet (RTP version 2, Opus), or null for anything else.</summary>
        public static uint? RtpSsrc(byte[] p)
        {
            if (p.Length < 12 || (p[0] & 0xC0) != 0x80) return null;
            int type = p[1] & 0x7F;
            if (type != 120) return null;              // Opus voice (not the control packets)
            return (uint)((p[8] << 24) | (p[9] << 16) | (p[10] << 8) | p[11]);
        }

        /// <summary>A small packet every few seconds keeps the route open (as Discord's own apps do).</summary>
        private static async Task KeepAliveAsync(UdpClient udp, CancellationToken ct)
        {
            long n = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    var b = BitConverter.GetBytes(++n);
                    await udp.SendAsync(b, b.Length).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { return; }
            }
        }

        private void Heard(uint ssrc) => _heard[ssrc] = DateTime.UtcNow.Ticks;

        /// <summary>Turns packets arriving (or not) into talking starts and stops, 20 times a second.</summary>
        private async Task WatchAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(50, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                long now = DateTime.UtcNow.Ticks;
                var talkingNow = new HashSet<string>();
                foreach (var (ssrc, at) in _heard)
                    if (now - at < QuietAfter.Ticks && _users.TryGetValue(ssrc, out var user)) talkingNow.Add(user);
                Report(talkingNow);
            }
        }

        private void Report(HashSet<string> talkingNow)
        {
            List<(string, bool)> changes;
            lock (_talking)
            {
                changes = talkingNow.Where(u => !_talking.Contains(u)).Select(u => (u, true))
                    .Concat(_talking.Where(u => !talkingNow.Contains(u)).Select(u => (u, false))).ToList();
                _talking.Clear();
                _talking.UnionWith(talkingNow);
            }
            foreach (var (user, on) in changes)
            {
                try { Talking?.Invoke(user, on); } catch (Exception) { }
            }
        }

        private void AllQuiet() => Report(new HashSet<string>());

        private static async Task<(string? Text, bool Binary)> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return (null, false);
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                if (result.MessageType == WebSocketMessageType.Binary) return (null, true);
                return (Encoding.UTF8.GetString(message.ToArray()), false);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _run?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            AllQuiet();
        }
    }
}
