using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>A game cut short by a referee's crash or restart: kept out of the standings until the caster decides.</summary>
    public sealed class Interruption
    {
        public string Id { get; set; } = "";
        public string Lobby { get; set; } = "";
        public string Game { get; set; } = "";
        public int Round { get; set; }
        public DateTime At { get; set; }
        public string Why { get; set; } = "";
        /// <summary>null (waiting), "count", "void" or "replay".</summary>
        public string? Decision { get; set; }
        public string? CardId { get; set; }
    }

    /// <summary>
    /// Part 22, backup for lobby drops: each lobby's health (green, yellow, red), what happens on stream
    /// when one goes down or comes back, messages sent again after a drop taken once and in order, and
    /// games cut short by a crash held back until the caster counts, voids or replays them.
    /// </summary>
    public sealed partial class CasterDesk
    {
        public HealthSettings HealthConfig { get; set; }
        /// <summary>Every live event from a lobby (not late ones), as it's taken: Twitch (Part 23) follows the games from here.</summary>
        public event Action<string, JsonElement>? Fed;
        /// <summary>A game was cut short (Twitch cancels its prediction).</summary>
        public event Action<Interruption>? Interrupted;
        /// <summary>health.json (thresholds and what happens), next to The Button's other files.</summary>
        public string? HealthPath { get; }
        public LobbyHealth Health { get; }

        // Per lobby and run of its mod: every message up to Base arrived; Above: later ones that came first.
        private sealed class Received
        {
            public long Base;
            public readonly SortedSet<long> Above = new SortedSet<long>();
            public DateTime GapSince;
            public DateTime Seen;
        }
        private readonly Dictionary<string, Received> _received = new Dictionary<string, Received>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"host needs update" for a lobby whose mod sends an older feed than this app expects, else null.</summary>
        public string? HostNeedsUpdate(string lobby)
        {
            lock (_lock) return _versions.TryGetValue(lobby, out var v) ? TournamentTracker.Broadcast.FeedProtocol.NeedsUpdate(v) : null;
        }

        private readonly Dictionary<string, string> _levels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Card> _downCards = new Dictionary<string, Card>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Game, int Round)> _playing = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Interruption> _interruptions = new List<Interruption>();
        private int _interruptionSeq;

        /// <summary>
        /// True the first time a message arrives. The referee's page sends each one again until the
        /// caster says it has it, so after a drop some arrive twice: those are dropped here.
        /// </summary>
        private bool FirstTime(string lobby, JsonElement item)
        {
            if (!item.TryGetProperty("src", out var sv) || sv.ValueKind != JsonValueKind.String || !item.TryGetProperty("seq", out var qv) || qv.ValueKind != JsonValueKind.Number) return true;
            long seq = qv.GetInt64();
            string key = lobby + "|" + sv.GetString();
            var now = _clock();
            lock (_lock)
            {
                if (!_received.TryGetValue(key, out var r)) _received[key] = r = new Received { Base = seq - 1 };
                r.Seen = now;
                if (seq <= r.Base || r.Above.Contains(seq)) return false;
                if (r.Above.Count == 0 && seq != r.Base + 1) r.GapSince = now;
                r.Above.Add(seq);
                while (r.Above.Count > 0 && r.Above.Min == r.Base + 1) { r.Base++; r.Above.Remove(r.Base); }
                // A gap that never fills (the referee's page lost those): stop waiting for it.
                if (r.Above.Count > 0 && (now - r.GapSince).TotalSeconds > 30) { r.Base = r.Above.Min; r.Above.Remove(r.Base); r.GapSince = now; while (r.Above.Count > 0 && r.Above.Min == r.Base + 1) { r.Base++; r.Above.Remove(r.Base); } }
                if (_received.Count > 200)
                    foreach (var old in _received.Where(x => (now - x.Value.Seen).TotalHours > 6).Select(x => x.Key).ToList()) _received.Remove(old);
                return true;
            }
        }

        /// <summary>What to tell each lobby's referee page it can stop sending again: run of the mod → last message had.</summary>
        public Dictionary<string, long> Acks(string lobby)
        {
            var now = _clock();
            lock (_lock)
                return _received.Where(x => x.Key.StartsWith(lobby + "|", StringComparison.OrdinalIgnoreCase) && (now - x.Value.Seen).TotalMinutes < 30)
                    .ToDictionary(x => x.Key.Substring(lobby.Length + 1), x => x.Value.Base);
        }

        public bool IsDown(string lobby) => Health.Lobbies.Contains(lobby, StringComparer.OrdinalIgnoreCase) && Health.Status(lobby).Level == "red";

        /// <summary>Down for longer than the grid keeps its tile.</summary>
        private bool DownLong(string lobby)
        {
            if (!Health.Lobbies.Contains(lobby, StringComparer.OrdinalIgnoreCase)) return false;
            var st = Health.Status(lobby);
            return st.DownSince is { } d && (_clock() - d).TotalSeconds >= HealthConfig.ReflowAfterSeconds;
        }

        /// <summary>Heard from recently enough that it's worth waiting for (its tile says RECONNECTING).</summary>
        private bool Reconnecting(string lobby) => Health.DataAge(lobby) is { } age && age < HealthConfig.DataRedSeconds + HealthConfig.ReflowAfterSeconds;

        /// <summary>Every lobby's health for the caster tab's panel, and the interrupted games.</summary>
        public object HealthState()
        {
            var now = _clock();
            var sim = _sim;
            var lobbies = Health.Lobbies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(l =>
            {
                var st = Health.Status(l);
                return new
                {
                    st.Lobby, st.Level, st.Problems, st.DataAge, st.Video, st.Audio, st.LagMs,
                    Down = st.DownSince is { } d ? (int)(now - d).TotalSeconds : (int?)null,
                    Sim = sim?.Failures(l),
                    Update = HostNeedsUpdate(l),
                };
            }).ToList();
            List<object> interrupted;
            lock (_lock) interrupted = _interruptions.OrderByDescending(i => i.At).Take(20).Select(i => (object)new { i.Id, i.Lobby, i.Game, i.Round, i.Why, i.Decision, At = i.At.ToString("o") }).ToList();
            return new { Lobbies = lobbies, Interrupted = interrupted, Settings = HealthConfig };
        }

        // ---- Cards from the desk ---------------------------------------------------------------

        private Card SystemCard(string lobby, string rule, string tier, string text, double value, double? seconds)
        {
            var now = _clock();
            lock (_lock)
            {
                var card = new Card
                {
                    Id = "c" + (++_cardSeq), Lobby = lobby, Rule = rule, Tier = tier, Text = text, At = now, Updated = now, Value = value,
                    System = true, Expires = seconds is { } s ? now.AddSeconds(s) : DateTime.MaxValue,
                };
                card.PlayKey = "sys|" + card.Id;
                _cards.Add(card);
                return card;
            }
        }

        /// <summary>A card from elsewhere in The Button (Twitch: chat's pick for the next lobby, with a button to put it on).</summary>
        public Card AddNotice(string lobby, string rule, string tier, string text, double value, double? seconds, string? action = null)
        {
            var card = SystemCard(lobby, rule, tier, text, value, seconds);
            card.Action = action;
            return card;
        }

        // ---- Games cut short ---------------------------------------------------------------------

        /// <summary>Follows each lobby's game from its start to its end, and notices one that never ended.</summary>
        private void TrackGame(string lobby, JsonElement m, DateTime at)
        {
            string type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            string? kind = m.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            string game = m.TryGetProperty("game", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : "";
            int round = m.TryGetProperty("round", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
            (string Game, int Round)? was;
            lock (_lock) was = _playing.TryGetValue(lobby, out var p) ? p : null;
            if (type == "event" && kind == "gameStart")
            {
                if (was is { } w && w.Game != game) Interrupt(lobby, w.Game, w.Round, "a new game started before it ended", at);
                lock (_lock) _playing[lobby] = (game, round);
            }
            else if (type == "event" && kind == "gameEnd")
            {
                lock (_lock) _playing.Remove(lobby);
                if (m.TryGetProperty("abandoned", out var ab) && ab.ValueKind == JsonValueKind.True && game.Length > 0)
                    Interrupt(lobby, game, round, "the referee's game was abandoned", at);
            }
            else if (type == "snap" && was is { } w2 && m.TryGetProperty("phase", out var ph) && ph.GetString() is "lobby" or "menu")
            {
                lock (_lock) _playing.Remove(lobby);
                Interrupt(lobby, w2.Game, w2.Round, "the referee's game restarted mid-game", at);
            }
        }

        private void Interrupt(string lobby, string game, int round, string why, DateTime at)
        {
            if (game.Length == 0) return;
            Interruption it;
            lock (_lock)
            {
                if (_interruptions.Any(x => x.Game == game && x.Round == round && x.Lobby.Equals(lobby, StringComparison.OrdinalIgnoreCase))) return;
                it = new Interruption { Id = "i" + (++_interruptionSeq), Lobby = lobby, Game = game, Round = round, At = at, Why = why };
                _interruptions.Add(it);
            }
            var card = SystemCard(lobby, "interrupted", "must", $"INTERRUPTED: {game} (round {round}): {why}. Not in the standings until you count, void or replay it.", 95, null);
            card.Interruption = it.Id;
            it.CardId = card.Id;
            Interrupted?.Invoke(it);
        }

        /// <summary>Games kept out of the standings: interrupted and not counted.</summary>
        private HashSet<(string Game, int Round)> HeldGames()
        {
            lock (_lock) return _interruptions.Where(i => i.Decision != "count").Select(i => (i.Game, i.Round)).ToHashSet();
        }

        public List<Interruption> Interruptions { get { lock (_lock) return _interruptions.ToList(); } }

        /// <summary>
        /// The caster's decision on an interrupted game: "count" (into the standings), "void" (posts !void
        /// for the tournament) or "replay" (void, and the lobby plays it again). Returns what to post (or
        /// null) and what to say.
        /// </summary>
        public (string? Command, string Message) Decide(string id, string decision)
        {
            Interruption? it;
            lock (_lock) it = _interruptions.FirstOrDefault(x => x.Id == id);
            if (it == null) return (null, "That game isn't on the list.");
            if (decision is not ("count" or "void" or "replay")) return (null, "Count, void or replay.");
            lock (_lock)
            {
                it.Decision = decision;
                if (it.CardId != null && _cards.FirstOrDefault(c => c.Id == it.CardId) is { } card) card.Expires = _clock();
            }
            _nextSlowTick = default;      // standings now
            switch (decision)
            {
                case "count":
                    bool has = Games.Any(g => g.Name == it.Game && g.Round == it.Round);
                    return (null, has ? $"{it.Game} counts: it's in the standings." : $"{it.Game} counts, but no result was saved for it (the referee's game didn't finish one), so the standings don't change.");
                case "void":
                    return ($"!void {it.Game} interrupted", $"{it.Game} voided.");
                default:
                    SystemCard(it.Lobby, "replayGame", "high", $"REPLAY: {it.Game} is void; {it.Lobby} plays it again.", 70, 120);
                    return ($"!void {it.Game} interrupted, to be replayed", $"{it.Game} voided; {it.Lobby} replays it.");
            }
        }

        // ---- What happens on stream ------------------------------------------------------------------

        /// <summary>
        /// Once a second: cards for lobbies going down and coming back, and (with auto switch on, even
        /// when switching by hand) moving off a lobby that's down. True when it changed what's on.
        /// </summary>
        private bool HealthTick()
        {
            var now = _clock();
            var s = HealthConfig;
            var wentDown = new List<string>();
            var cameBack = new List<string>();
            foreach (var lobby in Health.Lobbies)
            {
                var st = Health.Status(lobby);
                string was;
                lock (_lock) { was = _levels.TryGetValue(lobby, out var w) ? w : "green"; _levels[lobby] = st.Level; }
                if (st.Level == "red" && was != "red") wentDown.Add(lobby);
                else if (st.Level != "red" && was == "red") cameBack.Add(lobby);
            }
            foreach (var lobby in cameBack)
            {
                lock (_lock) if (_downCards.TryGetValue(lobby, out var dc)) { dc.Expires = now; _downCards.Remove(lobby); }
                SystemCard(lobby, "lobbyBack", "high", $"{lobby} is back.", 60, 60);
            }

            var air = OnAir;
            bool changed = false;
            string? moved = null;
            if (s.AutoSwitch)
            {
                switch (air.Layout)
                {
                    case "full" or "break" when air.Slots.FirstOrDefault() is { } on && IsDown(on):
                        moved = AwayFrom(on);
                        changed = true;
                        break;
                    case "2up" or "4up":
                    {
                        var live = air.Slots.Where(x => x != null).Select(x => x!).ToList();
                        if (live.Count > 0 && live.All(IsDown)) { moved = AwayFrom(live[0]); changed = true; break; }
                        // A tile down for a while: the next lobby takes its place.
                        var slots = air.Slots.ToList();
                        bool swap = false;
                        for (int i = 0; i < slots.Count; i++)
                        {
                            if (slots[i] is not { } l || !DownLong(l)) continue;
                            slots[i] = Board.Ranking().Where(r => r.Online && !IsDown(r.Lobby) && r.Phase != "menu" && !slots.Contains(r.Lobby, StringComparer.OrdinalIgnoreCase)).Select(r => r.Lobby).FirstOrDefault();
                            swap = true;
                        }
                        if (swap) { Show("", air.Layout, null, slots.Select(x => x ?? "").ToList(), "health"); changed = true; }
                        break;
                    }
                    case "grid":
                        if (air.Slots.Any(x => x != null) && GridLobbies().All(IsDown)) { moved = AwayFrom(null); changed = true; }
                        break;
                    case "slate" when air.By == "health":
                        // Something to show again: the top lobby if it's worth it, else the grid.
                        if (Board.Ranking().Any(r => r.Online && !IsDown(r.Lobby) && r.Phase != "menu")) { AwayFrom(null); changed = true; }
                        break;
                }
            }
            foreach (var lobby in wentDown)
            {
                var st = Health.Status(lobby);
                string why = string.Join(", ", st.Problems);
                string text = $"LOBBY {lobby} DOWN: {why}." + (moved != null && air.Has(lobby) ? $" Switched to {moved}." : "");
                var card = SystemCard(lobby, "lobbyDown", air.Has(lobby) ? "must" : "high", text, air.Has(lobby) ? 100 : 80, null);
                lock (_lock) { if (_downCards.TryGetValue(lobby, out var old)) old.Expires = now; _downCards[lobby] = card; }
            }
            return changed;
        }

        /// <summary>
        /// Off a lobby that's down: the next lobby worth showing, else the grid, else the "be right back"
        /// slate (or intermission when nobody was mid-game). Says where it went.
        /// </summary>
        private string AwayFrom(string? down)
        {
            var healthy = Board.Ranking().Where(r => r.Online && !IsDown(r.Lobby) && r.Phase != "menu" && !string.Equals(r.Lobby, down, StringComparison.OrdinalIgnoreCase)).ToList();
            var hot = healthy.FirstOrDefault(r => r.Tier is "must" or "veryHigh" or "high");
            if (hot != null) { Show(hot.Lobby, "full", null, null, "health"); return hot.Lobby; }
            if (healthy.Count > 0) { ShowGrid("health"); return "the grid"; }
            // Nobody was mid-game (between games or rounds): intermission says it better.
            bool midGame = Board.Ranking().Any(r => r.Phase is "ingame" or "meeting");
            if (HealthConfig.Slate && midGame)
            {
                if (OnAir.Layout != "slate") ShowSlate("health");
                return "the be-right-back screen";
            }
            if (OnAir.Layout != "intermission") ShowIntermission("health");
            return "intermission";
        }

        /// <summary>"Technical difficulties: be right back", with the standings and storylines taking turns.</summary>
        public OnAir ShowSlate(string by = "button")
        {
            OnAir next;
            lock (_lock)
            {
                next = new OnAir { Layout = "slate", Slots = new List<string?>(), By = by, Since = _clock() };
                _onAir = next;
            }
            Left(next);
            Switch?.Invoke(next);
            return next;
        }

        /// <summary>Removes a lobby from the health panel (its referee has gone for the day).</summary>
        public void ForgetHealth(string lobby)
        {
            Health.Forget(lobby);
            lock (_lock)
            {
                _levels.Remove(lobby);
                if (_downCards.TryGetValue(lobby, out var dc)) { dc.Expires = _clock(); _downCards.Remove(lobby); }
            }
        }

        /// <summary>Simulation: a lobby's video, audio or data drops, everything drops, lag, the referee crashes, or it reconnects.</summary>
        public bool SimFail(string lobby, string what)
        {
            var sim = _sim;
            if (sim == null) return false;
            lock (sim) return sim.Fail(lobby, what, _clock());
        }
    }
}
