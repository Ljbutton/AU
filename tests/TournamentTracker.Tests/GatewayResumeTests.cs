using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TournamentTracker.Discord;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>A local stand-in for Discord's gateway: each connection runs the test's script.</summary>
public sealed class FakeGatewayServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _accept;

    public sealed class Conn
    {
        private readonly WebSocket _ws;
        private readonly SemaphoreSlim _send = new(1, 1);
        public Conn(WebSocket ws, int index) { _ws = ws; Index = index; }
        public int Index { get; }

        public async Task Send(string json)
        {
            await _send.WaitAsync();
            try { await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, default); }
            finally { _send.Release(); }
        }

        /// <summary>The client's next message, or null once it has gone.</summary>
        public async Task<JsonElement?> Receive(int timeoutMs = 5000)
        {
            var buf = new byte[16384];
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                var r = await _ws.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                return JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, r.Count)).RootElement.Clone();
            }
            catch (Exception) { return null; }
        }

        /// <summary>The client's next message with this op code (heartbeats along the way are acked).</summary>
        public async Task<JsonElement?> Expect(int op)
        {
            while (true)
            {
                var m = await Receive();
                if (m == null) return null;
                int got = m.Value.GetProperty("op").GetInt32();
                if (got == op) return m;
                if (got == 1) await Send("""{"op":11}""");
            }
        }

        public async Task Close(int code)
        {
            await _send.WaitAsync();
            try { await _ws.CloseOutputAsync((WebSocketCloseStatus)code, "closed by test", default); }
            catch (Exception) { }
            finally { _send.Release(); }
        }
    }

    public FakeGatewayServer(Func<Conn, Task> script)
    {
        Port = Random.Shared.Next(20000, 40000);
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _accept = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                int index = Interlocked.Increment(ref _connections) - 1;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                        await script(new Conn(ws, index));
                    }
                    catch (Exception e) { Errors.Enqueue(e); }
                });
            }
        });
    }

    private int _connections;
    public int Port { get; }
    public string Url => $"ws://127.0.0.1:{Port}/";
    public int Connections => Volatile.Read(ref _connections);
    public ConcurrentQueue<Exception> Errors { get; } = new();

    public void Dispose()
    {
        try { _listener.Stop(); } catch (Exception) { }
        _listener.Close();
    }
}

public class GatewayResumeTests
{
    private static string Ready(int seq, string? resumeUrl)
    {
        string resume = resumeUrl == null ? "" : ",\"resume_gateway_url\":\"" + resumeUrl + "\"";
        return "{\"op\":0,\"s\":" + seq + ",\"t\":\"READY\",\"d\":{\"session_id\":\"sess-1\",\"user\":{\"id\":\"bot1\"},\"application\":{\"id\":\"app1\"}" + resume + "}}";
    }

    [Fact]
    public async Task A_dropped_connection_is_resumed_and_the_events_in_between_still_arrive()
    {
        int identifies = 0;
        JsonElement? resume = null;
        FakeGatewayServer? server = null;
        server = new FakeGatewayServer(async c =>
        {
            await c.Send("""{"op":10,"d":{"heartbeat_interval":5000}}""");
            if (c.Index == 0)
            {
                await c.Expect(2);
                Interlocked.Increment(ref identifies);
                await c.Send(Ready(1, $"ws://127.0.0.1:{server!.Port}"));
                await c.Send("""{"op":0,"s":2,"t":"GUILD_CREATE","d":{"id":"g1","voice_states":[{"user_id":"1","channel_id":"vc"}],"members":[{"user":{"id":"1","username":"alice"}}]}}""");
                await c.Send("""{"op":7,"d":null}""");                 // Discord: reconnect, please
                await c.Receive(2000);
            }
            else
            {
                var m = await c.Receive();
                if (m?.GetProperty("op").GetInt32() == 2) Interlocked.Increment(ref identifies);
                resume = m;
                // Replayed while the bot was away, then RESUMED.
                await c.Send("""{"op":0,"s":3,"t":"MESSAGE_CREATE","d":{"id":"55","channel_id":"300","content":"!start","author":{"id":"9"}}}""");
                await c.Send("""{"op":0,"s":4,"t":"RESUMED","d":null}""");
                await c.Receive(3000);
            }
        });
        using var _ = server;

        using var gateway = new VoiceGateway("tok", "g1", NullLog.Instance, server.Url, listenToMessages: true) { WaitScale = 0.01 };
        var messages = new ConcurrentQueue<ChannelMessage>();
        gateway.State.MessageCreated += messages.Enqueue;
        gateway.Start();

        await Wait.Until(() => messages.Count == 1 && gateway.Connected, 5000);
        Assert.Equal("!start", messages.Single().Content);
        Assert.Equal(1, identifies);                                  // resumed, not identified again
        Assert.Equal(6, resume!.Value.GetProperty("op").GetInt32());
        var d = resume.Value.GetProperty("d");
        Assert.Equal("sess-1", d.GetProperty("session_id").GetString());
        Assert.Equal(2, d.GetProperty("seq").GetInt32());
        Assert.Equal("tok", d.GetProperty("token").GetString());
        Assert.Single(gateway.Members);                               // voice states kept across the resume
    }

