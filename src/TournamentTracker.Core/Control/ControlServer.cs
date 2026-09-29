using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.Control
{
    /// <summary>
    /// The private connection the Tournament Tracker app uses to run the mod: a tiny web API
    /// on this computer only (127.0.0.1), guarded by a random token that's written, with the
    /// port, to control.json in the mod's data folder for the app to read.
    /// <list type="bullet">
    /// <item>GET /api/status: the lobby, players, round, automute and the rest.</item>
    /// <item>GET /api/activity?since=N: what the mod has said since line N.</item>
    /// <item>POST /api/command {"command":"r2"}: runs a host command; answers with what the mod said.</item>
    /// </list>
    /// </summary>
    public sealed class ControlServer : IDisposable
    {
        public const string FileName = "control.json";
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Func<string> _status;
        private readonly Func<long, string> _activity;
        private readonly Func<string, Task<string>> _command;
        private readonly ILog _log;

        public int Port { get; }
        public string Token { get; }

        public ControlServer(int port, string dataDir, Func<string> status, Func<long, string> activity, Func<string, Task<string>> command, ILog log)
        {
            _status = status;
            _activity = activity;
            _command = command;
            _log = log;
            _listener = Listen(port);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            var bytes = new byte[18];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            Token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
            try
            {
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(Path.Combine(dataDir, FileName), JsonSerializer.Serialize(new { port = Port, token = Token, pid = Environment.ProcessId }));
            }
            catch (Exception e) { log.Warn("Could not write control.json: " + e.Message); }
            Task.Run(AcceptLoop);
        }

        /// <summary>The configured port, or any free one if it's taken (control.json says which).</summary>
        private static TcpListener Listen(int port)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, Math.Max(0, port));
                l.Start();
                return l;
            }
            catch (SocketException)
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                return l;
            }
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
                    var stream = client.GetStream();
                    var (method, path, headers, body) = await HttpRequest.ReadAsync(stream).ConfigureAwait(false);
                    int status = 200;
                    string reply;
                    string query = path.Contains('?') ? path.Substring(path.IndexOf('?') + 1) : "";
                    string route = path.Split('?')[0];
                    headers.TryGetValue("x-tt-token", out var token);
                    if (token != Token && HttpRequest.Query(query, "token") != Token)
                    {
                        status = 401;
                        reply = "{\"error\":\"bad token\"}";
                    }
                    else if (method == "GET" && route == "/api/status") reply = _status();
                    else if (method == "GET" && route == "/api/activity")
                        reply = _activity(long.TryParse(HttpRequest.Query(query, "since"), out var since) ? since : 0);
                    else if (method == "POST" && route == "/api/command")
                    {
                        string command = "";
                        try { command = JsonDocument.Parse(body).RootElement.GetProperty("command").GetString() ?? ""; }
                        catch (Exception) { }
                        reply = await _command(command).ConfigureAwait(false);
                    }
                    else
                    {
                        status = 404;
                        reply = "{\"error\":\"not found\"}";
                    }
                    await HttpRequest.WriteAsync(stream, status, "application/json", Encoding.UTF8.GetBytes(reply)).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _log.Warn("Control request failed: " + e.Message);
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
