using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.Stats
{
    public sealed class PlayerTotals
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public int LastColorId { get; set; }

        public int Games { get; set; }
        public int Wins { get; set; }
        public int ImpostorGames { get; set; }
        public int ImpostorWins { get; set; }
        public int CrewGames { get; set; }
        public int CrewWins { get; set; }

        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int TimesEjected { get; set; }
        public int Survived { get; set; }
        public int Disconnects { get; set; }
        public int FirstDeaths { get; set; }

        public int CorrectVotes { get; set; }
        public int IncorrectVotes { get; set; }
        public int Skips { get; set; }
        public int MissedVotes { get; set; }
        public int MeetingsCalled { get; set; }
        public int BodiesReported { get; set; }

        public int TasksCompleted { get; set; }
        public int TasksTotal { get; set; }
        public int Sabotages { get; set; }

        public int FirstBloods { get; set; }
        public int KillersCaught { get; set; }

        public double Points { get; set; }

        public int Losses => Games - Wins;
        public double VoteAccuracy => CorrectVotes + IncorrectVotes == 0 ? 0 : (double)CorrectVotes / (CorrectVotes + IncorrectVotes);
        public double TaskCompletion => TasksTotal == 0 ? 0 : (double)TasksCompleted / TasksTotal;
    }

    /// <summary>Cumulative per-player totals for one tournament, persisted as JSON.</summary>
    public sealed class StatsStore
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public string Tournament { get; set; } = "";
        public int GamesRecorded { get; set; }
        public int CrewWins { get; set; }
        public int ImpostorWins { get; set; }
        public Dictionary<string, PlayerTotals> Players { get; set; } = new Dictionary<string, PlayerTotals>();

        /// <summary>Games finished per lobby label, counted or not, so each host's games number 1, 2, 3…</summary>
        public Dictionary<string, int> GamesByHost { get; set; } = new Dictionary<string, int>();

        public int NextGameNumber(string host) => (GamesByHost.TryGetValue(host, out var n) ? n : 0) + 1;

        public void NoteFinished(GameRecord game) => GamesByHost[game.Host] = Math.Max(game.GameNumber, GamesByHost.TryGetValue(game.Host, out var n) ? n : 0);

        public void Apply(GameRecord game)
        {
            if (game.Counted) Add(game, 1);
        }

        /// <summary>Takes a game back out of the totals, e.g. one voided after it was applied.</summary>
        public void Remove(GameRecord game)
        {
            Add(game, -1);
            foreach (var key in Players.Where(kv => kv.Value.Games <= 0).Select(kv => kv.Key).ToList()) Players.Remove(key);
        }

        private void Add(GameRecord game, int n)
        {
            if (game.Winner == null) return;

            GamesRecorded += n;
            if (game.Winner == Outcome.Impostors) ImpostorWins += n;
            else CrewWins += n;

            var firstDeath = game.Players
                .Where(p => p.DeathCause == "Killed" && p.DiedAtSeconds.HasValue)
                .OrderBy(p => p.DiedAtSeconds)
                .FirstOrDefault();

            foreach (var p in game.Players)
            {
                if (!Players.TryGetValue(p.Key, out var t))
                {
                    t = new PlayerTotals { Key = p.Key };
                    Players[p.Key] = t;
                }

                if (n > 0)
                {
                    t.Name = p.Name;
                    t.LastColorId = p.ColorId;
                }
                t.Games += n;
                if (p.Won) t.Wins += n;
                if (p.IsImpostor)
                {
                    t.ImpostorGames += n;
                    if (p.Won) t.ImpostorWins += n;
                }
                else
                {
                    t.CrewGames += n;
                    if (p.Won) t.CrewWins += n;
                }

                t.Kills += n * p.Kills;
                if (p.DeathCause == "Killed") t.Deaths += n;
                if (p.DeathCause == "Ejected") t.TimesEjected += n;
                if (p.DeathCause == "Disconnected") t.Disconnects += n;
                if (p.Survived) t.Survived += n;
                if (ReferenceEquals(p, firstDeath)) t.FirstDeaths += n;

                t.CorrectVotes += n * p.CorrectVotes;
                t.IncorrectVotes += n * p.IncorrectVotes;
                t.Skips += n * p.Skips;
                t.MissedVotes += n * p.MissedVotes;
                t.MeetingsCalled += n * p.MeetingsCalled;
                t.BodiesReported += n * p.BodiesReported;

                t.TasksCompleted += n * p.TasksCompleted;
                t.TasksTotal += n * p.TasksTotal;
                t.Sabotages += n * p.Sabotages;
                if (p.FirstBlood) t.FirstBloods += n;
                t.KillersCaught += n * p.CaughtKiller;
                t.Points += n * p.Points;
            }
        }

        public IReadOnlyList<PlayerTotals> Leaderboard() => Players.Values
            .OrderByDescending(t => t.Points)
            .ThenByDescending(t => t.Wins)
            .ThenByDescending(t => t.Kills)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        public PlayerTotals? Find(string key) => Players.TryGetValue(key, out var t) ? t : null;

        public static StatsStore Load(string path, string tournament)
        {
            try
            {
                if (File.Exists(path))
                {
                    var store = JsonSerializer.Deserialize<StatsStore>(File.ReadAllText(path), Json);
                    if (store != null) return store;
                }
            }
            catch (Exception)
            {
                // A corrupt file must never stop a tournament game; keep a copy and start over.
                File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
            }
            return new StatsStore { Tournament = tournament };
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
