using System;
using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker.Stats
{
    /// <summary>Everything recorded about one game. Serialised as-is to games/*.json.</summary>
    public sealed class GameRecord
    {
        public int GameNumber { get; set; }

        /// <summary>The lobby label (the host's name unless LobbyLabel is set). Games are numbered per host.</summary>
        public string Host { get; set; } = "";

        /// <summary>Unique across hosts and restarts: label, number and start time, e.g. "LJ-3-20261003-192144".</summary>
        public string Id { get; set; } = "";

        /// <summary>What people see: "LJ-3", or just "3" without a label.</summary>
        public string Name => Host.Length > 0 ? $"{Host}-{GameNumber}" : GameNumber.ToString();
        public string Tournament { get; set; } = "";
        public string TournamentId { get; set; } = "";

        /// <summary>"Preliminary", "Tournament" or "Standard".</summary>
        public string Mode { get; set; } = "";

        /// <summary>Tournament round (1, 2, 3…); 0 when rounds aren't used.</summary>
        public int Round { get; set; }

        /// <summary>Preliminaries: the server the game was played in.</summary>
        public string Server { get; set; } = "";
        public string LobbyCode { get; set; } = "";
        public string Map { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public DateTime? EndedUtc { get; set; }

        /// <summary>"Crewmates", "Impostors", or null when the game was abandoned.</summary>
        public string? Winner { get; set; }
        public string EndReason { get; set; } = "";

        public List<GamePlayer> Players { get; set; } = new List<GamePlayer>();
        public List<MeetingRecord> Meetings { get; set; } = new List<MeetingRecord>();
        public List<TimelineEvent> Timeline { get; set; } = new List<TimelineEvent>();

        /// <summary>
        /// Thrown out by the host or a referee (a restarted game): kept for the record, but it
        /// scores nothing and doesn't count toward any leaderboard or round.
        /// </summary>
        public bool Voided { get; set; }
        public string VoidReason { get; set; } = "";

        public bool Counted => Winner != null && !Voided;

        public double DurationSeconds => ((EndedUtc ?? StartedUtc) - StartedUtc).TotalSeconds;

        public GamePlayer? ById(byte playerId) => Players.FirstOrDefault(p => p.PlayerId == playerId);
        public GamePlayer? ByKey(string key) => Players.FirstOrDefault(p => p.Key == key);
    }

    public sealed class GamePlayer
    {
        public byte PlayerId { get; set; }
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public int ColorId { get; set; }
        public string Role { get; set; } = "";
        public bool IsImpostor { get; set; }

        public int Kills { get; set; }
        public bool FirstBlood { get; set; }
        public string? KilledByKey { get; set; }
        public bool DiedFirst { get; set; }

        /// <summary>"Killed", "Ejected" or "Disconnected"; null while alive.</summary>
        public string? DeathCause { get; set; }
        public double? DiedAtSeconds { get; set; }

        public int TasksCompleted { get; set; }
        public int TasksTotal { get; set; }
        /// <summary>The long tasks among those (a long task weighs more in the task bonus).</summary>
        public int LongTasksCompleted { get; set; }
        public int LongTasksTotal { get; set; }

        public int MeetingsCalled { get; set; }
        public int BodiesReported { get; set; }
        public int VotesCast { get; set; }
        public int CorrectVotes { get; set; }
        public int IncorrectVotes { get; set; }
        /// <summary>Crewmate votes for someone who wasn't ejected that meeting, split by what they were.</summary>
        public int ReadVotesCorrect { get; set; }
        public int ReadVotesIncorrect { get; set; }
        public int Skips { get; set; }
        public int MissedVotes { get; set; }
        public int VotesReceived { get; set; }

        /// <summary>Votes this player cast for someone who then got ejected, split by what the ejected player was.</summary>
        public int EjectVotesOnImpostor { get; set; }
        public int EjectVotesOnCrewmate { get; set; }

        /// <summary>Meetings this crewmate called that ended with an impostor ejected.</summary>
        public int CaughtKiller { get; set; }

        /// <summary>1 for the first impostor ejected this game, 2 for the next; null if never ejected.</summary>
        public int? ImpostorEjectOrder { get; set; }

        public int Sabotages { get; set; }

        public bool Won { get; set; }
        public bool Survived { get; set; }
        public double Points { get; set; }

        /// <summary>Where the points came from, so a referee can check them against the sheet.</summary>
        public List<PointLine> PointBreakdown { get; set; } = new List<PointLine>();

        public string Label => $"{Colors.Name(ColorId)} ({Name})";
        public bool AllTasksDone => !IsImpostor && TasksTotal > 0 && TasksCompleted >= TasksTotal;
    }

    public sealed class PointLine
    {
        public PointLine() { }

        public PointLine(string rule, double points)
        {
            Rule = rule;
            Points = points;
        }

        public string Rule { get; set; } = "";
        public double Points { get; set; }
    }

    public sealed class MeetingRecord
    {
        public int Number { get; set; }
        public double AtSeconds { get; set; }
        public string? CallerKey { get; set; }

        /// <summary>The reported body, or null for an emergency button meeting.</summary>
        public string? BodyKey { get; set; }

        public List<VoteRecord> Votes { get; set; } = new List<VoteRecord>();
        public string? EjectedKey { get; set; }
        public bool? EjectedWasImpostor { get; set; }
        public bool Tie { get; set; }
    }

    public sealed class VoteRecord
    {
        public string VoterKey { get; set; } = "";

        /// <summary>The accused, or null for a skip or a missed vote.</summary>
        public string? TargetKey { get; set; }
        public bool Skipped { get; set; }
        public bool Missed { get; set; }
    }

    public sealed class TimelineEvent
    {
        public double AtSeconds { get; set; }
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
    }

    /// <summary>One cast vote as the game reports it: raw player ids plus the special skip/missed ids.</summary>
    public readonly struct VoteCast
    {
        public const byte DeadVote = 252;
        public const byte SkippedVote = 253;
        public const byte MissedVote = 254;
        public const byte HasNotVoted = 255;

        public VoteCast(byte voterId, byte votedForId)
        {
            VoterId = voterId;
            VotedForId = votedForId;
        }

        public byte VoterId { get; }
        public byte VotedForId { get; }
    }
}
