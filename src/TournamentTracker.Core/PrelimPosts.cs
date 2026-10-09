using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TournamentTracker.Discord;
using TournamentTracker.Stats;

namespace TournamentTracker
{
    /// <summary>
    /// Preliminaries post to two places: the organiser's private reports channel (every report
    /// with its data file, and this lobby's count of impostor against crew points, kept at the
    /// bottom) and, when the host set one in The Button, their own server's public channel
    /// (the report and this lobby's standings, no data file and no count).
    /// </summary>
    public sealed partial class TournamentSession
    {
        private string PrelimCountPath => Path.Combine(_gamesDir, "prelim-count.json");

        private sealed class CountMessage
        {
            public string Id { get; set; } = "";
            public string Webhook { get; set; } = "";
        }

        /// <summary>This lobby's report (and standings) in the host's public channel.</summary>
        /// <summary>The public channel gets the game's results and timeline, and nothing else (no standings, no data).</summary>
        private void PostPublic(WebhookMessage message)
        {
            string url = _settings.PublicWebhookUrl;
            if (string.IsNullOrWhiteSpace(url)) return;
            Post(url, message);
        }

        /// <summary>Replaces this lobby's count in the private channel with a fresh one at the bottom.</summary>
        private void UpdatePrelimCount()
        {
            string url = _settings.StatsWebhookUrl;
            if (_settings.Mode != TrackerMode.Preliminary || string.IsNullOrWhiteSpace(url)) return;
            Chain(async () =>
            {
                var games = LocalGames().Where(g => g.Mode == nameof(TrackerMode.Preliminary)).ToList();
                string lobby = games.Count > 0 ? PrelimCount.LobbyOf(games.OrderByDescending(g => g.StartedUtc).First())
                    : (_settings.ServerName.Length > 0 ? _settings.ServerName + " · " : "") + LobbyLabel();
                var message = PrelimCount.LobbyMessage(_settings.TournamentName, PrelimCount.Count(games, lobby));

                CountMessage? old = null;
                try { if (File.Exists(PrelimCountPath)) old = JsonSerializer.Deserialize<CountMessage>(File.ReadAllText(PrelimCountPath)); }
                catch (Exception) { }
                if (old != null && old.Id.Length > 0 && old.Webhook == url)
                    await _rest.DeleteWebhookMessageAsync(url, old.Id).ConfigureAwait(false);

                var result = await _rest.ExecuteWebhookAsync(url, message).ConfigureAwait(false);
                if (!result.Ok) { _log.Error("Couldn't post the lobby's count: " + result); return; }
                string id = "";
                try { if (JsonDocument.Parse(result.Body).RootElement.TryGetProperty("id", out var v)) id = v.GetString() ?? ""; }
                catch (Exception) { }
                TrySave(() =>
                {
                    Directory.CreateDirectory(_gamesDir);
                    File.WriteAllText(PrelimCountPath, JsonSerializer.Serialize(new CountMessage { Id = id, Webhook = url }));
                }, "count message");
            });
        }
    }
}
