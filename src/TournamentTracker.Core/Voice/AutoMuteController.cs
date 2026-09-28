using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker.Voice
{
    /// <summary>
    /// Maps the game's phase and who is alive onto the Discord voice state of every linked
    /// player, and hands only the changes to the dispatcher.
    /// </summary>
    public sealed class AutoMuteController
    {
        private readonly AutoMuteSettings _settings;
        private readonly LinkRegistry _links;
        private readonly MuteDispatcher _dispatcher;
        private readonly Dictionary<string, VoiceState> _sent = new Dictionary<string, VoiceState>();
        private bool _enabled = true;

        public AutoMuteController(AutoMuteSettings settings, LinkRegistry links, MuteDispatcher dispatcher)
        {
            _settings = settings;
            _links = links;
            _dispatcher = dispatcher;
        }

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

        public void Update(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players)
        {
            Phase = phase;
            if (!_enabled) return;

            var present = new HashSet<string>();
            foreach (var p in players)
            {
                var link = _links.Find(p.Key);
                if (link == null || !present.Add(link.DiscordUserId)) continue;
                Send(link.DiscordUserId, MutePlanner.Plan(phase, p.IsAlive, _settings));
            }

            // Anyone we muted who has since left the lobby, or been unlinked, gets their voice back.
            foreach (var userId in _sent.Keys.Where(id => !present.Contains(id)).ToList())
            {
                _dispatcher.SetDesired(userId, VoiceState.Open);
                _sent.Remove(userId);
            }
        }

        /// <summary>Unmutes and undeafens everyone the mod has touched.</summary>
        public void ReleaseAll()
        {
            foreach (var userId in _sent.Keys.Concat(_dispatcher.KnownUsers).Distinct().ToList())
                _dispatcher.SetDesired(userId, VoiceState.Open);
            _sent.Clear();
        }

        private void Send(string userId, VoiceState state)
        {
            if (_sent.TryGetValue(userId, out var last) && last == state) return;
            _sent[userId] = state;
            _dispatcher.SetDesired(userId, state);
        }
    }
}
