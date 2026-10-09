using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TournamentTracker.Discord;
using TournamentTracker.Voice;

namespace TournamentTracker
{
    /// <summary>What happens every tick: automute, spectators, auto-link and the live status message.</summary>
    public sealed partial class TournamentSession
    {
        private static readonly TimeSpan StatusMinInterval = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan AutoLinkInterval = TimeSpan.FromSeconds(2);

        private VoicePhase _lastPhase = VoicePhase.Menu;
        private readonly HashSet<string> _knownDead = new HashSet<string>();
        private readonly HashSet<string> _noAutoLink = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _nextAutoLink;

        // Live status message state, guarded by _postLock.
        private string? _statusMessageId;
        private string _statusDesiredJson = "";
        private WebhookMessage? _statusDesired;
        private string _statusSentJson = "";
        /// <summary>
        /// What Discord last turned down (HTTP 400): the colour menu when the menu was the problem, else
        /// the whole message. A status that would be sent the same way again isn't sent.
        /// </summary>
        private string? _statusRejected;
        private bool _statusFlushQueued;
        private bool _statusClosed = true;
        private DateTime _statusNextSend;

        // The bot posts the live status itself when it can, so it can carry the colour menu
        // (webhook messages can't have menus). Null until the webhook's channel is looked up.
        private bool? _statusByBot;
        private string? _statusChannel;
        private bool _statusIdByBot;

        /// <summary>The channel the host picked with /new (the bot posts there); null = the status webhook's channel.</summary>
        private string? _statusChosen;

        /// <summary>The channel the current live message is in (null: the webhook's).</summary>
        private string? _statusMessageChannel;
        private readonly string _linkMenuId = "tt-link:" + Guid.NewGuid().ToString("N").Substring(0, 12);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _crewEmojis = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();

        /// <summary>The ID of this lobby's colour menu in the live status.</summary>
        public string LinkMenuId => _linkMenuId;

        private bool LinkMenuWanted => _settings.AutoMute.IsConfigured && _settings.AutoMute.LinkMenu && (_statusChosen != null || _statusByBot != false);

        /// <summary>A live message is kept: turned on in the settings with a channel for it, or a channel picked with /new.</summary>
        private bool LiveStatusOn => (_settings.LiveStatus && !string.IsNullOrWhiteSpace(StatusWebhook)) || (_statusChosen != null && _settings.AutoMute.IsConfigured);

        /// <summary>The Among Us server region the host is on (set by the plugin; "North America"…).</summary>
        public string Server { get; set; } = "";

        /// <summary>Called a few times a second with the current phase and players.</summary>
        /// <param name="publish">
        /// False: leave the live status, overlay, live data and the app's status for <see cref="PublishTick"/>
        /// (the mod does that on a later frame, so one frame doesn't do everything).
        /// </param>
        public void VoiceTick(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, string lobbyCode = "", string map = "", bool publish = true)
        {
            bool changed = phase != _phase || lobbyCode != _lobbyCode || players.Count != Players.Count;
            Players = players;
            _phase = phase;
            _lobbyCode = lobbyCode;
            _map = map;
            foreach (var p in players) Links.Touch(p.Key, p.Name);
            TrackKnownDeaths(phase, players);

            var channel = GameVoiceChannel(players);
            // Not hosting (in the menus, or a player in someone else's lobby, even with the mod): the
            // people in voice aren't this game's spectators, and their voice is the host's to set.
            var spectators = channel == null || phase == VoicePhase.Menu ? null : Spectators(channel, players);
            // The referee ghost is never muted: take them out of the players and the spectators.
            string? referee = RefSlotKey == null ? null : Links.Find(RefSlotKey)?.DiscordUserId;
            if (referee != null) spectators?.Remove(referee);
            var playing = WithoutReferee(players);

            if (phase == VoicePhase.Lobby && _settings.AutoMute.AutoLinkByName) AutoLink(players, channel);

            // Referee mode is for the lobby; a game starting ends it so meetings aren't silenced.
            if (AutoMute != null && AutoMute.RefereeMode && phase == VoicePhase.Tasks)
            {
                AutoMute.StopRefereeMode();
                Reply("Referee mode ended because the game started.", false);
            }
            AutoMute?.Update(phase, playing, spectators);
            TalkingVoiceChannel = TalkingChannel(phase, players);
            _gateway?.WantVoice(TalkingVoiceChannel);
            _tickSpectators = spectators?.Count ?? 0;
            _tickChanged |= changed;
            _lastPhase = phase;
            if (publish) PublishTick();
        }

