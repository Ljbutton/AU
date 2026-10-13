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

        /// <summary>
        /// The results channel as the lobby standings just read it, and when. The next step of the
        /// same job (the round summary after a game, the round standings after !standings) uses it
        /// instead of reading the whole channel again a second later. Only the post chain touches these.
        /// </summary>
        private SharedLoad? _freshLoad;
        private DateTime _freshLoadAt;
        private static readonly TimeSpan FreshLoadFor = TimeSpan.FromSeconds(10);

        /// <summary>Reads the results channel, recording the load as fresh (only if the read worked).</summary>
        private async Task RefreshCombinedFreshAsync()
        {
            var before = Combined;
            await RefreshCombinedAsync().ConfigureAwait(false);
            _freshLoad = ReferenceEquals(Combined, before) ? null : Combined;
            _freshLoadAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Reads the results channel unless the lobby standings read it moments ago and nothing has
        /// read it since (any other read could have been after a new post, e.g. a void).
        /// </summary>
        private Task RefreshCombinedUnlessFreshAsync()
        {
            bool fresh = _freshLoad != null && ReferenceEquals(_freshLoad, Combined) && DateTime.UtcNow - _freshLoadAt < FreshLoadFor;
            _freshLoad = null;              // used once
            return fresh ? Task.CompletedTask : RefreshCombinedAsync();
        }

        /// <summary>This lobby's standings for the current round, with the cut line.</summary>
        private async Task PostLobbyStandingsAsync()
        {
            await RefreshCombinedFreshAsync().ConfigureAwait(false);
            if (Combined == null) return;
            await AcknowledgeAdjustmentsAsync(Combined).ConfigureAwait(false);
            string lobby = LobbyLabel();
            var rows = Stats.Standings.Lobby(Combined.GameRecords, lobby, Round, _settings.AdvanceCount, _settings.GamesPerRound);
            int played = Stats.Standings.GamesPlayed(Combined.GameRecords, lobby, Round);
            await PostNowAsync(_settings.StatsWebhookUrl, StandingsFormatter.Lobby(_settings.TournamentName, lobby.Length > 0 ? lobby : "This lobby", Round, rows,
                _settings.AdvanceCount, played, _settings.GamesPerRound)).ConfigureAwait(false);
            await NoteTiebreaksAsync(Combined).ConfigureAwait(false);
        }

        /// <summary>
        /// The lobby played its last game of the round: post every player's score and where it
        /// came from in the private results channel (staff only, so it needs the bot).
        /// </summary>
        private async Task PostRoundSummaryAsync(int round)
        {
            if (Shared == null) return;
            // After a game this follows the lobby standings, which read the channel a moment ago.
            await RefreshCombinedUnlessFreshAsync().ConfigureAwait(false);
            IReadOnlyList<GameRecord> games = Combined?.GameRecords ?? (IReadOnlyList<GameRecord>)LocalGames();
            string lobby = LobbyLabel();
            var rows = Stats.Standings.Lobby(games, lobby, round, _settings.AdvanceCount, _settings.GamesPerRound);
            if (rows.Count == 0) return;
            var roundGames = games.Where(g => g.Round == round && string.Equals(g.Host, lobby, StringComparison.OrdinalIgnoreCase)).ToList();
            var message = StandingsFormatter.RoundSummary(_settings.TournamentName, lobby.Length > 0 ? lobby + "'s lobby" : "This lobby", round, rows, roundGames);
            var result = await _rest.PostEmbedsAsync(Shared.Token, Shared.ChannelId, message).ConfigureAwait(false);
            if (!result.Ok) _log.Warn("Couldn't post the round summary: " + result);
        }

        /// <summary>Everyone in the current round across every lobby.</summary>
        private async Task PostRoundStandingsAsync()
        {
            await RefreshCombinedUnlessFreshAsync().ConfigureAwait(false);
            if (Combined == null) return;
            var rows = Stats.Standings.Round(Combined.GameRecords, Round, _settings.AdvanceCount, _settings.GamesPerRound);
            var progress = Combined.GameRecords.Where(g => g.Round == Round && g.Counted)
                .GroupBy(g => g.Host, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Count()))
                .ToList();
            await PostNowAsync(_settings.StatsWebhookUrl, StandingsFormatter.Round(_settings.TournamentName, Round, rows, progress, _settings.GamesPerRound)).ConfigureAwait(false);
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
            await PostNowAsync(_settings.StatsWebhookUrl, StandingsFormatter.Servers(_settings.TournamentName, servers)).ConfigureAwait(false);
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

        private readonly HashSet<string> _notedTiebreaks = new HashSet<string>();

        /// <summary>Tells the referees, in the private results channel, about cut-line ties in this lobby's finished rounds.</summary>
        private async Task NoteTiebreaksAsync(SharedLoad load)
        {
            if (Shared == null) return;
            string lobby = LobbyLabel();
            foreach (var tie in Stats.Standings.Tiebreaks(load.GameRecords, _settings.AdvanceCount, _settings.GamesPerRound))
            {
                if (!string.Equals(tie.Host, lobby, StringComparison.OrdinalIgnoreCase)) continue;
                string text = "Tiebreak · " + tie.Describe();
                if (!_notedTiebreaks.Add(text)) continue;
                if (load.Notes.Contains(text)) continue;   // already posted before a restart
                await Shared.PostNoteAsync(text).ConfigureAwait(false);
            }
        }
    }
}
