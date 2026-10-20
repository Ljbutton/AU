using System.Collections.Concurrent;
using System.Net;
using TournamentTracker.App;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// A pretend internet for Check my connection: uploads go through a link of a set speed (shared by every
/// stream, like a real upload), other asks answer after a set time. Asks to this computer (the mod, The
/// Button) go through for real.
/// </summary>
public sealed class FakeNet : DelegatingHandler
{
    public long BytesPerSecond { get; set; } = 2_000_000;
    public TimeSpan PingTime { get; set; } = TimeSpan.FromMilliseconds(5);
    public HttpStatusCode UploadStatus { get; set; } = HttpStatusCode.OK;
    public long Received;
    public ConcurrentQueue<string> Asked { get; } = new();
    private readonly SemaphoreSlim _link = new(1, 1);

    public FakeNet() : base(new HttpClientHandler()) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.Host == "127.0.0.1") return await base.SendAsync(request, ct);
        Asked.Enqueue(request.Method + " " + request.RequestUri.AbsoluteUri);
        if (request.Method == HttpMethod.Post && request.Content != null)
        {
            if (UploadStatus != HttpStatusCode.OK) return new HttpResponseMessage(UploadStatus);
            await request.Content.CopyToAsync(new Link(this), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        await Task.Delay(PingTime, ct);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    /// <summary>Takes bytes at the link's speed (one write at a time across every upload).</summary>
    private sealed class Link : Stream
    {
        private readonly FakeNet _net;
        public Link(FakeNet net) => _net = net;
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await _net._link.WaitAsync(ct);
            try { await Task.Delay(TimeSpan.FromSeconds((double)count / _net.BytesPerSecond), ct); }
            finally { _net._link.Release(); }
            Interlocked.Add(ref _net.Received, count);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            await WriteAsync(buffer.ToArray(), 0, buffer.Length, ct);
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => 0; public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Check my connection: what counts as enough, the measuring itself (within its data budget), and stopping it.</summary>
public class ConnectionCheckTests
{
    private static ConnectionCheck.Options Quick(long budget = 30L * 1024 * 1024) => new()
    {
        UploadUrl = "https://up.example/__up", DiscordUrl = "https://discord.example/api/v10/gateway", VdoUrl = "https://vdo.example/",
        Duration = TimeSpan.FromSeconds(2.5), Warmup = TimeSpan.FromSeconds(0.6), Sample = TimeSpan.FromMilliseconds(100),
        RequestBytes = 512 * 1024, Budget = budget, Pings = 3, LoadedPingEvery = TimeSpan.FromMilliseconds(200),
    };

    private static PingResult Ping(string name, double ms, double jitter = 2, int failed = 0) => new() { Name = name, Ms = ms, JitterMs = jitter, Tries = 5, Failed = failed };

    [Theory]
    [InlineData(25.0, "good")]
    [InlineData(10.0, "good")]
    [InlineData(9.0, "ok")]       // enough, little to spare
    [InlineData(7.9, "warn")]     // under 8
    [InlineData(4.9, "bad")]      // under 5
    public void Upload_is_scored_against_what_sending_the_game_needs(double mbps, string level)
    {
        var score = ConnectionCheck.Score(mbps, Ping("Discord", 30), Ping("VDO.Ninja", 40), 45);
        Assert.Equal(level, score.Level);
        Assert.Contains(score.Facts, f => f.StartsWith("Upload: ") && f.EndsWith("(10 Mbps recommended)"));
        if (level == "good") Assert.Empty(score.Advice);
        if (level is "warn" or "bad") Assert.Contains(score.Advice, a => a.Contains("wired (Ethernet)"));
        if (level != "good") Assert.Contains(score.Advice, a => a.Contains("Lower quality"));
        if (level == "bad") Assert.Contains(score.Advice, a => a.Contains("Tell the organiser"));
    }

    [Fact]
    public void Slow_answers_jitter_lag_under_load_and_unreachable_servers_are_warned_about()
    {
        Assert.Equal("warn", ConnectionCheck.Score(20, Ping("Discord", 250), Ping("VDO.Ninja", 40), null).Level);
        Assert.Equal("bad", ConnectionCheck.Score(20, Ping("Discord", 450), Ping("VDO.Ninja", 40), null).Level);
        Assert.Equal("warn", ConnectionCheck.Score(20, Ping("Discord", 30, jitter: 40), Ping("VDO.Ninja", 40), null).Level);
        Assert.Equal("bad", ConnectionCheck.Score(20, Ping("Discord", 30), Ping("VDO.Ninja", 40, jitter: 90), null).Level);
        Assert.Equal("warn", ConnectionCheck.Score(20, Ping("Discord", 30), Ping("VDO.Ninja", 40, failed: 1), null).Level);

        var lag = ConnectionCheck.Score(20, Ping("Discord", 30), Ping("VDO.Ninja", 40), 230);
        Assert.Equal("warn", lag.Level);
        Assert.Contains(lag.Advice, a => a.Contains("lags while it uploads"));
        Assert.Equal("bad", ConnectionCheck.Score(20, Ping("Discord", 30), Ping("VDO.Ninja", 40), 500).Level);

        var blocked = ConnectionCheck.Score(20, Ping("Discord", 30), new PingResult { Name = "VDO.Ninja", Tries = 2, Failed = 2 }, null);
        Assert.Equal("bad", blocked.Level);
        Assert.Contains("VDO.Ninja: didn't answer", blocked.Facts);
        Assert.Contains(blocked.Advice, a => a.StartsWith("VDO.Ninja didn't answer"));

        var none = ConnectionCheck.Score(null, Ping("Discord", 30), Ping("VDO.Ninja", 40), null, "the speed test server answered 403");
        Assert.Equal("bad", none.Level);
        Assert.Contains("Upload: couldn't be measured (the speed test server answered 403)", none.Facts);
        Assert.Equal("The check found a problem: see below.", none.Headline);
    }

    [Fact]
    public void The_middle_answer_time_jitter_and_speed_are_worked_out_from_samples()
    {
        var p = PingResult.From("Discord", new[] { 10.0, 30, 20 }, 1);
        Assert.Equal(20, p.Ms);
        Assert.Equal(15, p.JitterMs);                  // |30-10| and |20-30|, averaged
        Assert.Equal(4, p.Tries);
        Assert.Null(PingResult.From("x", Array.Empty<double>(), 2).Ms);
        Assert.Equal(25, PingResult.From("x", new[] { 20.0, 30 }, 0).Ms);

        // 2 MB in the 2 s after the warm-up: 8 Mbps (the warm-up's bytes aren't counted).
        var samples = new List<(double, long)> { (0, 0), (0.5, 900_000), (1, 1_000_000), (2, 2_000_000), (3, 3_000_000) };
        Assert.Equal(8, ConnectionCheck.Speed(samples, 1)!.Value, 3);
        // Over before the warm-up was done (a very fast upload used the whole budget): from the start.
        Assert.Equal(16, ConnectionCheck.Speed(new List<(double, long)> { (0, 0), (0.5, 1_000_000) }, 1.5)!.Value, 3);
        Assert.Null(ConnectionCheck.Speed(new List<(double, long)> { (0, 0) }, 1));
        Assert.Equal("23 Mbps", ConnectionCheck.Mbps(23.4));
        Assert.Equal("7.9 Mbps", ConnectionCheck.Mbps(7.94));
        Assert.Equal("over 100 Mbps", ConnectionCheck.Mbps(250));
        Assert.Contains("10 Mbps upload is recommended", ConnectionCheck.Needs);
    }

    [Fact]
    public async Task It_measures_the_upload_in_parallel_and_asks_Discord_and_VDO_Ninja()
    {
        var net = new FakeNet { BytesPerSecond = 2_000_000 };            // 16 Mbps
        var phases = new ConcurrentQueue<string>();
        var r = await ConnectionCheck.RunAsync(new HttpClient(net), Quick(), (phase, _) => phases.Enqueue(phase), CancellationToken.None);
        Assert.NotNull(r.UploadMbps);
        Assert.InRange(r.UploadMbps!.Value, 10, 20);
        Assert.True(r.Discord!.Reached);
        Assert.True(r.Vdo!.Reached);
        Assert.NotNull(r.LoadedMs);
        Assert.Contains("Measuring your upload…", phases);
        Assert.Contains(net.Asked, a => a.StartsWith("POST https://up.example/__up?bytes="));
        Assert.Contains(net.Asked, a => a.StartsWith("HEAD https://vdo.example/"));
        Assert.Contains(net.Asked, a => a.StartsWith("GET https://discord.example/api/v10/gateway"));
        Assert.True(net.Asked.Count(a => a.StartsWith("POST")) >= 4);                 // several uploads side by side
        Assert.True(r.BytesSent >= net.Received);
        Assert.Equal("good", r.Score.Level);
    }

    [Fact]
    public async Task A_fast_upload_stops_at_the_data_budget()
    {
        var net = new FakeNet { BytesPerSecond = 40_000_000 };
        long budget = 3L * 1024 * 1024;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var r = await ConnectionCheck.RunAsync(new HttpClient(net), Quick(budget), null, CancellationToken.None);
        Assert.True(r.BytesSent <= budget, $"sent {r.BytesSent}");
        Assert.True(net.Received <= budget);
        Assert.True(r.BytesSent > budget / 2);
        Assert.True(r.UploadMbps > 15, $"{r.UploadMbps} Mbps");   // the fake network paces itself with short sleeps, which Windows rounds up to ~15 ms
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.4));                      // finished before the 2.5 s it was allowed
    }

    [Fact]
    public async Task Stopping_it_sends_nothing_more_and_a_refused_upload_is_a_problem_not_a_speed()
    {
        var net = new FakeNet { BytesPerSecond = 500_000 };
        using var cts = new CancellationTokenSource();
        var run = ConnectionCheck.RunAsync(new HttpClient(net), Quick(), (phase, _) => { if (phase.StartsWith("Measuring")) cts.CancelAfter(300); }, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        long after = Interlocked.Read(ref net.Received);
        await Task.Delay(400);
        Assert.Equal(after, Interlocked.Read(ref net.Received));

        var refused = new FakeNet { UploadStatus = HttpStatusCode.Forbidden };
        var r = await ConnectionCheck.RunAsync(new HttpClient(refused), Quick(), null, CancellationToken.None);
        Assert.Null(r.UploadMbps);
        Assert.Equal("the speed test server answered 403", r.Problem);
        Assert.Equal("bad", r.Score.Level);
    }
}
