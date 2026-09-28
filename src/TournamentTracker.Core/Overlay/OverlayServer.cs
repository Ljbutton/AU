using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
                    string body, type;
                    int status = 200;
                    if (path.StartsWith("/state", StringComparison.Ordinal))
                    {
                        body = path.Contains("full=1") ? FullJson : SafeJson;
                        type = "application/json";
                    }
                    else if (path == "/" || path.StartsWith("/?", StringComparison.Ordinal) || path.StartsWith("/overlay", StringComparison.Ordinal))
                    {
                        body = OverlayPage.Html;
                        type = "text/html; charset=utf-8";
                    }
                    else
                    {
                        status = 404;
                        body = "Not found";
                        type = "text/plain";
                    }
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\n" +
                                  "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
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

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (Exception) { }
        }
    }
}
