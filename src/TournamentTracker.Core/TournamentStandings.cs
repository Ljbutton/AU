using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TournamentTracker.Discord;
using TournamentTracker.Stats;

namespace TournamentTracker
{
    /// <summary>Tournament mode: per-lobby round standings, all-lobby standings, server standings, referee adjustments.</summary>
    public sealed partial class TournamentSession
    {
        private readonly HashSet<string> _acknowledged = new HashSet<string>();

        /// <summary>This lobby's standings for the current round, with the cut line.</summary>
        private async Task PostLobbyStandingsAsync()
        {
            await RefreshCombinedAsync().ConfigureAwait(false);
            if (Combined == null) return;
            await AcknowledgeAdjustmentsAsync(Combined).ConfigureAwait(false);
            string lobby = LobbyLabel();
            var rows = Stats.Standings.Lobby(Combined.GameRecords, lobby, Round, _settings.AdvanceCount);
            await PostNowAsync(StandingsFormatter.Lobby(_settings.TournamentName, lobby.Length > 0 ? lobby : "This lobby", Round, rows, _settings.AdvanceCount)).ConfigureAwait(false);
        }

        /// <summary>Everyone in the current round across every lobby.</summary>
        private async Task PostRoundStandingsAsync()
        {
            await RefreshCombinedAsync().ConfigureAwait(false);
            if (Combined == null) return;
            var rows = Stats.Standings.Round(Combined.GameRecords, Round);
            int lobbies = Combined.GameRecords.Where(g => g.Round == Round).Select(g => g.Host).Distinct().Count();
            await PostNowAsync(StandingsFormatter.Round(_settings.TournamentName, Round, rows, lobbies)).ConfigureAwait(false);
        }

        /// <summary>Servers ranked by their players' total points; home servers come from the preliminary channels.</summary>
        private async Task PostServerStandingsAsync()
        {
            if (Shared == null) return;
            await RefreshCombinedAsync().ConfigureAwait(false);
            if (Combined == null) return;
            var prelims = new List<GameRecord>();
            foreach (var channel in _settings.PrelimChannelIds)
            {
                var data = await Shared.ReadAsync(channel).ConfigureAwait(false);
                if (data != null) prelims.AddRange(data.Games.Where(g => g.Mode == nameof(TrackerMode.Preliminary)));
            }
            var servers = Stats.Standings.Servers(prelims, Combined.GameRecords, Round);
            await PostNowAsync(StandingsFormatter.Servers(_settings.TournamentName, servers)).ConfigureAwait(false);
        }

        /// <summary>
        /// Reacts to referee adjustments for this lobby's games: ✅ applied, ❓ player not found.
        /// Only the lobby's own bot reacts, so each message gets one answer.
        /// </summary>
        private async Task AcknowledgeAdjustmentsAsync(SharedLoad load)
        {
            if (Shared == null) return;
            string lobby = LobbyLabel();
            foreach (var r in load.Adjustments)
            {
                if (r.Game == null || !string.Equals(r.Game.Host, lobby, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_acknowledged.Add(r.Adjustment.MessageId)) continue;
                await Shared.ReactAsync(r.Adjustment.MessageId, r.Applied ? "✅" : "❓").ConfigureAwait(false);
            }
        }

        private async Task PostNowAsync(WebhookMessage message)
        {
            if (string.IsNullOrWhiteSpace(_settings.StatsWebhookUrl)) return;
            var result = await _rest.ExecuteWebhookAsync(_settings.StatsWebhookUrl, message).ConfigureAwait(false);
            if (!result.Ok) _log.Error("Discord webhook post failed: " + result);
        }
    }
}
