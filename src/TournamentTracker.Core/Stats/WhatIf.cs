using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.Stats
{
    /// <summary>
    /// "Points on the line": what each player would score if the game ended right now, for each way it
    /// could end, with the tournament's own point sheet (<see cref="Scoring"/>).
    /// </summary>
    public static class WhatIf
    {
        /// <summary>The ways a running game is likely to end, as the game names them.</summary>
        public static readonly (string Name, string Reason, string Winner)[] Endings =
        {
            ("impostors", "ImpostorsByKill", Outcome.Impostors),
            ("crewVote", "HumansByVote", Outcome.Crewmates),
            ("crewTasks", "HumansByTask", Outcome.Crewmates),
        };

        /// <summary>For each ending: each player's points (by player key) if the game ended that way now.</summary>
        public static Dictionary<string, Dictionary<string, double>> Points(GameRecord live, ScoringRules rules)
        {
            var result = new Dictionary<string, Dictionary<string, double>>();
            foreach (var (name, reason, winner) in Endings)
            {
                var game = JsonSerializer.Deserialize<GameRecord>(JsonSerializer.Serialize(live))!;
                game.EndReason = reason;
                game.Winner = winner;
                foreach (var p in game.Players)
                {
                    p.Survived = p.DeathCause == null;
                    p.Won = p.IsImpostor == (winner == Outcome.Impostors) && p.DeathCause != "Disconnected";
                    // A task win means every task got done.
                    if (name == "crewTasks" && !p.IsImpostor) p.TasksCompleted = p.TasksTotal;
                }
                Scoring.ScoreGame(game, rules);
                result[name] = game.Players.ToDictionary(p => p.Key, p => Math.Round(p.Points, 2));
            }
            return result;
        }
    }
}
