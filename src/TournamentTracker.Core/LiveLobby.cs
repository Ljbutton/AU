using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
        /// <summary>
        /// Sends in a row that didn't go through (Discord down, slow, or rate limited past the
        /// retries). The status isn't counted as sent; it's tried again on a timer, less often while
        /// it keeps failing.
        /// </summary>
        private int _statusFailures;
        /// <summary>When a failed status is due to be tried again (session clock ticks; 0: nothing owed).</summary>
        private long _statusRetryAt;
        /// <summary>
        /// The status wanted is the last word ("lobby closed"): it goes out without a failed send's
        /// wait, since the game may be closing and no tick would come to send it after that wait.
        /// </summary>
        private bool _statusFinal;
        /// <summary>The last word may still be tried once more at once when it fails (no tick may come to retry it).</summary>
        private bool _statusFinalRetry;

        /// <summary>A failed status send is due again (checked on every publish tick, without the lock).</summary>
        private bool StatusRetryDue
        {
            get
            {
                long at = Interlocked.Read(ref _statusRetryAt);
                return at != 0 && _clock().Ticks >= at;
            }
        }

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
            // Back in the lobby or the menus: the game is over, and safe mode (held for it since its
            // roles were handed out) follows the host's choice again.
            if (phase == VoicePhase.Lobby || phase == VoicePhase.Menu) SafeMode?.ReleaseGame();
            bool changed = phase != _phase || lobbyCode != _lobbyCode || players.Count != Players.Count;
            Players = players;
            _phase = phase;
            _lobbyCode = lobbyCode;
            _map = map;
            foreach (var p in players) Links.Touch(p.Key, p.Name);
            TrackKnownDeaths(phase, players);

            // Who's in voice, copied once for the whole tick (each read of Members copies the roster).
            var members = Presence != null && Presence.Connected ? Presence.Members : null;
            // Someone whose mute waited for them to come back to voice gets it now.
            if (members != null && _dispatcher != null && _dispatcher.WaitingForVoice) _dispatcher.InVoice(members.Select(m => m.UserId));

            var channel = GameVoiceChannel(players, members);
            // Not hosting (in the menus, or a player in someone else's lobby, even with the mod): the
            // people in voice aren't this game's spectators, and their voice is the host's to set.
            var spectators = channel == null || phase == VoicePhase.Menu ? null : Spectators(channel, players, members!);
            // The referee ghost is never muted: take them out of the players and the spectators.
            string? refKey = ActiveRefSlotKey;
            string? referee = refKey == null ? null : Links.Find(refKey)?.DiscordUserId;
            if (referee != null) spectators?.Remove(referee);
            var playing = WithoutReferee(players);

            if (phase == VoicePhase.Lobby && _settings.AutoMute.AutoLinkByName) AutoLink(players, channel, members);

            // Referee mode is for the lobby; a game starting ends it so meetings aren't silenced.
            if (AutoMute != null && AutoMute.RefereeMode && phase == VoicePhase.Tasks)
            {
                AutoMute.StopRefereeMode();
                Reply("Referee mode ended because the game started.", false);
            }
            AutoMute?.Update(phase, playing, spectators);
            TalkingVoiceChannel = TalkingChannel(phase, players, members);
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
            // A status Discord didn't take is sent again once its wait is over (the game's ticks
            // drive it, so a closed session stops trying).
            if (LiveStatusOn && StatusRetryDue) ScheduleStatusFlush();
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
        private string? GameVoiceChannel(IReadOnlyList<PlayerSnapshot> players) =>
            GameVoiceChannel(players, Presence != null && Presence.Connected ? Presence.Members : null);

        /// <param name="members">Who's in voice (null: not known, the bot isn't connected).</param>
        private string? GameVoiceChannel(IReadOnlyList<PlayerSnapshot> players, IReadOnlyList<VoiceMember>? members)
        {
            if (members == null) return null;
            if (_settings.AutoMute.VoiceChannelId.Length > 0) return _settings.AutoMute.VoiceChannelId;
            var host = players.FirstOrDefault(p => p.IsHost);
            var hostId = host == null ? null : Links.Find(host.Key)?.DiscordUserId;
            var hostChannel = hostId == null ? null : members.FirstOrDefault(m => m.UserId == hostId)?.ChannelId;
            if (hostChannel != null) return hostChannel;
            var linked = new HashSet<string>(players.Select(p => Links.Find(p.Key)?.DiscordUserId).Where(id => id != null)!);
            return members
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
        private string? TalkingChannel(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, IReadOnlyList<VoiceMember>? members)
        {
            if (!_settings.AutoMute.ShowTalking || phase == VoicePhase.Menu || members == null) return null;
            var host = players.FirstOrDefault(p => p.IsHost);
            string? hostId = host == null ? null : Links.Find(host.Key)?.DiscordUserId;
            if (string.IsNullOrEmpty(hostId)) return null;
            return members.FirstOrDefault(m => m.UserId == hostId)?.ChannelId;
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

        private List<string> Spectators(string channel, IReadOnlyList<PlayerSnapshot> players, IReadOnlyList<VoiceMember> members)
        {
            var playing = new HashSet<string>(players.Select(p => Links.Find(p.Key)?.DiscordUserId).Where(id => id != null)!);
            return members
                .Where(m => m.ChannelId == channel && !m.IsBot && !playing.Contains(m.UserId))
                .Select(m => m.UserId)
                .ToList();
        }

        // ---- Auto-link ---------------------------------------------------------------------

        /// <summary>
        /// Links unlinked players whose in-game name matches exactly one person in voice
        /// (by server nickname, display name or username, ignoring case, spaces and symbols).
        /// </summary>
        private void AutoLink(IReadOnlyList<PlayerSnapshot> players, string? channel, IReadOnlyList<VoiceMember>? members)
        {
            if (members == null) return;
            var now = _clock();
            if (now < _nextAutoLink) return;
            _nextAutoLink = now + AutoLinkInterval;

            var candidates = members
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
                Note($"Auto-linked {p} to @{member.DisplayName}. Wrong? Use /unlink in Discord.");
            }
        }

        internal static string NormalizeName(string name) =>
            new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        // ---- Live status message ------------------------------------------------------------

        private string StatusWebhook =>
            _settings.StatusWebhookUrl.Length > 0 ? _settings.StatusWebhookUrl : _settings.StatsWebhookUrl;

        private void UpdateStatus(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, string lobbyCode, string map, int spectators)
        {
            if (!LiveStatusOn)
            {
                _statusInputsKnown = false;
                return;
            }
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
                _statusInputsKnown = false;
                lock (_postLock)
                {
                    _statusMessageId = null;
                    _statusSentJson = "";
                }
            }

            // This runs on the game's main thread several times a second, and almost always nothing
            // the message shows has changed: compare a cheap fingerprint before building it.
            int inputs = StatusInputs(phase, players, lobbyCode, map, spectators);
            if (_statusInputsKnown && inputs == _statusInputs) return;
            _statusInputs = inputs;
            _statusInputsKnown = true;

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
                Referee = ActiveRefSlotKey is { } refKey ? players.FirstOrDefault(p => p.Key == refKey) : null,
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

        /// <summary>A fingerprint of everything the live status is built from (main thread only).</summary>
        private int _statusInputs;
        private bool _statusInputsKnown;

        private int StatusInputs(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, string lobbyCode, string map, int spectators)
        {
            var h = new HashCode();
            h.Add(phase);
            h.Add(lobbyCode);
            h.Add(map);
            h.Add(Server);
            h.Add(_settings.LobbyLabel);
            h.Add(_settings.Mode);
            h.Add(Round);
            h.Add(AutoMute?.Enabled);
            h.Add(AutoMute?.RefereeMode);
            h.Add(_settings.AutoMute.MuteSpectators);
            h.Add(spectators);
            h.Add(_settings.CommandPrefix);
            h.Add(ActiveRefSlotKey);
            h.Add(_crewEmojis.Count);
            h.Add(LinkMenuWanted);
            h.Add(Links.Version);
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                h.Add(p.Key);
                h.Add(p.Name);
                h.Add(p.ColorId);
                h.Add(p.IsHost);
                h.Add(p.Disconnected);
                h.Add(_knownDead.Contains(p.Key));
            }
            return h.ToHashCode();
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
            _statusInputsKnown = false;
            // The last word goes out now, not after a failed send's wait: the game may be closing.
            // A send still on the wire can fail after this and set a wait again, so the closed
            // status is also marked final (see SetStatus), which goes past any wait.
            lock (_postLock) NothingOwed();
            SetStatus(StatusFormatter.Build(new StatusInfo { Phase = VoicePhase.Menu, Label = LobbyLabel() }), final: true);
        }

        /// <param name="final">The lobby closed: the last status for this message (see <see cref="_statusFinal"/>).</param>
        private void SetStatus(WebhookMessage message, bool final = false) => Work.Post(() =>
        {
            string json = JsonSerializer.Serialize(message, WebhookMessage.JsonOptions);
            lock (_postLock)
            {
                // Set with the status it goes with (both on this thread, in order): a new lobby's status clears it.
                _statusFinal = final;
                if (final) _statusFinalRetry = true;
                if (json == _statusDesiredJson && !final) return;
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
                    bool final;
                    lock (_postLock)
                    {
                        if (!repost) _statusFlushQueued = false;
                        final = _statusFinal;
                        // A send that failed waits its turn (see PublishTick), not tried again on every change
                        // meanwhile. The lobby-closed status doesn't wait: no tick may come after the wait.
                        if (!repost && !final && _statusRetryAt != 0 && _clock().Ticks < _statusRetryAt) return;
                        message = _statusDesired;
                        json = _statusDesiredJson;
                        id = _statusMessageId;
                        if (message == null || (json == _statusSentJson && !repost))
                        {
                            NothingOwed();
                            return;
                        }
                        if (_statusRejected != null && (_statusRejected == json || _statusRejected == MenuShape(message)))
                        {
                            _statusSentJson = json;      // it would fail the same way: wait for a change that matters
                            NothingOwed();
                            return;
                        }
                    }
                    DiscordResult? refused = null;
                    // The last step didn't go through (and wasn't turned down for good): not sent.
                    bool failed = false;
                    // Discord's answer to editing the message in place: 404, someone deleted it (a new one is posted).
                    void Edited(DiscordResult edit)
                    {
                        failed = !edit.Ok && edit.Status != 404 && edit.Status != 400;
                        if (edit.Status == 404) id = null;
                        else if (edit.Status == 400) refused = edit;
                        else if (!edit.Ok) _log.Warn("Could not update the live status: " + edit);
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
                            if (id != null) Edited(await _rest.EditEmbedsAsync(token!, channel, id, message).ConfigureAwait(false));
                            if (id == null && refused == null)
                            {
                                var created = await _rest.PostEmbedsAsync(token!, channel, message).ConfigureAwait(false);
                                failed = !created.Ok && created.Status != 400 && created.Status != 403 && created.Status != 401;
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
                            if (id != null) Edited(await _rest.EditWebhookMessageAsync(url, id, plain).ConfigureAwait(false));
                            if (id == null && refused == null)
                            {
                                var created = await _rest.ExecuteWebhookAsync(url, plain).ConfigureAwait(false);
                                failed = !created.Ok && created.Status != 400;
                                if (created.Ok) id = DiscordRest.MessageIdOf(created);
                                else if (created.Status == 400) refused = created;
                                else _log.Warn("Could not post the live status: " + created);
                            }
                        }
                        _statusIdByBot = byBot;
                        _statusMessageChannel = byBot ? channelNow : null;
                    }
                    catch (Exception e)
                    {
                        failed = true;
                        _log.Warn("Could not update the live status: " + e.Message);
                    }
                    finally
                    {
                        lock (_postLock)
                        {
                            _statusMessageId = id;
                            if (failed && refused == null)
                            {
                                // Not counted as sent, or the message would stay stale until the next change:
                                // tried again in 5 s, then 10, 20, 40 and every minute while Discord keeps failing.
                                // What the message shows now isn't known (a post may have failed after the old
                                // one was found deleted), so the retry sends even if the status went back.
                                _statusSentJson = "";
                                _statusFailures++;
                                var retryIn = TimeSpan.FromSeconds(Math.Min(60, 5 << Math.Min(_statusFailures - 1, 4)));
                                Interlocked.Exchange(ref _statusRetryAt, (_clock() + retryIn).Ticks);
                            }
                            else
                            {
                                NothingOwed();
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
                            }
                            _statusNextSend = DateTime.UtcNow + StatusMinInterval;
                        }
                    }

                    // A newer status arrived while this one was on the wire (a failed one waits for its retry).
                    bool again;
                    lock (_postLock)
                    {
                        again = !failed && _statusDesiredJson != _statusSentJson;
                        // The lobby-closed status failed: the game may be closing, and then no tick comes to
                        // send it again. It's tried once more now (ShutdownAsync waits for that too).
                        if (failed && final && _statusFinal && _statusFinalRetry)
                        {
                            _statusFinalRetry = false;
                            again = true;
                        }
                    }
                    if (again) ScheduleStatusFlush();
                }, TaskScheduler.Default).Unwrap();
            }
        }

        /// <summary>No failed status is waiting to be sent again (called with <see cref="_postLock"/> held).</summary>
        private void NothingOwed()
        {
            _statusFailures = 0;
            Interlocked.Exchange(ref _statusRetryAt, 0);
        }

        /// <summary>The colour menu of a status message, as sent (null: no menu).</summary>
        private static string? MenuShape(WebhookMessage message) =>
            message.Components == null ? null : "menu:" + JsonSerializer.Serialize(message.Components, WebhookMessage.JsonOptions);
    }
}
