using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Team watch: the Watch page for the production team (<see cref="WatchServer"/>), its pictures
    /// (<see cref="WatchCapture"/>), what it shows, and the team's suggestions on the Live desk (Into
    /// Preview, Cut, Dismiss) and the Stream Deck (suggest/next). Settings → Team watch turns it on,
    /// shows the code and who's watching.
    /// </summary>
    public sealed partial class BroadcastServer
    {
        private WatchServer _watch = null!;
        private WatchCapture? _capture;
        private readonly SharedAnswer _watchAnswer = new SharedAnswer();
        /// <summary>How long one Watch answer is shared by everyone asking.</summary>
        private static readonly TimeSpan WatchFresh = TimeSpan.FromMilliseconds(250);

        /// <summary>The Watch page's server (tests).</summary>
        internal WatchServer Watch => _watch;
        internal WatchCapture? Capture => _capture;

        /// <summary>Made once with the app: listening only when Team watch was left on.</summary>
        private void StartWatch()
        {
            _watch = new WatchServer(_env.WatchPort, _settings.WatchCode)
            {
                State = () => _watchAnswer.GetAsync(() => JsonSerializer.SerializeToUtf8Bytes(WatchPayload(), Json), WatchFresh, 0).GetAwaiter().GetResult(),
                Frame = lobby => _capture?.Latest(lobby),
                Suggest = WatchSuggest,
                Log = m => Log.Info(m),
            };
            if (_settings.WatchCode != _watch.Code) { _settings.WatchCode = _watch.Code; TrySave(); }
            _capture = new WatchCapture(() => _watch.AnyoneWatching && _obs?.Connected == true, CaptureLobbies,
                (lobby, width) => _obs is { } obs ? obs.LobbyShotAsync(lobby, width) : Task.FromResult<byte[]?>(null), run: _env.WatchCapture);
            if (_settings.TeamWatch && !_watch.Start()) Log.Warn("Team watch didn't start: " + _watch.Problem);
            else if (_settings.TeamWatch) Log.Info($"Team watch is on (port {_watch.Port}).");
        }

        /// <summary>The lobbies whose pictures the Watch page gets: every lobby sending, by number.</summary>
        private IReadOnlyList<string> CaptureLobbies()
        {
            var desk = _desk;
            if (desk == null) return Array.Empty<string>();
            return desk.Board.Ranking().Where(r => r.Online).Select(r => r.Lobby).OrderBy(l => desk.NumberOf(l)).Take(WatchCapture.MaxLobbies).ToList();
        }

        // ---- What the Watch page shows ------------------------------------------------------------------

        private static string LayoutWords(string layout) => layout switch
        {
            "full" => "full screen", "2up" => "2-up", "4up" => "quad", "grid" => "grid", "cam" => "player cam", "cams" => "player cams", "break" => "sponsor break",
            "intermission" => "Intermission", "slate" => "Be right back", "soon" => "Starting soon", "replay" => "a replay", _ => "nothing",
        };

        /// <summary>What's on stream (or in Preview) in a few words: "SIM-1 (full screen)", "Intermission".</summary>
        private static string AirWords(string layout, IEnumerable<string?> slots)
        {
            var on = slots.Where(x => !string.IsNullOrEmpty(x)).ToList();
            if (layout is "intermission" or "slate" or "soon") return LayoutWords(layout);
            if (on.Count == 0) return layout == "none" ? "Nothing" : LayoutWords(layout);
            return $"{string.Join(", ", on)} ({LayoutWords(layout)})";
        }

        /// <summary>Everything the Watch page shows (the same for everyone signed in): impostors included.</summary>
        internal object WatchPayload()
        {
            var desk = _desk;
            if (desk == null) return new { locked = true };
            var now = DateTime.UtcNow;
            var air = desk.OnAir;
            var pv = desk.Preview;
            bool obsOn = _obs?.Connected == true;
            var fresh = _capture?.Fresh() ?? new HashSet<string>();
            // Without OBS: each lobby's own video (VDO.Ninja, small and muted, like the desk's tiles); the simulation's stand-in from here.
            var video = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_caster != null)
                foreach (var (lobby, url) in ObsFeeds(_caster, desk))
                    video[lobby] = ObsDirector.IsStandIn(url) ? "/watch/sim?lobby=" + Uri.EscapeDataString(lobby)
                        : url.Contains("vdo.ninja", StringComparison.OrdinalIgnoreCase) ? url + "&scale=25&noaudio" : url;
            var health = desk.Health.Lobbies.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var live = desk.Suggestions.Live();
            return new
            {
                locked = false,
                tournament = _organizer?.Tournament,
                round = desk.Tables.CurrentRound,
                sim = desk.Simulating,
                obs = obsOn,
                fps = _capture?.Fps ?? WatchCapture.MaxFps,
                program = new { layout = air.Layout, slots = air.Slots.Where(x => x != null).ToList(), label = AirWords(air.Layout, air.Slots), since = air.Since == default ? null : air.Since.ToString("o") },
                preview = new { layout = pv.Layout, slots = pv.Slots.ToList(), label = pv.Replay != null ? "Replay: " + NameTag.Plain(pv.Replay.Note) : pv.IsEmpty ? "Nothing" : AirWords(pv.Layout, pv.Slots) },
                lobbies = desk.Board.Ranking().OrderBy(r => desk.NumberOf(r.Lobby)).Take(24).Select(r =>
                {
                    var lobbyLive = desk.Board.Lobby(r.Lobby);
                    var st = health.Contains(r.Lobby) ? desk.Health.Status(r.Lobby) : null;
                    var (camOn, following) = CasterDesk.CamOf(r.Spec);
                    return new
                    {
                        no = desk.NumberOf(r.Lobby),
                        lobby = r.Lobby,
                        online = r.Online,
                        phase = r.Phase,
                        map = r.Map,
                        crew = r.Crew,
                        imps = r.Imps,
                        tasks = r.TaskPct,
                        sabotage = r.Sabotage == null ? null : new { system = r.Sabotage, left = r.SabotageLeft is { } left ? Math.Round(left, 1) : (double?)null, critical = r.SabotageCritical, fixing = r.SabotageFixing },
                        health = st?.Level ?? "green",
                        problems = st?.Problems ?? new List<string>(),
                        tally = CasterDesk.Tally(r.Lobby, air, pv),
                        onAir = air.Label(r.Lobby),
                        tier = r.Tier,
                        score = r.Score,
                        reasons = r.Plays.Take(3).Select(p => p.Text).ToList(),
                        players = r.People.OrderBy(p => p.Color).ThenBy(p => p.Id).Select(p => new
                        {
                            id = p.Id,
                            name = lobbyLive == null ? p.Name : desk.Board.DisplayName(lobbyLive, p.Id, p.Name),
                            color = p.Color,
                            dead = p.Dead == true,
                            imp = p.Imp == true,
                        }).ToList(),
                        events = desk.Events.Recent(r.Lobby, 8).Select(e => new { kind = e.Kind, text = e.Text, at = e.At.ToString("o"), ago = Math.Max(0, (int)(now - e.At).TotalSeconds) }).ToList(),
                        frame = fresh.Contains(r.Lobby),
                        video = video.TryGetValue(r.Lobby, out var u) ? u : null,
                        cam = new { on = camOn && !_settings.CamOff, following },
                    };
                }).ToList(),
                camsOff = _settings.CamOff,
                suggestions = desk.Suggestions.State(),
            };
        }

        /// <summary>A "Show this" from the Watch page: the lobby (and the player, for a camera) checked, then into the box.</summary>
        private (int Status, string Message) WatchSuggest(Watcher who, JsonElement input)
        {
            var desk = _desk;
            if (desk == null) return (423, "Red Alert is locked.");
            string Arg(string name) => input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int? player = input.TryGetProperty("player", out var pl) && pl.ValueKind == JsonValueKind.Number && pl.TryGetInt32(out var pid) ? pid : null;
            string asked = Arg("lobby");
            var rank = desk.Board.Ranking().FirstOrDefault(r => string.Equals(r.Lobby, asked, StringComparison.OrdinalIgnoreCase));
            if (rank == null) return (404, $"No lobby {asked}.");
            string reason = Arg("reason").Trim().ToLowerInvariant();
            string? name = null;
            int? color = null;
            if (reason == "cam")
            {
                if (_settings.CamOff) return (409, "Player cameras are off in Red Alert.");
                var live = desk.Board.Lobby(rank.Lobby);
                if (player is not int id || live == null || !live.People.TryGetValue(id, out var p)) return (404, "That player isn't in the lobby.");
                name = desk.Board.DisplayName(live, p.Id, p.Name);
                color = p.Color;
                if (p.Dead == true) return (409, $"{name} is dead: pick a living player.");
            }
            var (status, message, _) = desk.Suggestions.Add(who.Id, who.Name, rank.Lobby, reason, Arg("text"), reason == "cam" ? player : null, name, color);
            return (status, message);
        }

        // ---- Suggestions on the Live desk and the Stream Deck ---------------------------------------------

        /// <summary>A suggestion into Preview: its lobby, or its lobby's camera on the player (as the desk's own Preview does).</summary>
        private (int Status, string Message) SuggestionToPreview(Suggestion s)
        {
            var desk = _desk!;
            (int Status, string Message) r;
            if (s.PlayerId is int id) r = CamPress(s.Lobby, id.ToString(System.Globalization.CultureInfo.InvariantCulture), arm: false, cut: true, transition: null, requireObs: false, preview: true);
            else
            {
                var pv = desk.PreviewOnly(s.Lobby);
                r = (200, $"Preview: {string.Join(", ", pv.Slots)} ({s.Who}: {s.Label}). Take puts it on.");
            }
            if (r.Status is >= 200 and < 300) desk.Suggestions.MarkPreviewed(s.Id);
            return r;
        }

        /// <summary>A suggestion straight on stream (the desk's own direct cut for a lobby, or for its camera).</summary>
        private (int Status, string Message) SuggestionCut(Suggestion s, string? transition)
        {
            var desk = _desk!;
            if (s.PlayerId is int id) return CamPress(s.Lobby, id.ToString(System.Globalization.CultureInfo.InvariantCulture), arm: false, cut: true, transition, requireObs: false);
            if (!desk.Board.Ranking().Any(r => r.Online && string.Equals(r.Lobby, s.Lobby, StringComparison.OrdinalIgnoreCase))) return (409, $"{s.Lobby} isn't sending its game: it can't go on stream.");
            desk.Show(s.Lobby, "full", null, null, "button", transition);
            return Switched($"{s.Lobby} is on stream.");
        }

        /// <summary>POST /app/admin/suggestion: preview, cut or dismiss one.</summary>
        private (int, string, byte[]) SuggestionAction(string id, string action, string? transition)
        {
            var desk = _desk;
            if (desk == null) return Say(false, Locked);
            var s = desk.Suggestions.Find(id);
            if (s == null || s.Cleared != null && action != "dismiss") return Say(false, "That suggestion has gone.");
            switch (action)
            {
                case "dismiss":
                    desk.Suggestions.Dismiss(id);
                    return Say(true, "Dismissed.");
                case "preview":
                {
                    var (status, message) = SuggestionToPreview(s);
                    return Say(status is >= 200 and < 300, message);
                }
                case "cut":
                {
                    var (status, message) = SuggestionCut(s, transition is "cut" or "swoosh" or "fade" ? transition : null);
                    return Say(status is >= 200 and < 300, message);
                }
                default: return Say(false, "Unknown suggestion action.");
            }
        }

        /// <summary>The Stream Deck's suggest/next: the newest team suggestion (not yet put there) into Preview.</summary>
        private (int Status, string Message) DeckSuggest(string what)
        {
            if (what is not ("next" or "")) return (404, "Suggest: next.");
            var s = _desk!.Suggestions.Next();
            return s == null ? (409, "No suggestions.") : SuggestionToPreview(s);
        }

        /// <summary>For the Stream Deck's state: how many suggestions, and the newest (its free text only with the impostor flag).</summary>
        private object DeckSuggestions()
        {
            var live = _desk!.Suggestions.Live();
            var next = live.FirstOrDefault(s => !s.Previewed) ?? live.FirstOrDefault();
            return new { count = live.Count, next = next == null ? null : new { id = next.Id, lobby = next.Lobby, label = next.Label, who = next.Who, text = next.Text } };
        }

        // ---- Settings → Team watch ----------------------------------------------------------------------

        private object TeamWatchState()
        {
            var now = DateTime.UtcNow;
            var w = _watch;
            int port = w.Listening ? w.Port : 0;
            return new
            {
                on = _settings.TeamWatch,
                listening = w.Listening,
                port,
                problem = w.Problem,
                code = w.Code,
                urls = port == 0 ? new List<string>() : WatchServer.LocalAddresses().Select(a => $"http://{a}:{port}/").ToList(),
                watchers = w.Watchers().Select(x => new
                {
                    id = x.Id, name = x.Name, ip = x.Ip,
                    seen = Math.Max(0, (int)(now - x.LastSeen).TotalSeconds),
                    watching = now - x.LastSeen < WatchServer.WatchingFor,
                }).ToList(),
                pictures = new { running = _capture?.Running == true, obs = _obs?.Connected == true, fps = _capture?.Fps ?? WatchCapture.MaxFps },
            };
        }

        /// <summary>POST /app/admin/teamwatch: on, off, a new code, or sign one person out.</summary>
        private object TeamWatchAction(string action, string id)
        {
            switch (action)
            {
                case "on":
                {
                    _settings.TeamWatch = true;
                    TrySave();
                    if (!_watch.Start()) return new { ok = false, message = _watch.Problem };
                    Log.Info($"Team watch is on (port {_watch.Port}).");
                    string where = WatchServer.LocalAddresses().FirstOrDefault() is { } ip ? $"http://{ip}:{_watch.Port}/" : $"port {_watch.Port}";
                    return new { ok = true, message = $"Team watch on: your team opens {where} and types the code." };
                }
                case "off":
                    _settings.TeamWatch = false;
                    TrySave();
                    _watch.Stop();
                    Log.Info("Team watch is off.");
                    return new { ok = true, message = "Team watch off: nobody can watch, and everyone was signed out." };
                case "code":
                    _settings.WatchCode = _watch.NewCode();
                    TrySave();
                    Log.Info("Team watch: a new code (everyone signed out).");
                    return new { ok = true, message = "New code: everyone watching was signed out and types the new one." };
                case "signout":
                    return _watch.SignOut(id) ? new { ok = true, message = "Signed out." } : new { ok = false, message = "They'd already gone." };
                default:
                    return new { ok = false, message = "Unknown Team watch action." };
            }
        }

        /// <summary>For the support bundle's versions.txt: Team watch on or off, and how many are signed in (never the code).</summary>
        private string TeamWatchLine()
        {
            var w = _watch;
            if (w == null || !_settings.TeamWatch) return "Team watch: off.";
            return w.Listening ? $"Team watch: on, port {w.Port}, {w.Watchers().Count} signed in." : "Team watch: on, but not listening (" + (w.Problem ?? "unknown") + ").";
        }
    }
}
