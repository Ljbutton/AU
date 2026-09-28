using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TournamentTracker.Discord;

namespace TournamentTracker.Stats
{
    public sealed class SharedLoad
    {
        public StatsStore Store { get; set; } = new StatsStore();
        public int Games { get; set; }
        public int Hosts { get; set; }
        public bool SinceReset { get; set; }

        /// <summary>Other bots' messages came back empty: their files can't be read without the Message Content intent.</summary>
        public bool MissingContentIntent { get; set; }
    }

    /// <summary>
    /// A private Discord channel used as the shared record of a tournament run by several
    /// hosts. Each host's mod posts every finished game there as a JSON file; any host can
    /// read them all back to build one leaderboard. A message saying !resetleaderboard
    /// (from anyone, e.g. the organiser on their phone) starts the leaderboard over:
    /// only games posted after the newest one count. Deleting it undoes the reset.
    /// </summary>
    public sealed class SharedResults
    {
        public const string ResetCommand = "!resetleaderboard";
        private const string FilePrefix = "tt-game-";
        private const int MaxPages = 100;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions();

        private readonly DiscordRest _rest;
        private readonly string _token;
        private readonly string _channelId;
        private readonly ILog _log;
        private readonly ConcurrentDictionary<string, GameRecord?> _downloaded = new ConcurrentDictionary<string, GameRecord?>();

        public SharedResults(DiscordRest rest, string botToken, string channelId, ILog log)
        {
            _rest = rest;
            _token = botToken;
            _channelId = channelId;
            _log = log;
        }

        public static bool IsReset(string content)
        {
            string first = content.Trim().Split('\n')[0].Trim();
            return string.Equals(first, ResetCommand, StringComparison.OrdinalIgnoreCase);
        }

        public async Task<bool> PublishAsync(GameRecord game)
        {
            byte[] file = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(game, Json));
            string summary = $"Game {game.Name} · {game.Winner ?? "no result"} · {game.Map}";
            var result = await _rest.PostFileAsync(_token, _channelId, summary, FilePrefix + game.Id + ".json", file).ConfigureAwait(false);
            if (!result.Ok) _log.Error($"Could not post game {game.Name} to the results channel: {result}");
            return result.Ok;
        }

        public async Task<bool> PostResetAsync(string who)
        {
            var result = await _rest.PostMessageAsync(_token, _channelId,
                $"{ResetCommand}\n(Leaderboard reset by {who}. Delete this message to undo it.)").ConfigureAwait(false);
            if (!result.Ok) _log.Error("Could not post the reset to the results channel: " + result);
            return result.Ok;
        }

        /// <summary>Every game of <paramref name="tournament"/> since the last reset, from every host.</summary>
        public async Task<SharedLoad?> LoadAsync(string tournament)
        {
            var files = new List<(string MessageId, string Url)>();
            var load = new SharedLoad { Store = new StatsStore { Tournament = tournament } };
            string? before = null;
            for (int page = 0; page < MaxPages; page++)
            {
                var messages = await _rest.GetMessagesAsync(_token, _channelId, before).ConfigureAwait(false);
                if (messages == null) return null;
                foreach (var m in messages)
                {
                    if (IsReset(m.Content))
                    {
                        load.SinceReset = true;
                        goto done;
                    }
                    foreach (var (name, url) in m.Attachments)
                        if (name.StartsWith(FilePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
                            files.Add((m.Id + "/" + name, url));
                    if (m.AuthorIsBot && m.Content.Length == 0 && m.Attachments.Count == 0 && m.Embeds == 0)
                        load.MissingContentIntent = true;
                }
                if (messages.Count < 100) break;
                before = messages[messages.Count - 1].Id;
            }
            done:

            var games = new Dictionary<string, GameRecord>();
            foreach (var (key, url) in files)
            {
                if (!_downloaded.TryGetValue(key, out var game))
                {
                    string? json = await _rest.DownloadAsync(url).ConfigureAwait(false);
                    if (json == null) continue;       // try again next time
                    try { game = JsonSerializer.Deserialize<GameRecord>(json, Json); }
                    catch (JsonException) { game = null; }
                    _downloaded[key] = game;
                }
                if (game == null || !string.Equals(game.Tournament, tournament, StringComparison.OrdinalIgnoreCase)) continue;
                string id = game.Id.Length > 0 ? game.Id : key;
                games.TryAdd(id, game);               // newest first: a resent game counts once
            }

            foreach (var game in games.Values.OrderBy(g => g.StartedUtc))
            {
                load.Store.Apply(game);
                load.Store.NoteFinished(game);
            }
            load.Games = load.Store.GamesRecorded;
            load.Hosts = games.Values.Select(g => g.Host).Distinct().Count();
            if (load.MissingContentIntent)
                _log.Warn("Some results-channel messages came back empty. Turn on the Message Content Intent for every host's bot, or the combined leaderboard will miss their games.");
            return load;
        }
    }
}
