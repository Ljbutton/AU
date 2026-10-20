using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
    /// <item>POST /api/command {"command":"r2"}: runs a host command; answers with what the mod said.</item>
    /// <item>GET /api/feed?since=N: the broadcast feed (events and snapshots) for the caster.</item>
    /// </list>
    /// </summary>
    public sealed class ControlServer : IDisposable
    {
        public const string FileName = "control.json";
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Func<string> _status;
        private readonly Func<string, Task<string>> _command;
        private readonly Func<long, string>? _feed;
        /// <summary>POST /api/names {"names":{key:name}}: the caster's roster names for this lobby.</summary>
        public Action<IDictionary<string, string>>? Names { get; set; }
        /// <summary>GET /api/pov?after=N: the player camera's newest picture (its number in front), for The Button's send page.</summary>
        public PlayerCam.PlayerCamFeed? Cam { get; set; }
        private readonly ILog _log;

        public int Port { get; }
        public string Token { get; }

        public ControlServer(int port, string dataDir, Func<string> status, Func<string, Task<string>> command, ILog log, Func<long, string>? feed = null)
        {
            _feed = feed;
            _status = status;
            _command = command;
            _log = log;
            // The configured port, or any free one if it's taken (control.json says which).
            _listener = LocalServer.Listen(port);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Token = LocalServer.NewToken();
            try
            {
                SafeFile.WriteAllText(Path.Combine(dataDir, FileName), JsonSerializer.Serialize(new { port = Port, token = Token, pid = Environment.ProcessId }));
            }
            catch (Exception e) { log.Warn("Could not write control.json: " + e.Message); }
            Task.Run(() => LocalServer.Serve(_listener, _cts.Token, Route, e => _log.Warn("Control request failed: " + e.Message)));
        }

        private async Task<(int, string, byte[])> Route(string method, string path, Dictionary<string, string> headers, string body)
        {
            string query = path.Contains('?') ? path.Substring(path.IndexOf('?') + 1) : "";
            string route = path.Split('?')[0];
            headers.TryGetValue("x-tt-token", out var token);
            if (token != Token && HttpRequest.Query(query, "token") != Token) return Json(401, "{\"error\":\"bad token\"}");
            if (method == "GET" && route == "/api/status") return Json(200, _status());
            if (method == "GET" && route == "/api/feed" && _feed != null)
                return Json(200, _feed(long.TryParse(HttpRequest.Query(query, "since"), out var from) ? from : 0));
            if (method == "GET" && route == "/api/pov" && Cam != null)
            {
                Cam.Asked();
                var (seq, jpeg) = await Cam.NextAsync(long.TryParse(HttpRequest.Query(query, "after"), out var after) ? after : 0, TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                return jpeg == null ? (204, "application/octet-stream", Array.Empty<byte>()) : (200, "application/octet-stream", PlayerCam.PlayerCamFeed.Frame(seq, jpeg));
            }
            if (method == "POST" && route == "/api/names" && Names != null)
            {
                var names = new Dictionary<string, string>();
                try
                {
                    foreach (var kv in JsonDocument.Parse(body).RootElement.GetProperty("names").EnumerateObject())
                        if (kv.Value.ValueKind == JsonValueKind.String) names[kv.Name] = kv.Value.GetString() ?? "";
                }
                catch (Exception) { }
                Names(names);
                return Json(200, "{\"ok\":true}");
            }
            if (method == "POST" && route == "/api/command")
            {
                string command = "";
                try { command = JsonDocument.Parse(body).RootElement.GetProperty("command").GetString() ?? ""; }
                catch (Exception) { }
                return Json(200, await _command(command).ConfigureAwait(false));
            }
            return Json(404, "{\"error\":\"not found\"}");
        }

        private static (int, string, byte[]) Json(int status, string json) => (status, "application/json", Encoding.UTF8.GetBytes(json));

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (Exception) { }
        }
    }
}
