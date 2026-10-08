using System;
using System.Collections.Generic;
using System.Linq;
using TournamentTracker.Broadcast;
using TournamentTracker.PlayerCam;

namespace TournamentTracker.Broadcast
{
    /// <summary>
    /// What the referee ghost (the spectator, whose screen goes to the caster) sees drawn on the
    /// map. Only ever drawn on that one game: nothing is sent to any player.
    /// </summary>
    public sealed class SpectatorSettings
    {
        /// <summary>The whole map lit, every player shown (impostors in vents stay hidden in them).</summary>
        public bool Lit { get; set; } = true;
        /// <summary>"off", "focus" (every living crewmate's real vision, or just the picked one's, the rest dimmed) or "rings" (everyone's vision outline).</summary>
        public string Vision { get; set; } = "focus";
        /// <summary>"!" over anyone close enough to report a body.</summary>
        public bool Report { get; set; } = true;
        /// <summary>A small eye by a crewmate's name while an impostor is in their sight.</summary>
        public bool Eye { get; set; } = true;
        /// <summary>The one crewmate whose vision is shown, or null for every crewmate's.</summary>
        public int? Focus { get; set; }
        /// <summary>How much darker everything outside the crewmates' vision is (0–1).</summary>
        public float Dim { get; set; } = 0.22f;
        /// <summary>
        /// The player camera: a second picture, sent with the host's game, that follows one player
        /// up close (turned on from Red Alert; the host has no switch for it).
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Cam { get => !CamOff; set => CamOff = !value; }
        /// <summary>Saved as "off" so it's on by default, also for a game saved before it existed.</summary>
        public bool CamOff { get; set; }
        /// <summary>Who the player camera follows, or null to pick by itself (an impostor closing in, else whoever is busy).</summary>
        public int? CamPlayer { get; set; }
        /// <summary>
        /// The player camera is what's on stream for this lobby (Red Alert says so): the host's game
        /// sound is then what the followed player would hear. Otherwise it's the whole map's
        /// (alarms and kills). Not saved: it follows the stream.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool CamAudio { get; set; }
    }
}

namespace TournamentTracker
{
    public sealed partial class TournamentSession
    {
        public SpectatorSettings Spectator { get; private set; } = new SpectatorSettings();
        /// <summary>The player camera's pictures on their way to The Button.</summary>
        public PlayerCamFeed Cam { get; } = new PlayerCamFeed();
        private byte? _camOn;
        private DateTime _camSince;
        private readonly Dictionary<byte, DateTime> _lastActive = new Dictionary<byte, DateTime>();
        private readonly Dictionary<(string, byte, byte), DateTime> _witnessed = new Dictionary<(string, byte, byte), DateTime>();

        /// <summary>True when this game is the spectator: the host, playing as the referee ghost.</summary>
        public bool IsSpectator => RefSlotKey != null && Players.Any(p => p.IsHost && p.Key == RefSlotKey);

        /// <summary>Notes a crewmate doing something, for picking whose vision to show.</summary>
        private void Active(byte? playerId)
        {
            if (playerId.HasValue) _lastActive[playerId.Value] = _clock();
        }

        /// <summary>
        /// Whose vision to show: the one picked from the caster tab, else a crewmate in danger,
        /// else whoever did something most recently, else the first crewmate alive.
        /// </summary>
        public byte? FocusPlayer()
        {
            var game = Tracker.Current;
            if (game == null) return null;
            bool Alive(int id) => game.ById((byte)id) is { IsImpostor: false, DeathCause: null };
            if (Spectator.Focus is int chosen && Alive(chosen)) return (byte)chosen;
            var danger = _danger.Where(d => Alive(d.Key.Crew)).OrderByDescending(d => d.Value).Select(d => (byte?)d.Key.Crew).FirstOrDefault();
            if (danger != null) return danger;
            var recent = _lastActive.Where(a => Alive(a.Key)).OrderByDescending(a => a.Value).Select(a => (byte?)a.Key).FirstOrDefault();
            return recent ?? game.Players.Where(p => !p.IsImpostor && p.DeathCause == null).Select(p => (byte?)p.PlayerId).FirstOrDefault();
        }

