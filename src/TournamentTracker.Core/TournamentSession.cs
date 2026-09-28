using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TournamentTracker.Discord;
using TournamentTracker.Stats;
using TournamentTracker.Voice;

namespace TournamentTracker
{
    /// <summary>A line to show in the in-game chat. Public lines are sent to the whole lobby.</summary>
    public sealed class ChatReply
    {
        public ChatReply(string text, bool isPublic)
        {
            Text = text;
            Public = isPublic;
        }

        public string Text { get; }
        public bool Public { get; }
    }

    /// <summary>
    /// Everything the mod does, independent of the game. The plugin reports what happens
    /// in the match; this records it, keeps the tournament totals, talks to Discord and
    /// runs automute. All public methods are called from the game's main thread.
    /// </summary>
    public sealed partial class TournamentSession : IDisposable
    {
        private static readonly JsonSerializerOptions GameJson = new JsonSerializerOptions { WriteIndented = true };

        private readonly TrackerSettings _settings;
        private readonly ILog _log;
        private readonly Func<DateTime> _clock;
        private readonly string _dataDir;
        private readonly string _statsPath;
        private readonly string _gamesDir;
        private readonly DiscordRest _rest;
        private readonly MuteDispatcher? _dispatcher;
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private readonly List<ChatReply> _outbox = new List<ChatReply>();
        private readonly object _postLock = new object();
        private Task _postChain = Task.CompletedTask;

        public TournamentSession(TrackerSettings settings, string dataDir, ILog log,
            HttpClient? http = null, Func<DateTime>? clock = null, IVoiceApi? voiceApi = null, IVoicePresence? presence = null)
        {
            _settings = settings;
            _log = log;
            _clock = clock ?? (() => DateTime.UtcNow);
            _dataDir = dataDir;
            string slug = Slug(settings.TournamentName);
            _statsPath = Path.Combine(dataDir, $"stats-{slug}.json");
            _gamesDir = Path.Combine(dataDir, "games", slug);

            Store = StatsStore.Load(_statsPath, settings.TournamentName);
            Links = LinkRegistry.Load(Path.Combine(dataDir, "links.json"), log);
            Tracker = new GameTracker(settings.Scoring);
            Tracker.EventRecorded += OnTimelineEvent;

            _rest = new DiscordRest(http ?? DiscordRest.CreateHttpClient(), log);

            var mute = settings.AutoMute;
            if (mute.IsConfigured)
            {
                _dispatcher = new MuteDispatcher(voiceApi ?? new DiscordVoiceApi(_rest, mute.GuildId), mute.BotTokens, log);
                AutoMute = new AutoMuteController(mute, Links, _dispatcher, _clock);
                _log.Info($"Automute ready with {mute.BotTokens.Count} bot token(s)");

                // Spectator muting and auto-link need to see who is in voice; that only
                // comes over the gateway, which uses the first bot token.
                Presence = presence;
                if (Presence == null && (mute.MuteSpectators || mute.AutoLinkByName))
                {
                    _gateway = new VoiceGateway(mute.BotTokens[0], mute.GuildId, log);
                    _gateway.Start();
                    Presence = _gateway;
                }
            }
            else if (mute.Enabled)
            {
                _log.Warn("Automute is enabled but BotTokens or GuildId is empty; it stays off.");
            }

            string? token = settings.AutoMute.BotTokens.FirstOrDefault();
            if (settings.ResultsChannelId.Length > 0)
            {
                if (token == null)
                {
                    _log.Warn("ResultsChannelId is set but there is no bot token; the combined leaderboard is off.");
                }
                else
                {
                    Shared = new SharedResults(_rest, token, settings.ResultsChannelId, log);
                    Chain(RefreshCombinedAsync);
                }
            }

            if (string.IsNullOrWhiteSpace(settings.StatsWebhookUrl))
                _log.Warn("StatsWebhookUrl is empty: games are saved locally but not posted to Discord.");
        }

        public StatsStore Store { get; private set; }
        public LinkRegistry Links { get; }
        public GameTracker Tracker { get; }
        public AutoMuteController? AutoMute { get; }
        public IVoicePresence? Presence { get; }

        /// <summary>The shared results channel, when several hosts share one leaderboard.</summary>
        public SharedResults? Shared { get; }

        /// <summary>The latest combined leaderboard read from the results channel.</summary>
        public SharedLoad? Combined { get; private set; }

        /// <summary>The leaderboard to show: combined across hosts when available, else this host's.</summary>
        public StatsStore Standings => Combined?.Store ?? Store;
        private readonly VoiceGateway? _gateway;

