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

        /// <summary>Called a few times a second with the current phase and players.</summary>
        public void VoiceTick(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, string lobbyCode = "", string map = "")
        {
            bool changed = phase != _phase || lobbyCode != _lobbyCode || players.Count != Players.Count;
            Players = players;
            _phase = phase;
            _lobbyCode = lobbyCode;
            _map = map;
            foreach (var p in players) Links.Touch(p.Key, p.Name);
            TrackKnownDeaths(phase, players);

            var channel = GameVoiceChannel(players);
            var spectators = channel == null ? null : Spectators(channel, players);
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
            UpdateStatus(phase, players, lobbyCode, map, spectators?.Count ?? 0);
            UpdateOverlay(phase, players, map);
            PublishLive(phase, players, lobbyCode, map);
            RefreshStatus(force: changed);
            _lastPhase = phase;
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

        private void SetStatus(WebhookMessage message)
        {
            string json = JsonSerializer.Serialize(message, WebhookMessage.JsonOptions);
            lock (_postLock)
            {
                if (json == _statusDesiredJson) return;
                _statusDesiredJson = json;
                _statusDesired = message;
            }
            ScheduleStatusFlush();
        }

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
                    }

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
                                else if (!edit.Ok) _log.Warn("Could not update the live status: " + edit);
                            }
                            if (id == null)
                            {
                                var created = await _rest.PostEmbedsAsync(token!, channel, message).ConfigureAwait(false);
                                if (created.Ok) id = DiscordRest.MessageIdOf(created);
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
                        if (!byBot && url.Length > 0)
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
                                else if (!edit.Ok) _log.Warn("Could not update the live status: " + edit);
                            }
                            if (id == null)
                            {
                                var created = await _rest.ExecuteWebhookAsync(url, plain).ConfigureAwait(false);
                                if (created.Ok) id = DiscordRest.MessageIdOf(created);
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
    }
}
