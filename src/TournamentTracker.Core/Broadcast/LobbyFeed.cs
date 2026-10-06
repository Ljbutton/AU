using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using TournamentTracker.Broadcast;
using TournamentTracker.Stats;
using TournamentTracker.Voice;

namespace TournamentTracker
{
    /// <summary>
    /// The broadcast feed: what's happening in this lobby, for the caster's Button only. The
    /// host plays as the referee ghost and sees everything, so this includes who the impostors
    /// are; it never goes to Discord or the game chat. Each message is tagged with the lobby,
    /// round, game and time. Events come as they happen; a snapshot comes every second.
    /// <list type="bullet">
    /// <item>Events: gameStart, kill, meeting, eject, sabotage, vent, tasks, killReady, danger, gameEnd.</item>
    /// <item>Snapshot ("snap"): phase, alive crew and impostors, task bar, sabotage and time left, game clock.</item>
    /// </list>
    /// Read with <see cref="FeedSince"/> (the app's /api/feed).
    /// </summary>
    public sealed partial class TournamentSession
    {
        private const int FeedKeep = 600;
        private static readonly JsonSerializerOptions FeedJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly object _feedLock = new object();
        private readonly List<(long Seq, string Json)> _feed = new List<(long, string)>();
        private long _feedSeq;
        /// <summary>This run of the mod: with each message's number, lets the caster drop repeats when the referee's page sends again after a drop.</summary>
        private static readonly string FeedSource = Guid.NewGuid().ToString("N").Substring(0, 10);

        public FeedTuning Tuning { get; set; } = new FeedTuning();

        // The game being reported: the running one, or the one that just ended while its end is told.
        private GameRecord? _endedGame;
        private GameRecord? FeedGame => Tracker.Current ?? _endedGame;

        private VoicePhase _feedPhase = VoicePhase.Menu;
        private FeedFrame? _lastFrame;
        private DateTime _nextSnapshot;
        private int _lastTaskPct = -1;
        private readonly Dictionary<byte, bool> _inVent = new Dictionary<byte, bool>();
        private readonly Dictionary<string, FeedSabotage> _sabotagesOn = new Dictionary<string, FeedSabotage>();
        private readonly Dictionary<string, (byte By, DateTime At)> _sabotagedBy = new Dictionary<string, (byte, DateTime)>();
        // Impostor → when their kill is ready again (estimated: the host can't see anyone else's timer).
        private readonly Dictionary<byte, DateTime> _killReadyAt = new Dictionary<byte, DateTime>();
        private readonly HashSet<byte> _killReadySaid = new HashSet<byte>();
        // (impostor, crewmate) → last time they were in danger, and whether it's been announced.
        private readonly Dictionary<(byte Imp, byte Crew), DateTime> _danger = new Dictionary<(byte, byte), DateTime>();

        /// <summary>Feed messages after <paramref name="since"/>: {"last":N,"items":[…]}.</summary>
        public string FeedSince(long since)
        {
            var sb = new StringBuilder();
            lock (_feedLock)
            {
                sb.Append("{\"last\":").Append(_feedSeq).Append(",\"items\":[");
                bool first = true;
                foreach (var (seq, json) in _feed)
                {
                    if (seq <= since) continue;
                    if (!first) sb.Append(',');
                    sb.Append(json);
                    first = false;
                }
            }
            return sb.Append("]}").ToString();
        }

        private void Emit(string type, string? kind, Dictionary<string, object?>? data = null)
        {
            var game = FeedGame;
            var now = _clock();
            var msg = new Dictionary<string, object?>
            {
                ["type"] = type,
                ["kind"] = kind,
                ["lobby"] = FeedLobby(game),
                ["round"] = Round,
                ["game"] = game?.Name,
                ["t"] = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
                ["clock"] = game == null ? (double?)null : Math.Round((now - game.StartedUtc).TotalSeconds, 1),
            };
            if (kind == null) msg.Remove("kind");
            if (data != null) foreach (var kv in data) msg[kv.Key] = kv.Value;
            msg["src"] = FeedSource;
            lock (_feedLock)
            {
                msg["seq"] = _feedSeq + 1;
                string json = JsonSerializer.Serialize(msg, FeedJson);
                _feed.Add((++_feedSeq, json));
                if (_feed.Count > FeedKeep) _feed.RemoveRange(0, _feed.Count - FeedKeep);
            }
        }

