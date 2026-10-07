using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TournamentTracker.Discord;
using TournamentTracker.Setup;
using TournamentTracker.Stats;

namespace TournamentTracker.App
{
    /// <summary>
    /// The tournament as the organiser sees it, unlocked with an administration code: every
    /// tournament lobby live (read from the "Live data" messages the hosts' games keep in the
    /// private results channel), the combined standings, and referee actions posted to that
    /// channel. The Button's Organiser tab and the broadcast app (Red Alert) both use it.
    /// </summary>
    public sealed class Organizer : IDisposable
    {
        private static readonly TimeSpan LivePoll = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan StandingsPoll = TimeSpan.FromSeconds(30);
        /// <summary>A lobby that hasn't sent anything for this long is shown as gone quiet.</summary>
        public static readonly TimeSpan Stale = TimeSpan.FromSeconds(150);
        /// <summary>Events everyone in the game already knows about (safe for the stream before a meeting reveals more).</summary>
        public static readonly IReadOnlyCollection<string> PublicEvents = new HashSet<string> { "meeting", "eject", "end" };
        private static readonly JsonSerializerOptions Camel = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly SetupCode _code;
        private readonly DiscordRest _rest;
        private readonly SharedResults _shared;
        private readonly string _token;
        private readonly Func<DateTime> _clock;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _lock = new object();
        private readonly Dictionary<string, (JsonElement Data, DateTime SeenUtc, long Sent)> _lobbies = new Dictionary<string, (JsonElement, DateTime, long)>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Each lobby's newest event and when it was first seen here, for "something's happening" marks.</summary>
        private readonly Dictionary<string, (string Key, string Kind, DateTime At)> _lastEvent = new Dictionary<string, (string, string, DateTime)>(StringComparer.OrdinalIgnoreCase);
        /// <summary>How long a kill, meeting or ejection keeps a lobby marked.</summary>
        public static readonly TimeSpan HotFor = TimeSpan.FromSeconds(20);
        private SharedLoad? _load;
        private DateTime _loadedUtc = DateTime.MinValue;

        /// <summary>After each read of the live lobbies (the broadcast app's caster pages follow).</summary>
        public event Action? LiveChanged;

        public Organizer(SetupCode code, HttpClient http, Func<DateTime>? clock = null, bool start = true)
        {
            _code = code;
            _clock = clock ?? (() => DateTime.UtcNow);
            _token = code.BotTokens![0];
            _rest = new DiscordRest(http, NullLog.Instance);
            _shared = new SharedResults(_rest, _token, code.ResultsChannelId!, NullLog.Instance);
            if (start) Task.Run(LoopAsync);
        }

        public string Tournament => _code.TournamentName;
        public string? Problem { get; private set; }

        /// <summary>The tournament's scored games since the last reset (shared results), for the broadcast.</summary>
        public IReadOnlyList<GameRecord> Games { get { lock (_lock) return _load?.GameRecords.ToList() ?? new List<GameRecord>(); } }
        public int Advance => _code.AdvanceCount ?? 5;
        public int GamesPerRound => _code.GamesPerRound ?? 3;

