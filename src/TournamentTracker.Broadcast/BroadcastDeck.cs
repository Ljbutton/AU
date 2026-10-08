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
    /// (http://127.0.0.1:8768/deck/…?key=…) that a Stream Deck key opens in the background,
    /// one per action. The key is Red Alert's own (Settings → Stream Deck shows the links), kept
    /// across restarts so the Stream Deck's buttons keep working.
    /// </summary>
    public sealed partial class BroadcastServer
    {
        /// <summary>The colours, by the game's colour number, as a Stream Deck link names them.</summary>
        public static readonly string[] Colours =
            { "red", "blue", "green", "pink", "orange", "yellow", "black", "white", "purple", "brown", "cyan", "lime", "maroon", "rose", "banana", "gray", "tan", "coral" };

        // Commands for a lobby's host that only the desk page can send (over that lobby's VDO.Ninja link).
        private readonly List<(string Lobby, string Command)> _specOut = new List<(string, string)>();
        // Who each lobby's camera follows, as last picked here: null = it picks by itself.
        private readonly Dictionary<string, int?> _camPick = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

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

        private void SpecOut(string lobby, string command)
        {
            lock (_specOut)
            {
                _specOut.RemoveAll(x => string.Equals(x.Lobby, lobby, StringComparison.OrdinalIgnoreCase) && x.Command.StartsWith("spec cam", StringComparison.Ordinal) && command.StartsWith("spec cam", StringComparison.Ordinal));
                _specOut.Add((lobby, command));
            }
            if (_desk != null && _desk.Simulating) _desk.SimSpec(lobby, command);
        }

        /// <summary>For the desk page: the commands waiting to go to hosts (each handed out once).</summary>
        private object TakeSpecOut()
        {
            lock (_specOut)
            {
                var list = _specOut.Select(x => new { lobby = x.Lobby, command = x.Command }).ToList();
                _specOut.Clear();
                return list;
            }
        }

        /// <summary>The lobby a camera or "wide" action is about: the one on stream (slot 1), else the top-ranked one.</summary>
        private string? CurrentLobby()
        {
            if (_desk == null) return null;
            var on = _desk.OnAir.Slots.FirstOrDefault(s => s != null);
            return on ?? _desk.Board.Ranking().FirstOrDefault(r => r.Online)?.Lobby;
        }

        /// <summary>
        /// A lobby's player camera on stream, following <paramref name="follow"/>: "auto", a player id,
        /// a colour name, "next" or "prev" (the living players in colour order).
        /// </summary>
        public (bool Ok, string Message) Cam(string? lobby, string follow)
        {
            if (_desk == null) return (false, "Administration is locked.");
            lobby = string.IsNullOrEmpty(lobby) ? CurrentLobby() : lobby;
            if (lobby == null) return (false, "No lobby to show.");
            var live = _desk.Board.Lobby(lobby);
            var players = live?.People.Values.Where(p => p.Dead != true).OrderBy(p => p.Color).ToList() ?? new List<LobbyPlayer>();
            _camPick.TryGetValue(lobby, out var current);
            int? pick;
            follow = (follow ?? "").Trim().ToLowerInvariant();
            if (follow is "" or "auto") pick = null;
            else if (follow is "next" or "prev")
            {
                if (players.Count == 0) return (false, $"{lobby}: nobody to follow.");
                int at = current.HasValue ? players.FindIndex(p => p.Id == current.Value) : -1;
                int step = follow == "next" ? 1 : -1;
                int i = at < 0 ? (step > 0 ? 0 : players.Count - 1) : ((at + step) % players.Count + players.Count) % players.Count;
                pick = players[i].Id;
            }
            else if (int.TryParse(follow, out var id)) pick = id;
            else
            {
                int colour = Array.IndexOf(Colours, follow);
                var who = colour < 0 ? null : live?.People.Values.FirstOrDefault(p => p.Color == colour);
                if (who == null) return (false, $"{lobby}: no {follow} player.");
                pick = who.Id;
            }
            _camPick[lobby] = pick;
            SpecOut(lobby, pick.HasValue ? $"spec cam {pick.Value}" : "spec cam auto");
            _desk.Show(lobby, "cam");
            string name = pick.HasValue && live != null && live.People.TryGetValue(pick.Value, out var lp) ? _desk.Board.DisplayName(live, lp.Id, lp.Name) : "";
            return (true, pick.HasValue ? $"{lobby}: player camera on {name}." : $"{lobby}: player camera (it picks who to follow).");
        }

        /// <summary>The player camera on or off for every lobby (on: hosts send it, ready to switch to).</summary>
        private string CamAll(bool on)
        {
            if (_desk == null) return "Administration is locked.";
            _settings.CamOn = on;
            TrySave();
            foreach (var r in _desk.Board.Ranking()) SpecOut(r.Lobby, on ? "spec cam on" : "spec cam off");
            if (!on && _desk.OnAir.Layout == "cam") _desk.Show(_desk.OnAir.Slots.FirstOrDefault() ?? "", "full");
            return on ? "Player cameras on: each host sends one (it starts within a few seconds)." : "Player cameras off.";
        }

        /// <summary>A Stream Deck link: /deck/&lt;action&gt;[/&lt;what&gt;]?key=….</summary>
        private (int Status, string Type, byte[] Body) Deck(string route, string query)
        {
            if (Control.HttpRequest.Query(query, "key") != DeckKey()) return Text(401, "application/json", "{\"ok\":false,\"message\":\"Wrong key: copy the link again from Red Alert (Settings → Stream Deck).\"}");
            if (_desk == null) return Ok(new { ok = false, message = "Red Alert is locked." });
            var parts = route.Substring("/deck/".Length).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            string action = parts.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "", what = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";
            var lobbies = _desk.Board.Ranking().Where(r => r.Online).Select(r => r.Lobby).OrderBy(l => _desk.NumberOf(l)).ToList();
            string? Shown() => _desk.OnAir.Slots.FirstOrDefault(s => s != null);
            object Done(bool ok, string message) => new { ok, message };
            switch (action)
            {
                case "lobby":
                {
                    if (lobbies.Count == 0) return Ok(Done(false, "No live lobbies."));
                    string? pick;
                    if (what is "next" or "prev")
                    {
                        int at = lobbies.FindIndex(l => string.Equals(l, Shown(), StringComparison.OrdinalIgnoreCase));
                        int step = what == "next" ? 1 : -1;
                        pick = lobbies[at < 0 ? 0 : ((at + step) % lobbies.Count + lobbies.Count) % lobbies.Count];
                    }
                    else if (int.TryParse(what, out var n)) pick = lobbies.FirstOrDefault(l => _desk.NumberOf(l) == n);
                    else pick = lobbies.FirstOrDefault(l => string.Equals(l, what, StringComparison.OrdinalIgnoreCase));
                    if (pick == null) return Ok(Done(false, $"No lobby {what}."));
                    _desk.Show(pick, "full");
                    return Ok(Done(true, $"{pick} is on stream."));
                }
                case "wide":
                {
                    var lobby = Shown() ?? lobbies.FirstOrDefault();
                    if (lobby == null) return Ok(Done(false, "No live lobbies."));
                    _desk.Show(lobby, "full");
                    return Ok(Done(true, $"{lobby}: the whole map."));
                }
                case "action":
                {
                    var ranked = _desk.Board.Ranking().Where(r => r.Online).ToList();
                    var hot = ranked.FirstOrDefault(r => r.Phase.Equals("meeting", StringComparison.OrdinalIgnoreCase) && !string.Equals(r.Lobby, Shown(), StringComparison.OrdinalIgnoreCase))
                              ?? ranked.FirstOrDefault(r => !string.Equals(r.Lobby, Shown(), StringComparison.OrdinalIgnoreCase)) ?? ranked.FirstOrDefault();
                    if (hot == null) return Ok(Done(false, "No live lobbies."));
                    _desk.Show(hot.Lobby, "full");
                    return Ok(Done(true, $"{hot.Lobby} is on stream."));
                }
                case "quad":
                    _desk.Show("", "4up");
                    return Ok(Done(true, "Quad on stream."));
                case "grid":
                    _desk.Show("", "grid");
                    return Ok(Done(true, "Grid on stream."));
                case "cam":
                {
                    if (what is "on" or "off") return Ok(Done(true, CamAll(what == "on")));
                    var (ok, message) = Cam(null, what.Length == 0 ? "auto" : what);
                    return Ok(Done(ok, message));
                }
                case "mute":
                    MuteHotkeyPressed();
                    return Ok(Done(true, "Lobby voice mute switched."));
                default:
                    return Ok(Done(false, "Unknown Stream Deck action."));
            }
        }

        /// <summary>For Settings → Stream Deck: every link, ready to copy.</summary>
        private object DeckLinks()
        {
            string Link(string path) => $"http://127.0.0.1:{Port}/deck/{path}?key={DeckKey()}";
            var list = new List<object>
            {
                new { group = "Lobbies", name = "Lobby 1 full screen (2, 3… the same way)", url = Link("lobby/1") },
                new { group = "Lobbies", name = "Next lobby", url = Link("lobby/next") },
                new { group = "Lobbies", name = "Previous lobby", url = Link("lobby/prev") },
                new { group = "Lobbies", name = "Where the action is", url = Link("action") },
                new { group = "Lobbies", name = "Quad", url = Link("quad") },
                new { group = "Lobbies", name = "Grid", url = Link("grid") },
                new { group = "Player camera", name = "Player camera (picks who to follow)", url = Link("cam") },
                new { group = "Player camera", name = "Next player", url = Link("cam/next") },
                new { group = "Player camera", name = "Previous player", url = Link("cam/prev") },
                new { group = "Player camera", name = "A colour's player (red, blue, … in the link)", url = Link("cam/red") },
                new { group = "Player camera", name = "Back to the whole map", url = Link("wide") },
                new { group = "Sound", name = "Mute all lobby voice (on/off)", url = Link("mute") },
            };
            return new { links = list, colours = Colours };
        }

        private object CamState() => new { on = _settings.CamOn, picks = _camPick.ToDictionary(kv => kv.Key, kv => kv.Value) };
    }
}
