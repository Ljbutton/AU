using System.Text;
using TournamentTracker.Control;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>The small HTTP reader every local server uses (the mod, The Button, Red Alert, the Discord bridge).</summary>
public class HttpRequestTests
{
    /// <summary>A stream that hands over at most <c>chunk</c> bytes a read, like a slow socket, and counts the reads.</summary>
    private sealed class Trickle : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _at;
        public int Reads { get; private set; }

        public Trickle(byte[] data, int chunk) { _data = data; _chunk = chunk; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            int n = Math.Min(Math.Min(count, _chunk), _data.Length - _at);
            Array.Copy(_data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Request(string head, byte[]? body = null) =>
        Encoding.ASCII.GetBytes(head).Concat(body ?? Array.Empty<byte>()).ToArray();

    [Fact]
    public async Task A_get_comes_back_with_its_path_and_headers()
    {
        var bytes = Request("GET /api/status?since=4 HTTP/1.1\r\nHost: 127.0.0.1:5000\r\nX-TT-Token: abc\r\n\r\n");
        var (method, path, headers, body) = await HttpRequest.ReadRawAsync(new MemoryStream(bytes), 1000);
        Assert.Equal("GET", method);
        Assert.Equal("/api/status?since=4", path);
        Assert.Equal("abc", headers["x-tt-token"]);
        Assert.Equal("127.0.0.1:5000", headers["Host"]);          // looked up either way
        Assert.Empty(body);
    }

    [Theory]
    [InlineData(1)]         // a byte at a time: the blank line straddles reads
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]      // all at once: the body's start comes with the header
    public async Task The_body_is_whole_however_the_bytes_arrive(int chunk)
    {
        var payload = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        var bytes = Request($"POST /api/command HTTP/1.1\r\nContent-Type: application/octet-stream\r\nContent-Length: {payload.Length}\r\n\r\n", payload);
        var (method, path, _, body) = await HttpRequest.ReadRawAsync(new Trickle(bytes, chunk), 1_000_000);
        Assert.Equal("POST", method);
        Assert.Equal("/api/command", path);
        Assert.Equal(payload, body);
    }

    [Fact]
    public async Task A_browser_sized_header_takes_a_couple_of_reads_not_one_per_byte()
    {
        var head = new StringBuilder("GET /app/state HTTP/1.1\r\n");
        for (int i = 0; i < 20; i++) head.Append($"X-Header-{i}: {new string('v', 30)}\r\n");
        head.Append("\r\n");
        Assert.True(head.Length > 700);
        var stream = new Trickle(Request(head.ToString()), 64 * 1024);
        var (_, path, headers, _) = await HttpRequest.ReadRawAsync(stream, 1000);
        Assert.Equal("/app/state", path);
        Assert.Equal(new string('v', 30), headers["x-header-19"]);
        Assert.True(stream.Reads <= 2, $"{stream.Reads} reads");
    }

    [Fact]
    public async Task The_json_body_reads_as_text()
    {
        var json = "{\"command\":\"spec cam 3\",\"name\":\"Zoë\"}";
        var bytes = Encoding.UTF8.GetBytes(json);
        var all = Request($"POST /api/command HTTP/1.1\r\nContent-Length: {bytes.Length}\r\n\r\n", bytes);
        var (_, _, _, body) = await HttpRequest.ReadRawAsync(new Trickle(all, 5), 1_000_000);
        Assert.Equal(json, Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task A_short_body_comes_back_short_and_a_too_big_one_is_left_out()
    {
        var cut = Request("POST /x HTTP/1.1\r\nContent-Length: 10\r\n\r\n", new byte[] { 1, 2, 3 });
        var (_, _, _, body) = await HttpRequest.ReadRawAsync(new MemoryStream(cut), 1000);
        Assert.Equal(new byte[] { 1, 2, 3 }, body);

        var big = Request("POST /x HTTP/1.1\r\nContent-Length: 2000\r\n\r\n", new byte[2000]);
        (_, _, _, body) = await HttpRequest.ReadRawAsync(new MemoryStream(big), 1000);
        Assert.Empty(body);
    }

    [Fact]
    public async Task A_connection_closed_before_the_blank_line_still_parses()
    {
        var (method, path, headers, body) = await HttpRequest.ReadRawAsync(new MemoryStream(Request("GET /api/pov HTTP/1.1\r\nHost: x")), 1000);
        Assert.Equal("GET", method);
        Assert.Equal("/api/pov", path);
        Assert.Equal("x", headers["host"]);
        Assert.Empty(body);
        (method, path, _, _) = await HttpRequest.ReadRawAsync(new MemoryStream(), 1000);
        Assert.Equal("", method);
        Assert.Equal("/", path);
    }

    [Fact]
    public async Task A_header_over_16_KB_is_turned_down()
    {
        var huge = Request("GET / HTTP/1.1\r\nX-Big: " + new string('a', 17 * 1024) + "\r\n\r\n");
        await Assert.ThrowsAsync<IOException>(() => HttpRequest.ReadRawAsync(new Trickle(huge, 1000), 1000));
        var justUnder = Request("GET / HTTP/1.1\r\nX-Big: " + new string('a', 15 * 1024) + "\r\n\r\n");
        var (_, _, headers, _) = await HttpRequest.ReadRawAsync(new Trickle(justUnder, 1000), 1000);
        Assert.Equal(15 * 1024, headers["x-big"].Length);
    }
}

/// <summary>What every local server shares: listening on this computer only, and its token.</summary>
public class LocalServerTests
{
    private static int PortOf(System.Net.Sockets.TcpListener l) => ((System.Net.IPEndPoint)l.LocalEndpoint).Port;

    [Fact]
    public void A_taken_port_is_passed_over_for_any_free_one_on_this_computer_only()
    {
        var first = LocalServer.Listen();
        var next = LocalServer.Listen(PortOf(first), 0, 70000);
        try
        {
            Assert.NotEqual(PortOf(first), PortOf(next));
            Assert.Equal(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)next.LocalEndpoint).Address);
        }
        finally { first.Stop(); next.Stop(); }
    }

    [Fact]
    public void Tokens_are_random_and_safe_in_a_link()
    {
        string token = LocalServer.NewToken();
        Assert.NotEqual(token, LocalServer.NewToken());
        Assert.Matches("^[A-Za-z0-9_-]{24}$", token);
        Assert.Matches("^[A-Za-z0-9_-]{20}$", LocalServer.NewToken(15));
    }
}
