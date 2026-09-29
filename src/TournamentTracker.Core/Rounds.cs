using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using TournamentTracker.Setup;
using TournamentTracker.Stats;

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
            public Dictionary<int, int> RoundGames { get; set; } = new Dictionary<int, int>();
            public Dictionary<int, Dictionary<string, int>> ImpostorGames { get; set; } = new Dictionary<int, Dictionary<string, int>>();
            public LobbyPlan? Plan { get; set; }
            public bool OverlayOn { get; set; }
            /// <summary>Where the host asked (with /new) for the live lobby message to go.</summary>
            public string? StatusChannel { get; set; }
            /// <summary>The host switched automute off (remembered, so AutoMuteUs can be used instead).</summary>
            public bool AutoMuteOff { get; set; }
        }

        /// <summary>Counted games this lobby has played per round.</summary>
        private Dictionary<int, int> _roundGames = new Dictionary<int, int>();

        /// <summary>Automute switched off by the host, remembered between sessions.</summary>
        private bool _autoMuteOff;

        public int GamesThisRound => _roundGames.TryGetValue(Round, out var n) ? n : 0;

        /// <summary>A counted game was added to (+1) or taken out of (-1) its round.</summary>
        private void CountRoundGame(GameRecord game, int change)
        {
            if (_settings.Mode != TrackerMode.Tournament || game.Round <= 0) return;
            _roundGames[game.Round] = Math.Max(0, (_roundGames.TryGetValue(game.Round, out var n) ? n : 0) + change);
            SaveState();
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
                    _roundGames = state?.RoundGames ?? new Dictionary<int, int>();
                    _impostorGames = state?.ImpostorGames ?? new Dictionary<int, Dictionary<string, int>>();
                    Plan = state?.Plan;
                    _overlayOn = state?.OverlayOn ?? false;
                    _statusChosen = state?.StatusChannel;
                    _autoMuteOff = state?.AutoMuteOff ?? false;
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
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new SavedState { Round = Round, RefSlotKey = RefSlotKey, RoundGames = _roundGames, ImpostorGames = _impostorGames, Plan = Plan, OverlayOn = _overlayOn, StatusChannel = _statusChosen, AutoMuteOff = _autoMuteOff }));
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
                Reply(Round == 0 ? $"No round set. To start one, {HowTo("r1", "Next round")}." : $"This is round {Round}.", false);
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
            if (RotationOn && Players.Count > 0) Reply("Impostor rotation is on: nobody is impostor twice this round until everyone has been once.", true);
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
            if (arg == "reload")
            {
                // The app saved a new code into the data folder.
                var saved = SetupCode.Load(_dataDir, _log);
                Reply(saved != null ? $"Setup applied: {saved.Describe()}. Restarting the tracker…" : "No setup code saved: back to the settings file. Restarting the tracker…", false);
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
            if (code.IsAdmin)
            {
                Reply("That's an administration code: it goes in The Button's Settings → Administration, not in the game.", false);
                return;
            }
            SetupCode.Save(_dataDir, code.Encode());
            Reply($"Setup applied: {code.Describe()}. Restarting the tracker…", false);
            RestartRequested?.Invoke();
        }
    }
}