        private int _tickSpectators;
        private bool _tickChanged;

        /// <summary>The live status, overlay, live data and the app's status, from the last <see cref="VoiceTick"/>.</summary>
        public void PublishTick()
        {
            bool changed = _tickChanged;
            _tickChanged = false;
            var phase = _phase;
            var players = Players;
            UpdateStatus(phase, players, _lobbyCode, _map, _tickSpectators);
            UpdateOverlay(phase, players, _map);
            PublishLive(phase, players, _lobbyCode, _map);
            RefreshStatus(force: changed);
        }

        /// <summary>
        /// Deaths everyone in the game already knows about: bodies are revealed at a meeting,
        /// ejections when it ends, and everything at game over. Keeps the status from spoiling kills.
        /// </summary>
        private void TrackKnownDeaths(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players)
        {
            bool reveal = phase == VoicePhase.Meeting || phase == VoicePhase.GameOver
                || (phase == VoicePhase.Tasks && _lastPhase == VoicePhase.Meeting);
            if (phase == VoicePhase.Lobby || phase == VoicePhase.Menu) _knownDead.Clear();
            else if (reveal)
                foreach (var p in players.Where(p => p.IsDead)) _knownDead.Add(p.Key);
        }

        // ---- Voice channel -------------------------------------------------------------

        /// <summary>
        /// The configured voice channel; else the one the host is in (if the host is linked);
        /// else the one most linked players are in.
        /// </summary>
        private string? GameVoiceChannel(IReadOnlyList<PlayerSnapshot> players)
        {
            if (Presence == null || !Presence.Connected) return null;
            if (_settings.AutoMute.VoiceChannelId.Length > 0) return _settings.AutoMute.VoiceChannelId;
            var host = players.FirstOrDefault(p => p.IsHost);
            var hostId = host == null ? null : Links.Find(host.Key)?.DiscordUserId;
            var hostChannel = hostId == null ? null : Presence.Members.FirstOrDefault(m => m.UserId == hostId)?.ChannelId;
            if (hostChannel != null) return hostChannel;
            var linked = new HashSet<string>(players.Select(p => Links.Find(p.Key)?.DiscordUserId).Where(id => id != null)!);
            return Presence.Members
                .Where(m => linked.Contains(m.UserId))
                .GroupBy(m => m.ChannelId)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
        }

        // ---- Who's talking ---------------------------------------------------------------------

        /// <summary>
        /// Where the bot listens for who's talking: the host's voice channel while they host (the bot
        /// follows them when they move, and leaves when they leave voice or stop hosting).
        /// </summary>
        private string? TalkingChannel(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players)
        {
            if (!_settings.AutoMute.ShowTalking || phase == VoicePhase.Menu || Presence == null || !Presence.Connected) return null;
            var host = players.FirstOrDefault(p => p.IsHost);
            string? hostId = host == null ? null : Links.Find(host.Key)?.DiscordUserId;
            if (string.IsNullOrEmpty(hostId)) return null;
            return Presence.Members.FirstOrDefault(m => m.UserId == hostId)?.ChannelId;
        }

        /// <summary>The voice channel the bot is asked to sit in (null: out of voice). Public for tests.</summary>
        public string? TalkingVoiceChannel { get; private set; }

        /// <summary>This player (their key) is linked to Discord and talking right now.</summary>
        public bool IsTalking(string playerKey)
        {
            var state = Presence as VoicePresenceState ?? _gateway?.State;
            if (state == null || !_settings.AutoMute.ShowTalking) return false;
            string? id = Links.Find(playerKey)?.DiscordUserId;
            return !string.IsNullOrEmpty(id) && state.IsTalking(id!);
        }

