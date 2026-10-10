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

        /// <summary>
        /// Experimental: tell the other players' games the referee is dead too (an exile message,
        /// sent once the game is under way), so their meeting screens cross the referee out. Off by
        /// default: an exile message at the very start of a game got the host kicked. Saved.
        /// </summary>
        public bool RefDeadForAll { get; private set; }

        /// <summary>!refdead on|off.</summary>
        private void RefDeadCommand(string[] args)
        {
            string arg = string.Join(" ", args).Trim().ToLowerInvariant();
            if (arg != "on" && arg != "off")
            {
                Reply($"Show the referee as dead to players is {(RefDeadForAll ? "ON" : "OFF")}.", false);
                return;
            }
            RefDeadForAll = arg == "on";
            SaveState();
            Reply(RefDeadForAll
                ? "Show the referee as dead to players ON (experimental): about 10 seconds into each game, the other players' games are told the referee died. If the host gets kicked when it's sent, turn this off."
                : "Show the referee as dead to players OFF.", false);
        }

        /// <summary>The referee's player ID in this lobby, if the slot is on and they're here.</summary>
        public byte? RefSlotPlayerId(IReadOnlyList<PlayerSnapshot> players) =>
            RefSlotKey == null ? null : players.FirstOrDefault(p => p.Key == RefSlotKey)?.PlayerId;

        /// <summary>The lobby without the referee ghost: who actually plays.</summary>
        public IReadOnlyList<PlayerSnapshot> WithoutReferee(IReadOnlyList<PlayerSnapshot> players) =>
            RefSlotKey == null ? players : players.Where(p => p.Key != RefSlotKey).ToList();

        /// <summary>!refslot on|off. Only the host can be the referee ghost.</summary>
        private void RefSlotCommand(PlayerSnapshot sender, string[] args)
        {
            string arg = string.Join(" ", args).Trim().ToLowerInvariant();
            if (arg != "on" && arg != "off")
            {
                Reply(RefSlotKey == null
                    ? $"Referee ghost slot is OFF. Turn on Referee ghost slot in The Button to be a ghost referee each game."
                    : $"Referee ghost slot is ON: you become a ghost each game. Turn it off in The Button to stop.", false);
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
