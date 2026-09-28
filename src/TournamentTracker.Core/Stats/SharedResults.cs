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

        /// <summary>This tournament's games since the last reset, with referee adjustments applied.</summary>
        public List<GameRecord> GameRecords { get; set; } = new List<GameRecord>();
        public List<AdjustmentResult> Adjustments { get; set; } = new List<AdjustmentResult>();
        public List<VoidResult> Voids { get; set; } = new List<VoidResult>();

        /// <summary>What the bots have written in the channel (e.g. tiebreak notes), so nothing is posted twice.</summary>
        public HashSet<string> Notes { get; set; } = new HashSet<string>();

        /// <summary>Games a !void or !unvoid changed that haven't been reposted yet: game ID to the file to post.</summary>
        public Dictionary<string, byte[]> Unsettled { get; set; } = new Dictionary<string, byte[]>();
    }

    /// <summary>Everything read from one channel (back to the newest reset message).</summary>
    public sealed class ChannelData
    {
        /// <summary>Fresh copies, newest first; a game posted twice appears once.</summary>
        public List<GameRecord> Games { get; set; } = new List<GameRecord>();
        public List<RefereeAdjustment> Adjustments { get; set; } = new List<RefereeAdjustment>();
        public List<RefereeVoid> Voids { get; set; } = new List<RefereeVoid>();
        public HashSet<string> Notes { get; set; } = new HashSet<string>();
        public bool SinceReset { get; set; }
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
        // Downloaded files by message/file, kept as JSON so every read gets fresh copies to adjust.
        private readonly ConcurrentDictionary<string, string?> _downloaded = new ConcurrentDictionary<string, string?>();

        public SharedResults(DiscordRest rest, string botToken, string channelId, ILog log)
        {
            _rest = rest;
            _token = botToken;
            _channelId = channelId;
            _log = log;
        }

        /// <summary>Matches by tournament ID; games recorded before IDs existed match by name.</summary>
        public static bool SameTournament(GameRecord game, string tournamentId, string tournamentName) =>
            game.TournamentId.Length > 0
                ? string.Equals(game.TournamentId, tournamentId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(game.Tournament, tournamentName, StringComparison.OrdinalIgnoreCase);

        public string ChannelId => _channelId;

        /// <summary>A plain message from the bot, e.g. a note for the referees.</summary>
        public async Task<bool> PostNoteAsync(string text)
        {
            var result = await _rest.PostMessageAsync(_token, _channelId, text).ConfigureAwait(false);
            if (!result.Ok) _log.Error("Could not post to the results channel: " + result);
            return result.Ok;
        }

        /// <summary>Marks a referee's message so they can see it was picked up (✅) or couldn't be matched (❓).</summary>
        public Task ReactAsync(string messageId, string emoji) => _rest.AddReactionAsync(_token, _channelId, messageId, emoji);

        public static bool IsReset(string content)
        {
            string first = content.Trim().Split('\n')[0].Trim();
            return string.Equals(first, ResetCommand, StringComparison.OrdinalIgnoreCase);
        }

        public async Task<bool> PublishAsync(GameRecord game)
        {
            string summary = $"Game {game.Name}{(game.Round > 0 ? $" · round {game.Round}" : "")} · {game.Winner ?? "no result"} · {game.Map}"
                + (game.Voided ? $" · VOID{(game.VoidReason.Length > 0 ? ": " + game.VoidReason : "")}" : "");
            var result = await _rest.PostFileAsync(_token, _channelId, summary, FileNameFor(game), FileFor(game)).ConfigureAwait(false);
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

        /// <summary>Every game of the tournament since the last reset, from every host, with referee adjustments applied.</summary>
        public async Task<SharedLoad?> LoadAsync(string tournamentId, string tournament)
        {
            var data = await ReadAsync(_channelId).ConfigureAwait(false);
            if (data == null) return null;
            var games = data.Games.Where(g => SameTournament(g, tournamentId, tournament)).ToList();
            var load = new SharedLoad
            {
                SinceReset = data.SinceReset,
                MissingContentIntent = data.MissingContentIntent,
                GameRecords = games,
                Notes = data.Notes,
                Voids = Standings.ApplyVoids(games, data.Voids),
            };
            load.Unsettled = UnsettledFiles(load.Voids);
            load.Adjustments = Standings.Apply(games.Where(g => g.Counted).ToList(), data.Adjustments);
            load.Store = Standings.Build(games, tournament);
            load.Games = load.Store.GamesRecorded;
            load.Hosts = games.Select(g => g.Host).Distinct().Count();
            if (load.MissingContentIntent)
                _log.Warn("Some results-channel messages came back empty. Turn on the Message Content Intent for the bot, or the standings will miss games.");
            return load;
        }

        /// <summary>Reads a channel: game files, referee adjustments, and where the last reset was.</summary>
        public async Task<ChannelData?> ReadAsync(string channelId)
        {
            var data = new ChannelData();
            var files = new List<(string Key, string Url)>();
            string? before = null;
            for (int page = 0; page < MaxPages; page++)
            {
                var messages = await _rest.GetMessagesAsync(_token, channelId, before).ConfigureAwait(false);
                if (messages == null) return null;
                foreach (var m in messages)
                {
                    if (IsReset(m.Content))
                    {
                        data.SinceReset = true;
                        goto done;
                    }
                    var adjustment = RefereeAdjustment.TryParse(m.Content, m.Id, RefereeAdjustment.TimeOfSnowflake(m.Id));
                    if (adjustment != null) data.Adjustments.Add(adjustment);
                    if (m.AuthorIsBot && m.Content.Length > 0 && m.Attachments.Count == 0) data.Notes.Add(m.Content);
                    var voiding = RefereeVoid.TryParse(m.Content, m.Id, RefereeAdjustment.TimeOfSnowflake(m.Id));
                    if (voiding != null) data.Voids.Add(voiding);
                    foreach (var (name, url) in m.Attachments)
                        if (name.StartsWith(FilePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
                            files.Add((m.Id + "/" + name, url));
                    if (m.AuthorIsBot && m.Content.Length == 0 && m.Attachments.Count == 0 && m.Embeds == 0)
                        data.MissingContentIntent = true;
                }
                if (messages.Count < 100) break;
                before = messages[messages.Count - 1].Id;
            }
            done:

            var seen = new HashSet<string>();
            foreach (var (key, url) in files)
            {
                if (!_downloaded.TryGetValue(key, out var json) || json == null)
                {
                    json = await _rest.DownloadAsync(url).ConfigureAwait(false);
                    if (json == null) continue;       // try again next time
                    _downloaded[key] = json;
                }
                GameRecord? game;
                try { game = JsonSerializer.Deserialize<GameRecord>(json, Json); }
                catch (JsonException) { game = null; }
                if (game == null) continue;
                if (seen.Add(game.Id.Length > 0 ? game.Id : key)) data.Games.Add(game);   // newest first: a resent game counts once
            }
            return data;
        }

        /// <summary>
        /// Copies to repost of the games a new !void or !unvoid changed, listing those commands
        /// as built in. Call before referee point changes are applied, so the copies are clean.
        /// </summary>
        public static Dictionary<string, byte[]> UnsettledFiles(IEnumerable<VoidResult> voids)
        {
            var files = new Dictionary<string, byte[]>();
            foreach (var byGame in voids.Where(v => v.Game != null && !v.Settled).GroupBy(v => v.Game!))
            {
                var game = byGame.Key;
                var clean = JsonSerializer.Deserialize<GameRecord>(JsonSerializer.Serialize(game, Json), Json)!;
                clean.VoidCommands.AddRange(byGame.Select(v => v.Command.MessageId));
                files[game.Id] = FileFor(clean);
            }
            return files;
        }

        /// <summary>
        /// Posts a game's new void state as its newest copy, so the !void or !unvoid message can
        /// be deleted without undoing it, then ticks the referee's messages.
        /// </summary>
        public async Task<bool> SettleVoidAsync(string channelId, GameRecord game, byte[] file, IEnumerable<VoidResult> commands)
        {
            string text = game.Voided
                ? $"Game {game.Name} is void{(game.VoidReason.Length > 0 ? ": " + game.VoidReason : "")}. It no longer counts."
                : $"Game {game.Name} counts again.";
            var result = await _rest.PostFileAsync(_token, channelId, text, FileNameFor(game), file).ConfigureAwait(false);
            if (!result.Ok)
            {
                _log.Error($"Could not repost game {game.Name}: {result}");
                return false;
            }
            foreach (var c in commands)
                await _rest.AddReactionAsync(_token, channelId, c.Command.MessageId, "✅").ConfigureAwait(false);
            return true;
        }

        public static string FileNameFor(GameRecord game) => FilePrefix + game.Id + ".json";

        public static byte[] FileFor(GameRecord game) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(game, Json));
    }
}
