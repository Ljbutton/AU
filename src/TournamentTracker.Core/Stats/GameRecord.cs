using System;
using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker.Stats
{
    /// <summary>Everything recorded about one game. Serialised as-is to games/*.json.</summary>
    public sealed class GameRecord
    {
        public int GameNumber { get; set; }
        public string Tournament { get; set; } = "";
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

        public bool Counted => Winner != null;

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
        public string? KilledByKey { get; set; }

        /// <summary>"Killed", "Ejected" or "Disconnected"; null while alive.</summary>
        public string? DeathCause { get; set; }
        public double? DiedAtSeconds { get; set; }

        public int TasksCompleted { get; set; }
        public int TasksTotal { get; set; }

        public int MeetingsCalled { get; set; }
        public int BodiesReported { get; set; }
        public int VotesCast { get; set; }
        public int CorrectVotes { get; set; }
        public int IncorrectVotes { get; set; }
        public int Skips { get; set; }
        public int MissedVotes { get; set; }
        public int VotesReceived { get; set; }
        public int Sabotages { get; set; }

        public bool Won { get; set; }
        public bool Survived { get; set; }
        public int Points { get; set; }

        public string Label => $"{Colors.Name(ColorId)} ({Name})";
        public bool AllTasksDone => !IsImpostor && TasksTotal > 0 && TasksCompleted >= TasksTotal;
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