    [Fact]
    public async Task A_session_Discord_says_is_gone_starts_a_new_one()
    {
        var ops = new ConcurrentQueue<(int Conn, int Op)>();
        FakeGatewayServer? server = null;
        server = new FakeGatewayServer(async c =>
        {
            await c.Send("""{"op":10,"d":{"heartbeat_interval":5000}}""");
            var m = await c.Receive();
            if (m == null) return;
            ops.Enqueue((c.Index, m.Value.GetProperty("op").GetInt32()));
            if (c.Index == 0)
            {
                await c.Send(Ready(1, $"ws://127.0.0.1:{server!.Port}"));
                await c.Send("""{"op":9,"d":false}""");                 // not resumable
            }
            else await c.Send(Ready(1, null));
            await c.Receive(3000);
        });
        using var _ = server;

        using var gateway = new VoiceGateway("tok", "g1", NullLog.Instance, server.Url) { WaitScale = 0.01 };
        gateway.Start();
        await Wait.Until(() => ops.Count >= 2 && gateway.Connected, 5000);
        Assert.Equal(new[] { (0, 2), (1, 2) }, ops.Take(2));          // identify, then identify again (no resume)
    }

    [Fact]
    public async Task A_resume_address_that_is_not_Discord_is_never_used()
    {
        var ops = new ConcurrentQueue<int>();
        var server = new FakeGatewayServer(async c =>
        {
            await c.Send("""{"op":10,"d":{"heartbeat_interval":5000}}""");
            var m = await c.Receive();
            if (m == null) return;
            ops.Enqueue(m.Value.GetProperty("op").GetInt32());
            await c.Send(Ready(1, "ws://elsewhere.example"));
            if (c.Index == 0) await c.Send("""{"op":7,"d":null}""");
            await c.Receive(3000);
        });
        using var _ = server;

        using var gateway = new VoiceGateway("tok", "g1", NullLog.Instance, server.Url) { WaitScale = 0.01 };
        gateway.Start();
        await Wait.Until(() => ops.Count >= 2, 5000);
        Assert.Equal(new[] { 2, 2 }, ops.Take(2));                    // the token never goes to that address
        Assert.Null(gateway.ResumeUri("wss://evil.example"));
        Assert.Equal("wss://gateway-us-east1-b.discord.gg/?v=10&encoding=json",
            new VoiceGateway("t", "g", NullLog.Instance).ResumeUri("wss://gateway-us-east1-b.discord.gg")!.AbsoluteUri);
    }

