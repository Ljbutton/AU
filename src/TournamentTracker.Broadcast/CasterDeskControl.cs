using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// What's staged to go on stream next (vMix's Preview): the lobbies in order and the layout picked
    /// for them. Kept here rather than in the desk page, so the Live desk and the Stream Deck share one
    /// Preview and it survives a page reload. Never changed in place: each change makes a new one.
    /// </summary>
    public sealed class PreviewState
    {
        /// <summary>The layout picked for it ("full", "2up", "4up", "grid", "cams"), or null: by how many lobbies.</summary>
        public string? Chosen { get; init; }
        public IReadOnlyList<string> Slots { get; init; } = Array.Empty<string>();
        /// <summary>
        /// Auto grid's own suggestion: "grid" (no lobby is mid-game) or "back" (a game started while its grid
        /// is on). Null: the caster's own Preview, which auto grid never changes.
        /// </summary>
        public string? Auto { get; init; }
        /// <summary>What the desk says about auto grid's suggestion ("All lobbies quiet: Grid is in Preview…"), or null.</summary>
        public string? Note { get; init; }
        /// <summary>The layout it goes on in: the one picked, else 1 full screen, 2 side by side, 3–4 the quad, more the grid.</summary>
        public string Layout => Slots.Count == 0 ? "none" : Chosen ?? CasterDesk.LayoutFor(Slots.Count);
        public bool Has(string lobby) => Slots.Contains(lobby, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The desk's switching controls shared by the Live desk and the Stream Deck: Preview and Take, the
    /// automation hold, the player cameras (who each one follows, and picks armed for when play
    /// resumes), and lobby numbers that stay the same across restarts.
    /// </summary>
    public sealed partial class CasterDesk
    {
        /// <summary>
        /// Something the Stream Deck shows changed (Preview, the hold, a camera pick, what's on stream):
        /// its waiting state requests answer at once instead of at their next look.
        /// </summary>
        public event Action? Changed;
        private void Touch()
        {
            try { Changed?.Invoke(); } catch (Exception) { }
        }

        /// <summary>A view's name as the Stream Deck and the desk page say it, as the desk's layout: quad is "4up", one or more cameras "cams".</summary>
        public static string? NormLayout(string? layout) => (layout ?? "").Trim().ToLowerInvariant() switch
        {
            "full" => "full",
            "2up" or "2-up" => "2up",
            "quad" or "4up" => "4up",
            "grid" => "grid",
            "cam" or "cams" => "cams",
            _ => null,
        };

        // ---- Preview and Take --------------------------------------------------------------------

        private PreviewState _preview = new PreviewState();

        public PreviewState Preview { get { lock (_lock) return _preview; } }

        /// <summary>Preview is this lobby alone (the layout goes back to "by how many").</summary>
        public PreviewState PreviewOnly(string lobby)
        {
            lock (_lock) _preview = new PreviewState { Slots = new[] { lobby } };
            Touch();
            return Preview;
        }

        /// <summary>Adds a lobby to Preview (to build a 2-up or quad one lobby at a time), or takes it out if it's in.</summary>
        public PreviewState PreviewAdd(string lobby)
        {
            lock (_lock)
            {
                var slots = _preview.Slots.ToList();
                int at = slots.FindIndex(x => string.Equals(x, lobby, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) slots.RemoveAt(at); else slots.Add(lobby);
                _preview = new PreviewState { Slots = slots, Chosen = slots.Count == 0 ? null : _preview.Chosen };
            }
            Touch();
            return Preview;
        }

        /// <summary>
        /// Preview in this layout. With nothing staged it takes the lobbies on stream now (every active
        /// lobby for the grid), so one press and Take puts what's on in another layout.
        /// </summary>
        public PreviewState PreviewLayout(string layout)
        {
            string? norm = NormLayout(layout);
            if (norm == null) return Preview;
            List<string>? seed = null;
            if (Preview.Slots.Count == 0)
                seed = norm == "grid" ? GridLobbies() : OnAir.Slots.Where(x => x != null).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            lock (_lock) _preview = new PreviewState { Slots = seed ?? _preview.Slots.ToList(), Chosen = norm };
            Touch();
            return Preview;
        }

        public void PreviewClear()
        {
            lock (_lock) _preview = new PreviewState();
            Touch();
        }

        /// <summary>
        /// Preview on stream (vMix's Take): in <paramref name="asLayout"/> when given, else the layout picked
        /// for it, else by how many lobbies. Preview is empty afterwards. Null when nothing was staged.
        /// Auto grid's suggestion goes on the same way: this is the caster's switch.
        /// </summary>
        public OnAir? Take(string? asLayout = null, string? transition = null, string by = "deck")
        {
            PreviewState staged;
            OnAir before;
            lock (_lock) { staged = _preview; before = _onAir; }
            if (staged.Slots.Count == 0) return null;
            var air = ShowPicked(staged.Slots.ToList(), by, NormLayout(asLayout) ?? staged.Chosen, transition);
            lock (_lock)
            {
                if (ReferenceEquals(_preview, staged)) _preview = new PreviewState();
                if (ReferenceEquals(_autoPick, staged)) AutoTaken(before, air);
            }
            Touch();
            return air;
        }

        /// <summary>A lobby's tally light: "pgm" on stream (red), "pvw" staged in Preview (green), else "none".</summary>
        public static string Tally(string lobby, OnAir air, PreviewState preview) =>
            air.Layout != "replay" && air.Has(lobby) ? "pgm" : preview.Has(lobby) ? "pvw" : "none";

        // ---- Hold: no switch by itself ------------------------------------------------------------

        private bool _hold;
        private DateTime? _holdUntil;
        private string? _holdWhy;

        /// <summary>An automatic switch that the hold stopped (shown on the desk as a toast), and when.</summary>
        public (string Text, DateTime At)? HeldBack { get; private set; }

        /// <summary>
        /// Automatic switching paused: auto intermission, the camera going back to the map, auto switch
        /// off a lobby that dropped, the grid closing up, a MUST SHOW play ending a sponsor break, the
        /// break timer, armed cameras and where-the-action-is all wait, and auto grid puts nothing in
        /// Preview (its suggestion comes out). The caster's own switches still go through.
        /// </summary>
        public bool Held
        {
            get { lock (_lock) return HoldOn; }
        }

        /// <summary>Held now: until released, or until a time still to come. Under the desk's lock.</summary>
        private bool HoldOn => _hold || _holdUntil is { } u && _clock() < u;

        /// <summary>Holds automatic switching (for a while, or until released), or lets it go again.</summary>
        public void SetHold(bool on, TimeSpan? forHowLong = null, string? why = null)
        {
            lock (_lock)
            {
                _hold = on && forHowLong == null;
                _holdUntil = on && forHowLong is { } t ? _clock() + t : null;
                _holdWhy = on ? why : null;
                if (!on) HeldBack = null;
            }
            Touch();
        }

        /// <summary>Why switching is held ("Panic", "Held from the Stream Deck"…); null while it isn't.</summary>
        public string? HoldWhy
        {
            get { lock (_lock) return HoldOn ? _holdWhy : null; }
        }

        /// <summary>The hold as it is now (until released, until a time, or off) and why, to put back exactly with <see cref="RestoreHold"/>.</summary>
        public (bool Forever, DateTime? Until, string? Why) HoldNow()
        {
            lock (_lock)
            {
                bool timed = !_hold && _holdUntil is { } u && _clock() < u;
                return (_hold, timed ? _holdUntil : null, _hold || timed ? _holdWhy : null);
            }
        }

        /// <summary>Puts back a hold <see cref="HoldNow"/> saved: until released, until the same time (none if that has passed), or off.</summary>
        public void RestoreHold((bool Forever, DateTime? Until, string? Why) hold)
        {
            lock (_lock)
            {
                _hold = hold.Forever;
                _holdUntil = hold.Forever ? null : hold.Until;
                bool on = HoldOn;
                _holdWhy = on ? hold.Why : null;
                if (!on) HeldBack = null;
            }
            Touch();
        }

        /// <summary>True (and noted for the desk) when the hold stops an automatic switch.</summary>
        private bool HoldBack(string what)
        {
            if (!Held) return false;
            var now = _clock();
            if (HeldBack is not { } last || last.Text != what || (now - last.At).TotalSeconds > 10) { HeldBack = (what, now); Touch(); }
            return true;
        }

        public object HoldState()
        {
            lock (_lock)
            {
                bool on = HoldOn;
                return new
                {
                    On = on,
                    Until = on ? _holdUntil?.ToString("o") : null,
                    Why = on ? _holdWhy : null,
                    HeldBack = on && HeldBack is { } h ? new { h.Text, At = h.At.ToString("o") } : null,
                };
            }
        }

        // ---- Player cameras: who each follows, and picks armed for when play resumes ----------------

        /// <summary>
        /// A panic is on in full (set by the app): its be-right-back screen, its hold and the voice muted.
        /// The desk's Panic button and hold pill undo it while it is; once any of it was put right by hand
        /// they're Panic and Hold again (the next Panic puts it all back on).
        /// </summary>
        public Func<bool> PanicOn { get; set; } = () => false;

        /// <summary>Player cameras are on for every lobby (Settings); off, no camera pick is sent again.</summary>
        public Func<bool> CamsOn { get; set; } = () => true;
        // Who each lobby's camera should follow (null: it picks by itself), for which game: ids are reused.
        private readonly Dictionary<string, (int? Pick, string? Game)> _camWant = new Dictionary<string, (int?, string?)>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Want, DateTime At)> _camSent = new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);
        // A camera pick waiting for its lobby to be back in play (a meeting, between games): it goes on stream then.
        private readonly Dictionary<string, (int? Pick, string? Game, string? Transition)> _armed = new Dictionary<string, (int?, string?, string?)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The game a camera pick is for: the one being played, or null for one made between games
        /// (it's for the next game, and is tied to it once it starts).
        /// </summary>
        private static string? GameOf(LobbyLive? l) => l != null && l.Phase is "ingame" or "meeting" ? l.Game : null;

        /// <summary>A pick made for <paramref name="game"/> still means the same players in the lobby now.</summary>
        private static bool StillFor(string? game, LobbyLive? l) => game == null || l == null || GameOf(l) == null || GameOf(l) == game;

        /// <summary>Who a lobby's camera should follow, as picked here (null: it picks by itself, or nothing was picked this game).</summary>
        public int? CamPick(string lobby)
        {
            var live = Board.Lobby(lobby);
            lock (_lock) return _camWant.TryGetValue(lobby, out var w) && StillFor(w.Game, live) ? w.Pick : null;
        }

        /// <summary>
        /// Who a lobby's camera follows from now: sent to its host at once, and again every 2 s until the
        /// host says it has it (the command goes over the desk page's link and can be lost).
        /// </summary>
        public void WantCam(string lobby, int? pick)
        {
            string? game = GameOf(Board.Lobby(lobby));
            lock (_lock) { _camWant[lobby] = (pick, game); _camSent[lobby] = (CamCommand(pick), _clock()); }
            HostCommand?.Invoke(lobby, CamCommand(pick));
            Touch();
        }

        private static string CamCommand(int? pick) => pick.HasValue ? $"spec cam {pick.Value}" : "spec cam auto";

        /// <summary>Arms a pick: the lobby's camera goes on stream following them as soon as its game is back in play.</summary>
        public void Arm(string lobby, int? pick, string? transition = null)
        {
            string? game = GameOf(Board.Lobby(lobby));
            lock (_lock) _armed[lobby] = (pick, game, transition);
            Touch();
        }

        /// <summary>Takes back an armed pick; true when there was one.</summary>
        public bool Disarm(string lobby)
        {
            bool had;
            lock (_lock) had = _armed.Remove(lobby);
            if (had) Touch();
            return had;
        }

        /// <summary>Armed cameras a switch by hand cancelled (said once on the desk), and when.</summary>
        public (string Text, DateTime At)? ArmedDropped { get; private set; }

        /// <summary>
        /// A switch by hand (the desk, the Stream Deck) is the caster's choice: cameras armed for lobbies it
        /// doesn't show are dropped, so one doesn't cut over it when its lobby's play resumes. Said on the desk.
        /// </summary>
        private void DropArmed(OnAir next)
        {
            List<string> dropped;
            lock (_lock)
            {
                dropped = _armed.Keys.Where(l => !next.Slots.Any(s => string.Equals(s, l, StringComparison.OrdinalIgnoreCase))).ToList();
                foreach (var l in dropped) _armed.Remove(l);
                if (dropped.Count > 0) ArmedDropped = ($"Armed camera for {string.Join(", ", dropped)} cancelled: you switched by hand", _clock());
            }
            if (dropped.Count > 0) Touch();
        }

        /// <summary>The pick armed for a lobby (Pick null: the camera picks by itself), or null when none is.</summary>
        public (int? Pick, bool Armed) Armed(string lobby)
        {
            lock (_lock) return _armed.TryGetValue(lobby, out var a) ? (a.Pick, true) : (null, false);
        }

        /// <summary>A lobby's player camera as its host last reported it: is it sending one, and who it follows.</summary>
        public static (bool On, int? Following) CamOf(JsonElement? spec) => spec is not { } s ? (false, null)
            : (s.Flag("cam"), s.Int("camOn"));

        /// <summary>A lobby's camera as the Live desk shows it.</summary>
        public object CamView(string lobby, JsonElement? spec)
        {
            var (on, following) = CamOf(spec);
            var armed = Armed(lobby);
            return new { On = on, Following = following, Pick = armed.Armed ? armed.Pick : CamPick(lobby), Armed = armed.Armed };
        }

        /// <summary>
        /// Once a second: a camera pick the host hasn't taken (its snapshot still says otherwise) is
        /// sent again, at most every 2 s; picks from a game that has ended are forgotten (player ids
        /// are used again in the next game).
        /// </summary>
        private void CamFollowTick()
        {
            List<(string Lobby, int? Pick, string? Game)> wants;
            lock (_lock) wants = _camWant.Select(kv => (kv.Key, kv.Value.Pick, kv.Value.Game)).ToList();
            var now = _clock();
            foreach (var (lobby, pick, game) in wants)
            {
                var live = Board.Lobby(lobby);
                if (live == null) continue;
                if (!StillFor(game, live)) { lock (_lock) { _camWant.Remove(lobby); _camSent.Remove(lobby); } continue; }
                // A pick made between games belongs to the game that has now started.
                if (game == null && GameOf(live) is { } started) lock (_lock) if (_camWant.TryGetValue(lobby, out var w) && w.Game == null) _camWant[lobby] = (w.Pick, started);
                if (!CamsOn() || live.Spec is not JsonElement spec || !spec.TryGetProperty("camPick", out var hp)) continue;     // an older host doesn't say
                int? has = hp.ValueKind == JsonValueKind.Number ? hp.GetInt32() : null;
                if (has == pick && CamOf(spec).On) continue;
                string want = CamCommand(pick);
                lock (_lock)
                {
                    if (_camSent.TryGetValue(lobby, out var last) && last.Want == want && (now - last.At).TotalSeconds < 2) continue;
                    _camSent[lobby] = (want, now);
                }
                HostCommand?.Invoke(lobby, want);
            }
        }

        /// <summary>
        /// Once a second: an armed camera whose lobby is back in play goes on stream (not over a replay,
        /// the be-right-back screen, intermission or a sponsor break, and not while switching is held). A
        /// pick from a game that has ended is dropped, and so is one for a lobby the caster switched away
        /// from by hand (<see cref="DropArmed"/>).
        /// </summary>
        private bool ArmedTick(OnAir air)
        {
            List<(string Lobby, int? Pick, string? Game, string? Transition)> armed;
            lock (_lock) armed = _armed.Select(kv => (kv.Key, kv.Value.Pick, kv.Value.Game, kv.Value.Transition)).ToList();
            foreach (var (lobby, pick, game, transition) in armed)
            {
                var live = Board.Lobby(lobby);
                if (live == null) continue;
                if (!StillFor(game, live)) { Disarm(lobby); continue; }      // a new game: those ids mean other players now
                if (live.Phase != "ingame" || !CamsOn()) continue;
                if (HoldBack($"{lobby}'s armed camera")) continue;
                Disarm(lobby);
                if (CamPick(lobby) != pick) WantCam(lobby, pick);
                bool among = air.Layout == "cams" && air.Slots.Any(s => string.Equals(s, lobby, StringComparison.OrdinalIgnoreCase));
                if (!among) Show(lobby, "cam", null, null, "armed", transition);
                return true;
            }
            return false;
        }

        // ---- Lobby numbers that stay the same --------------------------------------------------------

        public const string NumbersFile = "lobby-numbers.json";
        private sealed class Numbered { public int No { get; set; } public DateTime Seen { get; set; } }
        private readonly string? _numbersPath;
        private Dictionary<string, Numbered> _numbered = new Dictionary<string, Numbered>(StringComparer.OrdinalIgnoreCase);
        /// <summary>A label not seen for this long gives its number up (to a new lobby that needs one).</summary>
        public static TimeSpan NumberKeep { get; set; } = TimeSpan.FromHours(18);
        private static readonly JsonSerializerOptions NumbersJson = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

        private void LoadNumbers()
        {
            try
            {
                if (_numbersPath != null && File.Exists(_numbersPath) && JsonSerializer.Deserialize<Dictionary<string, Numbered>>(File.ReadAllText(_numbersPath), NumbersJson) is { } map)
                    _numbered = new Dictionary<string, Numbered>(map.Where(kv => kv.Value.No > 0), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception) { }
        }

        private void SaveNumbers()
        {
            if (_numbersPath == null) return;
            try
            {
                Dictionary<string, Numbered> copy;
                lock (_numbered) copy = _numbered.Where(kv => !IsSimLobby(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                Files.WriteJson(_numbersPath, copy, NumbersJson);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// A lobby's number: the same one every time (kept in lobby-numbers.json, so a Stream Deck's
        /// "Lobby 3" key still means the same lobby after a restart). A new lobby gets the number at the
        /// end of its label when that's free ("Lobby 3" is 3), else the lowest free one.
        /// </summary>
        public int NumberOf(string lobby)
        {
            bool save = false;
            int n;
            var now = _clock();
            lock (_numbered)
            {
                if (_numbered.TryGetValue(lobby, out var known))
                {
                    if ((now - known.Seen).TotalHours >= 1) { known.Seen = now; save = true; }
                    n = known.No;
                }
                else
                {
                    bool Taken(int no) => _numbered.Any(kv => kv.Value.No == no && now - kv.Value.Seen < NumberKeep);
                    var m = Regex.Match(lobby, @"(\d{1,3})\s*$");
                    n = m.Success && int.TryParse(m.Groups[1].Value, out var want) && want > 0 && !Taken(want) ? want : 0;
                    if (n == 0) { n = 1; while (Taken(n)) n++; }
                    // A label not seen for a long time gives its number up.
                    foreach (var old in _numbered.Where(kv => kv.Value.No == n).Select(kv => kv.Key).ToList()) _numbered.Remove(old);
                    _numbered[lobby] = new Numbered { No = n, Seen = now };
                    save = true;
                }
            }
            if (save && !IsSimLobby(lobby)) SaveNumbers();
            return n;
        }

        /// <summary>The lobby with this number, among those given (online ones, say), or null.</summary>
        public string? LobbyNumbered(int no, IEnumerable<string> among) => among.FirstOrDefault(l => NumberOf(l) == no);

        /// <summary>The highest number a lobby can be given by hand.</summary>
        public const int MaxNumber = 99;

        /// <summary>Every lobby holding a number (seen in the last <see cref="NumberKeep"/>), by number, and whether it's sending now: for Settings → Stream Deck.</summary>
        public List<(string Lobby, int No, bool Online)> Numbers()
        {
            var ranking = Board.Ranking();
            foreach (var r in ranking) NumberOf(r.Lobby);              // every lobby on the board has one
            var online = new HashSet<string>(ranking.Where(r => r.Online).Select(r => r.Lobby), StringComparer.OrdinalIgnoreCase);
            var now = _clock();
            lock (_numbered)
                return _numbered.Where(kv => now - kv.Value.Seen < NumberKeep).OrderBy(kv => kv.Value.No).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => (kv.Key, kv.Value.No, online.Contains(kv.Key))).ToList();
        }

        /// <summary>
        /// Numbers given by hand (Settings → Stream Deck), lobby → number (1 to <see cref="MaxNumber"/>). A
        /// number another lobby holds is swapped: that lobby takes this one's old number. Saved in
        /// lobby-numbers.json, so the Stream Deck's lobby keys mean these lobbies from now on.
        /// </summary>
        public void SetNumbers(IEnumerable<(string Lobby, int No)> want)
        {
            var now = _clock();
            foreach (var (lobby, no) in want)
            {
                if (string.IsNullOrWhiteSpace(lobby) || no < 1 || no > MaxNumber) continue;
                int old = NumberOf(lobby);
                if (old == no) continue;
                lock (_numbered)
                {
                    foreach (var other in _numbered.Where(kv => kv.Value.No == no && !string.Equals(kv.Key, lobby, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).ToList())
                    {
                        other.No = old;
                        other.Seen = now;
                    }
                    _numbered[lobby] = new Numbered { No = no, Seen = now };
                }
            }
            SaveNumbers();
            Touch();
        }

        /// <summary>
        /// The online lobbies numbered 1, 2, 3… in their order now (the start of a day or a round with new
        /// lobbies); every other lobby gives its number up, and gets the lowest free one if it comes back.
        /// The lobbies, in their new order.
        /// </summary>
        public List<string> RenumberOnline()
        {
            var online = Board.Ranking().Where(r => r.Online).Select(r => r.Lobby).OrderBy(NumberOf).ToList();
            var now = _clock();
            lock (_numbered)
            {
                _numbered.Clear();
                for (int i = 0; i < online.Count; i++) _numbered[online[i]] = new Numbered { No = i + 1, Seen = now };
            }
            SaveNumbers();
            Touch();
            return online;
        }
    }
}
