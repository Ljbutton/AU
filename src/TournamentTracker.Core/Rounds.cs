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
            /// <summary>The last counted game's impostors, for the rotation.</summary>
            public List<string> LastImpostors { get; set; } = new List<string>();
            public LobbyPlan? Plan { get; set; }
            public bool OverlayOn { get; set; }
            /// <summary>Where the host asked (with /new) for the live lobby message to go.</summary>
            public string? StatusChannel { get; set; }
            /// <summary>The host switched automute off (remembered, so AutoMuteUs can be used instead).</summary>
            public bool AutoMuteOff { get; set; }
            /// <summary>The private VDO.Ninja link for "Send my game to the caster", kept for the tournament.</summary>
            public string? FeedId { get; set; }
            public string? FeedKey { get; set; }
            public bool FeedOn { get; set; }
            /// <summary>What the referee ghost's screen shows (lit map, vision, "!", eye).</summary>
            public TournamentTracker.Broadcast.SpectatorSettings? Spectator { get; set; }
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
                    _lastImpostors = state?.LastImpostors ?? new List<string>();
                    Plan = state?.Plan;
                    _overlayOn = state?.OverlayOn ?? false;
                    Spectator = state?.Spectator ?? new TournamentTracker.Broadcast.SpectatorSettings();
                    _statusChosen = state?.StatusChannel;
                    _autoMuteOff = state?.AutoMuteOff ?? false;
                    _feedId = state?.FeedId;
                    _feedKey = state?.FeedKey;
                    _feedOn = state?.FeedOn ?? false;
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
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new SavedState { Round = Round, RefSlotKey = RefSlotKey, RoundGames = _roundGames, LastImpostors = _lastImpostors, Plan = Plan, OverlayOn = _overlayOn, StatusChannel = _statusChosen, AutoMuteOff = _autoMuteOff, FeedId = _feedId, FeedKey = _feedKey, FeedOn = _feedOn, Spectator = Spectator }));
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
            if (RotationOn && Players.Count > 0) Reply("Impostor rotation is on: last game's impostors are very unlikely to be impostor again straight away.", true);
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

        /// <summary>Raised after the setup changed; the plugin then starts a fresh session with it.</summary>
        public event Action? RestartRequested;

        /// <summary>The Button saved a new setup (or the host's Discord) during a game: restart once it ends.</summary>
        private bool _reloadAfterGame;

        private void ReloadIfSaved()
        {
            if (!_reloadAfterGame) return;
            _reloadAfterGame = false;
            Reply("Using the setup saved during the game. Restarting the tracker…", false);
            RestartRequested?.Invoke();
        }

        public SetupCode? Setup { get; private set; }

        private void SetupCommand(string[] args)
        {
            string arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (Tracker.InGame)
            {
                if (arg == "reload")
                {
                    // Saved in The Button mid-game: used as soon as this game ends.
                    _reloadAfterGame = true;
                    Reply("New setup saved: it's used as soon as this game ends.", false);
                    return;
                }
                Reply("Change the setup in the lobby, between games.", false);
                return;
            }
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
            Reply(Setup != null ? $"Current setup: {Setup.Describe()}. Paste a new code into The Button to change it." : "No setup code in use: paste one into The Button.", false);
        }
    }
}
