namespace TournamentTracker.Stats
{
    public static class Outcome
    {
        public const string Crewmates = "Crewmates";
        public const string Impostors = "Impostors";

        /// <summary>
        /// Maps the game's GameOverReason name to the winning team. Works on the name rather
        /// than the enum so it survives the game renaming "Humans*" to "Crewmates*".
        /// </summary>
        public static string? WinnerFromReason(string reason)
        {
            string r = reason.ToLowerInvariant();
            bool crew = r.Contains("human") || r.Contains("crewmate");
            bool imp = r.Contains("impostor") || r.Contains("imposter");

            if (r.Contains("disconnect"))
            {
                // "ImpostorDisconnect": the impostors left, so the crew wins, and vice versa.
                if (imp) return Crewmates;
                if (crew) return Impostors;
                return null;
            }

            if (imp && !crew) return Impostors;
            if (crew && !imp) return Crewmates;
            return null;
        }

        public static string Describe(string reason) => reason switch
        {
            "HumansByVote" or "CrewmatesByVote" => "Impostors voted out",
            "HumansByTask" or "CrewmatesByTask" => "All tasks completed",
            "ImpostorByVote" or "ImpostorsByVote" => "Wrong ejection",
            "ImpostorByKill" or "ImpostorsByKill" => "Crew killed",
            "ImpostorBySabotage" or "ImpostorsBySabotage" => "Sabotage",
            "ImpostorDisconnect" or "ImpostorsDisconnect" => "Impostors disconnected",
            "HumansDisconnect" or "CrewmatesDisconnect" or "CrewmateDisconnect" => "Crewmates disconnected",
            _ => reason,
        };
    }
}
