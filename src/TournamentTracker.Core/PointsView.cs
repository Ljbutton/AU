using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TournamentTracker.Stats;

namespace TournamentTracker
{
    /// <summary>
    /// Point totals for The Button's Points page: for the host and the referees only (players
    /// never see these in the game or in Discord commands).
    /// </summary>
    public sealed partial class TournamentSession
    {
        private static readonly TimeSpan PointsInterval = TimeSpan.FromSeconds(3);
        private object? _points;
        private string _pointsKey = "";
        private DateTime _nextPoints;

        /// <summary>The standings sections, rebuilt when the games behind them change (checked every few seconds).</summary>
        private object? PointsForApp()
        {
            var now = _clock();
            if (_points != null && now < _nextPoints) return _points;
            _nextPoints = now + PointsInterval;

            var local = LocalGameFilesRead();
            string key = $"{Combined?.GameRecords.Count}|{Combined?.GetHashCode()}|{Round}|{LobbyLabel()}|{Links.Version}|{local.Count}|{(local.Count > 0 ? local.Max(f => f.LastWriteTimeUtc).Ticks : 0)}";
            if (_points != null && key == _pointsKey) return _points;
            _pointsKey = key;
            try { _points = BuildPoints(local); }
            catch (Exception e) { _log.Warn("Couldn't build the points view: " + e.Message); }
            return _points;
        }

        // This PC's game files, listed and read off the game's main thread (every few seconds, with
        // the points view): what the last look found, each file with its game.
        private volatile List<(FileInfo File, GameRecord? Game)> _localGames = new List<(FileInfo, GameRecord?)>();
        private readonly Dictionary<string, (DateTime Written, GameRecord? Game)> _localGameCache = new Dictionary<string, (DateTime, GameRecord?)>();
        private int _localGamesQueued;

        /// <summary>The game files as last read (and another look queued). Inline (tests, tools): read now.</summary>
        private List<FileInfo> LocalGameFilesRead()
        {
            if (System.Threading.Interlocked.Exchange(ref _localGamesQueued, 1) == 0)
                Work.Post(() =>
                {
                    try
                    {
                        var list = new List<(FileInfo, GameRecord?)>();
                        lock (_localGameCache)
                            foreach (var f in LocalGameFiles())
                            {
                                if (!_localGameCache.TryGetValue(f.FullName, out var known) || known.Written != f.LastWriteTimeUtc)
                                {
                                    GameRecord? g = null;
                                    try { g = JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(f.FullName)); } catch (Exception) { }
                                    _localGameCache[f.FullName] = known = (f.LastWriteTimeUtc, g);
                                }
                                list.Add((f, known.Game));
                            }
                        _localGames = list;
                    }
                    finally { System.Threading.Interlocked.Exchange(ref _localGamesQueued, 0); }
                });
            return _localGames.Select(x => x.File).ToList();
        }

        private List<FileInfo> LocalGameFiles()
        {
            try
            {
                var dir = new DirectoryInfo(_gamesDir);
                return dir.Exists ? dir.GetFiles("game-*.json").ToList() : new List<FileInfo>();
            }
            catch (Exception) { return new List<FileInfo>(); }
        }

        /// <summary>This PC's saved games for the tournament (every game file in the games folder).</summary>
        private List<GameRecord> LocalGames(List<FileInfo>? files = null) => (files ?? LocalGameFiles()).Select(f =>
        {
            try { return JsonSerializer.Deserialize<GameRecord>(File.ReadAllText(f.FullName)); }
            catch (Exception) { return null; }
        }).Where(g => g != null).Select(g => g!).ToList();

        private object BuildPoints(List<FileInfo> files)
        {
            bool combined = Combined != null;
            IReadOnlyList<GameRecord> games = Combined?.GameRecords ?? _localGames.Where(x => x.Game != null).Select(x => x.Game!).ToList();
            var counted = games.Where(g => g.Counted).ToList();
            var sections = new List<object>();
            string lobby = LobbyLabel();

            if (_settings.Mode == TrackerMode.Tournament && Round > 0)
            {
                int played = Stats.Standings.GamesPlayed(games, lobby, Round);
                sections.Add(Section($"Round {Round} · {(lobby.Length > 0 ? lobby + "'s lobby" : "this lobby")}",
                    $"After game {played} of {_settings.GamesPerRound}. The top {_settings.AdvanceCount} move on.",
                    Stats.Standings.Lobby(games, lobby, Round, _settings.AdvanceCount, _settings.GamesPerRound), _settings.AdvanceCount, withTotal: Round > 1));
                if (combined)
                    sections.Add(Section($"Round {Round} · every lobby", "Players move on per lobby, so there's no cut line here.",
                        Stats.Standings.Round(games, Round, _settings.AdvanceCount, _settings.GamesPerRound), 0, withTotal: Round > 1));
            }
            var all = Stats.Standings.Build(counted, "").Leaderboard()
                .Select(t => new StandingRow { Stats = t, Total = t.Points }).ToList();
            string allNote = _settings.Mode == TrackerMode.Tournament
                ? "Every counted game in every round" + (combined ? ", every lobby." : " this lobby played.")
                : _settings.Mode == TrackerMode.Preliminary
                    ? "Every counted game in this lobby. The organiser's bot posts the combined preliminary board every 10 minutes."
                    : "Every counted game in this lobby.";
            // In the first round the running total is the round table again: only show it once there's more.
            bool sameAsRound = _settings.Mode == TrackerMode.Tournament && Round > 0 && counted.All(g => g.Round == Round);
            if (!sameAsRound)
                sections.Add(Section(_settings.Mode == TrackerMode.Tournament ? "Running total" : "Leaderboard", allNote, all, 0, withTotal: false));

            return new
            {
                Combined = combined,
                Games = counted.Count,
                Voided = games.Count(g => g.Voided),
                Sections = sections,
            };
        }

        private object Section(string title, string note, List<StandingRow> rows, int cut, bool withTotal) => new
        {
            Title = title,
            Note = note,
            Cut = cut,
            WithTotal = withTotal,
            Rows = rows.Select((r, i) => new
            {
                Rank = i + 1,
                r.Stats.Name,
                Discord = Links.Find(r.Stats.Key)?.DiscordName is { Length: > 0 } d ? d : null,
                Color = r.Stats.LastColorId,
                Points = Math.Round(r.Stats.Points, 2),
                Total = Math.Round(r.Total, 2),
                r.Stats.Games,
                r.Stats.Wins,
                r.Stats.Losses,
                Impostor = $"{r.Stats.ImpostorWins}/{r.Stats.ImpostorGames}",
                r.Stats.Kills,
                Votes = $"{r.Stats.CorrectVotes}✓ {r.Stats.IncorrectVotes}✗",
                Advancing = r.Advancing,
            }).ToList(),
        };
    }
}
