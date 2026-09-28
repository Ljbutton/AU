using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TournamentTracker.Discord;

namespace TournamentTracker.Stats
{
    /// <summary>
    /// Run by the organiser's scheduled GitHub job, not by the mod. Preliminary hosts have no
    /// bot, so no single game can see the others; this reads every preliminary game file in
    /// the organiser's preliminary channels (with the organiser's bot) and keeps one
    /// leaderboard message per preliminary up to date, edited in place.
    /// </summary>
    public sealed class PrelimLeaderboards
    {
        public const string TitleSuffix = " — Preliminary leaderboard";
        private readonly DiscordRest _rest;
        private readonly string _token;
        private readonly ILog _log;

        public PrelimLeaderboards(DiscordRest rest, string botToken, ILog log)
        {
            _rest = rest;
            _token = botToken;
            _log = log;
        }

        /// <summary>Updates the leaderboards for each channel. Returns how many messages were posted or edited.</summary>
        /// <param name="postTo">Where leaderboards go; empty: into the same preliminary channel.</param>
        public async Task<int> UpdateAsync(IEnumerable<string> channels, string? postTo = null, int size = 15)
        {
            int changed = 0;
            var reader = new SharedResults(_rest, _token, "", _log);
            foreach (var channel in channels.Where(c => c.Trim().Length > 0).Select(c => c.Trim()))
            {
                var data = await reader.ReadAsync(channel).ConfigureAwait(false);
                if (data == null) continue;
                if (data.MissingContentIntent)
                    _log.Warn($"Channel {channel}: some messages came back empty. Turn on the bot's Message Content Intent.");
                var games = data.Games.Where(g => g.Mode == nameof(TrackerMode.Preliminary)).ToList();
                var voids = Standings.ApplyVoids(games, data.Voids);
                foreach (var file in SharedResults.UnsettledFiles(voids))
                {
                    var game = games.First(g => g.Id == file.Key);
                    await reader.SettleVoidAsync(channel, game, file.Value, voids.Where(v => v.Game == game && !v.Settled)).ConfigureAwait(false);
                }
                Standings.Apply(games.Where(g => g.Counted).ToList(), data.Adjustments);
                string target = string.IsNullOrWhiteSpace(postTo) ? channel : postTo!.Trim();
                var existing = await _rest.GetMessagesAsync(_token, target).ConfigureAwait(false) ?? new List<ChannelMessage>();

                foreach (var prelim in games.GroupBy(g => g.TournamentId.Length > 0 ? g.TournamentId : g.Tournament))
                {
                    string name = prelim.OrderByDescending(g => g.StartedUtc).First().Tournament;
                    var message = Board(name, prelim.ToList(), size);
                    var embed = message.Embeds![0];
                    var mine = existing.FirstOrDefault(m => m.AuthorIsBot && m.EmbedTitle == embed.Title);
                    if (mine != null && mine.EmbedDescription == embed.Description && mine.EmbedFooter == embed.Footer?.Text) continue;
                    var result = mine == null
                        ? await _rest.PostEmbedsAsync(_token, target, message).ConfigureAwait(false)
                        : await _rest.EditEmbedsAsync(_token, target, mine.Id, message).ConfigureAwait(false);
                    if (result.Ok) changed++;
                    else _log.Error($"Could not update the {name} leaderboard: {result}");
                }
            }
            return changed;
        }

        public static WebhookMessage Board(string name, IReadOnlyList<GameRecord> games, int size)
        {
            var store = Standings.Build(games, name);
            var message = ReportFormatter.Leaderboard(store, size);
            var embed = message.Embeds![0];
            embed.Title = ReportFormatter.Clip(name + TitleSuffix, Embed.TitleLimit);
            var servers = games.Select(g => g.Server).Where(s => s.Length > 0).Distinct().OrderBy(s => s).ToList();
            int lobbies = games.Select(g => g.Host).Distinct().Count();
            embed.Footer = new EmbedFooter
            {
                Text = $"{store.GamesRecorded} game{(store.GamesRecorded == 1 ? "" : "s")} · {lobbies} lobb{(lobbies == 1 ? "y" : "ies")}"
                     + (servers.Count > 0 ? " · " + string.Join(", ", servers) : "") + " · updates every few minutes",
            };
            return message;
        }
    }
}
