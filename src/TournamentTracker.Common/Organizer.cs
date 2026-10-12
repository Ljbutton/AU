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
        /// <summary>
        /// How long one Discord request may take. The apps hand in their shared HttpClient, made for
        /// big downloads (a 10-minute timeout), and a stuck read would stop the live lobbies that long.
        /// </summary>
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
        /// <summary>A lobby whose live message is older than the newest 100 is read on its own this often (quiet ones once a minute).</summary>
        private static readonly TimeSpan OwnReadEvery = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan OwnReadQuietEvery = TimeSpan.FromSeconds(60);
        /// <summary>Lobbies read on their own per poll at most, so a big event can't use up the read limit.</summary>
        private const int OwnReadsPerPoll = 5;
        /// <summary>Pages of older messages read once, at the start, to find lobbies that posted a while ago.</summary>
        private const int StartPages = 2;
        private static readonly TimeSpan StandingsPoll = TimeSpan.FromSeconds(30);
        /// <summary>A lobby that hasn't sent anything for this long is shown as gone quiet.</summary>
        public static readonly TimeSpan Stale = TimeSpan.FromSeconds(150);
        /// <summary>Events everyone in the game already knows about (safe for the stream before a meeting reveals more).</summary>
        public static readonly IReadOnlyCollection<string> PublicEvents = new HashSet<string> { "meeting", "eject", "end" };

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
        /// <summary>
        /// Each lobby's live message and when it was last read on its own. A host's message is
        /// edited in place and only moved down after each game, so in a busy channel it can be
        /// further up than the newest 100 messages; it is then read by its ID.
        /// </summary>
        private readonly Dictionary<string, (string MessageId, DateTime ReadUtc)> _liveMessages = new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private bool _readBack;
        private readonly Random _random = new Random();

        /// <summary>After each read of the live lobbies (the broadcast app's caster pages follow).</summary>
        public event Action? LiveChanged;

        private readonly ILog _log;
        private string? _botName;

        public Organizer(SetupCode code, HttpClient http, Func<DateTime>? clock = null, bool start = true, ILog? log = null)
        {
            _code = code;
            _clock = clock ?? (() => DateTime.UtcNow);
            _log = log ?? NullLog.Instance;
            _token = code.BotTokens![0];
            _rest = new DiscordRest(http, _log) { RequestTimeout = RequestTimeout };
            _shared = new SharedResults(_rest, _token, code.ResultsChannelId!, _log);
            if (start) Task.Run(LoopAsync);
        }

        public string Tournament => _code.TournamentName;
        public string? ResultsChannelId => _code.ResultsChannelId;
        public string? GuildId => _code.GuildId;
        /// <summary>The administration bot (its token's ID part), to compare with a host's code.</summary>
        public string BotId => SetupCode.BotIdOf(_token);

        /// <summary>What's stopping the live lobbies from showing, in words (null: nothing).</summary>
        public string? Problem
        {
            get => _problem;
            private set
            {
                if (value == _problem) return;
                _problem = value;
                if (value != null) _log.Warn("Organiser: " + value);
                else _log.Info("Organiser: reading the results channel again.");
            }
        }
        private string? _problem;

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
                catch (Exception e) { Problem = "Couldn't reach Discord: " + e.Message; _log.Error("Organiser poll failed: " + e); }
                // A little jitter: Red Alert and referees' Buttons poll the same channel with the same bot.
                double jitter;
                lock (_random) jitter = (_random.NextDouble() - 0.5) * 0.8;
                try { await Task.Delay(LivePoll + TimeSpan.FromSeconds(jitter), _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Reads the newest messages in the results channel for each lobby's live data.</summary>
        public async Task PollLiveAsync()
        {
            var (messages, result) = await _rest.ReadMessagesAsync(_token, _code.ResultsChannelId!).ConfigureAwait(false);
            if (messages == null)
            {
                Problem = ReadProblem(result, _code.ResultsChannelId!);
                return;
            }
            int read = 0, blank = 0;
            var seen = new HashSet<string>();
            foreach (var m in messages)
            {
                seen.Add(m.Id);
                if (!m.AuthorIsBot) continue;
                if (TakeMessage(m)) read++;
                // Another bot's message that came back with nothing in it: the Message Content Intent is off.
                else if (m.Embeds == 0 && m.Content.Length == 0 && m.Attachments.Count == 0) blank++;
            }
            if (!_readBack && messages.Count >= 100)
            {
                // Once, at the start: lobbies whose live message is already further up.
                _readBack = true;
                string? before = messages[messages.Count - 1].Id;
                for (int page = 0; page < StartPages && before != null; page++)
                {
                    var older = await _rest.GetMessagesAsync(_token, _code.ResultsChannelId!, before).ConfigureAwait(false);
                    if (older == null || older.Count == 0) break;
                    foreach (var m in older)
                    {
                        seen.Add(m.Id);
                        if (m.AuthorIsBot && TakeMessage(m)) read++;
                    }
                    before = older.Count >= 100 ? older[older.Count - 1].Id : null;
                }
            }
            else _readBack = true;
            read += await ReadOwnAsync(seen).ConfigureAwait(false);
            if (blank > 0 && read == 0)
            {
                _botName ??= await _rest.BotNameAsync(_token).ConfigureAwait(false);
                Problem = $"The hosts' messages in the results channel come back empty: turn on Message Content Intent for {(_botName != null ? _botName : "the administration bot")} (Discord Developer Portal → your app → Bot → Privileged Gateway Intents), then restart Red Alert.";
            }
            else Problem = null;
            LiveChanged?.Invoke();
        }

        /// <summary>Discord's refusal to read the results channel, in words that say what to do.</summary>
        public static string ReadProblem(DiscordResult result, string channelId)
        {
            if (result.Status == 401)
                return "The bot token in the administration code is invalid (was it reset?). Make a new administration code with the bot's current token.";
            if (result.Status == 403 || result.ErrorCode == 50001 || result.ErrorCode == 50013)
                return "The administration bot can't see the results channel: in that channel's permissions, add the bot with View Channel and Read Message History.";
            if (result.Status == 404 || result.ErrorCode == 10003)
                return $"The results channel in the administration code doesn't exist (channel ID {channelId}). Check the ID and make a new administration code.";
            if (result.Status == 0)
                return "Couldn't reach Discord: " + (result.Body.Length > 0 ? result.Body : "no answer") + ".";
            return $"Discord wouldn't let the administration bot read the results channel (HTTP {result.Status}{(result.ErrorCode != 0 ? ", code " + result.ErrorCode : "")}).";
        }

        /// <summary>
        /// Lobbies whose live message wasn't among the messages just read: each is read by its ID
        /// (a few per poll, quiet lobbies rarely). A deleted one is forgotten; its lobby's new
        /// message shows up at the bottom of the channel.
        /// </summary>
        private async Task<int> ReadOwnAsync(HashSet<string> seen)
        {
            var now = _clock();
            List<(string Lobby, string MessageId)> due;
            lock (_lock)
            {
                due = _liveMessages
                    .Where(kv => !seen.Contains(kv.Value.MessageId))
                    .Where(kv => now - kv.Value.ReadUtc >= (QuietLocked(kv.Key, now) ? OwnReadQuietEvery : OwnReadEvery))
                    .OrderBy(kv => kv.Value.ReadUtc)
                    .Take(OwnReadsPerPoll)
                    .Select(kv => (kv.Key, kv.Value.MessageId))
                    .ToList();
                foreach (var (lobby, id) in due) _liveMessages[lobby] = (id, now);
            }
            int read = 0;
            foreach (var (lobby, id) in due)
            {
                var (message, result) = await _rest.GetMessageAsync(_token, _code.ResultsChannelId!, id).ConfigureAwait(false);
                if (message != null)
                {
                    if (TakeMessage(message)) read++;
                }
                else if (result.Status == 404)
                {
                    lock (_lock)
                    {
                        if (_liveMessages.TryGetValue(lobby, out var have) && have.MessageId == id) _liveMessages.Remove(lobby);
                    }
                }
            }
            return read;
        }

        /// <summary>The lobby hasn't sent anything for a while (with <see cref="_lock"/> held).</summary>
        private bool QuietLocked(string lobby, DateTime now)
        {
            if (!_lobbies.TryGetValue(lobby, out var l)) return true;
            var sent = SentUtc(l.Sent);
            return now - (sent == DateTime.MinValue ? l.SeenUtc : sent) > Stale;
        }

        /// <summary>Takes one message read from the channel; a lobby's newest live message is remembered.</summary>
        private bool TakeMessage(ChannelMessage m)
        {
            string? lobby = TakeLobby(m.EmbedTitle, m.EmbedDescription, out bool newest);
            if (lobby == null) return false;
            if (newest && lobby.Length > 0 && m.Id.Length > 0)
                lock (_lock) _liveMessages[lobby] = (m.Id, _clock());
            return true;
        }

        /// <summary>Takes one "Live data" message (title and description). Public for tests. True: it was one.</summary>
        public bool Take(string? title, string? description) => TakeLobby(title, description, out _) != null;

        /// <summary>
        /// Takes one "Live data" message: its lobby ("" when it can't be read), or null when it
        /// isn't one. <paramref name="newest"/>: nothing newer is known for the lobby (an older
        /// copy left behind isn't the one to follow).
        /// </summary>
        private string? TakeLobby(string? title, string? description, out bool newest)
        {
            newest = false;
            if (title == null || description == null || !title.StartsWith(TournamentSession.LiveTitlePrefix, StringComparison.Ordinal)) return null;
            string json = description.Trim();
            if (json.StartsWith("```")) json = json.Substring(json.IndexOf('\n') + 1);
            if (json.EndsWith("```")) json = json.Substring(0, json.Length - 3);
            JsonElement data;
            try { data = JsonDocument.Parse(json).RootElement.Clone(); }
            catch (JsonException) { return ""; }
            string lobby = data.TryGetProperty("lobby", out var l) ? l.GetString() ?? "" : "";
            long sent = data.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
            if (lobby.Length == 0) return "";
            lock (_lock)
            {
                bool known = _lobbies.TryGetValue(lobby, out var have);
                newest = !known || sent >= have.Sent;
                if (known && have.Sent >= sent) return lobby;
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
            return lobby;
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

        /// <summary>A lobby's player camera link (the host's second picture, following one player). Public for tests.</summary>
        public static string? CamUrl(JsonElement d)
        {
            var video = VideoUrl(d);
            if (video == null) return null;
            var m = System.Text.RegularExpressions.Regex.Match(video, @"view=([a-z0-9]+)&");
            return m.Success ? video.Replace("view=" + m.Groups[1].Value + "&", "view=" + m.Groups[1].Value + "c&") : null;
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