        private async Task LoopAsync()
        {
            DateTime nextStandings = DateTime.MinValue;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await PollLiveAsync().ConfigureAwait(false);
                    if (DateTime.UtcNow >= nextStandings)
                    {
                        nextStandings = DateTime.UtcNow + StandingsPoll;
                        await LoadStandingsAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception e) { Problem = "Couldn't reach Discord: " + e.Message; }
                try { await Task.Delay(LivePoll, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Reads the newest messages in the results channel for each lobby's live data.</summary>
        public async Task PollLiveAsync()
        {
            var messages = await _rest.GetMessagesAsync(_token, _code.ResultsChannelId!).ConfigureAwait(false);
            if (messages == null)
            {
                Problem = "The administration bot can't read the private results channel. Give it View Channel and Read Message History there, and turn on its Message Content Intent (Discord Developer Portal → Bot).";
                return;
            }
            Problem = null;
            foreach (var m in messages)
                if (m.AuthorIsBot) Take(m.EmbedTitle, m.EmbedDescription);
            LiveChanged?.Invoke();
        }

        /// <summary>Takes one "Live data" message (title and description). Public for tests.</summary>
        public void Take(string? title, string? description)
        {
            if (title == null || description == null || !title.StartsWith(TournamentSession.LiveTitlePrefix, StringComparison.Ordinal)) return;
            string json = description.Trim();
            if (json.StartsWith("```")) json = json.Substring(json.IndexOf('\n') + 1);
            if (json.EndsWith("```")) json = json.Substring(0, json.Length - 3);
            JsonElement data;
            try { data = JsonDocument.Parse(json).RootElement.Clone(); }
            catch (JsonException) { return; }
            string lobby = data.TryGetProperty("lobby", out var l) ? l.GetString() ?? "" : "";
            long sent = data.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
            if (lobby.Length == 0) return;
            lock (_lock)
            {
                bool known = _lobbies.TryGetValue(lobby, out var have);
                if (known && have.Sent >= sent) return;
                _lobbies[lobby] = (data, _clock(), sent);
                if (data.TryGetProperty("f", out var f) && f.ValueKind == JsonValueKind.Array && f.GetArrayLength() > 0)
                {
                    var last = f[f.GetArrayLength() - 1];
                    string key = last.GetRawText();
                    // The first time a lobby is seen its old events aren't news.
                    if (!_lastEvent.TryGetValue(lobby, out var was) || was.Key != key)
                        _lastEvent[lobby] = (key, last[1].GetString() ?? "", known ? _clock() : DateTime.MinValue);
                }
            }
        }

        public async Task LoadStandingsAsync()
        {
            var load = await _shared.LoadAsync(_code.TournamentId, _code.TournamentName).ConfigureAwait(false);
            if (load == null) return;
            lock (_lock) { _load = load; _loadedUtc = DateTime.UtcNow; }
        }

        private DateTime SentUtc(long sent) => sent > 0 ? DateTimeOffset.FromUnixTimeSeconds(sent).UtcDateTime : DateTime.MinValue;

        /// <summary>Every lobby's newest live data (label, data, when it was sent), in the order they were first seen.</summary>
        public List<(string Label, JsonElement Data, long Sent)> LiveLobbies()
        {
            lock (_lock) return _lobbies.Select(kv => (kv.Key, kv.Value.Data, kv.Value.Sent)).ToList();
        }

        private static readonly Dictionary<string, string> HotNames = new Dictionary<string, string>
        {
            ["meeting"] = "Meeting", ["eject"] = "Ejection", ["end"] = "Game over",
            ["kill"] = "Kill", ["sabotage"] = "Sabotage", ["disconnect"] = "Player left",
        };

        /// <summary>
        /// Why a lobby is worth a look right now (a meeting, or an event in the last few seconds),
        /// or null. Kills, sabotages and disconnects only with <paramref name="full"/>: they're
        /// not public until a meeting.
        /// </summary>
        public string? Hot(string lobby, bool full)
        {
            lock (_lock)
            {
                if (!_lobbies.TryGetValue(lobby, out var l)) return null;
                if (l.Data.TryGetProperty("phase", out var ph) && ph.GetString() == "Meeting") return "Meeting";
                if (!_lastEvent.TryGetValue(lobby, out var e) || _clock() - e.At > HotFor) return null;
                if (!full && !PublicEvents.Contains(e.Kind)) return null;
                return HotNames.TryGetValue(e.Kind, out var name) ? name : null;
            }
        }

        /// <summary>The VDO.Ninja page showing a host's game, or null when they aren't sending it. Public for tests.</summary>
        public static string? VideoUrl(JsonElement d)
        {
            if (!d.TryGetProperty("vdo", out var v) || v.ValueKind != JsonValueKind.String) return null;
            var parts = (v.GetString() ?? "").Split(':');
            if (parts.Length != 2 || !System.Text.RegularExpressions.Regex.IsMatch(parts[0] + parts[1], "^[a-z0-9]+$")) return null;
            return $"{TournamentSession.VdoNinja}?view={parts[0]}&password={parts[1]}&noaudio&cleanoutput";
        }

        /// <summary>A lobby's voice stream link. Public for tests.</summary>
        public static string? VoiceUrl(JsonElement d)
        {
            var video = VideoUrl(d);
            if (video == null) return null;
            var m = System.Text.RegularExpressions.Regex.Match(video, @"view=([a-z0-9]+)&");
            return m.Success ? video.Replace("view=" + m.Groups[1].Value + "&", "view=" + m.Groups[1].Value + "v&").Replace("&noaudio&cleanoutput", "&novideo&cleanoutput") : null;
        }

        /// <summary>
        /// The same host's game sound on its own (no picture). The caster's video page plays only
        /// the lobby on air: the pictures stay connected silently, and this moves with the cast,
        /// so two lobbies' sound can never play at once. Public for tests.
        /// </summary>
        public static string? SoundUrl(JsonElement d) =>
            VideoUrl(d)?.Replace("&noaudio&cleanoutput", "&novideo&cleanoutput");

        /// <summary>Everything the organiser tab shows.</summary>
        public object State()
        {
            var now = _clock();
            List<(string Label, JsonElement Data, DateTime Seen, long Sent)> lobbies;
            SharedLoad? load;
            lock (_lock)
            {
                lobbies = _lobbies.Select(kv => (kv.Key, kv.Value.Data, kv.Value.SeenUtc, kv.Value.Sent)).ToList();
                load = _load;
            }
            int adv = _code.AdvanceCount ?? 5, per = _code.GamesPerRound ?? 3;
            int round = lobbies.Select(l => l.Data.TryGetProperty("round", out var r) ? r.GetInt32() : 0).DefaultIfEmpty(0).Max();
            if (load != null && round == 0) round = load.GameRecords.Select(g => g.Round).DefaultIfEmpty(0).Max();
            // A round that hasn't had a game yet would show empty tables: keep the last round with games (its final standings) until then.
            int shown = round;
            if (load != null && !load.GameRecords.Any(g => g.Counted && g.Round == round))
                shown = load.GameRecords.Where(g => g.Counted && g.Round > 0).Select(g => g.Round).DefaultIfEmpty(round).Max();
            return new
            {
                Tournament = _code.TournamentName,
                Problem,
                Round = round,
                StandingsRound = shown,
                StandingsFinal = shown != round || (load != null && per > 0 && load.GameRecords.Where(g => g.Counted && g.Round == shown).GroupBy(g => g.Host).All(g => g.Count() >= per)),
                StandingsAt = load == null ? null : _loadedUtc.ToString("o"),
                Lobbies = lobbies
                    .OrderBy(l => l.Label, StringComparer.OrdinalIgnoreCase)
                    .Select(l =>
                    {
                        var sent = SentUtc(l.Sent);
                        double age = Math.Max(0, (now - (sent == DateTime.MinValue ? l.Seen : sent)).TotalSeconds);
                        return new
                        {
                            Label = l.Label,
                            Age = Math.Round(age),
                            Quiet = age > Stale.TotalSeconds,
                            Video = VideoUrl(l.Data) != null,
                            Hot = Hot(l.Label, full: false),
                            HotFull = Hot(l.Label, full: true),
                            Data = l.Data,
                        };
                    }).ToList(),
                Standings = load == null || shown == 0 ? null : new
                {
                    Games = load.GameRecords.Count(g => g.Counted && g.Round == shown),
                    Lobbies = load.GameRecords.Where(g => g.Round == shown).Select(g => g.Host).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                        .Select(h => new
                        {
                            Lobby = h,
                            Played = Stats.Standings.GamesPlayed(load.GameRecords, h, shown),
                            Rows = Rows(Stats.Standings.Lobby(load.GameRecords, h, shown, adv, per)),
                        }).ToList(),
                    All = Rows(Stats.Standings.Round(load.GameRecords, shown, adv, per)),
                },
                Advance = adv,
                PerRound = per,
                Awards = load == null ? null : new
                {
                    Round = shown == 0 ? null : Awards(load.GameRecords.Where(g => g.Round == shown)),
                    All = Awards(load.GameRecords),
                },
            };
        }

        /// <summary>
        /// Candidates for the organiser's awards (picked by hand): the top three in each category,
        /// over the counted games given. Public for tests.
        /// </summary>
        public static List<object> Awards(IEnumerable<GameRecord> games)
        {
            var players = Stats.Standings.Build(games.Where(g => g.Counted), "").Leaderboard().Where(p => p.Games > 0).ToList();
            object Category(string title, string note, Func<PlayerTotals, double> score, Func<PlayerTotals, string> value, Func<PlayerTotals, bool>? eligible = null) => new
            {
                Title = title, Note = note,
                Top = players.Where(p => (eligible == null || eligible(p)) && score(p) > 0)
                    .OrderByDescending(score).ThenByDescending(p => p.Points).Take(3)
                    .Select(p => new { p.Name, Color = p.LastColorId, Value = value(p) }).ToList(),
            };
            static string N(double v) => Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new List<object>
            {
                Category("Top score", "Most points", p => p.Points, p => $"{N(p.Points)} pts"),
                Category("Most kills", "As impostor", p => p.Kills, p => $"{p.Kills} kill{(p.Kills == 1 ? "" : "s")}"),
                Category("Best impostor", "Impostor wins, then kills", p => p.ImpostorWins * 100 + p.Kills, p => $"{p.ImpostorWins}/{p.ImpostorGames} wins"),
                Category("Sharpest voter", "Right votes, at least 3 cast", p => p.CorrectVotes + p.VoteAccuracy, p => $"{p.CorrectVotes} right ({Math.Round(p.VoteAccuracy * 100)}%)",
                    p => p.CorrectVotes + p.IncorrectVotes >= 3),
                Category("Impostor hunter", "Impostors voted out with their vote", p => p.KillersCaught, p => $"{p.KillersCaught} caught"),
                Category("Task machine", "Tasks done", p => p.TasksCompleted, p => $"{p.TasksCompleted} tasks"),
                Category("Survivor", "Games survived", p => p.Survived, p => $"{p.Survived} of {p.Games}"),
            };
        }

        private static List<object> Rows(List<StandingRow> rows) => rows.Select((r, i) => (object)new
        {
            Rank = i + 1, r.Stats.Name, Color = r.Stats.LastColorId, Points = Math.Round(r.Stats.Points, 2), Total = Math.Round(r.Total, 2),
            r.Stats.Games, r.Stats.Wins, r.Stats.Losses, r.Advancing,
        }).ToList();

        /// <summary>Posts a referee command (!adjust, !void, !start…) to the results channel as the administration bot.</summary>
        public async Task<string> CommandAsync(string text)
        {
            text = text.Trim();
            if (!text.StartsWith("!") || text.Length < 3) return "Commands start with ! (for example !adjust LJ red -2 meta call).";
            var result = await _rest.PostMessageAsync(_token, _code.ResultsChannelId!, text).ConfigureAwait(false);
            if (!result.Ok) return $"Discord didn't take it ({result.Status}). Check the administration bot can send messages in the results channel.";
            _ = Task.Run(async () => { await Task.Delay(4000).ConfigureAwait(false); await LoadStandingsAsync().ConfigureAwait(false); });
            return $"Posted: {text}";
        }

        public void Dispose() => _cts.Cancel();
    }
}
