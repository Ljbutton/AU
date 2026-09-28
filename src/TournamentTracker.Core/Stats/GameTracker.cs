using System;
using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker.Stats
{
    /// <summary>
    /// Turns the stream of game events the host sees into a <see cref="GameRecord"/>.
    /// Every method is safe to call when no game is running; it simply does nothing.
    /// </summary>
    public sealed class GameTracker
    {
        private readonly ScoringRules _rules;
        private GameRecord? _game;
        private MeetingRecord? _openMeeting;
        private readonly Dictionary<(byte, string), DateTime> _recentSabotages = new Dictionary<(byte, string), DateTime>();

        public GameTracker(ScoringRules rules)
        {
            _rules = rules;
        }

        public GameRecord? Current => _game;
        public bool InGame => _game != null;

        /// <summary>Raised for every timeline entry, for the live feed.</summary>
        public event Action<TimelineEvent>? EventRecorded;

        public GameRecord Start(int gameNumber, string tournament, string lobbyCode, string map,
            IEnumerable<PlayerSnapshot> players, DateTime nowUtc)
        {
            _game = new GameRecord
            {
                GameNumber = gameNumber,
                Tournament = tournament,
                LobbyCode = lobbyCode,
                Map = map,
                StartedUtc = nowUtc,
            };
            _openMeeting = null;
            _recentSabotages.Clear();

            foreach (var s in players)
            {
                if (s.Disconnected) continue;
                _game.Players.Add(new GamePlayer
                {
                    PlayerId = s.PlayerId,
                    Key = s.Key,
                    Name = s.Name,
                    ColorId = s.ColorId,
                    Role = s.Role,
                    IsImpostor = s.IsImpostor,
                    TasksTotal = s.IsImpostor ? 0 : s.TasksTotal,
                    TasksCompleted = s.IsImpostor ? 0 : s.TasksCompleted,
                });
            }

            var impostors = _game.Players.Where(p => p.IsImpostor).Select(p => p.Label);
            Add(nowUtc, "start", $"Game started on {map}. Impostors: {string.Join(", ", impostors)}");
            return _game;
        }

        public void Kill(byte killerId, byte victimId, DateTime nowUtc)
        {
            var killer = _game?.ById(killerId);
            var victim = _game?.ById(victimId);
            if (_game == null || victim == null || victim.DeathCause != null) return;

            bool firstBlood = _game.Players.All(p => p.DeathCause != "Killed");
            victim.DeathCause = "Killed";
            victim.DiedAtSeconds = Elapsed(nowUtc);
            victim.KilledByKey = killer?.Key;
            victim.DiedFirst = firstBlood;
            if (killer != null)
            {
                killer.Kills++;
                if (firstBlood) killer.FirstBlood = true;
            }

            string who = killer?.Label ?? "Someone";
            Add(nowUtc, "kill", $"{who} killed {victim.Label}{(firstBlood ? " (first blood)" : "")}");
        }

        /// <summary>A meeting was called. <paramref name="bodyId"/> is null for the emergency button.</summary>
        public void MeetingCalled(byte? callerId, byte? bodyId, DateTime nowUtc)
        {
            if (_game == null || _openMeeting != null) return;

            var caller = callerId.HasValue ? _game.ById(callerId.Value) : null;
            var body = bodyId.HasValue ? _game.ById(bodyId.Value) : null;

            _openMeeting = new MeetingRecord
            {
                Number = _game.Meetings.Count + 1,
                AtSeconds = Elapsed(nowUtc),
                CallerKey = caller?.Key,
                BodyKey = body?.Key,
            };
            _game.Meetings.Add(_openMeeting);

            if (caller != null)
            {
                if (body != null) caller.BodiesReported++;
                else caller.MeetingsCalled++;
            }

            string who = caller?.Label ?? "Someone";
            Add(nowUtc, "meeting", body != null
                ? $"{who} reported {body.Label}'s body"
                : $"{who} called an emergency meeting");
        }

        public void VotingComplete(IReadOnlyList<VoteCast> votes, byte? exiledId, bool tie, DateTime nowUtc)
        {
            if (_game == null) return;
            var meeting = _openMeeting ?? StartUnattributedMeeting(nowUtc);
            _openMeeting = null;

            foreach (var v in votes)
            {
                var voter = _game.ById(v.VoterId);
                if (voter == null || v.VotedForId == VoteCast.DeadVote) continue;

                var record = new VoteRecord { VoterKey = voter.Key };
                if (v.VotedForId == VoteCast.SkippedVote)
                {
                    record.Skipped = true;
                    voter.Skips++;
                }
                else if (v.VotedForId == VoteCast.MissedVote || v.VotedForId == VoteCast.HasNotVoted)
                {
                    record.Missed = true;
                    voter.MissedVotes++;
                }
                else
                {
                    var target = _game.ById(v.VotedForId);
                    if (target == null) continue;
                    record.TargetKey = target.Key;
                    voter.VotesCast++;
                    target.VotesReceived++;
                    // Only crewmates are graded: an impostor always knows who is who.
                    if (!voter.IsImpostor)
                    {
                        if (target.IsImpostor) voter.CorrectVotes++;
                        else voter.IncorrectVotes++;
                    }
                }
                meeting.Votes.Add(record);
            }

            meeting.Tie = tie;
            var exiled = exiledId.HasValue ? _game.ById(exiledId.Value) : null;
            string tally = string.Join(", ", meeting.Votes.Select(DescribeVote));

            if (exiled != null)
            {
                // Votes only score when they put someone out: see Scoring.
                foreach (var v in meeting.Votes.Where(v => v.TargetKey == exiled.Key))
                {
                    var voter = _game.ByKey(v.VoterKey);
                    if (voter == null) continue;
                    if (exiled.IsImpostor && !voter.IsImpostor) voter.EjectVotesOnImpostor++;
                    else if (!exiled.IsImpostor) voter.EjectVotesOnCrewmate++;
                }
                if (exiled.IsImpostor)
                {
                    exiled.ImpostorEjectOrder = 1 + _game.Players.Count(p => p.ImpostorEjectOrder.HasValue);
                    var caller = meeting.CallerKey == null ? null : _game.ByKey(meeting.CallerKey);
                    if (caller != null && !caller.IsImpostor) caller.CaughtKiller++;
                }

                meeting.EjectedKey = exiled.Key;
                meeting.EjectedWasImpostor = exiled.IsImpostor;
                if (exiled.DeathCause == null)
                {
                    exiled.DeathCause = "Ejected";
                    exiled.DiedAtSeconds = Elapsed(nowUtc);
                }
                Add(nowUtc, "eject", $"{exiled.Label} was ejected ({(exiled.IsImpostor ? "Impostor" : "not an Impostor")}). Votes: {tally}");
            }
            else
            {
                Add(nowUtc, "eject", $"No one was ejected{(tie ? " (tie)" : "")}. Votes: {tally}");
            }
        }

        /// <summary>The meeting screen closed; lets a meeting whose votes never arrived be closed off.</summary>
        public void MeetingClosed() => _openMeeting = null;

        public void TaskCompleted(byte playerId, DateTime nowUtc)
        {
            var p = _game?.ById(playerId);
            if (p == null || p.IsImpostor) return;
            p.TasksCompleted++;
            if (p.TasksTotal > 0 && p.TasksCompleted == p.TasksTotal)
                Add(nowUtc, "tasks", $"{p.Label} finished all {p.TasksTotal} tasks");
        }

        public void Sabotage(byte playerId, string system, DateTime nowUtc)
        {
            var p = _game?.ById(playerId);
            if (p == null) return;

            // The game can route one sabotage through two overloads; count it once.
            var key = (playerId, system);
            if (_recentSabotages.TryGetValue(key, out var last) && (nowUtc - last).TotalSeconds < 2) return;
            _recentSabotages[key] = nowUtc;

            p.Sabotages++;
            Add(nowUtc, "sabotage", $"{p.Label} sabotaged {system}");
        }

        public void Disconnected(byte playerId, DateTime nowUtc)
        {
            var p = _game?.ById(playerId);
            if (p == null || p.DeathCause == "Disconnected") return;
            if (p.DeathCause == null) p.DiedAtSeconds = Elapsed(nowUtc);
            p.DeathCause = "Disconnected";
            Add(nowUtc, "disconnect", $"{p.Label} disconnected");
        }

        /// <summary>
        /// Closes the game. <paramref name="finalPlayers"/> corrects task counts and deaths the
        /// hooks may have missed. Returns the finished record and resets the tracker.
        /// </summary>
        public GameRecord? End(string reason, string? winner, IEnumerable<PlayerSnapshot> finalPlayers, DateTime nowUtc)
        {
            if (_game == null) return null;
            var game = _game;

            foreach (var s in finalPlayers)
            {
                var p = game.ById(s.PlayerId);
                if (p == null) continue;
                if (!p.IsImpostor && s.TasksTotal > 0)
                {
                    p.TasksTotal = s.TasksTotal;
                    p.TasksCompleted = Math.Max(p.TasksCompleted, s.TasksCompleted);
                    p.TasksCompleted = Math.Min(p.TasksCompleted, p.TasksTotal);
                }
                if (p.DeathCause == null && s.IsDead)
                {
                    p.DeathCause = "Killed";
                    p.DiedAtSeconds = Elapsed(nowUtc);
                }
                if (p.DeathCause == null && s.Disconnected) p.DeathCause = "Disconnected";
            }

            game.EndedUtc = nowUtc;
            game.EndReason = reason;
            game.Winner = winner;

            foreach (var p in game.Players)
            {
                p.Survived = p.DeathCause == null;
                p.Won = winner != null && p.IsImpostor == (winner == Outcome.Impostors) && p.DeathCause != "Disconnected";
                p.PointBreakdown = winner != null ? Scoring.Breakdown(p, game, _rules) : new List<PointLine>();
                p.Points = Scoring.Total(p.PointBreakdown);
            }

            Add(nowUtc, "end", winner != null
                ? $"{winner} win ({Outcome.Describe(reason)})"
                : $"Game ended without a result ({reason})");

            _game = null;
            _openMeeting = null;
            return game;
        }

        private MeetingRecord StartUnattributedMeeting(DateTime nowUtc)
        {
            var m = new MeetingRecord { Number = _game!.Meetings.Count + 1, AtSeconds = Elapsed(nowUtc) };
            _game.Meetings.Add(m);
            return m;
        }

        private string DescribeVote(VoteRecord v)
        {
            string voter = Short(v.VoterKey);
            if (v.Skipped) return voter + "→skip";
            if (v.Missed) return voter + "→none";
            return voter + "→" + Short(v.TargetKey!);
        }

        private string Short(string key)
        {
            var p = _game?.ByKey(key);
            return p == null ? "?" : Colors.Name(p.ColorId);
        }

        private double Elapsed(DateTime nowUtc) => Math.Max(0, (nowUtc - _game!.StartedUtc).TotalSeconds);

        private void Add(DateTime nowUtc, string kind, string text)
        {
            var e = new TimelineEvent { AtSeconds = Math.Round(Elapsed(nowUtc), 1), Kind = kind, Text = text };
            _game!.Timeline.Add(e);
            EventRecorded?.Invoke(e);
        }
    }
}
