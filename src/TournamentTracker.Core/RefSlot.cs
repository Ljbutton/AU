using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker
{
    /// <summary>
    /// The referee ghost slot: the host joins as an extra player who is made a
    /// ghost as each game starts, so an 11-player lobby plays like 10 while the referee
    /// watches everything. The ghost is never an impostor, has no tasks, and is left out of
    /// stats, points and automute (they can always talk). Off until the host turns it on.
    /// </summary>
    public sealed partial class TournamentSession
    {
        /// <summary>The referee's player key while the slot is on; null when off. Saved.</summary>
        public string? RefSlotKey { get; private set; }

        /// <summary>The referee's player ID in this lobby, if the slot is on and they're here.</summary>
        public byte? RefSlotPlayerId(IReadOnlyList<PlayerSnapshot> players) =>
            RefSlotKey == null ? null : players.FirstOrDefault(p => p.Key == RefSlotKey)?.PlayerId;

        /// <summary>The lobby without the referee ghost: who actually plays.</summary>
        public IReadOnlyList<PlayerSnapshot> WithoutReferee(IReadOnlyList<PlayerSnapshot> players) =>
            RefSlotKey == null ? players : players.Where(p => p.Key != RefSlotKey).ToList();

        /// <summary>!refslot on|off. Only the host can be the referee ghost.</summary>
        private void RefSlotCommand(PlayerSnapshot sender, string[] args)
        {
            string p = _settings.CommandPrefix;
            string arg = string.Join(" ", args).Trim().ToLowerInvariant();
            if (arg != "on" && arg != "off")
            {
                Reply(RefSlotKey == null
                    ? $"Referee ghost slot is OFF. {p}refslot on makes you (the host) a ghost referee each game."
                    : $"Referee ghost slot is ON: you become a ghost each game. {p}refslot off to stop.", false);
                return;
            }
            if (Tracker.InGame)
            {
                Reply("Change the referee slot in the lobby, between games.", false);
                return;
            }
            if (arg == "off")
            {
                RefSlotKey = null;
                SaveState();
                Reply("Referee ghost slot OFF: you play normally.", false);
                return;
            }
            RefSlotKey = sender.Key;
            SaveState();
            Reply("Referee ghost slot ON: you become a ghost at the start of each game (never impostor, no tasks, not scored). " +
                  "Set the lobby to 11 players. Zoom out with the mouse wheel or + and -.", false);
        }
    }
}
