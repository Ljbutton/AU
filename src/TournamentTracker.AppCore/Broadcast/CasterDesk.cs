using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>A notification card: one play in one lobby, updated in place when it happens again.</summary>
    public sealed class Card
    {
        public string Id { get; set; } = "";
        public string Lobby { get; set; } = "";
        public string Rule { get; set; } = "";
        public string Tier { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTime At { get; set; }
        public DateTime Updated { get; set; }
        public int Repeats { get; set; } = 1;
        public int? Crew { get; set; }
        public int? Imps { get; set; }
        public int? TaskPct { get; set; }
        public double Value { get; set; }
        /// <summary>When it happened while its lobby was on stream (the SHOWN badge), or when it was put on.</summary>
        public DateTime? ShownAt { get; set; }
        public string? ShownHow { get; set; }
        public bool Dismissed { get; set; }
        /// <summary>The event behind it, as the lobby sent it (kill positions etc., for the replay later).</summary>
        public JsonElement? Source { get; set; }
        internal string PlayKey = "";
    }

    /// <summary>What's on stream: one lobby full screen, two side by side, or four.</summary>
    public sealed class OnAir
    {
        /// <summary>"full", "2up", "4up", or "none".</summary>
        public string Layout { get; set; } = "none";
        public List<string?> Slots { get; set; } = new List<string?>();
        /// <summary>Who set it: "button" (a click here) or "obs" (switched in OBS).</summary>
        public string By { get; set; } = "button";
        /// <summary>The OBS scene that's live, when OBS is connected.</summary>
        public string? Scene { get; set; }
        public DateTime Since { get; set; }

        public static int SlotsFor(string layout) => layout == "4up" ? 4 : layout == "2up" ? 2 : layout == "full" ? 1 : 0;

        /// <summary>"LIVE (full)", "LIVE (2-up, slot 1)", "LIVE (quad, slot 2)", or null when not on.</summary>
        public string? Label(string lobby)
        {
            int i = Slots.FindIndex(s => string.Equals(s, lobby, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            return Layout switch
            {
                "full" => "LIVE (full)",
                "2up" => $"LIVE (2-up, slot {i + 1})",
                "4up" => $"LIVE (quad, slot {i + 1})",
                _ => null,
            };
        }

        public bool Has(string lobby) => Label(lobby) != null;
    }

    /// <summary>
    /// The caster tab's brain: takes every lobby's feed (or the simulator's), ranks the lobbies,
    /// keeps the notification cards (one per play, merged), the history, and what's on stream.
    /// Only on the PC with the administration code; may show impostors, so it never goes anywhere else.
    /// </summary>
    public sealed class CasterDesk : IDisposable
    {
        private const int KeepHistory = 200;
        private readonly object _lock = new object();
        private readonly Func<DateTime> _clock;
        private readonly PriorityConfigFile? _file;
        private readonly Func<PriorityConfig> _config;
        private readonly List<Card> _cards = new List<Card>();
        private readonly List<Card> _history = new List<Card>();
        private readonly Dictionary<string, Card> _byPlay = new Dictionary<string, Card>();
        private long _cardSeq;
        private OnAir _onAir = new OnAir();
        private FeedSimulator? _sim;
        private Timer? _simTimer;

        public LobbyBoard Board { get; }

        /// <summary>Called to put lobbies on stream (Part 4 drives OBS here). Gets the new on-air state.</summary>
        public Action<OnAir>? Switch { get; set; }

        public CasterDesk(string? configPath, Func<DateTime>? clock = null, PriorityConfig? config = null)
        {
            _clock = clock ?? (() => DateTime.UtcNow);
            if (config != null) _config = () => config;
            else
            {
                _file = configPath == null ? null : new PriorityConfigFile(configPath);
                _config = () => _file?.Refresh() ?? new PriorityConfig();
            }
            Board = new LobbyBoard(_config, _clock);
            Board.PlayChanged += OnPlay;
        }

        public string? ConfigPath => _file?.Path;
        public bool Simulating => _sim != null;

        // ---- Feed in ------------------------------------------------------------------------

        public void Apply(string json)
        {
            try { Board.Apply(json); } catch (Exception) { /* one bad message doesn't stop the rest */ }
        }

        public void Apply(JsonElement item)
        {
            try { Board.Apply(item); } catch (Exception) { }
        }

        private void OnPlay(string lobby, Play play)
        {
            var c = _config();
            var rule = c.Rule(play.Rule);
            // Cards only for what's worth a look (medium and up); the ranking still counts the rest.
            if (!rule.On || rule.Points < c.Tiers.Medium) return;
            var now = _clock();
            var live = Board.Lobby(lobby);
            lock (_lock)
            {
                string key = lobby + "|" + play.Key + "|" + play.At.Ticks;
                if (!_byPlay.TryGetValue(key, out var card))
                {
                    card = new Card { Id = "c" + (++_cardSeq), Lobby = lobby, Rule = play.Rule, At = play.At, PlayKey = key };
                    _byPlay[key] = card;
                    _cards.Add(card);
                }
                card.Tier = c.TierOf(rule.Points);
                card.Text = play.Text;
                card.Updated = now;
                card.Repeats = play.Repeats;
                card.Source = play.Source;
                card.Crew = live?.Crew;
                card.Imps = live?.Imps;
                card.TaskPct = live?.TaskPct;
                card.Value = rule.Points;
                card.Dismissed = false;
                if (card.ShownAt == null && _onAir.Has(lobby))
                {
                    card.ShownAt = now;
                    card.ShownHow = _onAir.Label(lobby);
                }
            }
        }

        // ---- Simulation -----------------------------------------------------------------------

        /// <summary>Simulation mode: four fake lobbies feed the desk, so the tab and switching can be tried without games.</summary>
        public void Simulate(bool on)
        {
            lock (_lock)
            {
                _simTimer?.Dispose();
                _simTimer = null;
                _sim = on ? new FeedSimulator(_clock(), 4, Environment.TickCount) : null;
                if (_sim != null) _simTimer = new Timer(_ => SimTick(), null, 0, 500);
            }
        }

        /// <summary>Runs the simulator up to now. Public for tests.</summary>
        public void SimTick()
        {
            FeedSimulator? sim;
            lock (_lock) sim = _sim;
            if (sim == null) return;
            List<string> messages;
            lock (sim) messages = sim.Advance(_clock());
            foreach (var m in messages) Apply(m);
        }

        public void SimOffline(string lobby, bool offline) => _sim?.SetOffline(lobby, offline);

        // ---- On stream ------------------------------------------------------------------------

        public OnAir OnAir { get { lock (_lock) return _onAir; } }

        /// <summary>
        /// Puts a lobby on stream. Full screen by default; 2-up or 4-up put it in the given slot (or
        /// the first) and fill the other slots with the top-ranked lobbies.
        /// </summary>
        public OnAir Show(string lobby, string layout = "full", int? slot = null, IList<string>? slots = null)
        {
            layout = OnAir.SlotsFor(layout) > 0 ? layout : "full";
            int n = OnAir.SlotsFor(layout);
            var ranked = Board.Ranking().Where(r => r.Online).Select(r => r.Lobby).ToList();
            OnAir next;
            lock (_lock)
            {
                var filled = new List<string?>(new string?[n]);
                if (slots != null)
                    for (int i = 0; i < n && i < slots.Count; i++) filled[i] = string.IsNullOrEmpty(slots[i]) ? null : slots[i];
                else if (_onAir.Layout == layout)
                    for (int i = 0; i < n && i < _onAir.Slots.Count; i++) filled[i] = _onAir.Slots[i];
                int at = Math.Max(0, Math.Min(n - 1, (slot ?? 1) - 1));
                if (lobby.Length > 0)
                {
                    for (int i = 0; i < n; i++) if (string.Equals(filled[i], lobby, StringComparison.OrdinalIgnoreCase)) filled[i] = null;
                    filled[at] = lobby;
                }
                // Empty slots get the top scorers not already on (unless the slots were picked by hand).
                foreach (var top in slots != null ? new List<string>() : ranked)
                {
                    int free = filled.IndexOf(null);
                    if (free < 0) break;
                    if (!filled.Any(f => string.Equals(f, top, StringComparison.OrdinalIgnoreCase))) filled[free] = top;
                }
                next = new OnAir { Layout = layout, Slots = filled, By = "button", Since = _clock() };
                _onAir = next;
                MarkShown(next);
            }
            Switch?.Invoke(next);
            return next;
        }

        /// <summary>OBS switched by itself (Part 4 calls this when you change scenes there).</summary>
        public void ObsChanged(OnAir state)
        {
            lock (_lock)
            {
                state.By = "obs";
                state.Since = _clock();
                _onAir = state;
                MarkShown(state);
            }
        }

        /// <summary>Live cards of lobbies that just went on air count as shown from now.</summary>
        private void MarkShown(OnAir state)
        {
            var now = _clock();
            foreach (var card in _cards.Where(c => c.ShownAt == null && state.Has(c.Lobby)))
            {
                card.ShownAt = now;
                card.ShownHow = state.Label(card.Lobby);
            }
        }

        public void Dismiss(string id)
        {
            lock (_lock)
            {
                var card = _cards.FirstOrDefault(c => c.Id == id);
                if (card != null) card.Dismissed = true;
            }
        }

        public Card? Find(string id)
        {
            lock (_lock) return _cards.FirstOrDefault(c => c.Id == id) ?? _history.FirstOrDefault(c => c.Id == id);
        }

        // ---- What the tab shows ----------------------------------------------------------------

        public object State()
        {
            var c = _config();
            var ranking = Board.Ranking();
            var values = ranking.SelectMany(r => r.Plays.Select(p => (Key: r.Lobby + "|" + p.Key + "|" + p.At.Ticks, p.Value)))
                .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Value);
            var online = ranking.ToDictionary(r => r.Lobby, r => r.Online, StringComparer.OrdinalIgnoreCase);
            var now = _clock();
            lock (_lock)
            {
                // A card whose play has faded (or was dismissed) moves to the history.
                foreach (var card in _cards.ToList())
                {
                    bool alive = values.TryGetValue(card.PlayKey, out var v) && v >= 5;
                    card.Value = alive ? v : 0;
                    if (alive && !card.Dismissed) continue;
                    _cards.Remove(card);
                    _byPlay.Remove(card.PlayKey);
                    _history.Insert(0, card);
                }
                if (_history.Count > KeepHistory) _history.RemoveRange(KeepHistory, _history.Count - KeepHistory);

                object View(Card x) => new
                {
                    x.Id, x.Lobby, x.Rule, x.Tier, x.Text, x.Repeats, x.Crew, x.Imps, x.TaskPct,
                    Value = Math.Round(x.Value, 1),
                    Ago = Math.Max(0, (int)(now - x.At).TotalSeconds),
                    UpdatedAgo = Math.Max(0, (int)(now - x.Updated).TotalSeconds),
                    At = x.At.ToString("o"),
                    Shown = x.ShownAt?.ToString("o"),
                    x.ShownHow,
                    OnAir = _onAir.Label(x.Lobby),
                    Offline = online.TryGetValue(x.Lobby, out var on) && !on,
                };
                return new
                {
                    Simulating,
                    ConfigPath,
                    Problem = _file?.Problem,
                    OnAir = new { _onAir.Layout, _onAir.Slots, _onAir.By, _onAir.Scene, Since = _onAir.Since == default ? null : _onAir.Since.ToString("o") },
                    Lobbies = ranking.Select(r => new
                    {
                        r.Lobby, r.Online, r.Score, r.Tier, r.Line, r.Phase, r.Crew, r.Imps, r.TaskPct, r.Game, r.Round,
                        OnAir = _onAir.Label(r.Lobby),
                    }).ToList(),
                    Cards = _cards.OrderByDescending(x => x.Value).ThenByDescending(x => x.Updated).Select(View).ToList(),
                    History = _history.OrderByDescending(x => x.At).Take(60).Select(View).ToList(),
                };
            }
        }

        public void Dispose()
        {
            lock (_lock) { _simTimer?.Dispose(); _simTimer = null; _sim = null; }
        }
    }
}
