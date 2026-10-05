using System;
using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker
{
    /// <summary>
    /// Keeps the host's lobby on the tournament's settings. Only in tournament or preliminary
    /// mode with settings in the setup code; !lock off frees them until the game restarts
    /// (for a casual game), and games still get checked when they start.
    /// </summary>
    public sealed partial class TournamentSession
    {
        private bool _lockOn = true;
        private DateTime _lastLockNotice = DateTime.MinValue;

        /// <summary>The settings to hold the lobby to right now, or null to leave the lobby alone.</summary>
        public LobbySettings? LockedSettings =>
            _lockOn && _settings.Mode != TrackerMode.Standard ? _settings.LobbySettings : null;

        /// <summary>The plugin put settings back; tells the host (at most every few seconds).</summary>
        public void SettingsRestored(IReadOnlyList<string> changed)
        {
            if (changed.Count == 0) return;
            var now = _clock();
            if ((now - _lastLockNotice).TotalSeconds < 5) return;
            _lastLockNotice = now;
            Reply($"Settings are locked for {_settings.TournamentName}: put back {string.Join(", ", changed)}. " +
                  $"For a casual game, {HowTo("lock off", "the Settings lock switch")}.", false);
        }

        /// <summary>Compares the settings a game started with against the tournament's and flags the game if they differ.</summary>
        public void CheckSettings(LobbySettings actual)
        {
            var want = _settings.LobbySettings;
            var game = Tracker.Current;
            if (want == null || game == null || _settings.Mode == TrackerMode.Standard) return;
            var issues = want.Differences(actual);
            game.SettingsIssues = issues;
            if (issues.Count == 0) return;
            Reply($"This game started with the wrong settings: {string.Join(", ", issues)}. Its report will say so. If it shouldn't count, {HowTo("void", "Void")}.", false);
            if (Shared != null && _settings.Mode == TrackerMode.Tournament)
            {
                string note = $"Settings · {game.Name} started with {string.Join(", ", issues)}.";
                Chain(() => Shared.PostNoteAsync(note));
            }
        }

        private void LockCommand(string[] args)
        {
            if (_settings.LobbySettings == null || _settings.Mode == TrackerMode.Standard)
            {
                Reply("No tournament settings to lock: they come with a tournament or preliminary setup code.", false);
                return;
            }
            string arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (arg == "on" || arg == "off") _lockOn = arg == "on";
            Reply(_lockOn
                ? $"Settings locked to the tournament's ({_settings.LobbySettings.Describe()}). Turn Settings lock off in The Button for a casual game."
                : $"Settings unlocked until you restart Among Us or turn Settings lock back on. Games still count for {_settings.TournamentName} while its setup code is in use (remove the code in The Button to stop).", false);
        }
    }
}
