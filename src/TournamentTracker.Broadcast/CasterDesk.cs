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
        /// <summary>A card from the desk itself (a lobby down or back, a game interrupted), not a play: kept until <see cref="Expires"/>.</summary>
        public bool System { get; set; }
        public DateTime Expires { get; set; } = DateTime.MaxValue;
        /// <summary>An interrupted game waiting for the caster's decision (its id).</summary>
        public string? Interruption { get; set; }
        /// <summary>A button on a desk card: "show:LJ" puts that lobby on (e.g. chat's pick for the next lobby).</summary>
        public string? Action { get; set; }
    }

    /// <summary>What's on stream: one lobby full screen, two side by side, or four.</summary>
    public sealed class OnAir
    {
        /// <summary>"full", "2up", "4up", "grid", "break", "intermission", "slate" (be right back), "replay" or "none".</summary>
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
                "intermission" => null,
                "slate" => null,
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
    public sealed partial class CasterDesk : IDisposable
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
        /// <summary>Banners for plays in lobbies that aren't on screen.</summary>
        public AlertQueue Alerts { get; }
        private readonly HashSet<string> _alerted = new HashSet<string>();
        private readonly Dictionary<string, (JsonElement Data, DateTime At)> _voice = new Dictionary<string, (JsonElement, DateTime)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Each lobby's voice as its referee's Button reports it: sending, Discord and Among Us found, levels, mic.</summary>
        public List<object> VoiceState()
        {
            var now = _clock();
            var lobbies = Board.Ranking().Select(r => r.Lobby).ToList();
            lock (_lock)
                return lobbies.Concat(_voice.Keys.Where(k => !lobbies.Contains(k, StringComparer.OrdinalIgnoreCase))).Select(l =>
                {
                    if (!_voice.TryGetValue(l, out var v)) return (object)new { Lobby = l, Online = false, Reported = false };
                    var d = v.Data;
                    string? S(string p) => d.TryGetProperty(p, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                    double N(string p) => d.TryGetProperty(p, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : -60;
                    bool B(string p) => d.TryGetProperty(p, out var x) && x.ValueKind == JsonValueKind.True;
                    return new
                    {
                        Lobby = l, Online = (now - v.At).TotalSeconds < 5, Reported = true,
                        On = B("on"), Sending = B("sending"), Problem = S("problem"),
                        Discord = S("discord"), Game = S("game"), VoiceDb = N("voiceDb"), GameDb = N("gameDb"), Mic = B("mic"),
                        Ago = Math.Max(0, (int)(now - v.At).TotalSeconds),
                    };
                }).ToList();
        }
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
                // Interrupted games stay out of the standings until the caster counts them.
                var held = HeldGames();
                if (held.Count > 0) list.RemoveAll(g => held.Contains((g.Name, g.Round)));
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
            HealthPath = dataFolder == null ? null : System.IO.Path.Combine(dataFolder, HealthSettings.FileName);
            HealthConfig = HealthSettings.Load(HealthPath);
            Health = new LobbyHealth(_clock, () => HealthConfig);
            Sponsors = new SponsorBook(dataFolder, _clock) { Hold = () => Simulating };
            Alerts = new AlertQueue(_clock, dataFolder == null ? null : System.IO.Path.Combine(dataFolder, "alerts.json"));
            Graphics = new GraphicsQueue(_clock);
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

        // ---- One fixed order: each lobby's number, given when it first connects ------------------
        private readonly Dictionary<string, int> _numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A lobby's number (1, 2, 3… in the order they connected; the lowest free one). Kept for the session.</summary>
        public int NumberOf(string lobby)
        {
            lock (_numbers)
            {
                if (_numbers.TryGetValue(lobby, out int n)) return n;
                n = 1;
                while (_numbers.ContainsValue(n)) n++;
                _numbers[lobby] = n;
                return n;
            }
        }

        /// <summary>Lobbies in their fixed order (by number), never by score: what's on the desk doesn't move.</summary>
        public List<LobbyRank> Ordered() => Board.Ranking().OrderBy(r => NumberOf(r.Lobby)).ToList();
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
                string type = item.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() ?? "" : "";
                long? t = item.TryGetProperty("t", out var tt) && tt.ValueKind == JsonValueKind.Number && tt.GetInt64() > 0 ? tt.GetInt64() : null;
                var now = _clock();
                // Sent again after a drop: each message once, in order (Part 22).
                if (lobby.Length > 0 && !FirstTime(lobby, item)) return;
                // How up to date the host's mod is (the broadcast feed's version).
                if (lobby.Length > 0 && TournamentTracker.Broadcast.FeedProtocol.FromMod(type)) lock (_lock) _versions[lobby] = TournamentTracker.Broadcast.FeedProtocol.VersionOf(item);
                if (type == "skip") return;
                if (lobby.Length > 0 && type is "snap" or "event") NumberOf(lobby);
                if (type == TournamentTracker.Broadcast.FeedProtocol.Types.Host)
                {
                    string? tw = item.TryGetProperty("twitch", out var th) && th.ValueKind == JsonValueKind.String ? th.GetString() : null;
                    if (lobby.Length > 0) lock (_lock) { if (TwitchHandle(tw) is { } h) _twitch[lobby] = h; else _twitch.Remove(lobby); }
                    return;
                }
                if (type == "health")
                {
                    if (lobby.Length > 0 && item.TryGetProperty("video", out var vs) && vs.ValueKind == JsonValueKind.String) Health.Video(lobby, vs.GetString() ?? "unknown");
                    return;
                }
                bool resent = item.TryGetProperty("re", out var re) && re.ValueKind is JsonValueKind.True or JsonValueKind.Number;
                if (lobby.Length > 0 && t is { } ms)
                {
                    Tracks.Arrived(lobby, ms, now);
                    if (type != "voice") Health.Data(lobby, ms, resent);
                }
                // Older than a few seconds (held back by a drop, or very late): it still counts for the
                // stats with its own time, but nothing shows it as live.
                DateTime? at = lobby.Length > 0 && t is { } ms2 ? Tracks.ToCaster(lobby, ms2) : null;
                bool stale = at is { } a0 && (now - a0).TotalSeconds > HealthConfig.StaleSeconds;
                if (type == "voice")
                {
                    if (lobby.Length > 0 && !stale)
                    {
                        lock (_lock) _voice[lobby] = (item.Clone(), now);
                        bool B(string p) => item.TryGetProperty(p, out var x) && x.ValueKind == JsonValueKind.True;
                        double N(string p) => item.TryGetProperty(p, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : -60;
                        string? problem = item.TryGetProperty("problem", out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString() : null;
                        Health.Audio(lobby, B("on") && B("sending"), N("voiceDb"), N("gameDb"), B("on") ? problem : null);
                    }
                    return;
                }
                if (type == "track")
                {
                    if (lobby.Length > 0 && !stale) Tracks.Add(lobby, item, now);
                    return;
                }
                if (stale)
                {
                    if (lobby.Length > 0 && type == "event")
                    {
                        Archive.Feed(lobby, item, p => WhoIs(lobby, p), at);
                        TrackGame(lobby, item, at ?? now);
                    }
                    return;
                }
                Board.Apply(item);
                if (lobby.Length > 0)
                {
                    Archive.Feed(lobby, item, p => WhoIs(lobby, p));
                    TrackGame(lobby, item, now);
                    if (type == "event") Fed?.Invoke(lobby, item);
                }
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
            // Off-screen banners: once per play (a meeting once its caller is known).
            var air = OnAir;
            if (AlertQueue.KindOf(play.Rule, play.Text) is { } kind && !(air.Has(lobby) && air.Layout != "replay"))
            {
                string once = lobby + "|" + play.Key + "|" + play.At.Ticks + "|" + kind;
                bool fresh;
                lock (_lock) { fresh = _alerted.Add(once); if (_alerted.Count > 2000) _alerted.Clear(); }
                if (fresh) Alerts.Push(lobby, kind, play.Text);
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

        /// <summary>A lobby made up by the simulator (SIM-1, SIM-2…).</summary>
        public static bool IsSimLobby(string? lobby) => FeedSimulator.IsSim(lobby);

        /// <summary>
        /// Simulation stopped and the desk is clean: everything else made for its fake lobbies (OBS
        /// sources, clips, montages, Twitch) goes now. Gets which lobbies were fake.
        /// </summary>
        public event Action<Func<string, bool>>? SimStopped;

        /// <summary>
        /// Simulation mode: four fake lobbies (SIM-1…SIM-4) feed the desk, so the tab and switching can
        /// be tried without games. Turning it off removes every trace of them: lobbies, cards, history,
        /// alerts, health, voice, games and standings (on disk too), and whatever on stream was theirs.
        /// Nothing in OBS is switched: a scene only changes when you click.
        /// </summary>
        public void Simulate(bool on)
        {
            bool was;
            lock (_lock)
            {
                was = _sim != null;
                _simTimer?.Dispose();
                _simTimer = null;
                _sim = on ? new FeedSimulator(_clock(), 4, Environment.TickCount) : null;
                Roster.Extra.Clear();
                if (on) Roster.Extra.AddRange(FeedSimulator.SimRoster());
                if (_sim != null) _simTimer = new Timer(_ => SimTick(), null, 0, 500);
            }
            if (!on && was) ForgetSim();
        }

        /// <summary>Removes everything the simulator left (public for tests).</summary>
        public void ForgetSim()
        {
            Func<string, bool> sim = IsSimLobby;
            Board.Forget(sim);
            lock (_numbers) foreach (var k in _numbers.Keys.Where(sim).ToList()) _numbers.Remove(k);
            Tracks.Forget(sim);
            Health.Forget(sim);
            Alerts.Forget(sim);
            Archive.Forget(sim);
            Tables.Forget(sim);
            lock (_lock)
            {
                foreach (var c in _cards.Where(c => sim(c.Lobby)).ToList()) { _cards.Remove(c); _byPlay.Remove(c.PlayKey); }
                _history.RemoveAll(c => sim(c.Lobby));
                _alerted.RemoveWhere(k => sim(k.Split('|')[0]));
                foreach (var d in new System.Collections.IDictionary[] { _voice, _twitch, _versions, _levels, _downCards, _playing })
                    foreach (var k in d.Keys.Cast<string>().Where(sim).ToList()) d.Remove(k);
                foreach (var k in _received.Keys.Where(k => sim(k.Split('|')[0])).ToList()) _received.Remove(k);
                _interruptions.RemoveAll(i => sim(i.Lobby));
                _offerDismissed.RemoveWhere(x => sim(x));
                // Whatever on stream was theirs: the desk forgets it, OBS stays where it is.
                if (_onAir.Slots.Any(x => sim(x ?? "")))
                {
                    var left = _onAir.Slots.Where(x => x != null && !sim(x)).ToList();
                    _onAir = left.Count == 0 && _onAir.Layout is not ("intermission" or "slate")
                        ? new OnAir { Layout = "none", Scene = _onAir.Scene, By = "button", Since = _clock() }
                        : new OnAir { Layout = _onAir.Layout, Slots = _onAir.Slots.Select(x => x != null && sim(x) ? null : x).ToList(), Scene = _onAir.Scene, By = _onAir.By, Boxes = _onAir.Boxes, Since = _onAir.Since };
                }
                if (_beforeIntermission != null && _beforeIntermission.Slots.Any(x => sim(x ?? ""))) _beforeIntermission = null;
                if (_beforeGrid != null && _beforeGrid.Slots.Any(x => sim(x ?? ""))) _beforeGrid = null;
                _gridSponsors.Clear();
            }
            if (LiveDuringIntermission != null && sim(LiveDuringIntermission)) LiveDuringIntermission = null;
            if (Break is { } b && sim(b.Lobby ?? "")) { Break = null; Sponsors.End("break"); }
            if (PlayerCard is { } pc && sim(pc.Lobby ?? "")) PlayerCard = null;
            Graphics.Remove(g => g.Lobby != null && sim(g.Lobby) || g.Kind == "storyline");
            lock (_lock) { _prompts.RemoveAll(x => sim(x.Lobby)); _savedTables.RemoveAll(x => sim(x.Lobby)); }
            if (_featured != null && sim(_featured)) _featured = null;
            // A storyline or note from the fake games may be up: it goes too.
            ShownNote = null;
            IntermissionOffer = false;
            _quietSince = null;
            _nextSlowTick = default;
            SimStopped?.Invoke(sim);
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

        /// <summary>The players a card is about (the key player first), for its Card buttons.</summary>
        private List<object> CardPeople(Card card)
        {
            var list = new List<object>();
            if (card.Source is not { } src || src.ValueKind != JsonValueKind.Object) return list;
            var seen = new HashSet<string>();
            foreach (var prop in new[] { "killer", "impostor", "caller", "ejected", "player", "witness", "victim", "body", "crewmate" })
                if (src.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object && WhoIs(card.Lobby, p) is { } w && seen.Add(w.Key))
                    list.Add(new { w.Key, w.Name, w.Color });
            return list;
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
            var table = StandingsTable();
            var focus = FocusKeys();
            return new
            {
                Scope = StandingsScope,
                Round = round,
                Title = table.Title,
                Standings = table.Rows.Take(20).ToList(),
                Notes = Storylines.Notes(focus).Select(n => new { n.Id, n.Kind, n.Text, n.Pinned, Focus = n.Players.Any(focus.Contains) }).ToList(),
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
        public OnAir Show(string lobby, string layout = "full", int? slot = null, IList<string>? slots = null, string by = "button")
        {
            if (layout == "grid") return ShowGrid(by);
            if (layout == "intermission") return ShowIntermission(by);
            if (layout == "slate") return ShowSlate(by);
            layout = OnAir.SlotsFor(layout) > 0 && layout != "break" ? layout : "full";
            int n = OnAir.SlotsFor(layout);
            var ranked = Board.Ranking().Where(r => r.Online && !IsDown(r.Lobby)).Select(r => r.Lobby).ToList();
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
                next = new OnAir { Layout = layout, Slots = filled, By = by, Since = _clock() };
                _onAir = next;
                MarkShown(next);
            }
            Left(next);
            Switch?.Invoke(next);
            return next;
        }

        // ---- Intermission (Part 19A) -----------------------------------------------------------

        /// <summary>When the next round starts (set in the tab), for the countdown.</summary>
        public DateTime? NextRoundAt { get; set; }
        /// <summary>Intermission by itself when every lobby has been out of a game for a while, and back when one starts.</summary>
        public bool AutoIntermission { get; set; }
        /// <summary>How long every lobby must be out of a game before intermission is offered (or comes up).</summary>
        public double IntermissionAfterSeconds { get; set; } = 45;
        /// <summary>Every lobby is between games: the tab offers intermission.</summary>
        public bool IntermissionOffer { get; private set; }
        /// <summary>During intermission: a lobby went live (the tab says so; auto mode goes back by itself).</summary>
        public string? LiveDuringIntermission { get; private set; }
        /// <summary>A montage waiting to be played (shown as "up next" in intermission).</summary>
        public (string Id, string Title)? QueuedMontage { get; set; }
        private DateTime? _quietSince;
        private OnAir? _beforeIntermission;
        private readonly HashSet<string> _offerDismissed = new HashSet<string>();

        public OnAir ShowIntermission(string by = "button")
        {
            OnAir next;
            lock (_lock)
            {
                if (_onAir.Layout is not ("intermission" or "replay" or "none" or "slate")) _beforeIntermission = _onAir;
                next = new OnAir { Layout = "intermission", Slots = new List<string?>(), By = by, Since = _clock() };
                _onAir = next;
                IntermissionOffer = false;
                LiveDuringIntermission = null;
            }
            Left(next);
            Switch?.Invoke(next);
            return next;
        }

        /// <summary>Out of intermission: the lobby that went live, else what was on before, else the top lobby.</summary>
        public OnAir EndIntermission()
        {
            string? live = LiveDuringIntermission;
            var back = _beforeIntermission;
            _beforeIntermission = null;
            LiveDuringIntermission = null;
            if (live != null) return Show(live);
            if (back != null && back.Layout is "full" or "2up" or "4up") return Show("", back.Layout, null, back.Slots.Select(x => x ?? "").ToList());
            if (back?.Layout == "grid") return ShowGrid();
            return Show(Board.Ranking().FirstOrDefault(r => r.Online && !IsDown(r.Lobby))?.Lobby ?? "");
        }

        public void DismissIntermissionOffer() { IntermissionOffer = false; _quietSince = _clock(); }

        /// <summary>Once a second: offers (or, in auto mode, starts) intermission between rounds, and notices a lobby going live during it.</summary>
        private bool IntermissionTick(OnAir air)
        {
            var now = _clock();
            var online = Board.Ranking().Where(r => r.Online).ToList();
            var playing = online.Where(r => r.Phase is "ingame" or "meeting").ToList();
            if (air.Layout == "intermission")
            {
                if (playing.Count == 0) { LiveDuringIntermission = null; return true; }
                LiveDuringIntermission ??= playing.OrderByDescending(r => r.Score).First().Lobby;
                if (AutoIntermission && air.By == "auto") EndIntermission();
                return true;
            }
            if (online.Count == 0 || playing.Count > 0) { _quietSince = null; IntermissionOffer = false; return false; }
            _quietSince ??= now;
            if ((now - _quietSince.Value).TotalSeconds < IntermissionAfterSeconds || air.Layout is "replay" or "break") return false;
            if (AutoIntermission) { ShowIntermission("auto"); return true; }
            IntermissionOffer = true;
            return false;
        }

        // ---- Win counter (Part 20) and player cards (Part 21) ------------------------------------

        /// <summary>"today" or "round".</summary>
        public string WinScope { get; set; } = "today";

        public (int Impostors, int Crew) Wins()
        {
            var games = WinScope == "round" ? Archive.InRound(Archive.Games.Select(g => g.Round).DefaultIfEmpty(0).Max()) : Archive.Today();
            return (games.Count(g => g.Winner == "Impostors"), games.Count(g => g.Winner == "Crewmates"));
        }

        /// <summary>The player card on stream: who, from which lobby, and since when.</summary>
        public (string Key, string? Lobby, DateTime At, bool Auto)? PlayerCard { get; private set; }
        public double PlayerCardSeconds { get; set; } = 6;
        /// <summary>A tile narrower than this (canvas pixels) is too small for a card on it.</summary>
        public double PlayerCardMinTile { get; set; } = 760;

        /// <summary>
        /// A player's card as a lower third, through the graphics queue (one big graphic at a time):
        /// on the lobby's tile in a multi-view, waiting while that lobby isn't on screen or its tile is
        /// too small. During a replay or montage, its own key player's card goes straight on.
        /// </summary>
        public string ShowPlayerCard(string key, string? lobby, bool auto = false)
        {
            var air = OnAir;
            lobby ??= Board.Ranking().FirstOrDefault(r => r.People.Any(p => p.Key == key))?.Lobby;
            if (auto && air.Layout == "replay")
            {
                PlayerCard = (key, lobby, _clock(), auto);
                return "Player card on stream.";
            }
            string name = lobby != null && Board.Lobby(lobby) is { } live && live.People.Values.FirstOrDefault(p => p.Key == key) is { } lp ? Board.DisplayName(live, lp.Id, lp.Name) : key;
            var g = Graphics.Add("playerCard", lobby, $"{NameTag.Plain(name)}'s card", key);
            var cur = Graphics.Current();
            return cur?.Id == g.Id ? "Player card on stream." : Graphics.CanShow(g) ? "Player card queued: it goes on after what's showing." : $"Player card queued: it goes on when {lobby} is on screen with room for it.";
        }

        /// <summary>The card still showing, or null once its time is up (a replay's own card, else the queue's).</summary>
        public (string Key, string? Lobby, DateTime At, bool Auto)? CardNow()
        {
            if (OnAir.Layout == "replay")
                return PlayerCard is { } c && (_clock() - c.At).TotalSeconds < PlayerCardSeconds ? c : null;
            return Graphics.Current() is { Kind: "playerCard" } g && g.Data is string key ? (key, g.Lobby, g.Started ?? _clock(), false) : null;
        }

        public void HidePlayerCard() { PlayerCard = null; Graphics.Remove("playerCard"); }

        // ---- Big graphics: one at a time; after-game tables asked about first (Part 14) ----------

        /// <summary>Tables, player cards and storyline notes take turns on stream.</summary>
        public GraphicsQueue Graphics { get; }
        /// <summary>After a game: the lobby's table goes straight into the queue (the old way) instead of asking.</summary>
        public Func<bool> AfterGameAuto { get; set; } = () => false;
        /// <summary>The after-game table is switched on in Graphics at all.</summary>
        public Func<bool> AfterGameOn { get; set; } = () => true;
        public sealed class AfterGamePrompt { public string Id = ""; public string Lobby = ""; public int Round; public object? Data; public DateTime At; }
        private readonly List<AfterGamePrompt> _prompts = new List<AfterGamePrompt>();
        private readonly List<AfterGamePrompt> _savedTables = new List<AfterGamePrompt>();
        private DateTime _changeSeen;
        private long _promptSeq;
        /// <summary>How long the "show table?" prompt waits before it counts as Skip.</summary>
        public double PromptSeconds { get; set; } = 60;

        /// <summary>A lobby finished a game and its table changed: ask (or, the old way, queue it).</summary>
        private void AfterGameTick()
        {
            if (Tables.Change is not { } change || Tables.ChangeAt == _changeSeen) goto Expire;
            _changeSeen = Tables.ChangeAt;
            if (!AfterGameOn()) goto Expire;
            var el = System.Text.Json.JsonSerializer.SerializeToElement(change);
            string lobby = el.TryGetProperty("lobby", out var l) ? l.GetString() ?? "" : "";
            int round = el.TryGetProperty("round", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Number ? r.GetInt32() : 0;
            if (AfterGameAuto()) Graphics.Add("afterGame", lobby, $"{lobby} after the game", change);
            else lock (_lock)
            {
                _prompts.RemoveAll(x => string.Equals(x.Lobby, lobby, StringComparison.OrdinalIgnoreCase));
                _prompts.Add(new AfterGamePrompt { Id = "p" + (++_promptSeq), Lobby = lobby, Round = round, Data = change, At = _clock() });
            }
        Expire:
            var now = _clock();
            lock (_lock) _prompts.RemoveAll(x => (now - x.At).TotalSeconds >= PromptSeconds);     // unanswered: skipped
            // Saved for intermission: they play once it's on.
            if (OnAir.Layout is "intermission" or "slate")
            {
                List<AfterGamePrompt> play;
                lock (_lock) { play = _savedTables.ToList(); _savedTables.Clear(); }
                foreach (var p in play) Graphics.Add("afterGame", p.Lobby, $"{p.Lobby} after the game (saved)", p.Data);
            }
        }

        /// <summary>The caster's answer to "show table?": show, skip or save (for intermission).</summary>
        public string AnswerPrompt(string id, string answer)
        {
            AfterGamePrompt? p;
            lock (_lock)
            {
                p = _prompts.FirstOrDefault(x => x.Id == id);
                if (p == null) return "That table has gone.";
                _prompts.Remove(p);
                if (answer == "save") _savedTables.Add(p);
            }
            switch (answer)
            {
                case "show":
                    var g = Graphics.Add("afterGame", p.Lobby, $"{p.Lobby} after the game", p.Data);
                    return Graphics.CanShow(g) ? $"{p.Lobby}'s table is in the queue." : $"{p.Lobby}'s table goes on when {p.Lobby} is on screen with room for it.";
                case "save": return $"{p.Lobby}'s table plays in the next intermission.";
                default: return "Skipped.";
            }
        }

        public object PromptState()
        {
            var now = _clock();
            lock (_lock)
                return new
                {
                    Prompts = _prompts.Select(x => new { x.Id, x.Lobby, x.Round, Left = Math.Max(0, (int)Math.Ceiling(PromptSeconds - (now - x.At).TotalSeconds)) }).ToList(),
                    Saved = _savedTables.Select(x => x.Lobby).ToList(),
                };
        }

        /// <summary>Everything a player card says: name, rank and points, and today's records.</summary>
        public object? PlayerCardData(string key, string? lobby)
        {
            int round = Tables.CurrentRound;
            var rows = round > 0 ? Tables.Round(round) : Tables.Overall();
            var row = rows.FirstOrDefault(r => r.Key == key);
            var today = Archive.Today();
            var mine = today.Where(g => g.Winner != null && g.P(key) != null).ToList();
            var imp = mine.Where(g => g.P(key)!.Imp).ToList();
            var crew = mine.Where(g => !g.P(key)!.Imp).ToList();
            string? name = row?.Name; int? color = row?.Color;
            var live = lobby != null ? Board.Lobby(lobby) : null;
            var lp = live?.People.Values.FirstOrDefault(p => p.Key == key);
            if (lp != null && live != null) { name = Board.DisplayName(live, lp.Id, lp.Name); color = lp.Color; }
            var ap = today.Select(g => g.P(key)).LastOrDefault(p => p != null);
            name ??= ap?.Name;
            color ??= ap?.Color;
            if (name == null) return null;
            var entry = Roster.Match(key, lp?.Discord, lp?.Name ?? name).Entry;
            return new
            {
                key, lobby, name, color = color ?? 0,
                pronunciation = entry?.Pronunciation,
                rank = row?.Rank, points = row?.Points, round, of = rows.Count,
                advancing = row?.Advancing,
                imp = new { w = imp.Count(g => g.P(key)!.Won == true), l = imp.Count(g => g.P(key)!.Won != true) },
                crew = new { w = crew.Count(g => g.P(key)!.Won == true), l = crew.Count(g => g.P(key)!.Won != true) },
                kills = today.Sum(g => g.P(key)?.Kills ?? 0),
            };
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
            foreach (var l in next.Slots) if (l != null && next.Layout != "replay") Alerts.Seen(l);
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

        /// <summary>
        /// Lobbies in the grid: everyone sending and in a lobby or a game, in name order. A lobby that
        /// drops keeps its tile (RECONNECTING) for a while, then the rest close up.
        /// </summary>
        public List<string> GridLobbies() => Board.Ranking().Where(r => r.Phase != "menu" && (r.Online ? !DownLong(r.Lobby) : Reconnecting(r.Lobby) && !DownLong(r.Lobby))).Select(r => r.Lobby)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// Exactly these lobbies on stream, picked in the Multiview card: one full screen, two side by
        /// side, four in the quad, three or five and more in the grid (in the order they were picked).
        /// </summary>
        public OnAir ShowPicked(IList<string> lobbies, string by = "button")
        {
            var pick = lobbies.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return pick.Count switch
            {
                0 => OnAir,
                1 => Show(pick[0], "full", null, null, by),
                2 => Show("", "2up", null, pick, by),
                4 => Show("", "4up", null, pick, by),
                _ => ShowGrid(by, pick),
            };
        }

        /// <summary>The grid on stream: one tile per active lobby (or the ones picked), empty tiles left for sponsors or the logo.</summary>
        public OnAir ShowGrid(string by = "button", IList<string>? pick = null)
        {
            var lobbies = pick?.ToList() ?? GridLobbies();
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
        /// <summary>
        /// What the standings show: "lobby" (the lobby on stream, its own round: lobbies can be in
        /// different rounds), "round" (that round, every lobby) or "overall" (the whole tournament).
        /// </summary>
        public string StandingsScope { get; set; } = "lobby";

        /// <summary>The lobby the standings follow: the one full screen, or slot 1 of a multi-view (else the last one).</summary>
        public string? FeaturedLobby
        {
            get
            {
                var on = OnAir.Slots.FirstOrDefault(x => x != null);
                if (on != null) _featured = on;
                return _featured;
            }
        }
        private string? _featured;

        /// <summary>The standings table for <see cref="StandingsScope"/>: its title, round and rows.</summary>
        public (string Title, int Round, string? Lobby, List<TableRow> Rows) StandingsTable()
        {
            int latest = Tables.CurrentRound;
            string? lobby = FeaturedLobby;
            int round = lobby != null && Board.Lobby(lobby) is { Round: > 0 } live ? live.Round : latest;
            if (StandingsScope == "overall" || round == 0) return ("Tournament standings", 0, null, Tables.Overall());
            if (StandingsScope == "round" || lobby == null) return ($"Round {round} · every lobby", round, null, Tables.Round(round));
            var (r, rows) = Tables.LobbyTable(lobby, round);
            return ($"{lobby} · round {r}{(r < round ? " final" : "")}", r, lobby, rows);
        }

        // ---- Hosts' Twitch channels (from their Button: the "host" message) ----------------------
        private readonly Dictionary<string, string> _twitch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A lobby host's Twitch channel name, or null.</summary>
        public string? TwitchOf(string lobby) { lock (_lock) return _twitch.TryGetValue(lobby, out var h) ? h : null; }

        public static string? TwitchHandle(string? s) => TournamentTracker.Broadcast.FeedProtocol.TwitchHandle(s);

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
            AfterGameTick();
            if (HealthTick()) return;
            OnAir air;
            lock (_lock) air = _onAir;
            if (air.Layout is "replay" or "slate") return;
            if (IntermissionTick(air)) return;
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
                    else if (Board.Ranking().FirstOrDefault(r => r.Online && !IsDown(r.Lobby)) is { } top) Show(top.Lobby);
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
        /// <summary>What's on stream changed without a switch from here (in OBS): the caster pages follow.</summary>
        public event Action<OnAir>? AirChangedInObs;

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
            AirChangedInObs?.Invoke(state);
        }

        /// <summary>OBS made the switch the desk asked for (or showed it's on what the desk has): note its scene.</summary>
        public void ObsConfirmed(OnAir air, string scene)
        {
            lock (_lock) if (ReferenceEquals(_onAir, air) || _onAir.Layout == air.Layout && _onAir.Slots.SequenceEqual(air.Slots)) _onAir.Scene = scene;
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
                    if (card.System)
                    {
                        if (now < card.Expires && !card.Dismissed) continue;
                        _cards.Remove(card);
                        _history.Insert(0, card);
                        continue;
                    }
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
                    People = CardPeople(x),
                    Offline = online.TryGetValue(x.Lobby, out var on) && !on,
                    x.System,
                    x.Interruption,
                    x.Action,
                };
                return new
                {
                    Simulating,
                    ConfigPath,
                    Problem = _file?.Problem,
                    AutoGrid,
                    Health = HealthState(),
                    Intermission = new { On = _onAir.Layout == "intermission", Offer = IntermissionOffer, Auto = AutoIntermission, Live = LiveDuringIntermission, NextRoundAt = NextRoundAt?.ToString("o"), Montage = QueuedMontage?.Title },
                    WinScope, Wins = new { Impostors = Wins().Impostors, Crew = Wins().Crew },
                    Break = Break is { } br ? new { Sponsor = br.Sponsor.Name, Left = Math.Max(0, (int)Math.Ceiling((br.Until - now).TotalSeconds)), br.Lobby } : null,
                    OnAir = new { _onAir.Layout, _onAir.Slots, _onAir.By, _onAir.Scene, Since = _onAir.Since == default ? null : _onAir.Since.ToString("o") },
                    Lobbies = ranking.OrderBy(r => NumberOf(r.Lobby)).Select(r => new
                    {
                        No = NumberOf(r.Lobby),
                        People = r.People.Where(p => p.Key.Length > 0).Select(p => { var l = Board.Lobby(r.Lobby); return new { p.Key, Name = l == null ? p.Name : Board.DisplayName(l, p.Id, p.Name), p.Color }; }).ToList(),
                        r.Lobby, r.Online, r.Score, r.Tier, r.Line, r.Phase, r.Crew, r.Imps, r.TaskPct, r.Game, r.Round, r.Spec,
                        Players = r.Players.Select(p => new
                        {
                            Id = p.TryGetProperty("id", out var i) ? i.GetInt32() : 0,
                            Name = p.TryGetProperty("name", out var n) ? n.GetString() : "",
                            Colour = p.TryGetProperty("colorName", out var c) ? c.GetString() : "",
                            Imp = p.TryGetProperty("imp", out var im) && im.ValueKind == JsonValueKind.True,
                        }).ToList(),
                        OnAir = _onAir.Label(r.Lobby),
                        Health = Health.Status(r.Lobby).Level,
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