        /// <summary>The most recent player list the plugin reported, used to resolve chat command targets.</summary>
        public IReadOnlyList<PlayerSnapshot> Players { get; private set; } = Array.Empty<PlayerSnapshot>();

        /// <summary>Completes when every Discord post queued so far has been sent (or failed).</summary>
        public Task PendingPosts
        {
            get { lock (_postLock) return _postChain; }
        }

        // ---- Game events ---------------------------------------------------------------

        public void GameStarted(string lobbyCode, string map, IReadOnlyList<PlayerSnapshot> players)
        {
            if (Tracker.InGame) return;
            Players = players;
            string label = LobbyLabel(players);
            var game = Tracker.Start(Store.NextGameNumber(label), _settings.TournamentName, lobbyCode, map, players, _clock());
            game.Host = label;
            game.Id = $"{(label.Length > 0 ? FileSafe(label) + "-" : "")}{game.GameNumber}-{game.StartedUtc:yyyyMMdd-HHmmss}";
            _log.Info($"Tracking game {game.Name} on {map} with {players.Count} players");
        }

        public void Kill(byte killerId, byte victimId) => Tracker.Kill(killerId, victimId, _clock());
        public void MeetingCalled(byte? callerId, byte? bodyId) => Tracker.MeetingCalled(callerId, bodyId, _clock());
        public void VotingComplete(IReadOnlyList<VoteCast> votes, byte? exiledId, bool tie) => Tracker.VotingComplete(votes, exiledId, tie, _clock());
        public void MeetingClosed() => Tracker.MeetingClosed();
        public void TaskCompleted(byte playerId) => Tracker.TaskCompleted(playerId, _clock());
        public void Sabotage(byte playerId, string system) => Tracker.Sabotage(playerId, system, _clock());
        public void PlayerLeft(byte playerId) => Tracker.Disconnected(playerId, _clock());

        /// <summary>The game ended normally. <paramref name="reason"/> is the GameOverReason name.</summary>
        public GameRecord? GameEnded(string reason, IReadOnlyList<PlayerSnapshot> players) =>
            Finish(reason, Outcome.WinnerFromReason(reason), players);

        /// <summary>The game stopped without a result (host left, lobby closed). Saved but not counted.</summary>
        public GameRecord? GameAbandoned(IReadOnlyList<PlayerSnapshot> players) => Finish("Abandoned", null, players);

        private GameRecord? Finish(string reason, string? winner, IReadOnlyList<PlayerSnapshot> players)
        {
            var game = Tracker.End(reason, winner, players, _clock());
            if (game == null) return null;

            Store.Apply(game);
            Store.NoteFinished(game);
            TrySave(() => Store.Save(_statsPath), "stats");
            TrySave(() =>
            {
                Directory.CreateDirectory(_gamesDir);
                string file = $"game-{game.Id}{(game.Counted ? "" : "-abandoned")}.json";
                File.WriteAllText(Path.Combine(_gamesDir, file), JsonSerializer.Serialize(game, GameJson));
            }, "game record");

            _log.Info($"Game {game.Name} over: {game.Winner ?? "no result"} ({reason})");
            Post(_settings.StatsWebhookUrl, ReportFormatter.GameReport(game));
            if (Shared != null)
            {
                if (game.Counted)
                {
                    Chain(async () =>
                    {
                        await Shared.PublishAsync(game).ConfigureAwait(false);
                        if (_settings.PostLeaderboardAfterEachGame) await PostCombinedAsync().ConfigureAwait(false);
                    });
                }
            }
            else if (game.Counted && _settings.PostLeaderboardAfterEachGame)
            {
                Post(_settings.StatsWebhookUrl, LeaderboardMessage());
            }
            // Move the live status below the report so it stays at the bottom of the channel.
            RepostStatus();
            return game;
        }

        private WebhookMessage LeaderboardMessage(StatsStore? store = null, string? note = null)
        {
            var message = ReportFormatter.Leaderboard(store ?? Store, _settings.LeaderboardSize,
                _settings.LeaderboardMinGames, _settings.LeaderboardMentions ? key => Links.Find(key)?.DiscordUserId : null);
            if (note != null && message.Embeds?.FirstOrDefault()?.Footer is EmbedFooter footer) footer.Text = note + " · " + footer.Text;
            return message;
        }

        private async Task RefreshCombinedAsync()
        {
            if (Shared == null) return;
            var load = await Shared.LoadAsync(_settings.TournamentName).ConfigureAwait(false);
            if (load != null) Combined = load;
        }

