namespace TournamentTracker.Stats
{
    public static class Scoring
    {
        public static int Score(GamePlayer p, ScoringRules rules)
        {
            int points = 0;
            if (p.Won)
            {
                points += rules.Win;
                if (p.IsImpostor) points += rules.ImpostorWinBonus;
            }
            points += p.Kills * rules.Kill;
            points += p.CorrectVotes * rules.CorrectVote;
            points += p.IncorrectVotes * rules.IncorrectVote;
            points += p.BodiesReported * rules.ReportedBody;
            if (p.AllTasksDone) points += rules.AllTasksCompleted;
            if (p.Survived) points += rules.Survived;
            return points;
        }
    }
}
