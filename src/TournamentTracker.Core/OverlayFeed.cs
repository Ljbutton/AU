using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TournamentTracker.Overlay;
using TournamentTracker.Stats;
using TournamentTracker.Voice;

namespace TournamentTracker
{
    /// <summary>
    /// The stream overlay (!overlay on): an OBS browser source at http://localhost:8765/.
    /// The normal view shows only what players in the game already know (deaths once a
    /// meeting reveals them, ejections); ?full=1 adds roles, kills and tasks for delayed streams.
    /// </summary>
    public sealed partial class TournamentSession
    {
        private static readonly JsonSerializerOptions OverlayJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private static readonly HashSet<string> PublicEvents = new HashSet<string> { "meeting", "eject", "end" };

        private OverlayServer? _overlay;
        private DateTime _nextOverlay;
        private SharedLoad? _overlayStandingsFor;
        private int _overlayStandingsRound = -1;
        private List<object> _overlayStandings = new List<object>();

        public OverlayServer? Overlay => _overlay;

        private void OverlayCommand(string[] args)
        {
            string arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (arg == "on") StartOverlay();
            else if (arg == "off")
            {
                _overlay?.Dispose();
                _overlay = null;
                _overlayOn = false;
                SaveState();
            }
            Reply(_overlay != null
                ? $"Stream overlay: add a Browser source in OBS with {_overlay.Url} (spoiler-free) or {_overlay.Url}?full=1 (roles and kills: delayed streams only). Size 360×900."
                : $"Stream overlay is off. {_settings.CommandPrefix}overlay on starts it.", false);
        }

        private bool _overlayOn;

        private void StartOverlay()
        {
            _overlayOn = true;
            SaveState();
            if (_overlay != null) return;
            try { _overlay = new OverlayServer(_settings.OverlayPort, _log); }
            catch (Exception e)
            {
                _log.Error("Could not start the overlay: " + e.Message);
                Reply($"Couldn't start the overlay on port {_settings.OverlayPort} ({e.Message}). Change OverlayPort in the config.", false);
            }
        }

        private void UpdateOverlay(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, string map)
        {
            if (_overlay == null) return;
            var now = _clock();
            if (now < _nextOverlay) return;
            _nextOverlay = now.AddSeconds(0.5);
            _overlay.SafeJson = JsonSerializer.Serialize(OverlayState(phase, players, map, full: false), OverlayJson);
            _overlay.FullJson = JsonSerializer.Serialize(OverlayState(phase, players, map, full: true), OverlayJson);
        }

        /// <summary>What the overlay shows. Public for tests.</summary>
        public object OverlayState(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, string map, bool full)
        {
            var game = Tracker.Current;
            bool playing = phase == VoicePhase.Tasks || phase == VoicePhase.Meeting;
            var shown = WithoutReferee(players);
            return new
            {
                Tournament = _settings.TournamentName,
                Lobby = LobbyLabel(),
                Round,
                Played = _settings.Mode == TrackerMode.Tournament ? GamesThisRound : (int?)null,
                PerRound = _settings.Mode == TrackerMode.Tournament ? _settings.GamesPerRound : 0,
                Phase = phase.ToString(),
                Map = map,
                Players = shown.Select(p => new
                {
                    Name = p.Name,
                    Color = p.ColorId,
                    Dead = p.Disconnected || (full ? p.IsDead : _knownDead.Contains(p.Key)),
                    Impostor = full && playing && p.IsImpostor,
                    Tasks = full && playing && !p.IsImpostor && p.TasksTotal > 0 ? new[] { p.TasksCompleted, p.TasksTotal } : null,
                }).ToList(),
                StandingsTitle = _settings.Mode == TrackerMode.Tournament && Round > 0 ? $"Round {Round} standings" : "Leaderboard",
                Advance = _settings.Mode == TrackerMode.Tournament ? _settings.AdvanceCount : 0,
                Standings = OverlayStandings(),
                Feed = game == null ? new List<object>() : game.Timeline
                    .Where(e => full || PublicEvents.Contains(e.Kind))
                    .TakeLast(6)
                    .Select(e => (object)new { At = TimeSpan.FromSeconds(e.AtSeconds).ToString(@"m\:ss"), e.Text })
                    .ToList(),
            };
        }

        private List<object> OverlayStandings()
        {
            if (_settings.Mode == TrackerMode.Tournament && Combined != null)
            {
                if (!ReferenceEquals(Combined, _overlayStandingsFor) || _overlayStandingsRound != Round)
                {
                    _overlayStandingsFor = Combined;
                    _overlayStandingsRound = Round;
                    _overlayStandings = Stats.Standings.Lobby(Combined.GameRecords, LobbyLabel(), Round, _settings.AdvanceCount, _settings.GamesPerRound)
                        .Take(10).Select(r => (object)new { r.Stats.Name, Points = ReportFormatterPts(r.Stats.Points) }).ToList();
                }
                return _overlayStandings;
            }
            return Store.Leaderboard().Take(10).Select(t => (object)new { t.Name, Points = ReportFormatterPts(t.Points) }).ToList();
        }

        private static string ReportFormatterPts(double v) => Discord.ReportFormatter.Pts(v);
    }
}
