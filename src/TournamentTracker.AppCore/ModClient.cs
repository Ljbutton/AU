using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TournamentTracker.App
{
    /// <summary>Talks to the mod inside Among Us over its private connection (control.json has the port and token).</summary>
    public sealed class ModClient
    {
        private readonly HttpClient _http;
        public ModClient(HttpClient http) => _http = http;

        private static (int Port, string Token)? Connection(string gameDir)
        {
            try
            {
                string file = Path.Combine(ModInstaller.DataDir(gameDir), Control.ControlServer.FileName);
                if (!File.Exists(file)) return null;
                var json = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
                return (json.GetProperty("port").GetInt32(), json.GetProperty("token").GetString() ?? "");
            }
            catch (Exception) { return null; }
        }

        private async Task<string?> SendAsync(string gameDir, HttpMethod method, string path, string? body = null)
        {
            var c = Connection(gameDir);
            if (c == null) return null;
            try
            {
                var request = new HttpRequestMessage(method, $"http://127.0.0.1:{c.Value.Port}/{path}");
                request.Headers.Add("X-TT-Token", c.Value.Token);
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(request).ConfigureAwait(false);
                return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
            }
            catch (Exception) { return null; }   // Among Us isn't running (or the mod isn't loaded)
        }

        public Task<string?> StatusAsync(string gameDir) => SendAsync(gameDir, HttpMethod.Get, "api/status");
        public Task<string?> ActivityAsync(string gameDir, long since) => SendAsync(gameDir, HttpMethod.Get, "api/activity?since=" + since);
        public Task<string?> NamesAsync(string gameDir, string namesJson) => SendAsync(gameDir, HttpMethod.Post, "api/names", "{\"names\":" + namesJson + "}");
        public Task<string?> FeedAsync(string gameDir, long since) => SendAsync(gameDir, HttpMethod.Get, "api/feed?since=" + since);
        public Task<string?> CommandAsync(string gameDir, string command) =>
            SendAsync(gameDir, HttpMethod.Post, "api/command", JsonSerializer.Serialize(new { command }));
    }
}
