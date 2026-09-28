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

        /// <summary>Called a few times a second with the current phase and players.</summary>
        public void VoiceTick(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, string lobbyCode = "", string map = "")
        {
            Players = players;
            foreach (var p in players) Links.Touch(p.Key, p.Name);
            TrackKnownDeaths(phase, players);

            var channel = GameVoiceChannel(players);
            var spectators = channel == null ? null : Spectators(channel, players);

            if (phase == VoicePhase.Lobby && _settings.AutoMute.AutoLinkByName) AutoLink(players, channel);

            // Referee mode is for the lobby; a game starting ends it so meetings aren't silenced.
            if (AutoMute != null && AutoMute.RefereeMode && phase == VoicePhase.Tasks)
            {
                AutoMute.StopRefereeMode();
                Reply("Referee mode ended because the game started.", false);
            }
            AutoMute?.Update(phase, players, spectators);
            UpdateStatus(phase, players, lobbyCode, map, spectators?.Count ?? 0);
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
                Reply($"Auto-linked {p} to @{member.DisplayName}. Wrong? Type {_settings.CommandPrefix}unlink", true);
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
            if (!_settings.LiveStatus || string.IsNullOrWhiteSpace(StatusWebhook)) return;
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
                LobbyCode = lobbyCode,
                Map = map,
                AutoMuteOn = AutoMute?.Enabled,
                Spectators = spectators,
                SpectatorsMuted = AutoMute != null && AutoMute.Enabled && _settings.AutoMute.MuteSpectators,
                RefereeMode = AutoMute != null && AutoMute.Enabled && AutoMute.RefereeMode,
                CommandPrefix = _settings.CommandPrefix,
                Players = players.Where(p => !p.Disconnected).Select(p => new StatusPlayer
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
            if (!_settings.LiveStatus || _statusClosed) return;
            ScheduleStatusFlush(repost: true);
        }

        private void CloseStatus()
        {
            if (_statusClosed || !_settings.LiveStatus) return;
            _statusClosed = true;
            SetStatus(StatusFormatter.Build(new StatusInfo { Phase = VoicePhase.Menu }));
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
                        if (repost && id != null)
                        {
                            await _rest.DeleteWebhookMessageAsync(url, id).ConfigureAwait(false);
                            id = null;
                        }
                        if (id != null)
                        {
                            var edit = await _rest.EditWebhookMessageAsync(url, id, message).ConfigureAwait(false);
                            if (edit.Status == 404) id = null;      // someone deleted it; post a new one
                            else if (!edit.Ok) _log.Warn("Could not update the live status: " + edit);
                        }
                        if (id == null)
                        {
                            var created = await _rest.ExecuteWebhookAsync(url, message).ConfigureAwait(false);
                            if (created.Ok) id = DiscordRest.MessageIdOf(created);
                            else _log.Warn("Could not post the live status: " + created);
                        }
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