        private List<string> Spectators(string channel, IReadOnlyList<PlayerSnapshot> players)
        {
            var playing = new HashSet<string>(players.Select(p => Links.Find(p.Key)?.DiscordUserId).Where(id => id != null)!);
            return Presence!.Members
                .Where(m => m.ChannelId == channel && !m.IsBot && !playing.Contains(m.UserId))
                .Select(m => m.UserId)
                .ToList();
        }

        // ---- Auto-link ---------------------------------------------------------------------

        /// <summary>
        /// Links unlinked players whose in-game name matches exactly one person in voice
        /// (by server nickname, display name or username, ignoring case, spaces and symbols).
        /// </summary>
        private void AutoLink(IReadOnlyList<PlayerSnapshot> players, string? channel)
        {
            if (Presence == null || !Presence.Connected) return;
            var now = _clock();
            if (now < _nextAutoLink) return;
            _nextAutoLink = now + AutoLinkInterval;

            var candidates = Presence.Members
                .Where(m => !m.IsBot && (channel == null || m.ChannelId == channel) && Links.FindByDiscordId(m.UserId) == null)
                .ToList();
            foreach (var p in players)
            {
                if (Links.Find(p.Key) != null || _noAutoLink.Contains(p.Key)) continue;
                string name = NormalizeName(p.Name);
                if (name.Length < 2) continue;
                var matches = candidates.Where(m => m.Names.Any(n => NormalizeName(n) == name)).ToList();
                if (matches.Count != 1) continue;
                var member = matches[0];
                Links.Link(p.Key, p.Name, member.UserId, member.DisplayName);
                candidates.Remove(member);
                Reply($"Auto-linked {p} to @{member.DisplayName}. Wrong? Use /unlink in Discord.", true, _settings.AnnounceLinks);
            }
        }

        internal static string NormalizeName(string name) =>
            new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        /// <summary>A player unlinked themselves: don't auto-link them again this session.</summary>
        private void BlockAutoLink(string playerKey) => _noAutoLink.Add(playerKey);

        // ---- Live status message ------------------------------------------------------------

        private string StatusWebhook =>
            _settings.StatusWebhookUrl.Length > 0 ? _settings.StatusWebhookUrl : _settings.StatsWebhookUrl;

        private void UpdateStatus(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, string lobbyCode, string map, int spectators)
        {
            if (!LiveStatusOn) return;
            if (phase == VoicePhase.Menu)
            {
                // Not hosting any more: one last update saying so, then stop.
                if (!_statusClosed) CloseStatus();
                return;
            }
            if (_statusClosed)
            {
                // A new lobby gets a new message; the old one keeps saying the lobby closed.
                _statusClosed = false;
                lock (_postLock)
                {
                    _statusMessageId = null;
                    _statusSentJson = "";
                }
            }

            var info = new StatusInfo
            {
                Phase = phase,
                Label = LobbyLabel(players),
                Round = _settings.Mode == TrackerMode.Tournament ? Round : 0,
                LobbyCode = lobbyCode,
                Map = map,
                Server = StatusFormatter.ShortServer(Server),
                AutoMuteOn = AutoMute?.Enabled,
                Spectators = spectators,
                SpectatorsMuted = AutoMute != null && AutoMute.Enabled && _settings.AutoMute.MuteSpectators,
                RefereeMode = AutoMute != null && AutoMute.Enabled && AutoMute.RefereeMode,
                CommandPrefix = _settings.CommandPrefix,
                Referee = RefSlotKey == null ? null : players.FirstOrDefault(p => p.Key == RefSlotKey),
                Emojis = _crewEmojis,
                LinkMenuId = LinkMenuWanted ? _linkMenuId : null,
                Players = WithoutReferee(players).Where(p => !p.Disconnected).Select(p => new StatusPlayer
                {
                    Player = p,
                    DiscordUserId = Links.Find(p.Key)?.DiscordUserId,
                    KnownDead = _knownDead.Contains(p.Key),
                }).ToList(),
            };
            SetStatus(StatusFormatter.Build(info));
        }