        private void Event(string kind, Dictionary<string, object?> data) => Emit("event", kind, data);

        /// <summary>The lobby's name for the caster: the host's lobby label, else the lobby code.</summary>
        private string FeedLobby(GameRecord? game)
        {
            string label = LobbyLabel();
            if (label.Length > 0) return label;
            if (!string.IsNullOrEmpty(game?.LobbyCode)) return game!.LobbyCode;
            return _lobbyCode.Length > 0 ? _lobbyCode : "Lobby";
        }

        private object? Who(byte? id)
        {
            var p = id.HasValue ? FeedGame?.ById(id.Value) : null;
            if (p == null) return null;
            return new { id = p.PlayerId, name = p.Name, display = DisplayName(p.Key) ?? p.Name, color = p.ColorId, colorName = Colors.Name(p.ColorId), imp = p.IsImpostor };
        }

        // Real names from the caster's roster (friend-code key → name), for events and the referee's nameplates.
        private readonly object _namesLock = new object();
        private Dictionary<string, string> _displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The caster's roster names for this lobby's players, by player key. Replaces the last set.</summary>
        public void SetDisplayNames(IDictionary<string, string> names)
        {
            lock (_namesLock) _displayNames = new Dictionary<string, string>(names.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value.Trim()), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A player's roster name, or null when the caster hasn't matched them.</summary>
        public string? DisplayName(string key)
        {
            lock (_namesLock) return _displayNames.TryGetValue(key, out var n) ? n : null;
        }

        // What each player would score if the game ended now (impostors win / crew by vote / crew by tasks), every few seconds.
        private object? _ifEnded;
        private DateTime _nextIfEnded;

        private object? IfEnded(GameRecord? game)
        {
            if (game == null) { _ifEnded = null; return null; }
            var now = _clock();
            if (now < _nextIfEnded && _ifEnded != null) return _ifEnded;
            _nextIfEnded = now.AddSeconds(5);
            try { _ifEnded = WhatIf.Points(game, _settings.Scoring); }
            catch (Exception e) { _log.Warn("Couldn't work out the points on the line: " + e.Message); _ifEnded = null; }
            return _ifEnded;
        }

        /// <summary>Everyone in the lobby with what the caster needs to match them to the roster.</summary>
        private List<object> PlayersForFeed()
        {
            var game = FeedGame;
            return WithoutReferee(Players).Select(p =>
            {
                var rec = game?.ById(p.PlayerId);
                string? discord = Links.Find(p.Key)?.DiscordUserId;
                return (object)new
                {
                    id = p.PlayerId, name = p.Name, display = DisplayName(p.Key), color = p.ColorId, key = p.Key,
                    discord = string.IsNullOrEmpty(discord) ? null : discord,
                    imp = rec?.IsImpostor, dead = rec == null ? (bool?)null : rec.DeathCause != null,
                };
            }).ToList();
        }

        private (int Crew, int Imps) AliveCounts()
        {
            var game = FeedGame;
            if (game == null) return (0, 0);
            var alive = game.Players.Where(p => p.DeathCause == null).ToList();
            return (alive.Count(p => !p.IsImpostor), alive.Count(p => p.IsImpostor));
        }

        /// <summary>The task bar, 0–100, as the game shows it (every crewmate's tasks).</summary>
        private int TaskPercent()
        {
            var crew = Tracker.Current?.Players.Where(p => !p.IsImpostor && p.DeathCause != "Disconnected").ToList();
            int total = crew?.Sum(p => p.TasksTotal) ?? 0;
            return total == 0 ? 0 : (int)Math.Floor(100.0 * crew!.Sum(p => Math.Min(p.TasksCompleted, p.TasksTotal)) / total);
        }

        /// <summary>A world point as 0–1 screen coordinates in the host's view (0,0 top left), or null without a camera.</summary>
        public static (double X, double Y, bool OnScreen)? ScreenPoint(FeedCamera? cam, float x, float y)
        {
            if (cam == null || cam.HalfWidth <= 0 || cam.HalfHeight <= 0) return null;
            double sx = (x - (cam.X - cam.HalfWidth)) / (2 * cam.HalfWidth);
            double sy = ((cam.Y + cam.HalfHeight) - y) / (2 * cam.HalfHeight);
            return (Math.Round(sx, 4), Math.Round(sy, 4), sx >= 0 && sx <= 1 && sy >= 0 && sy <= 1);
        }

        private static string SabotageName(string system) => system switch
        {
            "Reactor" => "Reactor",
            "LifeSupp" => "O2",
            "Laboratory" => "Seismic stabilizers",
            "HeliSabotage" => "Crash course",
            "Electrical" => "Lights",
            "Comms" => "Comms",
            "MushroomMixupSabotage" => "Mushroom mixup",
            _ => system,
        };

        /// <summary>The game's room name (SystemTypes) as people say it: "LifeSupp" → "O2", "UpperEngine" → "Upper Engine".</summary>
        public static string? RoomName(string? room) => room switch
        {
            null or "" => null,
            "LifeSupp" => "O2",
            "MedBay" => "MedBay",
            "Hallway" => null,
            "Laboratory" => "Laboratory",
            "Nav" => "Navigation",
            "Admin" => "Admin",
            "LockerRoom" => "Locker Room",
            "Decontamination" or "Decontamination2" or "Decontamination3" => "Decontamination",
            "GapRoom" => "Gap Room",
            "HeliSabotage" => "Cockpit",
            _ => System.Text.RegularExpressions.Regex.Replace(room!, "(?<=[a-z])(?=[A-Z0-9])", " "),
        };

        private static string EndHow(string reason)
        {
            string r = reason.ToLowerInvariant();
            if (r.Contains("disconnect")) return "disconnect";
            if (r.Contains("sabotage")) return "sabotage";
            if (r.Contains("task")) return "tasks";
            if (r.Contains("vote")) return "vote";
            if (r.Contains("kill")) return "kills";
            return "other";
        }

        // ---- From the game's hooks (called by the session's own event methods) -------------

        private void FeedGameStarted(GameRecord game)
        {
            _killReadyAt.Clear();
            _killReadySaid.Clear();
            _danger.Clear();
            _inVent.Clear();
            _lastTaskPct = -1;
            float cooldown = _lastFrame?.KillCooldown ?? 25;
            var ready = _clock().AddSeconds(Math.Min(cooldown, Tuning.FirstKillCooldown));
            foreach (var p in game.Players.Where(p => p.IsImpostor)) _killReadyAt[p.PlayerId] = ready;
            var (crew, imps) = AliveCounts();
            Event("gameStart", new Dictionary<string, object?>
            {
                ["map"] = game.Map,
                ["players"] = game.Players.Select(p => Who(p.PlayerId)).ToList(),
                ["roster"] = PlayersForFeed(),
                ["crewAlive"] = crew,
                ["impAlive"] = imps,
            });
        }

        private void FeedKill(byte killerId, byte victimId, FeedPlace? at)
        {
            var victim = Tracker.Current?.ById(victimId);
            if (victim == null || victim.DeathCause != "Killed") return;
            float cooldown = _lastFrame?.KillCooldown ?? 25;
            _killReadyAt[killerId] = _clock().AddSeconds(cooldown);
            _killReadySaid.Remove(killerId);
            _danger.Keys.Where(k => k.Crew == victimId).ToList().ForEach(k => _danger.Remove(k));

            var (crew, imps) = AliveCounts();
            var data = new Dictionary<string, object?>
            {
                ["killer"] = Who(killerId),
                ["victim"] = Who(victimId),
                ["room"] = RoomName(at?.Room),
                ["crewAlive"] = crew,
                ["impAlive"] = imps,
                // Impostors win when they match the crew: this kill ends the game.
                ["winning"] = imps > 0 && imps >= crew,
            };
            if (at != null)
            {
                data["pos"] = new { x = Math.Round(at.X, 2), y = Math.Round(at.Y, 2) };
                var screen = ScreenPoint(at.Camera ?? _lastFrame?.Camera, at.X, at.Y);
                if (screen.HasValue) data["screen"] = new { x = screen.Value.X, y = screen.Value.Y, onScreen = screen.Value.OnScreen };
            }
            Event("kill", data);
        }

        private void FeedMeeting(int meetingsBefore, byte? callerId, byte? bodyId)
        {
            var game = Tracker.Current;
            if (game == null || game.Meetings.Count == meetingsBefore) return;   // already recorded
            Event("meeting", new Dictionary<string, object?>
            {
                ["caller"] = Who(callerId),
                ["body"] = Who(bodyId),
                ["emergency"] = bodyId == null,
            });
        }

        private void FeedVote(int ejectsBefore)
        {
            var game = Tracker.Current;
            var meeting = game?.Meetings.LastOrDefault();
            if (game == null || meeting == null) return;
            var ejected = meeting.EjectedKey == null ? null : game.ByKey(meeting.EjectedKey);
            var (crew, imps) = AliveCounts();
            Event("eject", new Dictionary<string, object?>
            {
                // Who voted for whom (null target: skipped or didn't vote).
                ["votes"] = meeting.Votes.Select(v => new
                {
                    voter = game.ByKey(v.VoterKey)?.PlayerId,
                    target = v.TargetKey == null ? (int?)null : game.ByKey(v.TargetKey)?.PlayerId,
                    skipped = v.Skipped,
                }).ToList(),
                ["ejected"] = ejected == null ? null : Who(ejected.PlayerId),
                ["wasImpostor"] = ejected?.IsImpostor,
                ["skipped"] = ejected == null && !meeting.Tie,
                ["tie"] = meeting.Tie,
                ["crewAlive"] = crew,
                ["impAlive"] = imps,
            });
        }

        private void FeedMeetingClosed()
        {
            // The game resets every impostor's cooldown when the meeting ends.
            float cooldown = _lastFrame?.KillCooldown ?? 25;
            var ready = _clock().AddSeconds(cooldown);
            foreach (var id in _killReadyAt.Keys.ToList()) _killReadyAt[id] = ready;
            _killReadySaid.Clear();
            _danger.Clear();
        }

        private void FeedTask()
        {
            int pct = TaskPercent();
            if (pct == _lastTaskPct) return;
            _lastTaskPct = pct;
            Event("tasks", new Dictionary<string, object?> { ["pct"] = pct });
        }

        private void FeedSabotageBy(byte playerId, string system) => _sabotagedBy[system] = (playerId, _clock());

        private void FeedGameEnded(GameRecord game, string reason)
        {
            _endedGame = game;
            var (crew, imps) = AliveCounts();
            Event("gameEnd", new Dictionary<string, object?>
            {
                ["winner"] = game.Winner,
                ["reason"] = reason,
                ["how"] = EndHow(reason),
                ["abandoned"] = reason == "Abandoned",
                ["crewAlive"] = crew,
                ["impAlive"] = imps,
                ["impostors"] = game.Players.Where(p => p.IsImpostor).Select(p => Who(p.PlayerId)).ToList(),
            });
            _endedGame = null;
            _killReadyAt.Clear();
            _danger.Clear();
            _sabotagesOn.Clear();
        }

        // ---- Every tick from the plugin ------------------------------------------------------

        /// <summary>The game as the host sees it right now: finds vents, sabotages, kill-ready and danger, and sends the snapshot.</summary>
        public void FeedTick(FeedFrame frame)
        {
            var now = _clock();
            _lastFrame = frame;
            _feedPhase = frame.Phase;
            var game = Tracker.Current;
            bool playing = game != null && frame.Phase == VoicePhase.Tasks;

            if (game != null) Sabotages(frame);
            if (playing)
            {
                Vents(game!, frame);
                KillReady(game!, now);
                Danger(game!, frame, now);
            }
            else
            {
                _inVent.Clear();
            }

            if (now >= _nextSnapshot)
            {
                _nextSnapshot = now.AddSeconds(Tuning.SnapshotSeconds);
                Snapshot(frame);
            }
            Track(game, frame, now);
        }

        // Where everyone is on the host's screen, a few times a second, for the caster's replays
        // (the crop follows the players even when the camera moves). Sent once a second.
        private readonly List<object> _track = new List<object>();
        private DateTime _nextTrackSample, _nextTrackSend;

        private void Track(GameRecord? game, FeedFrame frame, DateTime now)
        {
            bool playing = game != null && (frame.Phase == VoicePhase.Tasks || frame.Phase == VoicePhase.Meeting);
            if (!playing || frame.Camera == null) { _track.Clear(); return; }
            if (now >= _nextTrackSample)
            {
                _nextTrackSample = now.AddSeconds(Tuning.TrackSeconds);
                var points = new List<int[]>();
                foreach (var p in frame.Players)
                {
                    if (p.Dead || p.Disconnected || game!.ById(p.Id) is not { DeathCause: null }) continue;
                    var sp = ScreenPoint(frame.Camera, p.X, p.Y);
                    if (sp == null) continue;
                    // id, x and y in thousandths of the screen (can be off screen), in a vent
                    points.Add(new[] { p.Id, (int)Math.Round(sp.Value.X * 1000), (int)Math.Round(sp.Value.Y * 1000), p.InVent ? 1 : 0 });
                }
                _track.Add(new { t = new DateTimeOffset(now).ToUnixTimeMilliseconds(), p = points });
            }
            if (now >= _nextTrackSend && _track.Count > 0)
            {
                _nextTrackSend = now.AddSeconds(1);
                Emit("track", null, new Dictionary<string, object?> { ["samples"] = _track.ToList() });
                _track.Clear();
            }
        }

        private void Vents(GameRecord game, FeedFrame frame)
        {
            foreach (var p in frame.Players)
            {
                bool was = _inVent.TryGetValue(p.Id, out var v) && v;
                _inVent[p.Id] = p.InVent;
                if (was == p.InVent || p.Dead || game.ById(p.Id) == null) continue;
                Event("vent", new Dictionary<string, object?>
                {
                    ["player"] = Who(p.Id),
                    ["action"] = p.InVent ? "enter" : "exit",
                    ["room"] = RoomName(p.Room),
                    ["pos"] = new { x = Math.Round(p.X, 2), y = Math.Round(p.Y, 2) },
                });
            }
        }

        private void Sabotages(FeedFrame frame)
        {
            var on = frame.Sabotages.GroupBy(s => s.System).ToDictionary(g => g.Key, g => g.First());
            foreach (var s in on.Values.Where(s => !_sabotagesOn.ContainsKey(s.System)))
            {
                byte? by = _sabotagedBy.TryGetValue(s.System, out var b) && _clock() - b.At < TimeSpan.FromSeconds(5) ? b.By : (byte?)null;
                Event("sabotage", new Dictionary<string, object?>
                {
                    ["system"] = SabotageName(s.System),
                    ["state"] = "start",
                    ["critical"] = s.TimeLeft.HasValue,
                    ["timeLeft"] = s.TimeLeft.HasValue ? Math.Round(s.TimeLeft.Value, 1) : (double?)null,
                    ["fixing"] = s.Fixing,
                    ["by"] = Who(by),
                });
            }
            foreach (var s in _sabotagesOn.Values.Where(s => !on.ContainsKey(s.System)).ToList())
            {
                Event("sabotage", new Dictionary<string, object?>
                {
                    ["system"] = SabotageName(s.System),
                    ["state"] = "fixed",
                    ["critical"] = s.TimeLeft.HasValue,
                    ["timeLeft"] = s.TimeLeft.HasValue ? Math.Round(s.TimeLeft.Value, 1) : (double?)null,
                });
            }
            _sabotagesOn.Clear();
            foreach (var kv in on) _sabotagesOn[kv.Key] = kv.Value;
        }

        private void KillReady(GameRecord game, DateTime now)
        {
            foreach (var (id, at) in _killReadyAt.ToList())
            {
                var imp = game.ById(id);
                if (imp == null || imp.DeathCause != null || now < at || _killReadySaid.Contains(id)) continue;
                _killReadySaid.Add(id);
                Event("killReady", new Dictionary<string, object?> { ["impostor"] = Who(id), ["estimated"] = true });
            }
        }

        private bool KillIsReady(byte id, DateTime now) => _killReadyAt.TryGetValue(id, out var at) && now >= at;

        private void Danger(GameRecord game, FeedFrame frame, DateTime now)
        {
            var alive = frame.Players.Where(p => !p.Dead && !p.Disconnected && game.ById(p.Id) is { DeathCause: null }).ToList();
            float reach = frame.KillDistance * Tuning.DangerReach, alone = Tuning.AloneRadius;
            static float Dist(FeedPlayer a, FeedPlayer b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

            var seen = new HashSet<(byte, byte)>();
            foreach (var imp in alive.Where(p => game.ById(p.Id)!.IsImpostor && !p.InVent && KillIsReady(p.Id, now)))
            {
                foreach (var crew in alive.Where(p => !game.ById(p.Id)!.IsImpostor && !p.InVent))
                {
                    float d = Dist(imp, crew);
                    if (d > reach) continue;
                    // Alone: nobody else alive (another impostor counts too) close enough to see it.
                    if (alive.Any(o => o.Id != imp.Id && o.Id != crew.Id && Dist(o, crew) <= alone)) continue;
                    var key = (imp.Id, crew.Id);
                    seen.Add(key);
                    if (!_danger.ContainsKey(key))
                        Event("danger", new Dictionary<string, object?>
                        {
                            ["state"] = "start",
                            ["impostor"] = Who(imp.Id),
                            ["crewmate"] = Who(crew.Id),
                            ["room"] = RoomName(crew.Room ?? imp.Room),
                            ["distance"] = Math.Round(d, 2),
                        });
                    _danger[key] = now;
                }
            }
            foreach (var (key, last) in _danger.ToList())
            {
                if (seen.Contains(key) || now - last < TimeSpan.FromSeconds(Tuning.DangerEndSeconds)) continue;
                _danger.Remove(key);
                Event("danger", new Dictionary<string, object?> { ["state"] = "end", ["impostor"] = Who(key.Imp), ["crewmate"] = Who(key.Crew) });
            }
        }

        private void Snapshot(FeedFrame frame)
        {
            var game = Tracker.Current;
            string phase = frame.Phase switch
            {
                VoicePhase.Lobby => "lobby",
                VoicePhase.Tasks => game != null ? "ingame" : "lobby",
                VoicePhase.Meeting => "meeting",
                VoicePhase.GameOver => "ended",
                _ => "menu",
            };
            var (crew, imps) = AliveCounts();
            var sab = _sabotagesOn.Values.OrderBy(s => s.TimeLeft.HasValue ? 0 : 1).ThenBy(s => s.TimeLeft ?? 0).FirstOrDefault();
            Emit("snap", null, new Dictionary<string, object?>
            {
                ["phase"] = phase,
                ["map"] = game?.Map,
                ["crewAlive"] = game == null ? (int?)null : crew,
                ["impAlive"] = game == null ? (int?)null : imps,
                ["alive"] = game == null ? (int?)null : crew + imps,
                ["taskPct"] = game == null ? (int?)null : TaskPercent(),
                ["sabotage"] = sab == null ? null : new
                {
                    system = SabotageName(sab.System),
                    critical = sab.TimeLeft.HasValue,
                    timeLeft = sab.TimeLeft.HasValue ? Math.Round(sab.TimeLeft.Value, 1) : (double?)null,
                    fixing = sab.Fixing,
                },
                ["killReady"] = game == null ? null : _killReadyAt.Where(k => KillIsReady(k.Key, _clock()) && game.ById(k.Key) is { DeathCause: null }).Select(k => (int)k.Key).ToList(),
                ["danger"] = _danger.Count > 0,
                ["video"] = FeedForLive != null,
                ["spec"] = SpectatorForFeed(),
                ["players"] = PlayersForFeed(),
                ["ifEnded"] = IfEnded(game),
            });
        }
    }
}
