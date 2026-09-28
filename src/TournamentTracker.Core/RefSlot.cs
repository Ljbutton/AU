using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker
{
    /// <summary>
    /// The referee ghost slot: an extra player (the host, or someone they pick) who is made a
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

        private void RefSlotCommand(PlayerSnapshot sender, string[] args)
        {
            string p = _settings.CommandPrefix;
            string arg = string.Join(" ", args).Trim();
            if (arg.Length == 0)
            {
                var who = RefSlotKey == null ? null : Players.FirstOrDefault(x => x.Key == RefSlotKey);
                Reply(RefSlotKey == null
                    ? $"Referee ghost slot is OFF. {p}refslot on makes you the ghost referee; {p}refslot <player> picks someone else."
                    : $"Referee ghost slot is ON: {(who != null ? who.ToString() : "the saved referee (not in this lobby)")} becomes a ghost each game. {p}refslot off to stop.", false);
                return;
            }
            if (Tracker.InGame)
            {
                Reply("Change the referee slot in the lobby, between games.", false);
                return;
            }
            if (arg.Equals("off", System.StringComparison.OrdinalIgnoreCase))
            {
                RefSlotKey = null;
                SaveState();
                Reply("Referee ghost slot OFF: everyone plays normally.", false);
                return;
            }
            var target = arg.Equals("on", System.StringComparison.OrdinalIgnoreCase) ? sender : FindPlayer(arg);
            if (target == null)
            {
                Reply($"No player matches \"{arg}\".", false);
                return;
            }
            RefSlotKey = target.Key;
            SaveState();
            Reply($"Referee ghost slot ON: {target} becomes a ghost at the start of each game (never impostor, no tasks, not scored). " +
                  "Set the lobby to 11 players. A ghost with the mod installed can zoom out with the mouse wheel.", false);
            if (target.Key != sender.Key) Reply($"{target.Name} is the referee ghost for the next games.", true);
        }
    }
}
