using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// What the Stream Deck shows (vMix's state document, for live keys): GET /deck/state?key=…
    /// answers at once without <c>since</c>, or waits up to 25 s for something newer than that
    /// revision. The revision goes up whenever anything in it changes: what's on stream and in
    /// Preview, each lobby's phase, players, camera and sabotage, replays, the voice, the hold and
    /// automation, the graphics, OBS streaming and recording. Impostors are left out unless asked for
    /// with &amp;imp=1 (the players' flags, and the titles of the newest replay, the best one and a replay in
    /// Preview: a kill card's text names the killer): Stream Deck keys sit on a desk that may be filmed.
    /// </summary>
    public sealed partial class BroadcastServer
    {
        private readonly object _deckLock = new object();
        private readonly object _deckBuild = new object();
        // Starts from the time, so a Stream Deck that remembers an old number after a restart isn't fooled.
        private int _deckRev = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100_000) * 1000;
        private string? _deckSig;
        private DateTime _deckBuiltAt;
        private (byte[] Full, byte[] Plain)? _deckJson;
        private TaskCompletionSource _deckWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>How long /deck/state waits for something new.</summary>
        internal static TimeSpan DeckPoll { get; set; } = TimeSpan.FromSeconds(25);
        /// <summary>The state is worked out again at most this often for everyone waiting.</summary>
        private static readonly TimeSpan DeckRebuild = TimeSpan.FromMilliseconds(200);

        /// <summary>Something changed: the Stream Deck's waiting requests look again now.</summary>
        internal void DeckPoke()
        {
            TaskCompletionSource wake;
            lock (_deckLock)
            {
                wake = _deckWake;
                _deckWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _deckBuiltAt = default;
            }
            wake.TrySetResult();
        }

        /// <summary>
        /// The port Red Alert listens on, and a warning when it isn't the usual one (saved Stream Deck links
        /// point there). On 8769–8771 the Stream Deck plugin finds it by itself; on any other it's told.
        /// </summary>
        private string? PortWarning => _env.Port != DefaultPort || Port == DefaultPort ? null
            : Port > DefaultPort && Port < DefaultPort + 4
                ? $"Port {DefaultPort} was taken, so Red Alert is on {Port}: Stream Deck links copied before point at {DefaultPort}. Copy them again (the Red Alert Stream Deck plugin finds it by itself)."
                : $"Port {DefaultPort} was taken, so Red Alert is on {Port}: Stream Deck links copied before point at {DefaultPort}. Copy them again (or set port {Port} in the Stream Deck plugin).";

        private object DeckPort() => new { port = Port, warning = PortWarning };

        /// <summary>The state now (worked out again when older than a moment), with its revision.</summary>
        internal (int Rev, byte[] Json) DeckSnapshot(bool imp)
        {
            lock (_deckBuild)
            {
                bool stale;
                lock (_deckLock) stale = _deckJson == null || DateTime.UtcNow - _deckBuiltAt > DeckRebuild;
                if (stale)
                {
                    var node = JsonSerializer.SerializeToNode(DeckStateObject(), Json)!.AsObject();
                    string sig = node.ToJsonString();
                    int rev;
                    bool changed;
                    lock (_deckLock)
                    {
                        changed = sig != _deckSig;
                        if (changed) { _deckRev++; _deckSig = sig; }
                        rev = _deckRev;
                    }
                    var full = new JsonObject { ["rev"] = rev };
                    foreach (var kv in node.ToList()) { node.Remove(kv.Key); full[kv.Key] = kv.Value; }
                    byte[] withImp = Encoding.UTF8.GetBytes(full.ToJsonString());
                    // The same without anyone's impostor flag, and the newest replay by its lobby and kind of
                    // play (its title is its card's text: "Jake killed Sam in Electrical" names the impostor).
                    if (full["lobbies"] is JsonArray lobbies)
                        foreach (var l in lobbies)
                            if (l?["players"] is JsonArray players)
                                foreach (var p in players) (p as JsonObject)?.Remove("imp");
                    if (full["replay"]?["last"] is JsonObject last) last["title"] = last["label"]?.GetValue<string>();
                    if (full["replay"]?["best"] is JsonObject bestOne) bestOne["title"] = bestOne["label"]?.GetValue<string>();
                    if (full["preview"]?["replay"] is JsonObject staged) staged["title"] = staged["label"]?.GetValue<string>();
                    // A team suggestion's own words could name an impostor: only its lobby and reason.
                    if (full["suggestions"]?["next"] is JsonObject next) next.Remove("text");
                    byte[] plain = Encoding.UTF8.GetBytes(full.ToJsonString());
                    lock (_deckLock) { _deckJson = (withImp, plain); _deckBuiltAt = DateTime.UtcNow; }
                    if (changed)
                    {
                        TaskCompletionSource wake;
                        lock (_deckLock) { wake = _deckWake; _deckWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); }
                        wake.TrySetResult();
                    }
                }
                lock (_deckLock) return (_deckRev, imp ? _deckJson!.Value.Full : _deckJson!.Value.Plain);
            }
        }

        /// <summary>GET /deck/state: at once, or (with since=REV) as soon as there's something newer, at most 25 s later.</summary>
        private async Task<(int, string, byte[])> DeckStateAsync(string query)
        {
            bool imp = Control.HttpRequest.Query(query, "imp") == "1";
            int? since = int.TryParse(Control.HttpRequest.Query(query, "since"), out var s) ? s : null;
            var until = DateTime.UtcNow + DeckPoll;
            while (true)
            {
                Task wake;
                lock (_deckLock) wake = _deckWake.Task;
                var (rev, json) = DeckSnapshot(imp);
                // Any other revision is news (a restarted Red Alert counts again from another number).
                if (since == null || rev != since || DateTime.UtcNow >= until) return (200, "application/json", json);
                double left = (until - DateTime.UtcNow).TotalMilliseconds;
                await Task.WhenAny(wake, Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(250, left))))).ConfigureAwait(false);
            }
        }

        /// <summary>The Stream Deck's state document (impostor flags included: they're taken out unless asked for).</summary>
        private object DeckStateObject()
        {
            var desk = _desk!;
            var obs = _obs;
            var air = desk.OnAir;
            var preview = desk.Preview;
            var ranking = desk.Board.Ranking();
            var replays = _replays;
            var newest = replays?.Newest();
            var best = BestNow();
            var v = obs?.Settings.Voice;
            var big = desk.Graphics.Current();
            bool connected = obs?.Connected == true;
            string? problem = obs == null ? null
                : !connected ? (obs.Problem ?? "OBS isn't connected.")
                : obs.SwitchProblem ?? obs.OutputWarnings().Where(w => w.Level == "bad").Select(w => w.Text).FirstOrDefault() ?? obs.Problem;
            int prompts = JsonSerializer.SerializeToElement(desk.PromptState(), Json).GetProperty("prompts").GetArrayLength();
            return new
            {
                obs = new { connected, streaming = obs?.Streaming == true, recording = obs?.Recording == true, problem },
                program = new
                {
                    layout = air.Layout,
                    slots = air.Slots.Select(x => x ?? "").ToList(),
                    confirmed = connected && air.Scene != null && obs!.SwitchingTo == null,
                    switching = connected ? obs!.SwitchingTo : null,
                },
                preview = new
                {
                    layout = preview.Layout,
                    slots = preview.Slots.ToList(),
                    replay = preview.Replay is { } pr ? new { id = pr.ClipId, lobby = pr.Lobby, tier = pr.Tier, title = pr.Note, label = pr.Label } : null,
                },
                voice = new { muteAll = v?.MuteAll ?? false, heard = obs?.LoudVoice(air), listen = v is { Pin.Length: > 0 } ? v.Pin : null, cue = v?.HearPreview ?? false, cueLobby = obs?.CueHeard },
                replay = new
                {
                    onAir = air.Layout == "replay" || replays?.Now != null,
                    playing = replays?.Playing == true,
                    speed = replays?.Speed ?? 100,
                    ready = replays?.ReadyCount ?? 0,
                    last = newest == null ? null : new { id = newest.Id, lobby = newest.Lobby, title = NameTag.Plain(newest.Title), label = ReplayLabel(newest) },
                    // What Best replay would put in Preview now.
                    best = best == null ? null : new { id = best.Clip.Id, lobby = best.Clip.Lobby, tier = best.Tier, title = NameTag.Plain(best.Clip.Title), label = ReplayLabel(best.Clip) },
                },
                hold = desk.Held,
                panic = _panic is { } inPanic && PanicInEffect(desk, inPanic),
                // intermission: auto intermission was removed; always false, for keys of older plugins.
                auto = new { grid = desk.AutoGrid, intermission = false, @switch = desk.HealthConfig.AutoSwitch },
                graphics = new
                {
                    standings = _broadcast?.Settings.Current.Elements.GetValueOrDefault("standings") == true,
                    alertsPaused = desk.Alerts.Settings.Paused,
                    now = big == null ? null : NameTag.Plain(big.Title),
                    waiting = desk.Graphics.Waiting,
                    prompts,
                },
                lobbies = ranking.OrderBy(r => desk.NumberOf(r.Lobby)).Select(r =>
                {
                    var live = desk.Board.Lobby(r.Lobby);
                    var (camOn, following) = CasterDesk.CamOf(live?.Spec);
                    var armed = desk.Armed(r.Lobby);
                    int slot = 0;
                    return new
                    {
                        no = desk.NumberOf(r.Lobby),
                        name = r.Lobby,
                        online = r.Online,
                        phase = r.Phase,
                        tier = r.Tier,
                        health = desk.Health.Lobbies.Contains(r.Lobby, StringComparer.OrdinalIgnoreCase) ? desk.Health.Status(r.Lobby).Level : "green",
                        tally = CasterDesk.Tally(r.Lobby, air, preview),
                        crew = r.Crew ?? 0,
                        imps = r.Imps ?? 0,
                        tasks = r.TaskPct ?? 0,
                        sabotage = r.Sabotage == null ? null : new { system = r.Sabotage, left = r.SabotageLeft is { } left ? Math.Round(left, 1) : (double?)null },
                        cam = new
                        {
                            on = camOn,
                            // The host's camera is on and its picture goes to OBS (a lobby sending its game sends it).
                            sending = camOn && !_settings.CamOff,
                            following,
                            pick = armed.Armed ? armed.Pick : desk.CamPick(r.Lobby),
                            armed = armed.Armed,
                        },
                        players = SlotPlayers(live).Select(p => new
                        {
                            slot = ++slot,
                            id = p.Id,
                            colour = p.Color >= 0 && p.Color < Colours.Length ? Colours[p.Color] : "",
                            color = p.Color,
                            name = live == null ? p.Name : desk.Board.DisplayName(live, p.Id, p.Name),
                            dead = p.Dead == true,
                            imp = p.Imp == true,
                        }).ToList(),
                    };
                }).ToList(),
                suggestions = DeckSuggestions(),
                port = Port,
                portWarning = PortWarning,
            };
        }

        /// <summary>
        /// A replay as a key may name it without saying who did what: its lobby and the kind of play
        /// ("LJ: winning kill"). "Replay that" clips' own titles say only that already.
        /// </summary>
        internal static string ReplayLabel(Clip clip)
        {
            if (clip.Rule == "instant") return NameTag.Plain(clip.Title);
            string play = clip.Rule.Length == 0 ? "replay" : Regex.Replace(clip.Rule, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
            return clip.Lobby.Length == 0 ? play : $"{clip.Lobby}: {play}";
        }

        // ---- Key pictures ---------------------------------------------------------------------------------

        private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        private static string Fit(string s, int max) => s.Length <= max ? s : s.Substring(0, Math.Max(1, max - 1)) + "…";
        private const string Red = "#e10600", Green = "#1fbf4a", Grey = "#3a3a42", Ink = "#f2f2f2", Dim = "#8a8a94";

        private static (int, string, byte[]) Svg(string body) =>
            (200, "image/svg+xml", Encoding.UTF8.GetBytes($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"144\" height=\"144\" viewBox=\"0 0 144 144\"><rect width=\"144\" height=\"144\" fill=\"#0b0b0d\"/>{body}</svg>"));

        /// <summary>
        /// A 144×144 key picture: /deck/img/lobby/3.svg (number, name, phase, crew v impostors, red/green
        /// tally border, sabotage timer), /deck/img/cam/3/slot2.svg (the player's crewmate, name, dimmed
        /// when dead, a ring when the camera follows them, ARMED), /deck/img/toggle/mute.svg (on or off).
        /// </summary>
        private (int, string, byte[]) DeckImage(string[] parts)
        {
            var desk = _desk!;
            string Clean(string s) => s.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 4) : s;
            string kind = parts.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "";
            var online = desk.Board.Ranking().Select(r => r.Lobby).ToList();
            switch (kind)
            {
                case "lobby":
                {
                    if (!int.TryParse(Clean(parts.ElementAtOrDefault(1) ?? ""), out var no)) return Text(404, "text/plain", "Not found");
                    var name = desk.LobbyNumbered(no, online);
                    var r = name == null ? null : desk.Board.Ranking().FirstOrDefault(x => string.Equals(x.Lobby, name, StringComparison.OrdinalIgnoreCase));
                    if (r == null) return Svg($"<rect x=\"3\" y=\"3\" width=\"138\" height=\"138\" fill=\"none\" stroke=\"{Grey}\" stroke-width=\"6\"/><text x=\"72\" y=\"82\" font-family=\"Arial\" font-size=\"48\" font-weight=\"700\" fill=\"{Dim}\" text-anchor=\"middle\">{no}</text><text x=\"72\" y=\"120\" font-family=\"Arial\" font-size=\"16\" fill=\"{Dim}\" text-anchor=\"middle\">NO LOBBY</text>");
                    string tally = CasterDesk.Tally(r.Lobby, desk.OnAir, desk.Preview);
                    string border = tally == "pgm" ? Red : tally == "pvw" ? Green : Grey;
                    string phase = !r.Online ? "OFFLINE" : r.Phase switch { "ingame" => "IN GAME", "meeting" => "MEETING", "lobby" => "LOBBY", "ended" => "GAME OVER", _ => "MENU" };
                    string health = desk.Health.Lobbies.Contains(r.Lobby, StringComparer.OrdinalIgnoreCase) ? desk.Health.Status(r.Lobby).Level : "green";
                    string dot = health == "red" ? Red : health == "yellow" ? "#ffc15a" : Green;
                    var sb = new StringBuilder();
                    sb.Append($"<rect x=\"4\" y=\"4\" width=\"136\" height=\"136\" fill=\"none\" stroke=\"{border}\" stroke-width=\"8\"/>");
                    sb.Append($"<circle cx=\"124\" cy=\"20\" r=\"6\" fill=\"{dot}\"/>");
                    sb.Append($"<text x=\"16\" y=\"52\" font-family=\"Arial\" font-size=\"44\" font-weight=\"700\" fill=\"{Ink}\">{no}</text>");
                    sb.Append($"<text x=\"16\" y=\"76\" font-family=\"Arial\" font-size=\"17\" font-weight=\"700\" fill=\"{Ink}\">{Esc(Fit(r.Lobby, 11))}</text>");
                    sb.Append($"<text x=\"16\" y=\"98\" font-family=\"Arial\" font-size=\"15\" fill=\"{(r.Phase == "meeting" ? "#ffc15a" : Dim)}\">{phase}</text>");
                    if (r.Phase is "ingame" or "meeting" && r.Crew.HasValue)
                        sb.Append($"<text x=\"16\" y=\"118\" font-family=\"Arial\" font-size=\"15\" fill=\"{Ink}\">{r.Crew}c v {r.Imps}i · {r.TaskPct ?? 0}%</text>");
                    if (r.Sabotage != null)
                        sb.Append($"<rect x=\"8\" y=\"122\" width=\"128\" height=\"18\" fill=\"{Red}\"/><text x=\"72\" y=\"136\" font-family=\"Arial\" font-size=\"13\" font-weight=\"700\" fill=\"#fff\" text-anchor=\"middle\">{Esc(Fit(r.Sabotage.ToUpperInvariant(), 9))}{(r.SabotageLeft is { } left ? $" {Math.Ceiling(left):0}s" : "")}</text>");
                    return Svg(sb.ToString());
                }
                case "cam":
                {
                    if (!int.TryParse(parts.ElementAtOrDefault(1), out var no)) return Text(404, "text/plain", "Not found");
                    string which = Clean(parts.ElementAtOrDefault(2) ?? "").ToLowerInvariant();
                    var name = desk.LobbyNumbered(no, online);
                    var live = name == null ? null : desk.Board.Lobby(name);
                    var players = SlotPlayers(live);
                    LobbyPlayer? p = which.StartsWith("slot", StringComparison.Ordinal) && int.TryParse(which.Substring(4), out var k) && k >= 1 && k <= players.Count ? players[k - 1]
                        : players.FirstOrDefault(x => x.Color >= 0 && x.Color < Colours.Length && Colours[x.Color] == which);
                    if (live == null || p == null) return Svg($"<text x=\"72\" y=\"64\" font-family=\"Arial\" font-size=\"22\" font-weight=\"700\" fill=\"{Dim}\" text-anchor=\"middle\">L{no}</text><text x=\"72\" y=\"96\" font-family=\"Arial\" font-size=\"16\" fill=\"{Dim}\" text-anchor=\"middle\">{Esc(which.ToUpperInvariant())}</text>");
                    int? following = CasterDesk.CamOf(live.Spec).Following;
                    var armed = desk.Armed(name!);
                    bool onAir = desk.OnAir.IsCam && desk.OnAir.Has(name!);
                    bool dead = p.Dead == true;
                    var head = ResourceBytes($"ui/crew/{Math.Max(0, p.Color)}.png");
                    string who = Esc(Fit(desk.Board.DisplayName(live, p.Id, p.Name), 12));
                    var sb = new StringBuilder();
                    sb.Append($"<g opacity=\"{(dead ? "0.35" : "1")}\">");
                    if (head.Length > 0) sb.Append($"<image href=\"data:image/png;base64,{Convert.ToBase64String(head)}\" x=\"30\" y=\"14\" width=\"84\" height=\"84\"/>");
                    sb.Append("</g>");
                    if (following == p.Id) sb.Append($"<circle cx=\"72\" cy=\"56\" r=\"46\" fill=\"none\" stroke=\"{(onAir ? Red : Ink)}\" stroke-width=\"5\"/>");
                    sb.Append($"<text x=\"72\" y=\"122\" font-family=\"Arial\" font-size=\"18\" font-weight=\"700\" fill=\"{(dead ? Dim : Ink)}\" text-anchor=\"middle\">{who}</text>");
                    sb.Append($"<text x=\"8\" y=\"20\" font-family=\"Arial\" font-size=\"13\" fill=\"{Dim}\">L{no}</text>");
                    if (dead) sb.Append($"<text x=\"136\" y=\"20\" font-family=\"Arial\" font-size=\"13\" fill=\"{Dim}\" text-anchor=\"end\">DEAD</text>");
                    if (armed.Armed && armed.Pick == p.Id) sb.Append($"<rect x=\"22\" y=\"126\" width=\"100\" height=\"16\" fill=\"#ffc15a\"/><text x=\"72\" y=\"139\" font-family=\"Arial\" font-size=\"12\" font-weight=\"700\" fill=\"#0b0b0d\" text-anchor=\"middle\">ARMED</text>");
                    return Svg(sb.ToString());
                }
                case "toggle":
                {
                    string which = Clean(parts.ElementAtOrDefault(1) ?? "").ToLowerInvariant();
                    var v = _obs?.Settings.Voice;
                    (string Label, bool? On) t = which switch
                    {
                        "mute" => ("MUTE ALL", v?.MuteAll),
                        "cue" => ("HEAR PVW", v?.HearPreview),
                        "hold" => ("HOLD", desk.Held),
                        "autogrid" => ("AUTO GRID", desk.AutoGrid),
                        "autointermission" => ("AUTO INTER", false),        // removed: always off
                        "autoswitch" => ("AUTO SWITCH", desk.HealthConfig.AutoSwitch),
                        "standings" => ("STANDINGS", _broadcast?.Settings.Current.Elements.GetValueOrDefault("standings")),
                        "alerts" => ("ALERTS", !desk.Alerts.Settings.Paused),
                        _ => ("", null),
                    };
                    if (t.Label.Length == 0) return Text(404, "text/plain", "Not found");
                    string fill = t.On == true ? (which is "mute" ? Red : which is "hold" ? "#ffc15a" : Green) : "#1a1a1f";
                    string ink = t.On == true && which == "hold" ? "#0b0b0d" : Ink;
                    return Svg($"<rect x=\"6\" y=\"6\" width=\"132\" height=\"132\" rx=\"10\" fill=\"{fill}\"/><text x=\"72\" y=\"64\" font-family=\"Arial\" font-size=\"18\" font-weight=\"700\" fill=\"{ink}\" text-anchor=\"middle\">{Esc(t.Label)}</text><text x=\"72\" y=\"100\" font-family=\"Arial\" font-size=\"24\" font-weight=\"700\" fill=\"{ink}\" text-anchor=\"middle\">{(t.On == true ? "ON" : t.On == false ? "OFF" : "?")}</text>");
                }
                default:
                    return Text(404, "text/plain", "Not found");
            }
        }
    }
}
