using System;
using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker.Stats
{
    /// <summary>
    /// The tournament point sheet. Impostors and crewmates score on different rules, and the
    /// win or loss points depend on how the game ended.
    /// </summary>
    public static class Scoring
    {
        public static List<PointLine> Breakdown(GamePlayer p, GameRecord game, ScoringRules r)
        {
            var lines = new List<PointLine>();
            void Add(string rule, double points, int times = 1)
            {
                if (times > 0 && points != 0) lines.Add(new PointLine(times > 1 ? $"{rule} x{times}" : rule, points * times));
            }

            string kind = Outcome.Kind(game.EndReason);
            bool left = p.DeathCause == "Disconnected";
            bool won = game.Winner != null && p.IsImpostor == (game.Winner == Outcome.Impostors);

            if (p.IsImpostor)
            {
                Add("Kill", r.Kill, p.Kills);
                if (p.FirstBlood) Add("First blood", r.FirstBlood);
                Add("Voted a crewmate out", r.VotedCrewmateOut, p.EjectVotesOnCrewmate);
                if (p.ImpostorEjectOrder == 1) Add("Voted out first", r.VotedOutFirst);
                else if (p.ImpostorEjectOrder > 1) Add("Voted out last", r.VotedOutLast);

                if (!left && game.Winner != null)
                {
                    if (won)
                    {
                        switch (kind)
                        {
                            case "Sabotage": Add("Sabotage win", r.ImpostorSabotageWin); break;
                            case "Vote": Add("Vote win", r.ImpostorVoteWin); break;
                            case "Kill": Add("Kill win", r.ImpostorKillWin); break;
                            case "Disconnect": Add("Win by disconnect", r.DisconnectWin); break;
                        }
                    }
                    else
                    {
                        switch (kind)
                        {
                            case "Tasks": Add("Lost to tasks", r.ImpostorTaskLoss); break;
                            case "Vote": Add("Lost to vote", r.ImpostorVoteLoss); break;
                        }
                    }
                }
            }
            else
            {
                if (p.AllTasksDone) Add("Completed tasks", r.CompletedTasks);
                Add("Correct vote out", r.CorrectVoteOut, p.EjectVotesOnImpostor);
                Add("Caught and voted out killer", r.CaughtKiller, p.CaughtKiller);
                if (p.DiedFirst) Add("Died first", r.DiedFirst);
                else if (p.DeathCause == "Killed") Add("Got killed", r.GotKilled);
                Add("Incorrect vote out", r.IncorrectVoteOut, p.EjectVotesOnCrewmate);

                int graded = p.CorrectVotes + p.IncorrectVotes;
                if (graded > 0)
                    Add($"Vote accuracy {Percent(p.CorrectVotes, graded)}%", Scaled(r.VoteAccuracyBonus, (double)p.CorrectVotes / graded, r.BonusRounding));
                if (p.TasksTotal > 0)
                    Add($"Tasks {Percent(p.TasksCompleted, p.TasksTotal)}%", Scaled(r.TaskPercentBonus, (double)p.TasksCompleted / p.TasksTotal, r.BonusRounding));

                if (!left && game.Winner != null)
                {
                    if (won)
                    {
                        switch (kind)
                        {
                            case "Tasks": Add("Task win", r.CrewTaskWin); break;
                            case "Vote": Add("Vote win", r.CrewVoteWin); break;
                            case "Disconnect": Add("Win by disconnect", r.DisconnectWin); break;
                        }
                    }
                    else if (kind == "Sabotage" && p.DeathCause == null)
                    {
                        Add("Lost to sabotage while alive", r.CrewSabotageLossAlive);
                    }
                    else if (kind != "Disconnect")
                    {
                        Add("Lost", r.CrewOtherLoss);
                    }
                }
            }
            return lines;
        }

        /// <summary><paramref name="max"/> scaled by <paramref name="share"/> (0 to 1), rounded to a multiple of <paramref name="step"/>.</summary>
        public static double Scaled(double max, double share, double step)
        {
            double value = max * Math.Clamp(share, 0, 1);
            return step > 0 ? Math.Round(value / step, MidpointRounding.AwayFromZero) * step : value;
        }

        private static int Percent(int part, int whole) => (int)Math.Round(100.0 * part / whole, MidpointRounding.AwayFromZero);

        public static double Total(IEnumerable<PointLine> lines) => lines.Sum(l => l.Points);
    }
}
