using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TournamentTracker.Broadcast;
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
            HttpClient? http = null, Func<DateTime>? clock = null, IVoiceApi? voiceApi = null, IVoicePresence? presence = null,
            Setup.SetupCode? setup = null)
        {
            // A setup code overrides the settings file; applied on a copy so the caller's object stays as it was.
            if (setup != null)
            {
                settings = JsonSerializer.Deserialize<TrackerSettings>(JsonSerializer.Serialize(settings))!;
                setup.ApplyTo(settings);
                // The host's own bots and channels, set in The Button, on top of the code.
                TournamentTracker.Setup.HostDiscord.Load(dataDir)?.ApplyTo(settings);
                Setup = setup;
            }
            _settings = settings;
            _log = log;
            Work = new BackgroundWork(log);
            _clock = clock ?? (() => DateTime.UtcNow);
            _dataDir = dataDir;
            string slug = settings.EffectiveTournamentId;
            _statsPath = Path.Combine(dataDir, $"stats-{slug}.json");
            _gamesDir = Path.Combine(dataDir, "games", slug);

            Store = StatsStore.Load(_statsPath, settings.TournamentName);
            LoadState();
            if (_overlayOn) StartOverlay();
            Links = LinkRegistry.Load(Path.Combine(dataDir, "links.json"), log);
            Tracker = new GameTracker(settings.Scoring);

            // In the game, Discord only goes through The Button (none when it isn't open).
            _viaButton = http == null;
            _rest = new DiscordRest(http ?? ButtonBridge.CreateClient(dataDir), log);

            var mute = settings.AutoMute;
            if (mute.IsConfigured)
            {
                _dispatcher = new MuteDispatcher(voiceApi ?? new DiscordVoiceApi(_rest, mute.GuildId), mute.BotTokens, log);
                AutoMute = new AutoMuteController(mute, Links, _dispatcher, _clock);
                // Switched off by the host before: stays off (the bot never touches voice).
                if (_autoMuteOff) AutoMute.Enabled = false;
                _log.Info($"Automute ready with {mute.BotTokens.Count} bot token(s)");

                // Spectator muting and auto-link need to see who is in voice; that only
                // comes over the gateway, which uses the first bot token.
                Presence = presence;
                bool listen = settings.ResultsChannelId.Length > 0 && settings.Mode == TrackerMode.Tournament;
                // The gateway also carries /link and /unlink, so it always runs. The Button keeps the
                // connection; the mod asks it what happened.
                if (Presence == null)
                {
                    _gateway = new ButtonGateway(dataDir, mute.BotTokens[0], mute.GuildId, listen, log);
                    _gateway.ButtonChanged += open => _mainThread.Enqueue(() => ButtonChanged(open));
                    _gateway.Start();
                    Presence = _gateway;
                }
                var state = presence as VoicePresenceState ?? _gateway?.State;
                if (state != null)
                {
                    state.MessageCreated += OnChannelMessage;
                    state.InteractionCreated += OnInteraction;
                    state.Ready += OnGatewayReady;
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
            StartControl();
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
        private readonly ButtonGateway? _gateway;

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
            players = WithoutReferee(players);
            var game = Tracker.Start(Store.NextGameNumber(label), _settings.TournamentName, lobbyCode, map, players, _clock());
            game.Host = label;
            game.TournamentId = _settings.EffectiveTournamentId;
            game.Mode = _settings.Mode.ToString();
            game.Server = _settings.ServerName;
            game.Round = Round;
            game.Id = $"{(label.Length > 0 ? FileSafe(label) + "-" : "")}{game.GameNumber}-{game.StartedUtc:yyyyMMdd-HHmmss}";
            _log.Info($"Tracking game {game.Name} on {map} with {players.Count} players");
            StartReplay(game);
            FeedGameStarted(game);
            WarnIfNoButton();
            if (_settings.Mode == TrackerMode.Tournament && Round == 0)
                Reply($"No round set, so this game counts as round 0. {Cap(HowTo("r1", "Next round"))} before the next game.", false);
            else if (_settings.Mode == TrackerMode.Tournament && _settings.GamesPerRound > 0 && GamesThisRound >= _settings.GamesPerRound)
                Reply($"This lobby has already played {GamesThisRound} of {_settings.GamesPerRound} games in round {Round}, so this one is extra. " +
                      $"If it shouldn't count, {HowTo("void", "Void")}. If a new round has started, {HowTo($"r{Round + 1}", "Next round")} before the next game.", false);
        }

        /// <summary>A kill. <paramref name="at"/>: where it happened (for the caster's feed), when the plugin can tell.</summary>
        public void Kill(byte killerId, byte victimId, FeedPlace? at = null)
        {
            bool before = Tracker.Current?.ById(victimId)?.DeathCause == null;
            Tracker.Kill(killerId, victimId, _clock());
            if (before) FeedKill(killerId, victimId, at);
        }

        public void MeetingCalled(byte? callerId, byte? bodyId)
        {
            int before = Tracker.Current?.Meetings.Count ?? 0;
            Tracker.MeetingCalled(callerId, bodyId, _clock());
            Active(callerId);
            FeedMeeting(before, callerId, bodyId);
        }

        public void VotingComplete(IReadOnlyList<VoteCast> votes, byte? exiledId, bool tie)
        {
            Tracker.VotingComplete(votes, exiledId, tie, _clock());
            if (Tracker.InGame) FeedVote(0);
        }

        public void MeetingClosed()
        {
            Tracker.MeetingClosed();
            if (Tracker.InGame) FeedMeetingClosed();
        }

        public void TaskCompleted(byte playerId)
        {
            Tracker.TaskCompleted(playerId, _clock());
            Active(playerId);
            if (Tracker.InGame) FeedTask();
        }

        public void Sabotage(byte playerId, string system)
        {
            Tracker.Sabotage(playerId, system, _clock());
            FeedSabotageBy(playerId, system);
        }
        /// <summary>A player left mid-game: recorded, and the host is told what their options are.</summary>
        public void PlayerLeft(byte playerId)
        {
            var game = Tracker.Current;
            var p = game?.ById(playerId);
            if (game == null || p == null || p.DeathCause == "Disconnected") return;
            Tracker.Disconnected(playerId, _clock());

            string at = TimeSpan.FromSeconds(p.DiedAtSeconds ?? 0).ToString(@"m\:ss");
            if (game.Meetings.Count == 0)
            {
                Reply($"{p.Name} left at {at}, before the first meeting. To restart, {HowTo($"void {p.Name} left", "Void")}, then start a new game.", false);
            }
            else
            {
                Reply($"{p.Name} left at {at}. The game plays on: they keep the points they'd earned and take the loss if their team loses. (To throw the game out instead, {HowTo("void", "Void")}.)", false);
            }
            if (Shared != null && _settings.Mode == TrackerMode.Tournament)
            {
                string note = $"Disconnect · {game.Name}: {p.Name} ({(p.IsImpostor ? "impostor" : "crewmate")}) left at {at}{(game.Meetings.Count == 0 ? ", before the first meeting" : "")}.";
                Chain(() => Shared.PostNoteAsync(note));
            }
        }

        /// <summary>The game ended normally. <paramref name="reason"/> is the GameOverReason name.</summary>
        public GameRecord? GameEnded(string reason, IReadOnlyList<PlayerSnapshot> players) =>
            Finish(reason, Outcome.WinnerFromReason(reason), players);

        /// <summary>The game stopped without a result (host left, lobby closed). Saved but not counted.</summary>
        public GameRecord? GameAbandoned(IReadOnlyList<PlayerSnapshot> players) => Finish("Abandoned", null, players);

        private GameRecord? Finish(string reason, string? winner, IReadOnlyList<PlayerSnapshot> players)
        {
            players = WithoutReferee(players);
            var game = Tracker.End(reason, winner, players, _clock());
            if (game == null) return null;
            FeedGameEnded(game, reason);
            LastGame = game;
            foreach (var p in game.Players)
            {
                string? linked = Links.Find(p.Key)?.DiscordUserId;
                p.DiscordId = string.IsNullOrEmpty(linked) ? null : linked;
            }
            bool roundDone = false;
            if (game.Counted)
            {
                CountRoundGame(game, 1);
                CountImpostorGames(game, 1);
                roundDone = _settings.Mode == TrackerMode.Tournament && _settings.GamesPerRound > 0 && GamesThisRound == _settings.GamesPerRound && game.Round == Round;
                if (roundDone)
                    Reply($"That was game {GamesThisRound} of {_settings.GamesPerRound}: round {Round} is done for this lobby." + (Shared != null ? " The round's scores are posted in the results channel." : ""), false);
            }

            Store.Apply(game);
            Store.NoteFinished(game);
            TrySave(() => Store.Save(_statsPath), "stats");
            TrySave(() => SaveGameFile(game), "game record");
            FinishReplay(game);

            _log.Info($"Game {game.Name} over: {game.Winner ?? "no result"} ({reason})");
            if (_settings.Mode == TrackerMode.Preliminary)
                foreach (var line in StandingsFormatter.ChatSummary(game)) Reply(line, false);
            // Nothing goes to Discord until the host has checked the game in The Button (Referee).
            if (PostsResults && _settings.VerifyResults) Hold(game, roundDone);
            else PostResults(game, roundDone);
            BumpLive();
            ApplyPendingRound();
            return game;
        }

        /// <summary>A game's results to Discord: its report, then the standings (and the round's summary when it closed the round).</summary>
        private void PostResults(GameRecord game, bool roundDone)
        {
            var report = ReportFormatter.GameReport(game);
            if (_settings.Mode == TrackerMode.Preliminary)
            {
                // The data file rides along with the report so the organiser's job can build standings.
                string url = _settings.StatsWebhookUrl;
                if (!string.IsNullOrWhiteSpace(url))
                    Chain(async () =>
                    {
                        var result = await _rest.ExecuteWebhookWithFileAsync(url, report, SharedResults.FileNameFor(game), SharedResults.FileFor(game)).ConfigureAwait(false);
                        if (!result.Ok) _log.Error("Discord webhook post failed: " + result);
                    });
                UpdatePrelimCount();
                PostPublic(report, game.Counted);
            }
            else
            {
                Post(_settings.StatsWebhookUrl, report);
            }

            if (Shared != null)
            {
                if (game.Counted || game.Voided)
                {
                    int round = game.Round;
                    Chain(async () =>
                    {
                        await Shared.PublishAsync(game).ConfigureAwait(false);
                        if (_settings.Mode == TrackerMode.Tournament) await PostLobbyStandingsAsync().ConfigureAwait(false);
                        else if (_settings.PostLeaderboardAfterEachGame) await PostCombinedAsync().ConfigureAwait(false);
                        if (roundDone) await PostRoundSummaryAsync(round).ConfigureAwait(false);
                    });
                }
            }
            else if (game.Counted && _settings.PostLeaderboardAfterEachGame)
            {
                Post(_settings.StatsWebhookUrl, LeaderboardMessage());
            }
            // Move the live status below the report so it stays at the bottom of the channel.
            RepostStatus();
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
            var load = await Shared.LoadAsync(_settings.EffectiveTournamentId, _settings.TournamentName).ConfigureAwait(false);
            if (load == null) return;
            Combined = load;
            await SettleVoidsAsync(load).ConfigureAwait(false);
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

        /// <summary>Discord goes through The Button (false in tests, which talk to a pretend Discord).</summary>
        private readonly bool _viaButton;
        private bool _buttonClosedOnce;

        /// <summary>This lobby uses Discord at all: a bot, a results channel or a webhook.</summary>
        private bool UsesDiscord => AutoMute != null || Shared != null || !string.IsNullOrWhiteSpace(_settings.StatsWebhookUrl) || !string.IsNullOrWhiteSpace(_settings.StatusWebhookUrl);

        /// <summary>The Button is open, so Discord works (always true when the mod talks to Discord itself).</summary>
        public bool ButtonOpen => !_viaButton || (_gateway?.ButtonOpen ?? ButtonBridge.Read(_dataDir) != null);

        /// <summary>The Button opened or closed while the game runs (seen by the bot's gateway).</summary>
        private void ButtonChanged(bool open)
        {
            if (!open)
            {
                _buttonClosedOnce = true;
                Reply("The Button closed, so Discord is off: automute stops and results won't post until it's open again.", false);
            }
            else if (_buttonClosedOnce)
            {
                Reply("The Button is open again: Discord is back on.", false);
                _dispatcher?.ResendAll();
            }
            RefreshStatus();
        }

        /// <summary>A game starting without The Button, in a lobby that posts to Discord: the host should know.</summary>
        private void WarnIfNoButton()
        {
            if (UsesDiscord && !ButtonOpen)
                Reply("The Button isn't open, so this game won't post to Discord" + (AutoMute != null ? " and automute is off" : "") + ". Open The Button to turn Discord on.", false);
        }

        private void Reply(string text, bool isPublic) => Reply(text, isPublic, false);

        /// <summary>How the host does something, for messages: the button in The Button ("press Void in The Button").</summary>
        private static string HowTo(string command, string button) => $"press {button} in The Button";

        private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>
        /// <paramref name="lobbyChat"/>: goes to everyone's chat even with PublicChat off. Only in
        /// the lobby, where the host talking in chat is normal (never while the host is playing).
        /// </summary>
        private void Reply(string text, bool isPublic, bool lobbyChat)
        {
            Log(text);
            // Something the game said on its own (not an answer to a button): Home shows it.
            if (!_answeringApp) AddNotice(text);
            _outbox.Add(new ChatReply(text, isPublic && (_settings.PublicChat || (lobbyChat && _phase == VoicePhase.Lobby))));
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
        /// <summary>JSON work kept off the game's main thread (inline until <see cref="BackgroundWork.Start"/>).</summary>
        public BackgroundWork Work { get; }

        public async Task ShutdownAsync(TimeSpan timeout)
        {
            Work.Flush(timeout);
            AutoMute?.ReleaseAll();
            if (_dispatcher != null) await _dispatcher.WaitIdleAsync(timeout).ConfigureAwait(false);
            CloseStatus();
            await Task.WhenAny(PendingPosts, Task.Delay(timeout)).ConfigureAwait(false);
        }

        public void Dispose()
        {
            Work.Dispose();
            _dispatcher?.Dispose();
            _gateway?.Dispose();
            _overlay?.Dispose();
            _control?.Dispose();
            Cam.Dispose();
        }
    }
}
