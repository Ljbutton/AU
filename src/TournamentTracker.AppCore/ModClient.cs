using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App
{
    /// <summary>Talks to the mod inside Among Us over its private connection (control.json has the port and token).</summary>
    public sealed class ModClient
    {
        /// <summary>
        /// How long a call waits for the mod. The shared HttpClient allows 10 minutes (for downloads), so
        /// without these a frozen or loading game held every refresh of The Button that long. A command
        /// can take the mod up to 13 s (5 s for the game, 8 s more for Discord).
        /// </summary>
        public static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(5), CommandTimeout = TimeSpan.FromSeconds(20);

        private readonly HttpClient _http;
        public ModClient(HttpClient http) => _http = http;

        /// <summary>control.json as last read: kept while the file is unchanged (it's looked at 30 times a second for the camera).</summary>
        private sealed class Known
        {
            public string File = "";
            public DateTime Written;
            public DateTime Checked;
            public (int Port, string Token)? Connection;
        }
        private volatile Known? _known;

        private (int Port, string Token)? Connection(string gameDir)
        {
            try
            {
                string file = Path.Combine(ModInstaller.DataDir(gameDir), Control.ControlServer.FileName);
                var now = DateTime.UtcNow;
                var known = _known;
                // The file's time is checked at most once a second; it's read again only when it changed
                // (the mod writes a new port and token each time Among Us starts).
                if (known != null && known.File == file && now - known.Checked < TimeSpan.FromSeconds(1)) return known.Connection;
                if (!File.Exists(file)) { _known = null; return null; }
                var written = File.GetLastWriteTimeUtc(file);
                if (known != null && known.File == file && known.Written == written && known.Connection != null)
                {
                    known.Checked = now;
                    return known.Connection;
                }
                var json = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
                var connection = (json.GetProperty("port").GetInt32(), json.GetProperty("token").GetString() ?? "");
                _known = new Known { File = file, Written = written, Checked = now, Connection = connection };
                return connection;
            }
            catch (Exception) { _known = null; return null; }
        }

        /// <summary>A call that didn't get through: read control.json again next time (Among Us may have restarted on a new port).</summary>
        private void Forget() => _known = null;

        /// <summary>One call to the mod, its answer read by <paramref name="read"/> (null: no answer).</summary>
        private async Task<T?> SendAsync<T>(string gameDir, HttpMethod method, string path, Func<HttpResponseMessage, CancellationToken, Task<T?>> read, string? body = null, TimeSpan? timeout = null)
            where T : class
        {
            var c = Connection(gameDir);
            if (c == null) return null;
            try
            {
                using var cts = new CancellationTokenSource(timeout ?? QuickTimeout);
                using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{c.Value.Port}/{path}");
                request.Headers.Add("X-TT-Token", c.Value.Token);
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
                return await read(response, cts.Token).ConfigureAwait(false);
            }
            catch (Exception) { Forget(); return null; }   // Among Us isn't running (or the mod isn't loaded), or it didn't answer in time
        }

        private Task<string?> SendAsync(string gameDir, HttpMethod method, string path, string? body = null, TimeSpan? timeout = null) =>
            SendAsync(gameDir, method, path, async (r, ct) => r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null, body, timeout);

        /// <summary>The player camera's newest picture after number <paramref name="after"/> (its number in the first 8 bytes), or null for none.</summary>
        public Task<byte[]?> PlayerCamAsync(string gameDir, long after) =>
            SendAsync(gameDir, HttpMethod.Get, $"api/pov?after={after}",
                async (r, ct) => r.StatusCode == System.Net.HttpStatusCode.OK ? await r.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : null);

        public Task<string?> StatusAsync(string gameDir) => SendAsync(gameDir, HttpMethod.Get, "api/status");
        public Task<string?> NamesAsync(string gameDir, string namesJson) => SendAsync(gameDir, HttpMethod.Post, "api/names", "{\"names\":" + namesJson + "}");
        public Task<string?> FeedAsync(string gameDir, long since) => SendAsync(gameDir, HttpMethod.Get, "api/feed?since=" + since);
        public Task<string?> CommandAsync(string gameDir, string command) =>
            SendAsync(gameDir, HttpMethod.Post, "api/command", JsonSerializer.Serialize(new { command }), CommandTimeout);
    }
}
