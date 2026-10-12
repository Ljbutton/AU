using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// The player camera from the desk, and the Stream Deck: plain web links on this PC
    /// (http://127.0.0.1:8768/deck/…?key=…) that a Stream Deck key opens in the background (or the
    /// Red Alert Stream Deck plugin presses), one per action. The key is Red Alert's own (Settings →
    /// Stream Deck shows the links), kept across restarts so the Stream Deck's buttons keep working.
    /// </summary>
    /// <remarks>
    /// Every answer is JSON {ok, message} with a status the key can show: 200 done, 202 accepted
    /// (queued, or armed for when play resumes), 401 wrong key, 404 no such action or lobby, 409
    /// refused (with why), 423 locked (or switching held), 500 it went wrong on the way (with what, e.g.
    /// OBS not answering), 503 OBS isn't connected (nothing was done; the desk's own switches go through
    /// without OBS, and say so). /deck/hello alone answers without the key.
    /// The same press twice within 300 ms counts once ("ignored repeat").
    /// </remarks>
    public sealed partial class BroadcastServer
    {
        /// <summary>The colours, by the game's colour number, as a Stream Deck link names them.</summary>
        public static readonly string[] Colours =
            { "red", "blue", "green", "pink", "orange", "yellow", "black", "white", "purple", "brown", "cyan", "lime", "maroon", "rose", "banana", "gray", "tan", "coral" };

        // ---- Commands for hosts (only the desk page's link to each host reaches them) ----------------

        private readonly List<(string Lobby, string Command)> _specOut = new List<(string, string)>();
        private DateTime _specPolledAt;
        /// <summary>The most commands kept waiting for the desk page (the oldest go first).</summary>
        private const int SpecOutMax = 200;

        /// <summary>A setting command's kind ("spec cam", "spec audio"…) when it says what to set it to; null for a toggle ("spec lit").</summary>
        private static string? Family(string command)
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3 ? parts[0] + " " + parts[1] : null;
        }

        /// <summary>
        /// A command for a lobby's host. Only the newest of each kind per lobby waits (a camera pick
        /// replaces the one before, the game sound's "cam" its "wide"), toggles are kept as they come,
        /// and the queue has a cap, so a desk page that isn't fetching them (minimised) can't pile up a
        /// backlog the host then works through.
        /// </summary>
        private void SpecOut(string lobby, string command)
        {
            lock (_specOut)
            {
                if (Family(command) is { } family)
                    _specOut.RemoveAll(x => string.Equals(x.Lobby, lobby, StringComparison.OrdinalIgnoreCase) && Family(x.Command) == family);
                _specOut.Add((lobby, command));
                if (_specOut.Count > SpecOutMax) _specOut.RemoveRange(0, _specOut.Count - SpecOutMax);
            }
            if (_desk != null && _desk.Simulating) _desk.SimSpec(lobby, command);
        }

        /// <summary>For the desk page: the commands waiting to go to hosts (each handed out once).</summary>
        private object TakeSpecOut()
        {
            lock (_specOut)
            {
                _specPolledAt = DateTime.UtcNow;
                var list = _specOut.Select(x => new { lobby = x.Lobby, command = x.Command }).ToList();
                _specOut.Clear();
                return list;
            }
        }

        /// <summary>
        /// The desk page hasn't fetched the hosts' commands for a few seconds (Red Alert's window
        /// minimised or stuck): a camera pick waits until it does. Simulated lobbies get theirs directly.
        /// </summary>
        private bool CommandsPaused(string lobby)
        {
            if (_desk == null || _desk.Simulating && CasterDesk.IsSimLobby(lobby)) return false;
            lock (_specOut) return (DateTime.UtcNow - _specPolledAt).TotalSeconds > 3;
        }

        private string DeckKey()
        {
            if (string.IsNullOrEmpty(_settings.DeckKey) || _settings.DeckKey!.Length < 16)
            {
                var bytes = new byte[15];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                _settings.DeckKey = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
                TrySave();
            }
            return _settings.DeckKey!;
        }

        /// <summary>The lobby a camera or "wide" action is about: the one on stream (slot 1), else the top-ranked one.</summary>
        private string? CurrentLobby()
        {
            if (_desk == null) return null;
            var on = _desk.OnAir.Slots.FirstOrDefault(s => s != null);
            return on ?? _desk.Board.Ranking().FirstOrDefault(r => r.Online)?.Lobby;
        }

        /// <summary>A lobby's players as the Stream Deck numbers them: living and dead, in colour order (slot 1, 2…), the same all game.</summary>
        private static List<LobbyPlayer> SlotPlayers(LobbyLive? live) =>
            live == null ? new List<LobbyPlayer>() : live.People.Values.OrderBy(p => p.Color).ThenBy(p => p.Id).ToList();

        /// <summary>OBS can do it: connected, or simulation (where the desk alone shows it).</summary>
        private bool ObsLive => _obs?.Connected == true || _desk?.Simulating == true;

        private const string NoObs = "OBS isn't connected: nothing was switched. Connect it on the OBS page.";

        /// <summary>Said after a switch from the desk while OBS isn't connected: Red Alert switched, OBS didn't.</summary>
        private const string ObsDidnt = " OBS isn't connected, so only Red Alert's own pages (/video, the overlay) switched.";

        /// <summary>
        /// A switch is refused for want of OBS: from the Stream Deck (<paramref name="requireObs"/>) while OBS
        /// isn't connected (503, nothing done). The desk switches anyway: what's on stream in Red Alert moves,
        /// and the caster pages (/video, the overlay) follow it, OBS connected or not.
        /// </summary>
        private bool NoObsFor(bool requireObs) => requireObs && !ObsLive;

        /// <summary>A switch's answer, saying when OBS didn't switch with it.</summary>
        private (int Status, string Message) Switched(string said) => (200, ObsLive ? said : said + ObsDidnt);

        // ---- Player cameras --------------------------------------------------------------------------

        /// <summary>The desk page's camera chips: like a Stream Deck camera key (cut), answered as ok and a message (switched without OBS too).</summary>
        public (bool Ok, string Message) Cam(string? lobby, string follow)
        {
            var (status, message) = CamPress(lobby, follow, arm: false, cut: true, transition: null, requireObs: false);
            return (status is >= 200 and < 300, message);
        }

        /// <summary>
        /// A camera press: who <paramref name="lobby"/>'s camera follows ("auto", a colour, "slotK", a
        /// player id, "next", "prev", or "keep" for as it is), and with <paramref name="cut"/> that camera
        /// on stream. Refused when player cameras are off or the host isn't sending one; while the
        /// lobby isn't in play (a meeting, between games) the cut is armed and happens when play resumes
        /// (the same press again takes it back). <paramref name="arm"/>: only who it follows, no cut.
        /// <paramref name="requireObs"/>: refused (503) while OBS isn't connected, as the Stream Deck is.
        /// </summary>
        private (int Status, string Message) CamPress(string? lobby, string who, bool arm, bool cut, string? transition, bool requireObs = true)
        {
            var desk = _desk;
            if (desk == null) return (423, "Red Alert is locked.");
            if (_settings.CamOff) return (409, "Player cameras are off (turn them on in Red Alert first).");
            lobby = string.IsNullOrEmpty(lobby) ? CurrentLobby() : lobby;
            if (lobby == null) return (404, "No lobby to show.");
            var live = desk.Board.Lobby(lobby);
            if (live == null) return (404, $"No lobby {lobby}.");
            var players = SlotPlayers(live);
            int? current = desk.Armed(lobby) is { Armed: true } a ? a.Pick : desk.CamPick(lobby);
            int? following = live.Spec is JsonElement sp && sp.TryGetProperty("camOn", out var co) && co.ValueKind == JsonValueKind.Number ? co.GetInt32() : null;
            int? pick;
            who = (who ?? "").Trim().ToLowerInvariant();
            if (who is "" or "auto") pick = null;
            else if (who == "keep") pick = current;
            else if (who is "next" or "prev")
            {
                var living = players.Where(p => p.Dead != true).ToList();
                if (living.Count == 0) return (409, $"{lobby}: nobody to follow.");
                // From who it follows now: the pick, else who the camera picked by itself.
                int at = (current ?? following) is int from ? living.FindIndex(p => p.Id == from) : -1;
                int step = who == "next" ? 1 : -1;
                int i = at < 0 ? (step > 0 ? 0 : living.Count - 1) : ((at + step) % living.Count + living.Count) % living.Count;
                pick = living[i].Id;
            }
            else if (who.StartsWith("slot", StringComparison.Ordinal) && int.TryParse(who.Substring(4), out var slot))
            {
                if (slot < 1 || slot > players.Count) return (404, $"{lobby}: no player in slot {slot}.");
                pick = players[slot - 1].Id;
            }
            else if (int.TryParse(who, out var id))
            {
                if (players.All(p => p.Id != id)) return (404, $"{lobby}: no player {id}.");
                pick = id;
            }
            else
            {
                int colour = Array.IndexOf(Colours, who);
                var found = colour < 0 ? null : players.FirstOrDefault(p => p.Color == colour);
                if (found == null) return (404, $"{lobby}: no {who} player.");
                pick = found.Id;
            }
            string name = pick is int pid && live.People.TryGetValue(pid, out var lp) ? desk.Board.DisplayName(live, lp.Id, lp.Name) : "";
            string whom = pick.HasValue ? name : "whoever it picks";
            string paused = CommandsPaused(lobby) ? " (queued: Red Alert's window is paused, so it goes when the window wakes)" : "";
            if (arm || !cut)
            {
                desk.WantCam(lobby, pick);
                return (202, $"{lobby}'s camera follows {whom} (not on stream).{paused}");
            }
            // Not in play: armed, on stream as soon as play resumes. The same press again takes it back.
            if (live.Phase != "ingame")
            {
                if (desk.Armed(lobby) is { Armed: true } armed && armed.Pick == pick)
                {
                    desk.Disarm(lobby);
                    return (200, $"{lobby}'s armed camera taken back.");
                }
                desk.WantCam(lobby, pick);
                desk.Arm(lobby, pick, transition);
                string why = live.Phase switch { "meeting" => "is in a meeting", "lobby" => "is between games", "ended" => "has finished its game", _ => "isn't in a game" };
                return (202, $"{lobby} {why}: its camera goes on stream following {whom} when play resumes.{paused}");
            }
            bool sending = live.Spec is JsonElement spec && spec.TryGetProperty("cam", out var c) && c.ValueKind == JsonValueKind.True;
            if (!sending)
            {
                desk.WantCam(lobby, pick);      // asks the host to turn it on, for the next press
                return (409, $"{lobby}'s player camera isn't on yet (its host's game isn't sending it). Asked again: try in a few seconds.");
            }
            if (NoObsFor(requireObs)) return (503, NoObs);
            desk.Disarm(lobby);
            desk.WantCam(lobby, pick);
            // Several cameras up with this lobby's among them: only who it follows changes.
            var air = desk.OnAir;
            bool among = air.Layout == "cams" && air.Slots.Any(s => string.Equals(s, lobby, StringComparison.OrdinalIgnoreCase));
            if (!among) desk.Show(lobby, "cam", null, null, "deck", transition);
            return (paused.Length > 0 ? 202 : 200, (pick.HasValue ? $"{lobby}: player camera on {name}." : $"{lobby}: player camera (it picks who to follow).") + paused + (ObsLive ? "" : ObsDidnt));
        }

        /// <summary>The player camera on or off for every lobby (on: hosts send it, ready to switch to).</summary>
        private string CamAll(bool on)
        {
            if (_desk == null) return "Administration is locked.";
            _settings.CamOff = !on;
            TrySave();
            foreach (var r in _desk.Board.Ranking()) SpecOut(r.Lobby, on ? "spec cam on" : "spec cam off");
            if (!on && _desk.OnAir.IsCam) _desk.Show(_desk.OnAir.Slots.FirstOrDefault() ?? "", "full");
            DeckPoke();
            return on ? "Player cameras on: each host sends one (it starts within a few seconds)." : "Player cameras off.";
        }

        private object CamState() => _desk == null ? new { on = !_settings.CamOff, picks = new Dictionary<string, int?>() }
            : new { on = !_settings.CamOff, picks = _desk.Board.Ranking().ToDictionary(r => r.Lobby, r => _desk.CamPick(r.Lobby)) };

        // ---- Panic, and undo ---------------------------------------------------------------------------

        /// <summary>What a panic replaced, for Undo: what was on, the voice, the alerts and the hold (until when, and why).</summary>
        private sealed class PanicState
        {
            public OnAir Air = new OnAir();
            public bool MuteAll, AlertsPaused, VoiceFailed;
            public (bool Forever, DateTime? Until, string? Why) Hold;
        }
        private PanicState? _panic;
        private const string PanicWhy = "Panic";

        /// <summary>The panic still holds in full: the be-right-back screen on, switching held and every lobby voice muted (in OBS too).</summary>
        private bool PanicInEffect(CasterDesk desk, PanicState p) =>
            desk.OnAir.Layout == "slate" && desk.Held && !p.VoiceFailed && (_obs == null || _obs.Settings.Voice.MuteAll);

        /// <summary>
        /// Something of the panic is still there to undo: its be-right-back screen, its hold (still the
        /// panic's: not released, nor replaced by one of the caster's) or its mute. Each put right by hand
        /// (another lobby cut to, the hold released, the voice back on), it's over: Undo undoes the last
        /// switch, and the next Panic is a new one.
        /// </summary>
        private bool PanicLeft(CasterDesk desk, PanicState p) =>
            desk.OnAir.Layout == "slate" || desk.HoldWhy == PanicWhy || _obs != null && _obs.Settings.Voice.MuteAll && !p.MuteAll;

        /// <summary>
        /// One press when something must come off stream now (a slur in voice, roles leaking): a fade to
        /// the be-right-back screen, every lobby voice muted, the graphics cleared, alerts paused and
        /// automatic switching held. Undo puts it all back. (With a stream delay, what's already sent
        /// still airs: the delay's own "stop streaming" is in OBS.) The picture goes first: OBS failing
        /// to mute the voice doesn't stop it, and is said (pressed again, the mute is tried again).
        /// </summary>
        public async Task<(int Status, string Message)> PanicAsync()
        {
            var desk = _desk;
            if (desk == null) return (423, "Red Alert is locked.");
            var obs = _obs;
            var was = _panic;
            if (was != null && PanicInEffect(desk, was)) return (200, "Already on the be-right-back screen: Undo puts it back.");
            // Part of a panic undone by hand: it's put in full again, and Undo still goes back to before
            // it (to the lobby cut to by hand, when its be-right-back screen was left). Over: a new one.
            PanicState p;
            if (was != null && PanicLeft(desk, was))
            {
                p = was;
                if (desk.OnAir.Layout != "slate") p.Air = desk.OnAir;
            }
            else p = new PanicState { Air = desk.OnAir, MuteAll = obs?.Settings.Voice.MuteAll ?? false, Hold = desk.HoldNow(), AlertsPaused = desk.Alerts.Settings.Paused };
            desk.SetHold(true, null, PanicWhy);
            desk.ShowSlate("deck", "fade");
            desk.Graphics.Clear();
            desk.HidePlayerCard();
            desk.Alerts.SetPaused(true);
            p.VoiceFailed = false;
            _panic = p;
            string? voice = null;
            if (obs != null)
            {
                obs.Settings.Voice.MuteAll = true;
                try { await obs.VoiceChangedAsync().ConfigureAwait(false); }
                catch (Exception e)
                {
                    p.VoiceFailed = true;
                    voice = e.Message;
                }
            }
            if (voice != null)
            {
                Log.Warn("Panic: be right back, switching held, but OBS didn't mute the lobby voice: " + voice);
                return (500, $"Panic: be right back and switching held, but the lobby voice didn't mute ({voice}). Mute it in OBS, or press Panic again.");
            }
            Log.Warn("Panic: be right back, lobby voice muted, switching held.");
            return (200, "Panic: be right back, lobby voice muted, switching held. Undo puts it back.");
        }

        /// <summary>
        /// After Panic: the voice, the alerts and the hold as they were (the caster's own hold, with its
        /// reason and time limit, if there was one; a hold the caster set since stays), and (still on its
        /// be-right-back screen) what was on. Null when there's no panic to undo (none, or it was all put
        /// right by hand), so Undo goes on to undo the last switch instead.
        /// </summary>
        private async Task<(int Status, string Message)?> UndoPanicAsync()
        {
            var p = _panic;
            var desk = _desk;
            if (p == null || desk == null) return null;
            _panic = null;
            if (!PanicLeft(desk, p)) return null;
            bool onSlate = desk.OnAir.Layout == "slate";
            string? voice = null;
            if (_obs is { } obs)
            {
                obs.Settings.Voice.MuteAll = p.MuteAll;
                try { await obs.VoiceChangedAsync().ConfigureAwait(false); }
                catch (Exception e) { voice = e.Message; }
            }
            desk.Alerts.SetPaused(p.AlertsPaused);
            if (desk.HoldWhy == PanicWhy) desk.RestoreHold(p.Hold);
            // Left by hand for a lobby: that stays on.
            if (onSlate && p.Air.Layout is not ("none" or "slate" or "replay")) desk.Restore(p.Air, "deck");
            desk.ForgetBack();
            Log.Info("Panic undone." + (voice != null ? " OBS didn't set the lobby voice: " + voice : ""));
            string said = (!onSlate ? "Panic undone: voice, alerts and the hold as they were (what's on stays)."
                : p.Air.Layout is "none" or "slate" or "replay" ? "Panic undone: voice and alerts back (still on the be-right-back screen)." : "Panic undone: back to what was on.")
                + (desk.Held ? " Switching stays held." : "");
            return voice == null ? (200, said) : (500, $"{said} But OBS didn't set the lobby voice ({voice}): check it in OBS.");
        }

        // ---- The Stream Deck -----------------------------------------------------------------------------

        private readonly object _pressLock = new object();
        private (string Press, DateTime At) _lastPress = ("", default);
        /// <summary>The same press again straight after, within this long, counts once (a key bounced, or pressed twice by mistake).</summary>
        internal static TimeSpan DeckRepeat { get; set; } = TimeSpan.FromMilliseconds(300);

        private static (int, string, byte[]) DeckReply(int status, bool ok, string message) =>
            Text(status, "application/json", JsonSerializer.Serialize(new { ok, message }, Json));

        private static (int, string, byte[]) DeckReply((int Status, string Message) r) => DeckReply(r.Status, r.Status is >= 200 and < 300, r.Message);

        /// <summary>
        /// A Stream Deck link: /deck/&lt;action&gt;[/&lt;what&gt;…]?key=…. Only /deck/hello answers without the
        /// key ("Red Alert … on port …"): the Stream Deck plugin asks it on 8768–8771 to find which port is
        /// Red Alert before it sends the key there.
        /// </summary>
        private async Task<(int Status, string Type, byte[] Body)> Deck(string route, string query)
        {
            var parts = route.Substring("/deck/".Length).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            string action = parts.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "";
            if (action == "hello" && parts.Length == 1) return DeckReply(200, true, $"Red Alert {_env.Version} on port {Port}.");
            if (Control.HttpRequest.Query(query, "key") != DeckKey()) return DeckReply(401, false, "Wrong key: copy the link again from Red Alert (Settings → Stream Deck).");
            if (_desk == null) return DeckReply(423, false, "Red Alert is locked: open it and type the administration code.");
            if (action == "state") return await DeckStateAsync(query).ConfigureAwait(false);
            if (action == "img") return DeckImage(parts.Skip(1).ToArray());
            // The same press twice in a row within a moment counts once (another press in between doesn't).
            string press = string.Join("/", parts).ToLowerInvariant() + "?" + Control.HttpRequest.Query(query, "t") + "&" + Control.HttpRequest.Query(query, "as") + "&" + Control.HttpRequest.Query(query, "arm");
            var now = DateTime.UtcNow;
            lock (_pressLock)
            {
                if (_lastPress.Press == press && now - _lastPress.At < DeckRepeat) return DeckReply(200, true, "ignored repeat");
                _lastPress = (press, now);
            }
            try { return DeckReply(await DeckActionAsync(parts, query, requireObs: true).ConfigureAwait(false)); }
            finally { DeckPoke(); }
        }

        /// <summary>
        /// Does one Stream Deck (or desk) action; its HTTP status and what to say. <paramref name="requireObs"/>
        /// (the Stream Deck): a switch while OBS isn't connected is refused (503) and nothing moves. The desk
        /// (false) switches anyway, as it always has, and is told OBS didn't. Panic and replays need OBS either way.
        /// </summary>
        internal async Task<(int Status, string Message)> DeckActionAsync(string[] parts, string query, bool requireObs)
        {
            bool noObs = NoObsFor(requireObs);
            var desk = _desk;
            if (desk == null) return (423, "Red Alert is locked.");
            string P(int i) => parts.ElementAtOrDefault(i)?.ToLowerInvariant() ?? "";
            string action = P(0), what = P(1);
            string? t = Control.HttpRequest.Query(query, "t") is "cut" or "swoosh" or "fade" ? Control.HttpRequest.Query(query, "t") : null;
            var online = desk.Board.Ranking().Where(r => r.Online).Select(r => r.Lobby).OrderBy(l => desk.NumberOf(l)).ToList();
            string? Shown() => desk.OnAir.Slots.FirstOrDefault(s => s != null);
            // A lobby by its number or its label (online ones).
            string? Lobby(string which) => int.TryParse(which, out var n) ? desk.LobbyNumbered(n, online) : online.FirstOrDefault(l => string.Equals(l, which, StringComparison.OrdinalIgnoreCase));
            switch (action)
            {
                case "lobby":
                case "action":
                {
                    if (action == "action") what = "action";
                    if (online.Count == 0) return (404, "No live lobbies.");
                    string? pick;
                    if (what is "next" or "prev")
                    {
                        int at = online.FindIndex(l => string.Equals(l, Shown(), StringComparison.OrdinalIgnoreCase));
                        int step = what == "next" ? 1 : -1;
                        pick = online[at < 0 ? 0 : ((at + step) % online.Count + online.Count) % online.Count];
                    }
                    else if (what == "action")
                    {
                        // Where the action is: chosen by itself, so it waits while switching is held.
                        if (desk.Held) return (423, "Switching is held: Where the action is waits. Release the hold first.");
                        var ranked = desk.Board.Ranking().Where(r => r.Online && !desk.IsDown(r.Lobby)).ToList();
                        var hot = ranked.FirstOrDefault(r => r.Phase.Equals("meeting", StringComparison.OrdinalIgnoreCase) && !string.Equals(r.Lobby, Shown(), StringComparison.OrdinalIgnoreCase))
                                  ?? ranked.FirstOrDefault(r => !string.Equals(r.Lobby, Shown(), StringComparison.OrdinalIgnoreCase)) ?? ranked.FirstOrDefault();
                        pick = hot?.Lobby;
                        if (pick == null) return (404, "No live lobby that's up.");
                    }
                    else pick = Lobby(what);
                    if (pick == null) return (404, $"No lobby {what}.");
                    if (noObs) return (503, NoObs);
                    desk.Show(pick, "full", null, null, "deck", t);
                    return Switched($"{pick} is on stream.");
                }
                case "pvw":
                {
                    if (what == "clear") { desk.PreviewClear(); return (200, "Preview cleared."); }
                    if (what is not ("lobby" or "add")) return (404, "Unknown Preview action.");
                    var lobby = Lobby(P(2));
                    if (lobby == null) return (404, $"No lobby {P(2)}.");
                    var pv = what == "add" ? desk.PreviewAdd(lobby) : desk.PreviewOnly(lobby);
                    return (200, pv.Slots.Count == 0 ? "Preview cleared." : $"Preview: {string.Join(", ", pv.Slots)} ({LayoutName(pv.Layout)}).");
                }
                case "take":
                {
                    if (desk.Preview.Slots.Count == 0) return (409, "Nothing in Preview: pick a lobby first.");
                    string? asLayout = CasterDesk.NormLayout(Control.HttpRequest.Query(query, "as"));
                    if (Control.HttpRequest.Query(query, "as").Length > 0 && asLayout == null) return (404, "Unknown layout.");
                    if (noObs) return (503, NoObs);
                    var air = desk.Take(asLayout, t);
                    return air == null ? (409, "Nothing in Preview.") : Switched($"On stream ({LayoutName(air.Layout)}): {string.Join(", ", air.Slots.Where(x => x != null))}.");
                }
                case "layout":
                {
                    if (CasterDesk.NormLayout(what) is not { } layout) return (404, "Unknown layout.");
                    var pv = desk.PreviewLayout(layout);
                    if (pv.Slots.Count == 0) return (409, "Nothing to put in that layout: pick lobbies first.");
                    return (200, $"Preview: {LayoutName(pv.Layout)} with {string.Join(", ", pv.Slots)}. Take puts it on.");
                }
                case "quad":
                    if (noObs) return (503, NoObs);
                    desk.Show("", "4up", null, null, "deck", t);
                    return Switched("Quad on stream.");
                case "grid":
                    if (noObs) return (503, NoObs);
                    desk.Show("", "grid", null, null, "deck", t);
                    return Switched("Grid on stream.");
                case "cam":
                {
                    bool arm = Control.HttpRequest.Query(query, "arm") is "1" or "true";
                    if (what is "on" or "off" && parts.Length == 2) return (200, CamAll(what == "on"));
                    if (parts.Length <= 1) return CamPress(null, "auto", arm, cut: !arm, t, requireObs);
                    if (what == "cut" && parts.Length == 2) return CamPress(null, "keep", false, cut: true, t, requireObs);
                    if (parts.Length == 2) return CamPress(null, what, arm, cut: !arm, t, requireObs);            // cam/{who}: the lobby on stream
                    string target = what, who = P(2);
                    string? lobby = target switch
                    {
                        "pgm" => CurrentLobby(),
                        "pvw" => desk.Preview.Slots.FirstOrDefault(),
                        _ => Lobby(target),
                    };
                    if (lobby == null) return (404, target == "pvw" ? "Nothing in Preview." : $"No lobby {target}.");
                    return CamPress(lobby, who.Length == 0 ? "auto" : who, arm, cut: !arm, t, requireObs);
                }
                case "wide":
                {
                    var air = desk.OnAir;
                    if (air.IsCam)
                    {
                        // Back to the whole map(s) of the lobbies whose cameras are up.
                        var lobbies = air.Slots.Where(s => s != null).Select(s => s!).ToList();
                        if (lobbies.Count == 0) return (404, "No live lobbies.");
                        if (noObs) return (503, NoObs);
                        if (lobbies.Count == 1) desk.Show(lobbies[0], "full", null, null, "deck", t); else desk.ShowPicked(lobbies, "deck", null, t);
                        return Switched($"{string.Join(", ", lobbies)}: the whole map.");
                    }
                    var lobby = Shown() ?? online.FirstOrDefault();
                    if (lobby == null) return (404, "No live lobbies.");
                    if (noObs) return (503, NoObs);
                    desk.Show(lobby, "full", null, null, "deck", t);
                    return Switched($"{lobby}: the whole map.");
                }
                case "replay": return await DeckReplayAsync(what).ConfigureAwait(false);
                case "break":
                {
                    if (noObs && what != "off") return (503, NoObs);
                    switch (what)
                    {
                        case "brb": desk.ShowSlate("deck", t); return Switched("On stream: Be right back.");
                        case "intermission": desk.ShowIntermission("deck", t); return Switched("On stream: Intermission.");
                        case "sponsor":
                        {
                            string said = desk.StartBreak();
                            return (desk.Break != null ? 200 : 409, said);
                        }
                        case "off":
                        {
                            if (noObs) return (503, NoObs);
                            var back = desk.EndBreak("deck", t);
                            return back == null ? (409, "No break is on.") : Switched($"Back to {string.Join(", ", back.Slots.Where(x => x != null))}.");
                        }
                        default: return (404, "Unknown break.");
                    }
                }
                case "panic":
                    if (!ObsLive) return (503, NoObs);
                    return await PanicAsync().ConfigureAwait(false);
                case "hold":
                {
                    if (what is not ("on" or "off" or "toggle" or "")) return (404, "Hold: on, off or toggle.");
                    bool on = what switch { "on" => true, "off" => false, _ => !desk.Held };
                    desk.SetHold(on, null, on ? "Held from the Stream Deck" : null);
                    return (200, on ? "Switching held: nothing switches by itself until you release it." : "Hold released: automatic switching is back.");
                }
                case "auto":
                {
                    string which = what, how = P(2);
                    bool Flip(bool now) => how switch { "on" => true, "off" => false, _ => !now };
                    if (how is not ("on" or "off" or "toggle" or "")) return (404, "Auto: on, off or toggle.");
                    switch (which)
                    {
                        case "grid": desk.AutoGrid = Flip(desk.AutoGrid); return (200, desk.AutoGrid ? "Auto grid on." : "Auto grid off.");
                        case "intermission": desk.AutoIntermission = Flip(desk.AutoIntermission); return (200, desk.AutoIntermission ? "Auto intermission on." : "Auto intermission off.");
                        case "switch":
                            desk.HealthConfig.AutoSwitch = Flip(desk.HealthConfig.AutoSwitch);
                            desk.HealthConfig.Save(desk.HealthPath);
                            return (200, desk.HealthConfig.AutoSwitch ? "Auto switch away from a lobby that drops: on." : "Auto switch away from a lobby that drops: off.");
                        default: return (404, "Auto: grid, intermission or switch.");
                    }
                }
                case "undo":
                {
                    if (await UndoPanicAsync().ConfigureAwait(false) is { } undone) return undone;
                    if (desk.AutoSwitched is { } note)
                    {
                        if (noObs) return (503, NoObs);
                        desk.UndoAutoSwitch();
                        return Switched($"Back to what you had. Auto switch leaves {note.Lobby} alone until it comes back and drops again.");
                    }
                    if (noObs) return (503, NoObs);
                    var back = desk.Back();
                    return back == null ? (409, "Nothing to undo.") : Switched($"Back to {LayoutName(back.Layout)}: {string.Join(", ", back.Slots.Where(x => x != null))}.");
                }
                case "gfx":
                {
                    if (_broadcast == null) return (423, "Red Alert is locked.");
                    string how = P(2);
                    switch (what)
                    {
                        case "standings":
                        {
                            bool now = _broadcast.Settings.Current.Elements.GetValueOrDefault("standings");
                            bool on = how switch { "on" => true, "off" => false, _ => !now };
                            _broadcast.Settings.Set("standings", on);
                            return (200, on ? "Standings on stream." : "Standings off stream.");
                        }
                        case "alerts":
                        {
                            bool running = !desk.Alerts.Settings.Paused;
                            bool on = how switch { "on" => true, "off" => false, _ => !running };
                            desk.Alerts.SetPaused(!on);
                            return (200, on ? "Off-screen alerts back on." : "Off-screen alerts paused.");
                        }
                        case "skip": desk.Graphics.Skip(); return (200, "Skipped: the next graphic comes up.");
                        case "clear":
                            desk.Graphics.Clear();
                            if (_broadcast.Settings.Current.Elements.GetValueOrDefault("standings")) _broadcast.Settings.Set("standings", false);
                            return (200, "Graphics queue cleared.");
                        case "card":
                        {
                            if (desk.TopCardPlayer() is not { } top) return (409, "No notification with a player.");
                            if (!_broadcast.Settings.Current.Elements.GetValueOrDefault("playerCards")) _broadcast.Settings.Set("playerCards", true);
                            return (200, desk.ShowPlayerCard(top.Key, top.Lobby));
                        }
                        default: return (404, "Graphics: standings, alerts, skip, clear or card.");
                    }
                }
                case "prompt":
                {
                    if (what is not ("show" or "skip" or "save")) return (404, "Prompt: show, skip or save.");
                    var first = JsonSerializer.SerializeToElement(desk.PromptState(), Json).GetProperty("prompts").EnumerateArray().FirstOrDefault();
                    if (first.ValueKind != JsonValueKind.Object) return (409, "No after-game table is waiting.");
                    return (200, desk.AnswerPrompt(first.GetProperty("id").GetString() ?? "", what));
                }
                case "voice":
                {
                    if (_obs == null) return (423, "Red Alert is locked.");
                    var v = _obs.Settings.Voice;
                    string how = P(2);
                    if (what == "mute")
                    {
                        v.MuteAll = how switch { "on" => true, "off" => false, _ => !v.MuteAll };
                        await _obs.VoiceChangedAsync().ConfigureAwait(false);
                        return (200, v.MuteAll ? "Every lobby voice muted." : "Lobby voice back on: it follows the picture.");
                    }
                    if (what == "listen")
                    {
                        if (how is "off" or "")
                        {
                            v.Pin = "";
                            await _obs.VoiceChangedAsync().ConfigureAwait(false);
                            return (200, "Lobby voice follows the picture again.");
                        }
                        var lobby = Lobby(how);
                        if (lobby == null) return (404, $"No lobby {how}.");
                        v.Pin = lobby;
                        await _obs.VoiceChangedAsync().ConfigureAwait(false);
                        return (200, $"{lobby}'s voice stays up whatever is on screen.");
                    }
                    return (404, "Voice: mute or listen.");
                }
                case "mute":
                    MuteHotkeyPressed();
                    return (200, "Lobby voice mute switched.");
                case "spec":
                {
                    if (what is not ("lit" or "vision" or "eye")) return (404, "Host view: lit, vision or eye.");
                    var lobby = CurrentLobby();
                    if (lobby == null) return (404, "No lobby on stream.");
                    SpecOut(lobby, "spec " + what);
                    return CommandsPaused(lobby) ? (202, $"{lobby}: {what} (queued: Red Alert's window is paused).") : (200, $"{lobby}: host view {what}.");
                }
                default:
                    return (404, "Unknown Stream Deck action.");
            }
        }

        /// <summary>The replay keys: play the newest clip, "replay that", back to live, and the controls.</summary>
        private async Task<(int Status, string Message)> DeckReplayAsync(string what)
        {
            var desk = _desk!;
            var replays = _replays;
            if (replays == null || _obs == null) return (423, "Red Alert is locked.");
            if (!_obs.Connected) return (503, "OBS isn't connected: replays play in OBS.");
            switch (what)
            {
                case "last":
                {
                    var clip = replays.Newest();
                    if (clip == null) return (409, "No replay is ready yet.");
                    string said = await replays.PlayAsync(clip).ConfigureAwait(false);
                    return (replays.Now?.Id == clip.Id ? 200 : 409, said);
                }
                case "that":
                {
                    var air = desk.OnAir;
                    if (air.Layout == "replay") return (409, "A replay is on: go back to live first.");
                    var lobby = air.Slots.FirstOrDefault(x => x != null);
                    if (lobby == null) return (409, "No lobby on stream to replay.");
                    var clip = replays.SaveNow(lobby);
                    return (202, $"Saving the last {clip.Pre:0} seconds of {lobby}: it plays as soon as it's saved.");
                }
                case "live":
                    if (replays.Now == null) return (409, "No replay is on.");
                    return (200, await replays.LiveAsync().ConfigureAwait(false));
            }
            string? control = what switch
            {
                "toggle" => "toggle", "back" => "back", "forward" => "forward", "frameback" => "frameBack", "frameforward" => "frameForward",
                "restart" => "restart", "zoomin" => "zoomIn", "zoomout" => "zoomOut", "follow" => "follow", "angle" => "angle", "slow" => "slow",
                _ => null,
            };
            if (control == null) return (404, "Unknown replay action.");
            if (replays.Now == null) return (409, "No replay is playing.");
            string? r = await replays.ControlAsync(control).ConfigureAwait(false);
            if (control == "slow") return (200, replays.Speed == 100 ? "Normal speed." : $"Slow motion: {replays.Speed}%.");
            if (control == "angle") return r != null && r.EndsWith(" angle.", StringComparison.Ordinal) ? (200, r) : (409, r ?? "");
            return r == null ? (200, "Done.") : (409, r);
        }

        private static string LayoutName(string layout) => layout switch
        {
            "full" => "full screen", "2up" => "2-up", "4up" => "quad", "grid" => "grid", "cam" => "player cam", "cams" => "player cams",
            "intermission" => "intermission", "slate" => "be right back", "break" => "sponsor break", "replay" => "replay", _ => layout,
        };

        /// <summary>For Settings → Stream Deck: every link, ready to copy (and the Stream Deck plugin's key and port).</summary>
        private object DeckLinks()
        {
            string key = DeckKey();
            object L(string group, string name, string path, string more = "") => new { group, name, url = $"http://127.0.0.1:{Port}/deck/{path}?key={key}{more}" };
            var list = new List<object>
            {
                L("Lobbies", "Lobby 1 full screen (2, 3… the same way)", "lobby/1"),
                L("Lobbies", "Next lobby", "lobby/next"),
                L("Lobbies", "Previous lobby", "lobby/prev"),
                L("Lobbies", "Where the action is", "lobby/action"),
                L("Lobbies", "Lobby 1 with a straight cut (no swoosh)", "lobby/1", "&t=cut"),
                L("Lobbies", "Quad now", "quad"),
                L("Lobbies", "Grid now", "grid"),
                L("Preview and Take", "Lobby 1 into Preview", "pvw/lobby/1"),
                L("Preview and Take", "Add lobby 2 to Preview (again: take it out)", "pvw/add/2"),
                L("Preview and Take", "Clear Preview", "pvw/clear"),
                L("Preview and Take", "Take: Preview on stream", "take"),
                L("Preview and Take", "Preview as a quad (full, 2up, quad, grid, cams the same way)", "layout/quad"),
                L("Player camera", "Player camera (picks who to follow)", "cam"),
                L("Player camera", "Next player", "cam/next"),
                L("Player camera", "Previous player", "cam/prev"),
                L("Player camera", "A colour's player (red, blue, … in the link)", "cam/red"),
                L("Player camera", "Lobby 1's camera on player slot 1 (slot1…slot15, pgm or pvw for the lobby)", "cam/1/slot1"),
                L("Player camera", "Lobby 1's camera follows slot 1, no cut", "cam/1/slot1", "&arm=1"),
                L("Player camera", "Cut to the camera of the lobby on stream", "cam/cut"),
                L("Player camera", "Back to the whole map", "wide"),
                L("Player camera", "Player cameras on for every lobby (off the same way)", "cam/on"),
                L("Replays", "Play the newest replay", "replay/last"),
                L("Replays", "Replay that: the last seconds of the lobby on stream", "replay/that"),
                L("Replays", "Back to live", "replay/live"),
                L("Replays", "Play / pause", "replay/toggle"),
                L("Replays", "Slow motion (100%, 50%, 25%)", "replay/slow"),
                L("Breaks", "Be right back", "break/brb"),
                L("Breaks", "Intermission", "break/intermission"),
                L("Breaks", "Sponsor break", "break/sponsor"),
                L("Breaks", "End the break", "break/off"),
                L("Breaks", "Panic: fade to be right back, mute, hold", "panic"),
                L("Breaks", "Undo (the panic, an auto switch, or the last switch)", "undo"),
                L("Automation", "Hold switching (on, off or toggle at the end)", "hold/toggle"),
                L("Automation", "Auto grid", "auto/grid/toggle"),
                L("Automation", "Auto intermission", "auto/intermission/toggle"),
                L("Automation", "Auto switch off a lobby that drops", "auto/switch/toggle"),
                L("Graphics", "Standings on stream", "gfx/standings/toggle"),
                L("Graphics", "Off-screen alerts", "gfx/alerts/toggle"),
                L("Graphics", "Skip the graphic on now", "gfx/skip"),
                L("Graphics", "Clear the graphics queue", "gfx/clear"),
                L("Graphics", "Card for the top notification's player", "gfx/card"),
                L("Graphics", "Show the after-game table", "prompt/show"),
                L("Graphics", "Skip the after-game table", "prompt/skip"),
                L("Sound", "Mute all lobby voice (on/off)", "mute"),
                L("Sound", "Mute all lobby voice (on, off or toggle at the end)", "voice/mute/toggle"),
                L("Sound", "Keep lobby 1's voice up whatever is on screen (off: follow the picture)", "voice/listen/1"),
                L("Host view", "Lit map on the lobby on stream (vision and eye the same way)", "spec/lit"),
            };
            return new { links = list, colours = Colours, key, port = Port, warning = PortWarning, state = $"http://127.0.0.1:{Port}/deck/state?key={key}" };
        }
    }
}
