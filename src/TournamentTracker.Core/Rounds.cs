using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using TournamentTracker.Setup;

namespace TournamentTracker
{
    /// <summary>Tournament rounds and the setup code, both driven from the lobby chat.</summary>
    public sealed partial class TournamentSession
    {
        private static readonly Regex RoundShortcut = new Regex(@"^r(\d{1,3})$", RegexOptions.IgnoreCase);

        private sealed class SavedState
        {
            public int Round { get; set; }
            public string? RefSlotKey { get; set; }
        }

        /// <summary>The current round (1, 2, 3…); 0 until the host sets one. Saved per tournament.</summary>
        public int Round { get; private set; }

        private string StatePath => Path.Combine(_dataDir, $"state-{_settings.EffectiveTournamentId}.json");

        private void LoadState()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    var state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath));
                    Round = state?.Round ?? 0;
                    RefSlotKey = state?.RefSlotKey;
                }
            }
            catch (Exception e)
            {
                _log.Warn("Could not read the saved round: " + e.Message);
            }
        }

        public void SetRound(int round)
        {
            Round = Math.Max(0, round);
            SaveState();
        }

        private void SaveState() => TrySave(() =>
        {
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new SavedState { Round = Round, RefSlotKey = RefSlotKey }));
        }, "state");

        /// <summary>!r3 or !round 3 sets the round; !round on its own says which it is.</summary>
        private bool TryRoundCommand(string command, string[] args)
        {
            int? round = null;
            var shortcut = RoundShortcut.Match(command);
            if (shortcut.Success) round = int.Parse(shortcut.Groups[1].Value);
            else if (command != "round") return false;
            else if (args.Length > 0 && int.TryParse(args[0].TrimStart('r', 'R'), out var n)) round = n;

            if (round == null)
            {
                Reply(Round == 0 ? $"No round set. Start one with {_settings.CommandPrefix}r1." : $"This is round {Round}.", false);
                return true;
            }
            if (Tracker.InGame)
            {
                Reply("Change the round in the lobby, between games.", false);
                return true;
            }
            int previous = Round;
            SetRound(round.Value);
            Reply($"Round {Round} started: points start from zero for this round (the running total keeps counting).", false);
            if (Players.Count > 0) Reply($"Round {Round} starts now!", true);
            if (previous > 0 && Round > previous) OnRoundAdvanced();
            return true;
        }

        /// <summary>A new round: post the server standings so far (tournament mode).</summary>
        private void OnRoundAdvanced()
        {
            if (Shared != null && _settings.Mode == TrackerMode.Tournament && _settings.PrelimChannelIds.Count > 0)
                Chain(PostServerStandingsAsync);
        }

        // ---- Setup code -----------------------------------------------------------------

        /// <summary>Reads the clipboard (set by the plugin). Setup codes are too long to type in chat.</summary>
        public Func<string?>? Clipboard { get; set; }

        /// <summary>Raised after the setup changed; the plugin then starts a fresh session with it.</summary>
        public event Action? RestartRequested;

        public SetupCode? Setup { get; private set; }

        private void SetupCommand(string[] args)
        {
            if (Tracker.InGame)
            {
                Reply("Change the setup in the lobby, between games.", false);
                return;
            }
            string arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (arg == "clear")
            {
                SetupCode.Clear(_dataDir);
                Reply("Setup code removed: back to the settings file. Restarting the tracker…", false);
                RestartRequested?.Invoke();
                return;
            }
            string? text = Clipboard?.Invoke();
            if (!SetupCode.TryParse(text, out var code, out var error))
            {
                Reply(Setup != null ? $"Current setup: {Setup.Describe()}." : "No setup code in use.", false);
                Reply($"To change it, copy the new code, then type {_settings.CommandPrefix}setup. ({error})", false);
                return;
            }
            SetupCode.Save(_dataDir, code.Encode());
            Reply($"Setup applied: {code.Describe()}. Restarting the tracker…", false);
            RestartRequested?.Invoke();
        }
    }
}