        /// <summary>
        /// Who the player camera follows now (null: nobody, it's off or no game). The caster's pick
        /// while they're alive; otherwise an impostor closing in on a crewmate alone, else whoever
        /// is busy. A pick it made itself is kept at least 4 seconds, unless an impostor closes in.
        /// </summary>
        public byte? CamPlayer()
        {
            var game = Tracker.Current;
            if (!Spectator.Cam || game == null) { _camOn = null; return null; }
            bool Alive(int id) => game.ById((byte)id) is { DeathCause: null };
            if (Spectator.CamPlayer is int chosen && game.ById((byte)chosen) != null)
            {
                // The caster's pick, still followed a few seconds after they die (to see the kill).
                if (Alive(chosen) || _camOn == chosen && _clock() - _camSince < TimeSpan.FromSeconds(4)) return Keep((byte)chosen, chosen: true);
            }
            var now = _clock();
            var hunter = _danger.Where(d => Alive(d.Key.Imp) && Alive(d.Key.Crew)).OrderByDescending(d => d.Value).Select(d => (byte?)d.Key.Imp).FirstOrDefault();
            if (hunter != null) return Keep(hunter.Value, chosen: false);
            if (_camOn is byte current && Alive(current) && now - _camSince < TimeSpan.FromSeconds(4)) return current;
            var busy = FocusPlayer();
            return busy == null ? null : Keep(busy.Value, chosen: false);

            byte Keep(byte id, bool chosen)
            {
                if (_camOn != id) { _camOn = id; _camSince = _clock(); }
                return id;
            }
        }

        /// <summary>
        /// A crewmate had the impostor in sight at a kill or a vent: "witnessed_kill" / "witnessed_vent"
        /// for the caster. Each pair counts once every few seconds.
        /// </summary>
        public void Witnessed(string kind, byte witness, byte impostor, FeedPlace? at)
        {
            var game = Tracker.Current;
            if (game?.ById(witness) is not { IsImpostor: false } || game.ById(impostor) is not { IsImpostor: true }) return;
            var key = (kind, witness, impostor);
            var now = _clock();
            if (_witnessed.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(4)) return;
            _witnessed[key] = now;
            Active(witness);
            var data = new Dictionary<string, object?>
            {
                ["witness"] = Who(witness),
                ["impostor"] = Who(impostor),
                ["room"] = RoomName(at?.Room),
            };
            if (at != null) data["pos"] = new { x = Math.Round(at.X, 2), y = Math.Round(at.Y, 2) };
            Event(kind == "vent" ? "witnessed_vent" : "witnessed_kill", data);
        }

        /// <summary>spec lit|report|eye on|off · spec vision off|focus|rings · spec focus auto|&lt;player id&gt; · spec dim 0.22 · spec cam on|off|auto|&lt;player id&gt; · spec audio cam|wide</summary>
        private void SpectatorCommand(string[] args)
        {
            string what = args.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "";
            string value = args.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "";
            var s = Spectator;
            bool On(bool current) => value == "on" ? true : value == "off" ? false : !current;
            switch (what)
            {
                case "lit": s.Lit = On(s.Lit); break;
                case "report": s.Report = On(s.Report); break;
                case "eye": s.Eye = On(s.Eye); break;
                case "vision":
                    s.Vision = value is "off" or "focus" or "rings" ? value : s.Vision == "off" ? "focus" : s.Vision == "focus" ? "rings" : "off";
                    break;
                case "focus":
                    s.Focus = int.TryParse(value, out int id) ? id : (int?)null;
                    break;
                case "cam":
                    if (value is "on" or "off") s.Cam = value == "on";
                    else if (value == "auto") { s.Cam = true; s.CamPlayer = null; }
                    else if (int.TryParse(value, out int follow) && follow >= 0 && follow < 256) { s.Cam = true; s.CamPlayer = follow; }
                    else s.Cam = !s.Cam;
                    RefreshStatus();
                    break;
                case "audio":
                    s.CamAudio = value == "cam";
                    break;
                case "dim":
                    if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dim)) s.Dim = Math.Max(0, Math.Min(0.8f, dim));
                    break;
                default:
                    Reply("Spectator view: spec lit|report|eye on|off, spec vision off|focus|rings, spec focus auto|<player>, spec cam on|off|auto|<player>, spec audio cam|wide.", false);
                    return;
            }
            SaveState();
            Reply($"Spectator view: map {(s.Lit ? "lit" : "normal")}, vision {s.Vision}{(s.Focus.HasValue ? $" (on player {s.Focus})" : "")}, \"!\" {(s.Report ? "on" : "off")}, eye {(s.Eye ? "on" : "off")}, player camera {(s.Cam ? s.CamPlayer.HasValue ? $"on player {s.CamPlayer}" : "on (picks by itself)" : "off")}.", false);
        }

        /// <summary>The spectator view's state, for the caster's snapshot.</summary>
        private object SpectatorForFeed() => new
        {
            lit = Spectator.Lit, vision = Spectator.Vision, report = Spectator.Report, eye = Spectator.Eye,
            focus = Spectator.Focus, focusing = FocusPlayer(), on = IsSpectator,
            cam = Spectator.Cam, camPick = Spectator.CamPlayer, camOn = CamPlayer(), audio = Spectator.CamAudio ? "cam" : "wide",
        };
    }
}
