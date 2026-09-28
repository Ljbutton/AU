using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TournamentTracker.Stats
{
    /// <summary>
    /// The tournament point sheet. Impostors and crewmates score on different rules, and the
    /// win or loss points depend on how the game ended.
    /// </summary>
    public static class Scoring
    {
        /// <summary>Scores every player, then lifts or lowers the first crewmate killed to their share of the crew average.</summary>
        public static void ScoreGame(GameRecord game, ScoringRules r)
        {
            foreach (var p in game.Players)
            {
                p.PointBreakdown = game.Winner != null ? Breakdown(p, game, r) : new List<PointLine>();
                p.Points = Total(p.PointBreakdown);
            }
            if (game.Winner == null || r.DiedFirstShareOfCrewAverage <= 0) return;

            var first = game.Players.FirstOrDefault(p => !p.IsImpostor && p.DiedFirst && p.DeathCause == "Killed");
            if (first == null) return;
            var teammates = game.Players.Where(p => !p.IsImpostor && p != first && p.DeathCause != "Disconnected").ToList();
            if (teammates.Count == 0) return;

            double average = teammates.Average(p => p.Points);
            double target = Round(r.DiedFirstShareOfCrewAverage * average, r.BonusRounding);
            double change = target - first.Points;
            if (change != 0)
            {
                first.PointBreakdown.Add(new PointLine(
                    $"Died first: {Percent(r.DiedFirstShareOfCrewAverage)} of crew average {OneDecimal(average)}", change));
                first.Points = target;
            }
        }

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

                // Leaving doesn't dodge a loss, but a player who left doesn't share the win.
                if (game.Winner != null && (!left || !won))
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
                            case "Tasks": Add("Lost to tasks" + Left(left), r.ImpostorTaskLoss); break;
                            case "Vote": Add("Lost to vote" + Left(left), r.ImpostorVoteLoss); break;
                        }
                    }
                }
            }
            else
            {
                if (p.AllTasksDone) Add("Completed tasks", r.CompletedTasks);
                Add("Correct vote out", r.CorrectVoteOut, p.EjectVotesOnImpostor);
                Add("Caught and voted out killer", r.CaughtKiller, p.CaughtKiller);
                if (p.DeathCause == "Killed") Add("Got killed", r.GotKilled);
                Add("Incorrect vote out", r.IncorrectVoteOut, p.EjectVotesOnCrewmate);

                int reads = p.ReadVotesCorrect + p.ReadVotesIncorrect;
                if (p.ReadVotesCorrect > 0)
                    Add($"Reads {p.ReadVotesCorrect}/{reads} on impostors", ReadBonus(p.ReadVotesCorrect, p.ReadVotesIncorrect, r));
                if (p.TasksTotal > 0)
                {
                    double effort = TaskEffort(p, r.LongTaskWeight);
                    Add($"Tasks {Percent(effort)}", Scaled(r.TaskPercentBonus, effort, r.BonusRounding));
                }

                if (game.Winner != null && (!left || !won))
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
                    else if (kind == "Sabotage" && (p.DeathCause == null || p.LeftAlive))
                    {
                        Add("Lost to sabotage while alive", r.CrewSabotageLossAlive);
                    }
                    else if (kind != "Disconnect")
                    {
                        Add("Lost" + Left(left), r.CrewOtherLoss);
                    }
                }
            }
            return lines;
        }

        /// <summary>
        /// One <see cref="ScoringRules.ReadVotePoints"/> per read on an impostor, capped at
        /// <see cref="ScoringRules.ReadVoteBonus"/>, times the share of reads that were right.
        /// </summary>
        public static double ReadBonus(int correct, int incorrect, ScoringRules r)
        {
            if (correct <= 0) return 0;
            double earned = Math.Min(r.ReadVoteBonus, r.ReadVotePoints * correct);
            return Round(earned * correct / (correct + incorrect), r.BonusRounding);
        }

        /// <summary>Share of the task work done, a long task counting <paramref name="longWeight"/> times a short one.</summary>
        public static double TaskEffort(GamePlayer p, double longWeight)
        {
            if (p.TasksTotal <= 0) return 0;
            double extra = Math.Max(0, longWeight - 1);
            double total = p.TasksTotal + extra * p.LongTasksTotal;
            return total <= 0 ? 0 : Math.Clamp((p.TasksCompleted + extra * p.LongTasksCompleted) / total, 0, 1);
        }

        /// <summary><paramref name="max"/> scaled by <paramref name="share"/> (0 to 1), rounded to a multiple of <paramref name="step"/>.</summary>
        public static double Scaled(double max, double share, double step)
        {
            return Round(max * Math.Clamp(share, 0, 1), step);
        }

        /// <summary>Rounds to a multiple of <paramref name="step"/>, halves away from zero; 0 keeps the exact value.</summary>
        public static double Round(double value, double step) =>
            step > 0 ? Math.Round(value / step, MidpointRounding.AwayFromZero) * step : value;

        private static string Left(bool left) => left ? " (left the game)" : "";

        private static string OneDecimal(double value)
        {
            double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            if (rounded == 0) rounded = 0;   // no "-0"
            return rounded.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static string Percent(double share) => (int)Math.Round(100 * share, MidpointRounding.AwayFromZero) + "%";

        public static double Total(IEnumerable<PointLine> lines) => lines.Sum(l => l.Points);
    }
}
