using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>An answer from OBS that says no.</summary>
    public sealed class ObsException : Exception
    {
        public int Code { get; }
        public ObsException(string request, int code, string? comment) : base($"OBS refused {request}: {comment ?? "code " + code}") => Code = code;
    }

    /// <summary>One request's answer inside a batch (<see cref="ObsClient.BatchAsync"/>).</summary>
    public readonly struct ObsResult
    {
        public string Type { get; }
        public bool Ok { get; }
        public int Code { get; }
        public string? Comment { get; }
        public JsonElement Data { get; }
        public ObsResult(string type, bool ok, int code, string? comment, JsonElement data) { Type = type; Ok = ok; Code = code; Comment = comment; Data = data; }
        public ObsException Error() => new ObsException(Type, Code, Comment);
    }

    /// <summary>
    /// A connection to OBS through obs-websocket 5 (built into OBS 28 and newer): sends requests
    /// and waits for their answers, and passes on the events it's subscribed to.
    /// </summary>
    public sealed class ObsClient : IAsyncDisposable
    {
        /// <summary>
        /// Event subscriptions: General (1), Scenes (4), Inputs (8), Outputs (64, the stream and the
        /// recording starting and stopping), SceneItems (128), MediaInputs (256), Vendors (512),
        /// InputVolumeMeters (65536, the level meters).
        /// </summary>
        public const int Subscriptions = 1 | 4 | 8 | 64 | 128 | 256 | 512 | 65536;
        private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

        private readonly ClientWebSocket _ws = new ClientWebSocket();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>();
        private readonly SemaphoreSlim _send = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private long _next;
        private Task? _reader;

        /// <summary>Every event: its type and data.</summary>
        public event Action<string, JsonElement>? Event;
        /// <summary>The connection closed (OBS quit, or the network dropped).</summary>
        public event Action<string>? Closed;

        public string? ObsVersion { get; private set; }
        public bool Connected => _ws.State == WebSocketState.Open;

        /// <summary>Connects and identifies (with the password if OBS asks for one).</summary>
        public static async Task<ObsClient> ConnectAsync(string host, int port, string? password, TimeSpan timeout)
        {
            var c = new ObsClient();
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await c._ws.ConnectAsync(new Uri($"ws://{host}:{port}"), cts.Token).ConfigureAwait(false);
                var hello = await c.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (hello.GetProperty("op").GetInt32() != 0) throw new InvalidOperationException("OBS didn't say hello.");
                var d = hello.GetProperty("d");
                c.ObsVersion = d.TryGetProperty("obsStudioVersion", out var v) ? v.GetString() : d.TryGetProperty("obsWebSocketVersion", out var w) ? "websocket " + w.GetString() : null;
                var identify = new System.Collections.Generic.Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = Subscriptions };
                if (d.TryGetProperty("authentication", out var auth))
                {
                    if (string.IsNullOrEmpty(password)) throw new UnauthorizedAccessException("OBS wants a password: it's in OBS under Tools → WebSocket Server Settings → Show Connect Info.");
                    identify["authentication"] = Auth(password!, auth.GetProperty("salt").GetString()!, auth.GetProperty("challenge").GetString()!);
                }
                await c.SendRawAsync(new { op = 1, d = identify }, cts.Token).ConfigureAwait(false);
                JsonElement identified;
                try { identified = await c.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (WebSocketException) { throw new UnauthorizedAccessException("OBS turned the password down. Check it under Tools → WebSocket Server Settings."); }
                if (identified.GetProperty("op").GetInt32() != 2)
                    throw new UnauthorizedAccessException("OBS turned the password down. Check it under Tools → WebSocket Server Settings.");
                c._reader = Task.Run(c.ReadLoop);
                return c;
            }
            catch (WebSocketException e) when (c._ws.State != WebSocketState.Open)
            {
                c._ws.Dispose();
                if (c._ws.CloseStatus == (WebSocketCloseStatus)4009)
                    throw new UnauthorizedAccessException("OBS turned the password down. Check it under Tools → WebSocket Server Settings.");
                throw new InvalidOperationException($"Can't reach OBS on port {port}. Is OBS open, with Tools → WebSocket Server Settings → Enable WebSocket server ticked? ({e.Message})");
            }
            catch (OperationCanceledException)
            {
                c._ws.Dispose();
                throw new InvalidOperationException($"OBS didn't answer on port {port} in time.");
            }
            catch
            {
                c._ws.Dispose();
                throw;
            }
        }

        /// <summary>obs-websocket 5 authentication: base64(sha256(base64(sha256(password + salt)) + challenge)).</summary>
        public static string Auth(string password, string salt, string challenge)
        {
            using var sha = SHA256.Create();
            string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
        }

        /// <summary>Sends one request and waits for its answer's responseData (an empty object if it has none).</summary>
        public async Task<JsonElement> RequestAsync(string type, object? data = null, TimeSpan? timeout = null)
        {
            string id = "tt" + Interlocked.Increment(ref _next);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                await SendRawAsync(new { op = 6, d = new { requestType = type, requestId = id, requestData = data ?? new { } } }, _cts.Token).ConfigureAwait(false);
                JsonElement d;
                // WaitAsync cancels its own timer when the answer comes (no 5 s Task.Delay left behind per request).
                try { d = await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { throw new TimeoutException($"OBS didn't answer {type}."); }
                var result = Result(type, d);
                if (!result.Ok) throw result.Error();
                return result.Data;
            }
            finally { _pending.TryRemove(id, out _); }
        }

        /// <summary>One answer as a result (the message is already a copy of its own: nothing to clone again).</summary>
        private static ObsResult Result(string type, JsonElement d)
        {
            var status = d.GetProperty("requestStatus");
            bool ok = status.GetProperty("result").GetBoolean();
            return new ObsResult(d.TryGetProperty("requestType", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? type : type, ok,
                status.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0,
                status.TryGetProperty("comment", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                d.TryGetProperty("responseData", out var r) && r.ValueKind == JsonValueKind.Object ? r : Empty);
        }

        /// <summary>
        /// Several requests in one message (obs-websocket's RequestBatch), done by OBS one after another
        /// as fast as it can, each answered (a failed one doesn't stop the rest unless
        /// <paramref name="haltOnFailure"/>). One round trip instead of one per request, so a switch's
        /// moves, shows, hides and mutes land together. Answers in the same order.
        /// </summary>
        public async Task<List<ObsResult>> BatchAsync(IReadOnlyList<(string Type, object? Data)> requests, bool haltOnFailure = false, TimeSpan? timeout = null)
        {
            var results = new List<ObsResult>();
            if (requests.Count == 0) return results;
            string id = "tt" + Interlocked.Increment(ref _next);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                var list = requests.Select((r, i) => new { requestType = r.Type, requestId = id + "." + i, requestData = r.Data ?? new { } }).ToList();
                // executionType 0: SerialRealtime (in order, as fast as possible).
                await SendRawAsync(new { op = 8, d = new { requestId = id, haltOnFailure, executionType = 0, requests = list } }, _cts.Token).ConfigureAwait(false);
                JsonElement d;
                try { d = await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5 + requests.Count * 0.05)).ConfigureAwait(false); }
                catch (TimeoutException) { throw new TimeoutException($"OBS didn't answer a batch of {requests.Count} ({requests[0].Type}…)."); }
                if (d.TryGetProperty("results", out var rs) && rs.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var r in rs.EnumerateArray()) results.Add(Result(i < requests.Count ? requests[i++].Type : "?", r));
                }
                return results;
            }
            finally { _pending.TryRemove(id, out _); }
        }

        private async Task SendRawAsync(object message, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await _send.WaitAsync(ct).ConfigureAwait(false);
            try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { _send.Release(); }
        }

        // One reader at a time (the hello, then the read loop), so its buffers are kept and reused:
        // OBS sends level meters 20 times a second, and a new 16 KB buffer and copy for each added up.
        private readonly byte[] _buffer = new byte[16384];
        private readonly System.IO.MemoryStream _message = new System.IO.MemoryStream();

        private async Task<JsonElement> ReceiveAsync(CancellationToken ct)
        {
            _message.SetLength(0);
            while (true)
            {
                var r = await _ws.ReceiveAsync(_buffer, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("OBS closed the connection" + (_ws.CloseStatusDescription is { Length: > 0 } why ? ": " + why : "."));
                if (r.EndOfMessage && _message.Length == 0)
                {
                    using var one = JsonDocument.Parse(new ReadOnlyMemory<byte>(_buffer, 0, r.Count));
                    return one.RootElement.Clone();
                }
                _message.Write(_buffer, 0, r.Count);
                if (r.EndOfMessage) break;
            }
            using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(_message.GetBuffer(), 0, (int)_message.Length));
            if (_message.Capacity > 1 << 20) { _message.SetLength(0); _message.Capacity = 16384; }     // a huge one (a screenshot) isn't kept
            return doc.RootElement.Clone();
        }

        private async Task ReadLoop()
        {
            string why = "OBS closed the connection.";
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var msg = await ReceiveAsync(_cts.Token).ConfigureAwait(false);
                    int op = msg.GetProperty("op").GetInt32();
                    var d = msg.GetProperty("d");
                    if ((op == 7 || op == 9) && _pending.TryGetValue(d.GetProperty("requestId").GetString() ?? "", out var tcs)) tcs.TrySetResult(d);
                    else if (op == 5)
                    {
                        try { Event?.Invoke(d.GetProperty("eventType").GetString() ?? "", d.TryGetProperty("eventData", out var ed) ? ed : default); }
                        catch (Exception) { /* a listener's problem isn't the connection's */ }
                    }
                }
            }
            catch (Exception e) { why = e is OperationCanceledException ? "Disconnected." : e.Message; }
            foreach (var p in _pending.Values) p.TrySetException(new InvalidOperationException(why));
            if (!_cts.IsCancellationRequested) Closed?.Invoke(why);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", new CancellationTokenSource(1000).Token).ConfigureAwait(false);
            }
            catch (Exception) { }
            _ws.Dispose();
            if (_reader != null) try { await _reader.ConfigureAwait(false); } catch (Exception) { }
        }
    }
}
