using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.Overlay
{
    /// <summary>
    /// A tiny web server on this computer only (127.0.0.1) for an OBS browser source:
    /// "/" is the overlay page and "/state" the live data it polls. "/state?full=1" includes
    /// roles, kills and tasks, for a stream on a delay. Nothing is reachable from outside.
    /// </summary>
    public sealed class OverlayServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ILog _log;

        /// <summary>The latest state, set by the game thread.</summary>
        public volatile string SafeJson = "{}";
        public volatile string FullJson = "{}";

        /// <summary>More pages (for the caster's copy of The Button): path → content type and body, or null.</summary>
        public Func<string, (string Type, byte[] Body)?>? Extra;

        public int Port { get; }
        public string Url => $"http://localhost:{Port}/";

        public OverlayServer(int port, ILog log)
        {
            _log = log;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(AcceptLoop);
        }

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
                    client.ReceiveTimeout = 2000;
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    string? requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (requestLine == null) return;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false))) { }

                    var parts = requestLine.Split(' ');
                    string path = parts.Length > 1 ? parts[1] : "/";
                    byte[] bytes;
                    string type, cache = "no-store";
                    int status = 200;
                    var crew = Regex.Match(path, @"^/crew/(\d{1,2})\.png$");
                    if (path.StartsWith("/state", StringComparison.Ordinal))
                    {
                        bytes = Encoding.UTF8.GetBytes(path.Contains("full=1") ? FullJson : SafeJson);
                        type = "application/json";
                    }
                    else if (path == "/" || path.StartsWith("/?", StringComparison.Ordinal) || path.StartsWith("/overlay", StringComparison.Ordinal))
                    {
                        bytes = Encoding.UTF8.GetBytes(OverlayPage.Html);
                        type = "text/html; charset=utf-8";
                    }
                    else if (crew.Success && CrewHead(int.Parse(crew.Groups[1].Value)) is byte[] png)
                    {
                        bytes = png;
                        type = "image/png";
                        cache = "max-age=86400";
                    }
                    else if (Extra?.Invoke(path) is (string, byte[]) extra)
                    {
                        (type, bytes) = extra;
                    }
                    else
                    {
                        status = 404;
                        bytes = Encoding.UTF8.GetBytes("Not found");
                        type = "text/plain";
                    }
                    string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\n" +
                                  $"Cache-Control: {cache}\r\nConnection: close\r\n\r\n";
                    byte[] headBytes = Encoding.ASCII.GetBytes(head);
                    await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
                    await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _log.Warn("Overlay request failed: " + e.Message);
                }
            }
        }

        private static readonly Dictionary<int, byte[]?> Heads = new Dictionary<int, byte[]?>();

        /// <summary>A crewmate head in colour <paramref name="colorId"/> (0–17), built into the DLL.</summary>
        public static byte[]? CrewHead(int colorId)
        {
            if (colorId < 0 || colorId > 17) return null;
            lock (Heads)
            {
                if (Heads.TryGetValue(colorId, out var cached)) return cached;
                using var stream = typeof(OverlayServer).Assembly.GetManifestResourceStream($"crew/{colorId}.png");
                byte[]? bytes = null;
                if (stream != null)
                {
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    bytes = copy.ToArray();
                }
                return Heads[colorId] = bytes;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (Exception) { }
        }
    }
}
