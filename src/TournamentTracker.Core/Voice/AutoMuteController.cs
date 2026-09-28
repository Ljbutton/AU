using System;
using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker.Voice
{
    /// <summary>
    /// Maps the game's phase and who is alive onto the Discord voice state of every linked
    /// player (and, optionally, spectators), and hands only the changes to the dispatcher.
    /// Phase changes take effect after the configured delay; deaths apply at once.
    /// </summary>
    public sealed class AutoMuteController
    {
        private readonly AutoMuteSettings _settings;
        private readonly LinkRegistry _links;
        private readonly MuteDispatcher _dispatcher;
        private readonly Func<DateTime> _clock;
        private readonly Dictionary<string, VoiceState> _sent = new Dictionary<string, VoiceState>();
        private bool _enabled = true;
        private VoicePhase? _pending;
        private DateTime _pendingAt;

        public AutoMuteController(AutoMuteSettings settings, LinkRegistry links, MuteDispatcher dispatcher, Func<DateTime>? clock = null)
        {
            _settings = settings;
            _links = links;
            _dispatcher = dispatcher;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>The phase voice is currently following; it lags the game by the phase delay.</summary>
        public VoicePhase Phase { get; private set; } = VoicePhase.Menu;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled && !value) ReleaseAll();
                _enabled = value;
            }
        }

        /// <summary>
        /// Referee mode: everyone in voice is muted (not deafened) except the referees, so the
        /// rules can be explained. Only ever switched on by hand; applies at once, no delay.
        /// </summary>
        public bool RefereeMode { get; private set; }
        private readonly HashSet<string> _referees = new HashSet<string>();

        public void StartRefereeMode(IEnumerable<string> refereeUserIds)
        {
            _referees.Clear();
            foreach (var id in refereeUserIds.Concat(_settings.RefereeUserIds)) _referees.Add(id);
            RefereeMode = true;
        }

        public void StopRefereeMode() => RefereeMode = false;

        private VoiceState RefereeState(string userId) =>
            _referees.Contains(userId) ? VoiceState.Open : new VoiceState(true, false);

        public bool MuteSpectators
        {
            get => _settings.MuteSpectators;
            set => _settings.MuteSpectators = value;
        }

        /// <param name="spectators">Discord users in the game's voice channel who aren't playing.</param>
        public void Update(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, IReadOnlyCollection<string>? spectators = null)
        {
            AdvancePhase(phase);
            if (!_enabled) return;

            var present = new HashSet<string>();
            foreach (var p in players)
            {
                var link = _links.Find(p.Key);
                if (link == null || !present.Add(link.DiscordUserId)) continue;
                Send(link.DiscordUserId, RefereeMode ? RefereeState(link.DiscordUserId) : MutePlanner.Plan(Phase, p.IsAlive, _settings));
            }

            if (spectators != null)
            {
                bool inGame = Phase == VoicePhase.Tasks || Phase == VoicePhase.Meeting;
                var state = _settings.MuteSpectators && inGame ? new VoiceState(true, false) : VoiceState.Open;
                foreach (var userId in spectators)
                {
                    if (RefereeMode)
                    {
                        if (present.Add(userId)) Send(userId, RefereeState(userId), lazy: true);
                        continue;
                    }
                    if (_settings.SpectatorExemptUserIds.Contains(userId) || !present.Add(userId)) continue;
                    Send(userId, state, lazy: true);
                }
            }

            // Anyone we muted who has since left, or been unlinked, gets their voice back.
            foreach (var userId in _sent.Keys.Where(id => !present.Contains(id)).ToList())
            {
                if (_sent[userId] != VoiceState.Open) _dispatcher.SetDesired(userId, VoiceState.Open);
                _sent.Remove(userId);
            }
        }

        private void AdvancePhase(VoicePhase phase)
        {
            var now = _clock();
            if (phase == Phase)
            {
                _pending = null;
                return;
            }
            if (_pending != phase)
            {
                _pending = phase;
                _pendingAt = now + TimeSpan.FromSeconds(Math.Max(0, DelayFor(Phase, phase)));
            }
            if (now >= _pendingAt)
            {
                Phase = phase;
                _pending = null;
            }
        }

        public double DelayFor(VoicePhase from, VoicePhase to)
        {
            bool playing = from == VoicePhase.Tasks || from == VoicePhase.Meeting;
            switch (to)
            {
                case VoicePhase.Tasks:
                    return from == VoicePhase.Meeting ? _settings.DelayMeetingEnd : _settings.DelayGameStart;
                case VoicePhase.Meeting:
                    return from == VoicePhase.Tasks ? _settings.DelayMeetingStart : 0;
                default:
                    return playing ? _settings.DelayGameEnd : 0;
            }
        }

        /// <summary>Unmutes and undeafens everyone the mod has touched, at once.</summary>
        public void ReleaseAll()
        {
            RefereeMode = false;
            foreach (var userId in _sent.Keys.Concat(_dispatcher.KnownUsers).Distinct().ToList())
                _dispatcher.SetDesired(userId, VoiceState.Open);
            _sent.Clear();
        }

        /// <param name="lazy">Skip an unmute for someone the mod has never touched (spectators).</param>
        private void Send(string userId, VoiceState state, bool lazy = false)
        {
            if (_sent.TryGetValue(userId, out var last) && last == state) return;
            if (lazy && !_sent.ContainsKey(userId) && state == VoiceState.Open && !_dispatcher.KnownUsers.Contains(userId))
            {
                _sent[userId] = state;
                return;
            }
            _sent[userId] = state;
            _dispatcher.SetDesired(userId, state);
        }
    }
}