        /// <summary>Posts the status as a fresh message at the bottom of the channel (after a game, or !refresh).</summary>
        public void RepostStatus()
        {
            if (!LiveStatusOn || _statusClosed) return;
            ScheduleStatusFlush(repost: true);
        }

        private void CloseStatus()
        {
            if (_statusClosed || !LiveStatusOn) return;
            _statusClosed = true;
            SetStatus(StatusFormatter.Build(new StatusInfo { Phase = VoicePhase.Menu, Label = LobbyLabel() }));
        }

        private void SetStatus(WebhookMessage message) => Work.Post(() =>
        {
            string json = JsonSerializer.Serialize(message, WebhookMessage.JsonOptions);
            lock (_postLock)
            {
                if (json == _statusDesiredJson) return;
                _statusDesiredJson = json;
                _statusDesired = message;
            }
            ScheduleStatusFlush();
        });

        /// <summary>
        /// Sends the newest status through the same queue as the reports, so ordering holds.
        /// Many changes in a burst collapse into one edit. A repost (delete, then post again at
        /// the bottom) is always its own step, queued behind whatever was posted before it.
        /// </summary>
        private void ScheduleStatusFlush(bool repost = false)
        {
            string url = StatusWebhook;
            lock (_postLock)
            {
                if (!repost)
                {
                    if (_statusFlushQueued) return;
                    _statusFlushQueued = true;
                }
                _postChain = _postChain.ContinueWith(async _ =>
                {
                    var wait = _statusNextSend - DateTime.UtcNow;
                    if (!repost && wait > TimeSpan.Zero && wait < TimeSpan.FromSeconds(5)) await Task.Delay(wait).ConfigureAwait(false);

                    WebhookMessage? message;
                    string json;
                    string? id;
                    lock (_postLock)
                    {
                        if (!repost) _statusFlushQueued = false;
                        message = _statusDesired;
                        json = _statusDesiredJson;
                        id = _statusMessageId;
                        if (message == null || (json == _statusSentJson && !repost)) return;
                        if (_statusRejected != null && (_statusRejected == json || _statusRejected == MenuShape(message)))
                        {
                            _statusSentJson = json;      // it would fail the same way: wait for a change that matters
                            return;
                        }
                    }
                    DiscordResult? refused = null;

                    try
                    {
                        string? token = _settings.AutoMute.BotTokens.FirstOrDefault();
                        bool byBot = false;
                        string? chosen = _statusChosen;
                        if (token != null && _settings.AutoMute.IsConfigured && chosen != null)
                        {
                            byBot = true;          // the host picked this channel with /new
                        }
                        else if (token != null && _settings.AutoMute.IsConfigured && _settings.AutoMute.LinkMenu && _statusByBot != false && url.Length > 0)
                        {
                            if (_statusByBot == null)
                            {
                                _statusChannel = await _rest.WebhookChannelAsync(url).ConfigureAwait(false);
                                _statusByBot = _statusChannel != null;
                                if (_statusChannel == null) _log.Warn("Couldn't find the live status channel from its webhook; it stays a webhook message, without the colour menu.");
                            }
                            byBot = _statusByBot == true;
                        }
                        string? channelNow = byBot ? chosen ?? _statusChannel : null;
                        if (id != null && (_statusIdByBot != byBot || _statusMessageChannel != channelNow))
                        {
                            // The message is somewhere else now (/new, or a switch of sender): remove the old one.
                            if (_statusIdByBot && _statusMessageChannel != null && token != null)
                                await _rest.DeleteMessageAsync(token, _statusMessageChannel, id).ConfigureAwait(false);
                            else if (!_statusIdByBot && url.Length > 0)
                                await _rest.DeleteWebhookMessageAsync(url, id).ConfigureAwait(false);
                            id = null;
                        }

                        if (byBot)
                        {
                            string channel = channelNow!;
                            if (repost && id != null)
                            {
                                await _rest.DeleteMessageAsync(token!, channel, id).ConfigureAwait(false);
                                id = null;
                            }
                            if (id != null)
                            {
                                var edit = await _rest.EditEmbedsAsync(token!, channel, id, message).ConfigureAwait(false);
                                if (edit.Status == 404) id = null;
                                else if (edit.Status == 400) refused = edit;
                                else if (!edit.Ok) _log.Warn("Could not update the live status: " + edit);
                            }
                            if (id == null && refused == null)
                            {
                                var created = await _rest.PostEmbedsAsync(token!, channel, message).ConfigureAwait(false);
                                if (created.Ok) id = DiscordRest.MessageIdOf(created);
                                else if (created.Status == 400) refused = created;
                                else if (created.Status == 403 || created.Status == 401)
                                {
                                    // The bot can't post there: back to the webhook, without the menu.
                                    _log.Warn("The bot can't post in the live status channel, so the colour menu is off. Give it Send Messages there to turn it on.");
                                    if (chosen != null)
                                    {
                                        StatusProblem = "The bot isn't allowed to post in the channel you picked with /new. Give it View Channel and Send Messages there, then /new again.";
                                        _statusChosen = null;
                                    }
                                    _statusByBot = false;
                                    byBot = false;          // the next tick rebuilds the status without the menu
                                }
                                else _log.Warn("Could not post the live status: " + created);
                            }
                        }
                        if (!byBot && url.Length > 0 && refused == null)
                        {
                            var plain = message.Components == null ? message : new WebhookMessage { Username = message.Username, Content = message.Content, Embeds = message.Embeds, AllowedMentions = message.AllowedMentions };
                            if (repost && id != null)
                            {
                                await _rest.DeleteWebhookMessageAsync(url, id).ConfigureAwait(false);
                                id = null;
                            }
                            if (id != null)
                            {
                                var edit = await _rest.EditWebhookMessageAsync(url, id, plain).ConfigureAwait(false);
                                if (edit.Status == 404) id = null;      // someone deleted it; post a new one
                                else if (edit.Status == 400) refused = edit;
                                else if (!edit.Ok) _log.Warn("Could not update the live status: " + edit);
                            }
                            if (id == null && refused == null)
                            {
                                var created = await _rest.ExecuteWebhookAsync(url, plain).ConfigureAwait(false);
                                if (created.Ok) id = DiscordRest.MessageIdOf(created);
                                else if (created.Status == 400) refused = created;
                                else _log.Warn("Could not post the live status: " + created);
                            }
                        }
                        _statusIdByBot = byBot;
                        _statusMessageChannel = byBot ? channelNow : null;
                    }
                    finally
                    {
                        lock (_postLock)
                        {
                            _statusMessageId = id;
                            _statusSentJson = json;
                            if (refused != null)
                            {
                                // Turned down as it was: sending it again would fail the same way.
                                bool menu = (refused.Body ?? "").IndexOf("COMPONENT", StringComparison.OrdinalIgnoreCase) >= 0;
                                string rejected = menu ? MenuShape(message) ?? json : json;
                                if (_statusRejected != rejected)
                                    _log.Warn("Discord turned down the live status (" + refused + "). It isn't sent again until it changes.");
                                _statusRejected = rejected;
                            }
                            else _statusRejected = null;
                            _statusNextSend = DateTime.UtcNow + StatusMinInterval;
                        }
                    }

                    // A newer status arrived while this one was on the wire.
                    bool again;
                    lock (_postLock) again = _statusDesiredJson != _statusSentJson;
                    if (again) ScheduleStatusFlush();
                }, TaskScheduler.Default).Unwrap();
            }
        }

        /// <summary>The colour menu of a status message, as sent (null: no menu).</summary>
        private static string? MenuShape(WebhookMessage message) =>
            message.Components == null ? null : "menu:" + JsonSerializer.Serialize(message.Components, WebhookMessage.JsonOptions);
    }
}