        /// <summary>Reads every host's games from the results channel and posts the combined leaderboard.</summary>
        private async Task PostCombinedAsync()
        {
            await RefreshCombinedAsync().ConfigureAwait(false);
            if (Combined == null || string.IsNullOrWhiteSpace(_settings.StatsWebhookUrl)) return;
            string note = $"All lobbies ({Combined.Hosts} host{(Combined.Hosts == 1 ? "" : "s")})" + (Combined.SinceReset ? " since the last reset" : "");
            var result = await _rest.ExecuteWebhookAsync(_settings.StatsWebhookUrl, LeaderboardMessage(Combined.Store, note)).ConfigureAwait(false);
            if (!result.Ok) _log.Error("Discord webhook post failed: " + result);
        }

        // ---- Voice ---------------------------------------------------------------------

        /// <summary>Emergency stop: gives everyone their voice back and turns automute off.</summary>
        public void UnmuteEveryone()
        {
            if (AutoMute == null) return;
            AutoMute.Enabled = false;
        }

        // ---- Main-thread plumbing --------------------------------------------------------

        /// <summary>Runs work finished in the background and returns chat lines to show. Call every frame.</summary>
        public IReadOnlyList<ChatReply> Pump()
        {
            while (_mainThread.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { _log.Error("Deferred action failed: " + e); }
            }
            if (_outbox.Count == 0) return Array.Empty<ChatReply>();
            var replies = _outbox.ToList();
            _outbox.Clear();
            return replies;
        }

        private void Reply(string text, bool isPublic) => _outbox.Add(new ChatReply(text, isPublic));

        private void OnTimelineEvent(TimelineEvent e)
        {
            if (string.IsNullOrWhiteSpace(_settings.LiveFeedWebhookUrl) || Tracker.Current == null) return;
            Post(_settings.LiveFeedWebhookUrl, ReportFormatter.LiveEvent(Tracker.Current, e));
        }

        /// <summary>Queues a webhook post. Posts go out one at a time, in order, off the game thread.</summary>
        private void Post(string webhookUrl, WebhookMessage message)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl)) return;
            Chain(async () =>
            {
                var result = await _rest.ExecuteWebhookAsync(webhookUrl, message).ConfigureAwait(false);
                if (!result.Ok) _log.Error("Discord webhook post failed: " + result);
            });
        }

        /// <summary>Runs Discord work in order with the posts, off the game thread.</summary>
        private void Chain(Func<Task> work)
        {
            lock (_postLock)
            {
                _postChain = _postChain.ContinueWith(async _ =>
                {
                    try { await work().ConfigureAwait(false); }
                    catch (Exception e) { _log.Error("Discord task failed: " + e.Message); }
                }, TaskScheduler.Default).Unwrap();
            }
        }

        private void TrySave(Action save, string what)
        {
            try { save(); }
            catch (Exception e) { _log.Error($"Could not save {what}: {e.Message}"); }
        }

        private string _hostLabel = "";

        /// <summary>The configured LobbyLabel, else the host's in-game name.</summary>
        public string LobbyLabel(IReadOnlyList<PlayerSnapshot>? players = null)
        {
            if (_settings.LobbyLabel.Trim().Length > 0) return Clean(_settings.LobbyLabel);
            var host = (players ?? Players).FirstOrDefault(p => p.IsHost);
            if (host != null && host.Name.Trim().Length > 0) _hostLabel = Clean(host.Name);
            return _hostLabel;

            static string Clean(string s)
            {
                s = s.Trim().Replace("`", "'");
                return s.Length > 20 ? s.Substring(0, 20) : s;
            }
        }

        /// <summary>A label safe for file names and IDs: letters, digits and dashes.</summary>
        internal static string FileSafe(string s)
        {
            var chars = s.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
            string safe = new string(chars).Trim('-');
            while (safe.Contains("--")) safe = safe.Replace("--", "-");
            return safe.Length == 0 ? "host" : safe;
        }

        private static string Slug(string name)
        {
            var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
            string slug = new string(chars).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return slug.Length == 0 ? "tournament" : slug;
        }

        /// <summary>Gives everyone their voice back and waits briefly for Discord, for when the game closes.</summary>
        public async Task ShutdownAsync(TimeSpan timeout)
        {
            AutoMute?.ReleaseAll();
            if (_dispatcher != null) await _dispatcher.WaitIdleAsync(timeout).ConfigureAwait(false);
            CloseStatus();
            await Task.WhenAny(PendingPosts, Task.Delay(timeout)).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _dispatcher?.Dispose();
            _gateway?.Dispose();
        }
    }
}
