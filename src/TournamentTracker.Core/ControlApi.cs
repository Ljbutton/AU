using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TournamentTracker.Control;
using TournamentTracker.Voice;

namespace TournamentTracker
{
    /// <summary>
    /// What the Tournament Tracker app sees and does: the mod's state as JSON, everything the
    /// mod has said (the activity log), and host commands run without typing in the chat.
    /// </summary>
    public sealed partial class TournamentSession
    {
        private static readonly JsonSerializerOptions ApiJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private const int ActivityLimit = 300;

        private readonly List<(long Seq, DateTime At, string Text)> _activity = new List<(long, DateTime, string)>();
        private long _activitySeq;
        private readonly object _activityLock = new object();
        private volatile string _statusJson = "{}";
        private DateTime _nextStatusJson;
        private ControlServer? _control;
        private VoicePhase _phase = VoicePhase.Menu;
        private string _lobbyCode = "", _map = "";

        public ControlServer? Control => _control;

        private void StartControl()
        {
            if (_settings.ControlPort < 0) return;
            try
            {
                _control = new ControlServer(_settings.ControlPort, _dataDir, () => _statusJson, ActivitySince, RunCommandForApp, _log);
                RefreshStatus();
                _log.Info($"App connection ready on port {_control.Port}");
            }
            catch (Exception e)
            {
                _log.Error("The app connection couldn't start: " + e.Message);
            }
        }

        private void Log(string text)
        {
            lock (_activityLock)
            {
                _activity.Add((++_activitySeq, _clock(), text));
                if (_activity.Count > ActivityLimit) _activity.RemoveRange(0, _activity.Count - ActivityLimit);
            }
        }

        private string ActivitySince(long since)
        {
            lock (_activityLock)
            {
                var lines = _activity.Where(a => a.Seq > since).Select(a => new { seq = a.Seq, at = a.At.ToString("o"), text = a.Text }).ToList();
                return JsonSerializer.Serialize(new { last = _activitySeq, lines });
            }
        }

        /// <summary>Runs a host command from the app on the game thread and answers with what the mod said.</summary>
        private async Task<string> RunCommandForApp(string command)
        {
            var done = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            long before;
            lock (_activityLock) before = _activitySeq;
            _mainThread.Enqueue(() =>
            {
                try { RunCommand(command); }
                catch (Exception e) { Log("That didn't work: " + e.Message); }
                RefreshStatus();
                done.TrySetResult(0);
            });
            var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (finished != done.Task) return JsonSerializer.Serialize(new { ok = false, replies = new[] { "Among Us didn't answer: is the game frozen or loading?" } });
            lock (_activityLock)
                return JsonSerializer.Serialize(new { ok = true, replies = _activity.Where(a => a.Seq > before).Select(a => a.Text).ToList() });
        }

        /// <summary>A host command from the app (the same ones the chat used to take, without the "!").</summary>
        public bool RunCommand(string command)
        {
            string text = command.Trim();
            if (text.Length == 0) return false;
            if (!text.StartsWith(_settings.CommandPrefix, StringComparison.Ordinal)) text = _settings.CommandPrefix + text;
            var host = Players.FirstOrDefault(p => p.IsHost) ?? new PlayerSnapshot { Name = "Host", IsHost = true, Key = "host" };
            bool handled = HandleCommand(host, fromHost: true, text);
            if (!handled) Log($"Unknown command: {command}");
            return handled;
        }

        /// <summary>Rebuilds the status the app reads (at most twice a second, or right away after a command).</summary>
        private void RefreshStatus(bool force = true)
        {
            var now = _clock();
            if (!force && now < _nextStatusJson) return;
            _nextStatusJson = now.AddSeconds(0.5);
            var last = LastGame;
            _statusJson = JsonSerializer.Serialize(new
            {
                Version = Version,
                Mode = _settings.Mode.ToString(),
                Tournament = _settings.TournamentName,
                TournamentId = _settings.EffectiveTournamentId,
                Setup = Setup?.Describe(),
                Lobby = LobbyLabel(),
                Phase = _phase.ToString(),
                LobbyCode = _lobbyCode,
                Map = _map,
                InGame = Tracker.InGame,
                Round,
                GamesThisRound,
                _settings.GamesPerRound,
                _settings.AdvanceCount,
                Players = Players.Select(p =>
                {
                    var link = Links.Find(p.Key);
                    return new
                    {
                        p.PlayerId, p.Name, Color = p.ColorId, ColorName = Colors.Name(p.ColorId), p.IsHost, p.Key,
                        Dead = p.IsDead, p.Disconnected,
                        DiscordId = string.IsNullOrEmpty(link?.DiscordUserId) ? null : link!.DiscordUserId,
                        DiscordName = string.IsNullOrEmpty(link?.DiscordName) ? null : link!.DiscordName,
                        Referee = RefSlotKey != null && p.Key == RefSlotKey,
                        Voice = VoiceOf(link?.DiscordUserId),
                    };
                }).ToList(),
                AutoMute = new
                {
                    Configured = AutoMute != null,
                    Enabled = AutoMute?.Enabled ?? false,
                    RefereeMode = AutoMute?.RefereeMode ?? false,
                    MuteSpectators = AutoMute?.MuteSpectators ?? false,
                    VoiceConnected = Presence?.Connected ?? false,
                    VoiceProblem = (_gateway?.State ?? Presence as Discord.VoicePresenceState)?.Problem,
                    VoiceWarning = (_gateway?.State ?? Presence as Discord.VoicePresenceState)?.Warning,
                },
                Lock = new { Available = _settings.LobbySettings != null && _settings.Mode != TrackerMode.Standard, On = LockedSettings != null, Settings = _settings.LobbySettings },
                Rotation = RotationOn,
                RefSlot = RefSlotKey != null,
                Overlay = new { On = _overlay != null, Url = _overlay?.Url },
                Lead = IsLead,
                Shared = Shared != null,
                LastGame = last == null ? null : new { last.Name, last.Winner, last.Voided, last.Counted },
                Points = PointsForApp(),
            }, ApiJson);
        }

        /// <summary>"deafened", "muted", "open", or null when automute isn't handling them.</summary>
        private string? VoiceOf(string? discordId)
        {
            if (AutoMute == null || !AutoMute.Enabled || string.IsNullOrEmpty(discordId)) return null;
            var state = AutoMute.StateFor(discordId!);
            return state == null ? null : state.Value.Deaf ? "deafened" : state.Value.Mute ? "muted" : "open";
        }

        public static string Version => typeof(TournamentSession).Assembly.GetName().Version?.ToString(3) ?? "";
    }
}