    [Fact]
    public async Task Connections_Discord_keeps_closing_are_tried_ever_more_slowly()
    {
        var identifies = new ConcurrentQueue<DateTime>();
        var server = new FakeGatewayServer(async c =>
        {
            await c.Send("""{"op":10,"d":{"heartbeat_interval":5000}}""");
            if (await c.Expect(2) != null) identifies.Enqueue(DateTime.UtcNow);
            await c.Close(4000);                                       // unknown error, before READY every time
        });
        using var _ = server;

        // A tenth of the real waits: identifies at least 0.55 s apart (5.5 s for real).
        using var gateway = new VoiceGateway("tok", "g1", NullLog.Instance, server.Url) { WaitScale = 0.1 };
        gateway.Start();
        await Task.Delay(2500);
        gateway.Dispose();
        var times = identifies.ToList();
        Assert.InRange(times.Count, 2, 5);
        for (int i = 1; i < times.Count; i++)
            Assert.True(times[i] - times[i - 1] >= TimeSpan.FromMilliseconds(500), $"identify {i} came {(times[i] - times[i - 1]).TotalMilliseconds:0} ms after the last");
        Assert.Null(gateway.State.Problem);
    }

    [Fact]
    public void The_wait_grows_to_a_minute_and_an_invalid_session_waits_one_to_five_seconds()
    {
        var gateway = new VoiceGateway("tok", "g1", NullLog.Instance);
        Assert.InRange(gateway.ReconnectWait(0, false).TotalSeconds, 0.5, 1.5);     // a good connection dropped: straight back
        Assert.InRange(gateway.ReconnectWait(1, false).TotalSeconds, 0.5, 1.5);
        Assert.Equal(2, gateway.ReconnectWait(2, false).TotalSeconds);
        Assert.Equal(16, gateway.ReconnectWait(5, false).TotalSeconds);
        Assert.Equal(60, gateway.ReconnectWait(12, false).TotalSeconds);
        Assert.InRange(gateway.ReconnectWait(0, true).TotalSeconds, 1, 5);
    }

    [Fact]
    public async Task A_refused_token_stops_the_bot_even_while_it_is_heartbeating()
    {
        var server = new FakeGatewayServer(async c =>
        {
            await c.Send("""{"op":10,"d":{"heartbeat_interval":15}}""");
            await c.Expect(2);
            var acks = Task.Run(async () => { while (await c.Expect(1) != null) await c.Send("""{"op":11}"""); });
            await Task.Delay(120);                                     // heartbeats flying both ways
            await c.Close(4004);
            await acks;
        });
        using var _ = server;

        using var gateway = new VoiceGateway("bad", "g1", NullLog.Instance, server.Url) { WaitScale = 0.01 };
        gateway.Start();
        await Wait.Until(() => gateway.State.Problem != null, 5000);
        Assert.StartsWith("Discord turned down the bot's token", gateway.State.Problem);
        await Task.Delay(300);
        Assert.Equal(1, server.Connections);                           // never tried again
        Assert.False(gateway.Connected);
    }

    [Fact]
    public async Task The_bot_asks_to_join_voice_again_when_it_is_not_listening()
    {
        var voice = new ConcurrentQueue<string?>();
        var server = new FakeGatewayServer(async c =>
        {
            await c.Send("""{"op":10,"d":{"heartbeat_interval":5000}}""");
            await c.Expect(2);
            await c.Send(Ready(1, null));
            while (true)
            {
                var m = await c.Expect(4);                             // Discord never answers the join
                if (m == null) return;
                var ch = m.Value.GetProperty("d").GetProperty("channel_id");
                voice.Enqueue(ch.ValueKind == JsonValueKind.String ? ch.GetString() : null);
            }
        });
        using var _ = server;

        using var gateway = new VoiceGateway("tok", "g1", NullLog.Instance, server.Url)
        {
            WaitScale = 0.01, VoiceCheckEvery = TimeSpan.FromMilliseconds(30), RejoinAfter = TimeSpan.FromMilliseconds(100),
        };
        gateway.JoinVoice("vc");
        gateway.Start();
        await Wait.Until(() => voice.Count >= 3, 5000);
        Assert.All(voice, ch => Assert.Equal("vc", ch));

        // Out of voice on purpose: no more asking.
        gateway.JoinVoice(null);
        await Wait.Until(() => voice.LastOrDefault() == null, 2000);
        int n = voice.Count;
        await Task.Delay(400);
        Assert.Equal(n, voice.Count);
    }
}
