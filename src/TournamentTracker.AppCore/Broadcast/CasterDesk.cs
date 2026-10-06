using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TournamentTracker.Stats;

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
        /// <summary>The replay clip saved for it, and how that's going ("saving", "ready", "failed").</summary>
        public string? ClipId { get; set; }
        public string? ClipState { get; set; }
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
        /// <summary>Where each slot is on the canvas, when the layout has its own (the grid); otherwise the standard slots.</summary>
        public List<Box>? Boxes { get; set; }
        public DateTime Since { get; set; }

        public static int SlotsFor(string layout) => layout == "4up" ? 4 : layout == "2up" ? 2 : layout is "full" or "grid" or "break" ? 1 : 0;

        /// <summary>"LIVE (full)", "LIVE (2-up, slot 1)", "LIVE (quad, slot 2)", "REPLAY", or null when not on.</summary>
        public string? Label(string lobby)
        {
            int i = Slots.FindIndex(s => string.Equals(s, lobby, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            return Layout switch
            {
                "full" => "LIVE (full)",
                "2up" => $"LIVE (2-up, slot {i + 1})",
                "4up" => $"LIVE (quad, slot {i + 1})",
                "grid" => $"LIVE (grid, tile {i + 1})",
                "break" => "LIVE (sponsor break)",
                "replay" => "REPLAY",
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
        /// <summary>Every lobby's recent screen positions, for replays.</summary>
        public Tracks Tracks { get; } = new Tracks();
        /// <summary>A new card (replays save a clip for kills from here).</summary>
        public event Action<Card>? CardMade;

        /// <summary>Called to put lobbies on stream (Part 4 drives OBS here). Gets the new on-air state.</summary>
        public Action<OnAir>? Switch { get; set; }

        /// <summary>The tournament's players: real names for everyone in the lobbies.</summary>
        public Roster Roster { get; }

        /// <summary>Every finished game (the stats database), standings and storyline notes.</summary>
        public GameArchive Archive { get; }
        public Tables Tables { get; }
        public Storylines Storylines { get; }
        /// <summary>Sponsors, where they show, and the log of every appearance.</summary>
        public SponsorBook Sponsors { get; }
        /// <summary>The tournament's scored game records (the organiser view's shared results).</summary>
        public Func<IReadOnlyList<GameRecord>>? ExternalGames { get; set; }
        public Func<int> Advance { get; set; } = () => 5;
        public Func<int> GamesPerRound { get; set; } = () => 3;

        /// <summary>The scored games standings come from: the tournament's, plus the simulator's in simulation mode.</summary>
        public IReadOnlyList<GameRecord> Games
        {
            get
            {
                var list = new List<GameRecord>(ExternalGames?.Invoke() ?? Array.Empty<GameRecord>());
                FeedSimulator? sim;
                lock (_lock) sim = _sim;
                if (sim != null) list.AddRange(sim.Games);
                return list;
            }
        }

        public CasterDesk(string? configPath, Func<DateTime>? clock = null, PriorityConfig? config = null, string? rosterPath = null, string? dataFolder = null)
        {
            Roster = new Roster(rosterPath);
            Archive = new GameArchive(dataFolder == null ? null : System.IO.Path.Combine(dataFolder, "broadcast-games"), clock);
            Tables = new Tables(() => Games, Roster, () => Advance(), () => GamesPerRound());
            Storylines = new Storylines(Archive, () => Tables, dataFolder == null ? null : System.IO.Path.Combine(dataFolder, "notes-state.json"));
            _clock = clock ?? (() => DateTime.UtcNow);
            Sponsors = new SponsorBook(dataFolder, _clock);
            if (config != null) _config = () => config;
            else
            {
                _file = configPath == null ? null : new PriorityConfigFile(configPath);
                _config = () => _file?.Refresh() ?? new PriorityConfig();
            }
            Board = new LobbyBoard(_config, _clock) { RosterName = (key, discord, name) => Roster.Match(key, discord, name).Entry?.Name };
            Board.PlayChanged += OnPlay;
            if (clock == null) _tick = new Timer(_ => { try { Tick(); } catch (Exception) { } }, null, 1000, 1000);
        }

        public string? ConfigPath => _file?.Path;
        public bool Simulating => _sim != null;

        // ---- Feed in ------------------------------------------------------------------------

        public void Apply(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                Apply(doc.RootElement);
            }
            catch (Exception) { /* one bad message doesn't stop the rest */ }
        }

        public void Apply(JsonElement item)
        {
            try
            {
                string lobby = item.TryGetProperty("lobby", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "";
                if (lobby.Length > 0 && item.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number) Tracks.Arrived(lobby, t.GetInt64(), _clock());
                if (item.TryGetProperty("type", out var type) && type.GetString() == "track")
                {
                    if (lobby.Length > 0) Tracks.Add(lobby, item, _clock());
                    return;
                }
                Board.Apply(item);
                if (lobby.Length > 0) Archive.Feed(lobby, item, p => WhoIs(lobby, p));
            }
            catch (Exception) { }
        }

        private void OnPlay(string lobby, Play play)
        {
            var c = _config();
            var rule = c.Rule(play.Rule);
            // Cards only for what's worth a look (medium and up); the ranking still counts the rest.
            if (!rule.On || rule.Points < c.Tiers.Medium) return;
            var now = _clock();
            var live = Board.Lobby(lobby);
            Card? made = null;
            lock (_lock)
            {
                string key = lobby + "|" + play.Key + "|" + play.At.Ticks;
                if (!_byPlay.TryGetValue(key, out var card))
                {
                    card = made = new Card { Id = "c" + (++_cardSeq), Lobby = lobby, Rule = play.Rule, At = play.At, PlayKey = key };
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
                if (card.ShownAt == null && _onAir.Has(lobby) && _onAir.Layout != "replay")
                {
                    card.ShownAt = now;
                    card.ShownHow = _onAir.Label(lobby);
                }
            }
            if (made != null) CardMade?.Invoke(made);
            // A MUST SHOW play cuts a sponsor break short: straight back to full screen on it.
            if (made != null && made.Tier == "must" && OnAir.Layout == "break") Show(lobby);
        }

        /// <summary>Notes a card's replay clip.</summary>
        public void SetClip(string cardId, string clipId, string state)
        {
            lock (_lock)
            {
                var card = _cards.FirstOrDefault(c => c.Id == cardId) ?? _history.FirstOrDefault(c => c.Id == cardId);
                if (card == null) return;
                card.ClipId = clipId;
                card.ClipState = state;
            }
        }

        /// <summary>A replay of <paramref name="lobby"/> is on stream.</summary>
        public void ReplayOn(string lobby)
        {
            lock (_lock) _onAir = new OnAir { Layout = "replay", Slots = new List<string?> { lobby }, By = "button", Since = _clock() };
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
                Roster.Extra.Clear();
                if (on) Roster.Extra.AddRange(FeedSimulator.SimRoster());
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

        /// <summary>A player object from a lobby's message as (key, real name, colour), for the stats database.</summary>
        private (string Key, string Name, int Color)? WhoIs(string lobby, JsonElement p)
        {
            var l = Board.Lobby(lobby);
            int id = p.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : -1;
            string fallback = p.TryGetProperty("display", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : p.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
            int color = p.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            string key = l != null && l.People.TryGetValue(id, out var lp) && lp.Key.Length > 0 ? lp.Key : p.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "name:" + fallback.ToLowerInvariant();
            return (key, l == null ? fallback : Board.DisplayName(l, id, fallback), color);
        }

        /// <summary>Roster names for a lobby's players (player key → name), for its referee's nameplates and events.</summary>
        public Dictionary<string, string> NamesFor(string lobby)
        {
            var names = new Dictionary<string, string>();
            var l = Board.Lobby(lobby);
            if (l == null) return names;
            foreach (var p in l.People.Values.ToList())
                if (p.Key.Length > 0 && Roster.Match(p.Key, p.Discord, p.Name).Entry is { } e) names[p.Key] = e.Name;
            return names;
        }

        /// <summary>Players in the lobbies on stream: their notes come first.</summary>
        public HashSet<string> FocusKeys()
        {
            var keys = new HashSet<string>();
            foreach (var lobby in OnAir.Slots.Where(x => x != null))
                if (Board.Lobby(lobby!) is { } l) foreach (var p in l.People.Values) keys.Add(p.Key);
            return keys;
        }

        /// <summary>Standings and storyline notes for the caster tab.</summary>
        public object StoryState()
        {
            int round = Tables.CurrentRound;
            var rows = StandingsScope == "overall" || round == 0 ? Tables.Overall() : Tables.Round(round);
            return new
            {
                Scope = StandingsScope,
                Round = round,
                Standings = rows.Take(20).ToList(),
                Notes = Storylines.Notes(FocusKeys()).Select(n => new { n.Id, n.Kind, n.Text, n.Pinned }).ToList(),
                Shown = ShownNote?.Text,
                Games = Archive.Today().Count,
            };
        }

        /// <summary>Everyone in every lobby and who they are on the roster, for the caster tab.</summary>
        public object RosterState()
        {
            var entries = Roster.Entries;
            var lobbies = Board.Ranking().Select(r => new
            {
                r.Lobby,
                Players = r.People.Select(p =>
                {
                    var (e, how) = Roster.Match(p.Key, p.Discord, p.Name);
                    return new { p.Id, p.Name, p.Color, p.Key, p.Discord, Roster = e?.Name, Say = e?.Pronunciation, How = how.ToString(), p.Imp, p.Dead };
                }).ToList(),
            }).ToList();
            return new
            {
                Path = Roster.Path,
                Problem = Roster.Problem,
                Count = entries.Count,
                Names = entries.Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                Unmatched = lobbies.Sum(l => l.Players.Count(p => p.Roster == null)),
                Lobbies = lobbies,
            };
        }

        /// <summary>A spectator view command for a simulated lobby (real lobbies get it over VDO.Ninja from the tab).</summary>
        public bool SimSpec(string lobby, string command) => _sim?.Spec(lobby, command) ?? false;

        // ---- On stream ------------------------------------------------------------------------

        public OnAir OnAir { get { lock (_lock) return _onAir; } }

        /// <summary>
        /// Puts a lobby on stream. Full screen by default; 2-up or 4-up put it in the given slot (or
        /// the first) and fill the other slots with the top-ranked lobbies.
        /// </summary>
        public OnAir Show(string lobby, string layout = "full", int? slot = null, IList<string>? slots = null)
        {
            if (layout == "grid") return ShowGrid();
            layout = OnAir.SlotsFor(layout) > 0 && layout != "break" ? layout : "full";
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
            Left(next);
            Switch?.Invoke(next);
            return next;
        }

        // ---- Sponsors on stream -----------------------------------------------------------------

        /// <summary>The split-screen sponsor break on now: who, until when, and the lobby kept on the left.</summary>
        public (Sponsor Sponsor, DateTime Until, string? Lobby)? Break { get; private set; }
        private readonly Dictionary<int, Sponsor> _gridSponsors = new Dictionary<int, Sponsor>();

        /// <summary>
        /// A split-screen sponsor break: the lobby (the one on now, or the top one) shrinks to the left,
        /// the sponsor's video or logo fills the right. Ends by itself; a MUST SHOW play ends it at once.
        /// </summary>
        public string StartBreak(string? sponsorName = null, string? lobby = null)
        {
            var list = Sponsors.Sponsors.Where(x => x.Placements.Contains("break")).ToList();
            var sp = sponsorName != null ? list.FirstOrDefault(x => string.Equals(x.Name, sponsorName, StringComparison.OrdinalIgnoreCase)) : Sponsors.Next("break");
            if (sp == null) return list.Count == 0 ? "No sponsor has the \"break\" placement: add it in sponsors.json." : "No such sponsor.";
            lobby ??= OnAir.Slots.FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? Board.Ranking().FirstOrDefault(r => r.Online)?.Lobby;
            OnAir next;
            lock (_lock)
            {
                next = new OnAir { Layout = "break", Slots = new List<string?> { lobby }, By = "button", Since = _clock() };
                _onAir = next;
                MarkShown(next);
                Break = (sp, _clock().AddSeconds(Math.Max(5, sp.BreakSeconds)), lobby);
            }
            Sponsors.EndAll("grid:");
            Sponsors.Begin("break", sp, "break", lobby);
            Switch?.Invoke(next);
            return $"Sponsor break: {sp.Name} for {Math.Max(5, sp.BreakSeconds)}s.";
        }

        /// <summary>Sponsors for the grid's empty tiles (tile number → sponsor), while the grid is on.</summary>
        public Dictionary<int, Sponsor> GridSponsors { get { lock (_lock) return new Dictionary<int, Sponsor>(_gridSponsors); } }

        /// <summary>Whatever was on before has gone: close its sponsor appearances.</summary>
        private void Left(OnAir next)
        {
            if (next.Layout != "break" && Break != null) { Break = null; Sponsors.End("break"); }
            if (next.Layout != "grid") { lock (_lock) _gridSponsors.Clear(); Sponsors.EndAll("grid:"); }
            else
            {
                // Empty tiles take turns between the grid's sponsors.
                Sponsors.EndAll("grid:");
                lock (_lock)
                {
                    _gridSponsors.Clear();
                    for (int i = 0; i < next.Slots.Count; i++)
                        if (next.Slots[i] == null && Sponsors.Next("grid") is { } sp) _gridSponsors[i + 1] = sp;
                }
                foreach (var (tile, sp) in GridSponsors) Sponsors.Begin("grid:" + tile, sp, "grid");
            }
        }

        // ---- The grid: every active lobby at once ----------------------------------------------

        /// <summary>Lobbies in the grid: everyone sending and in a lobby or a game, in name order.</summary>
        public List<string> GridLobbies() => Board.Ranking().Where(r => r.Online && r.Phase != "menu").Select(r => r.Lobby)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>The grid on stream: one tile per active lobby, empty tiles left for sponsors or the logo.</summary>
        public OnAir ShowGrid(string by = "button")
        {
            var lobbies = GridLobbies();
            var (cols, rows) = ObsDirector.GridShape(lobbies.Count);
            var slots = lobbies.Cast<string?>().ToList();
            while (slots.Count < cols * rows) slots.Add(null);
            OnAir next;
            lock (_lock)
            {
                next = new OnAir { Layout = "grid", Slots = slots, By = by, Since = _clock() };
                _onAir = next;
                MarkShown(next);
            }
            Left(next);
            Switch?.Invoke(next);
            return next;
        }

        // ---- What the caster puts on stream from the tab ---------------------------------------

        /// <summary>The storyline note on stream (a lower third), and when it went up.</summary>
        public (string Text, DateTime At)? ShownNote { get; set; }
        /// <summary>Standings on stream: "round" (this round, every lobby) or "overall".</summary>
        public string StandingsScope { get; set; } = "round";

        /// <summary>Use the grid by itself whenever no lobby is mid-game (all in meetings or between games).</summary>
        public bool AutoGrid { get; set; }
        private OnAir? _beforeGrid;
        private Timer? _tick;

        /// <summary>Once a second: keeps the grid's tiles matching the active lobbies, and runs auto grid.</summary>
        private DateTime _nextSlowTick;

        public void Tick()
        {
            if (_clock() >= _nextSlowTick)
            {
                _nextSlowTick = _clock().AddSeconds(5);
                var games = Games;
                Archive.Merge(games);
                Tables.Update(Board.Ranking().Select(r => r.Lobby), _clock());
            }
            OnAir air;
            lock (_lock) air = _onAir;
            if (air.Layout == "replay") return;
            if (air.Layout == "break")
            {
                if (Break is { } b && _clock() >= b.Until) Show(b.Lobby ?? "");
                return;
            }
            var ranking = Board.Ranking().Where(r => r.Online && r.Phase != "menu").ToList();
            bool quiet = ranking.Count > 0 && ranking.All(r => r.Phase != "ingame");
            if (air.Layout == "grid")
            {
                if (AutoGrid && _beforeGrid != null && !quiet)
                {
                    var back = _beforeGrid;
                    _beforeGrid = null;
                    if (back.Layout is "full" or "2up" or "4up") Show("", back.Layout, null, back.Slots.Select(x => x ?? "").ToList());
                    else if (Board.Ranking().FirstOrDefault(r => r.Online) is { } top) Show(top.Lobby);
                    return;
                }
                var want = GridLobbies();
                var have = air.Slots.Where(x => x != null).ToList();
                if (!want.SequenceEqual(have!, StringComparer.OrdinalIgnoreCase)) ShowGrid(air.By);
                return;
            }
            if (AutoGrid && quiet && air.Layout != "none")
            {
                _beforeGrid = air;
                ShowGrid("auto");
            }
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
            Left(state);
        }

        /// <summary>Live cards of lobbies that just went on air count as shown from now.</summary>
        private void MarkShown(OnAir state)
        {
            var now = _clock();
            if (state.Layout == "replay") return;
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
                    Clip = x.ClipId == null ? null : new { Id = x.ClipId, State = x.ClipState },
                    Offline = online.TryGetValue(x.Lobby, out var on) && !on,
                };
                return new
                {
                    Simulating,
                    ConfigPath,
                    Problem = _file?.Problem,
                    AutoGrid,
                    Break = Break is { } br ? new { Sponsor = br.Sponsor.Name, Left = Math.Max(0, (int)Math.Ceiling((br.Until - now).TotalSeconds)), br.Lobby } : null,
                    OnAir = new { _onAir.Layout, _onAir.Slots, _onAir.By, _onAir.Scene, Since = _onAir.Since == default ? null : _onAir.Since.ToString("o") },
                    Lobbies = ranking.Select(r => new
                    {
                        r.Lobby, r.Online, r.Score, r.Tier, r.Line, r.Phase, r.Crew, r.Imps, r.TaskPct, r.Game, r.Round, r.Spec,
                        Players = r.Players.Select(p => new
                        {
                            Id = p.TryGetProperty("id", out var i) ? i.GetInt32() : 0,
                            Name = p.TryGetProperty("name", out var n) ? n.GetString() : "",
                            Colour = p.TryGetProperty("colorName", out var c) ? c.GetString() : "",
                            Imp = p.TryGetProperty("imp", out var im) && im.ValueKind == JsonValueKind.True,
                        }).ToList(),
                        OnAir = _onAir.Label(r.Lobby),
                    }).ToList(),
                    Cards = _cards.OrderByDescending(x => x.Value).ThenByDescending(x => x.Updated).Select(View).ToList(),
                    History = _history.OrderByDescending(x => x.At).Take(60).Select(View).ToList(),
                };
            }
        }

        public void Dispose()
        {
            _tick?.Dispose();
            lock (_lock) { _simTimer?.Dispose(); _simTimer = null; _sim = null; }
        }
    }
}
