using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public sealed partial class Organizer : IDisposable
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

        /// <param name="cacheFile">
        /// Where the lobbies last known (and their links) are kept, so they're back at once after a restart
        /// (Red Alert's; see OrganizerCache.cs). Null: nothing kept (The Button's Organiser tab).
        /// </param>
        public Organizer(SetupCode code, HttpClient http, Func<DateTime>? clock = null, bool start = true, ILog? log = null, string? cacheFile = null)
        {
            _code = code;
            _clock = clock ?? (() => DateTime.UtcNow);
            _log = log ?? NullLog.Instance;
            _token = code.BotTokens![0];
            _rest = new DiscordRest(http, _log) { RequestTimeout = RequestTimeout };
            _shared = new SharedResults(_rest, _token, code.ResultsChannelId!, _log);
            _cacheFile = cacheFile;
            LoadCache();
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
            SaveCache();
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
                        // Remembered from before a restart, and its message is gone: so is the lobby.
                        if (_cached.Remove(lobby)) _lobbies.Remove(lobby);
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
                // Remembered from before a restart: the same message, read now, confirms it (and brings its players back).
                bool cached = _cached.Contains(lobby);
                newest = !known || sent >= have.Sent;
                if (known && (have.Sent > sent || have.Sent == sent && !cached)) return lobby;
                if (cached) { _cached.Remove(lobby); known = false; }
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
                if (!_lobbies.TryGetValue(lobby, out var l) || _cached.Contains(lobby)) return null;
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
            HashSet<string> cached;
            lock (_lock)
            {
                lobbies = _lobbies.Select(kv => (kv.Key, kv.Value.Data, kv.Value.SeenUtc, kv.Value.Sent)).ToList();
                load = _load;
                cached = new HashSet<string>(_cached, StringComparer.OrdinalIgnoreCase);
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
                            // Remembered from before a restart: not confirmed by the results channel yet.
                            Cached = cached.Contains(l.Label),
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

        // ---- Changes posted in the results channel ------------------------------------------------
        // Every points change and round change is made in The Button: its Referee tab (points, voids)
        // and Organiser tab (rounds, lobbies, a reset) post it in the private results channel as the
        // administration bot, and the mods act on bots' messages only. Nobody types a command, and
        // only these shapes are ever posted: no free text.

        private const int MaxField = 60, MaxReason = 100, MaxCommand = 300;
        /// <summary>The organiser's reset (the one message on two lines: the command, then who).</summary>
        public static readonly string OrganiserReset = SharedResults.ResetMessage("the organiser");
        private static readonly Regex[] CommandShapes =
        {
            new Regex(@"^!adjust \S+ \S.*? [+-]\d+(?:\.\d+)?(?: \S.*)?$", RegexOptions.CultureInvariant),
            new Regex(@"^!void \S+(?: \S.*)?$", RegexOptions.CultureInvariant),
            new Regex(@"^!unvoid \S+$", RegexOptions.CultureInvariant),
            new Regex(@"^!start \d{1,2}(?: in \d{1,3})?$", RegexOptions.CultureInvariant),
            new Regex(@"^!lobbies \d{1,2}$", RegexOptions.CultureInvariant),
            new Regex(@"^!(?:move|add) \S.* \S+$", RegexOptions.CultureInvariant),
            new Regex(@"^!swap \S+ \S+$", RegexOptions.CultureInvariant),
            new Regex(@"^!drop \S.*$", RegexOptions.CultureInvariant),
            new Regex(@"^!host \S+ \S.*$", RegexOptions.CultureInvariant),
        };

        /// <summary>One of the commands The Button builds (one line, a known shape, or the reset). Public for tests.</summary>
        public static bool IsChange(string text) =>
            text == OrganiserReset
            || text.Length <= MaxCommand && text.IndexOfAny(new[] { '\r', '\n' }) < 0 && CommandShapes.Any(s => s.IsMatch(text));

        /// <summary>
        /// The command for one change from The Button's fields (by name: game, player, points, reason,
        /// round, minutes, lobby, other, host), or null with what's wrong in <paramref name="problem"/>.
        /// Each is read back the way the mods read it, so it can't mean something else. Public for tests.
        /// </summary>
        public static string? BuildChange(string kind, Func<string, string> field, out string problem)
        {
            string F(string name) => Regex.Replace(field(name) ?? "", @"\s+", " ").Trim();
            var (text, why) = Build(kind, F);
            problem = why;
            if (text != null && !IsChange(text)) { problem = "That's too long to post. Shorten the names or the reason."; return null; }
            return text;
        }

        private static (string? Text, string Problem) Build(string kind, Func<string, string> f)
        {
            static (string?, string) Fail(string why) => (null, why);
            static (string?, string) Done(string text) => (text, "");
            string game = f("game"), player = f("player"), reason = f("reason");
            if (reason.Length > MaxReason) return Fail($"Keep the reason under {MaxReason} characters.");
            switch (kind)
            {
                case "adjust":
                {
                    if (!OneWord(game)) return Fail("Pick the lobby (LJ) or the game (LJ-3).");
                    if (player.Length == 0 || player.Length > MaxField) return Fail("Pick the player (colour or name) and the points.");
                    string raw = f("points").Replace(',', '.');
                    if (!double.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double points)
                        || points == 0 || Math.Abs(points) > 1000 || !TournamentSession.ThreeSigFigs(points))
                        return Fail("Use a number of points up to 1000 with up to 3 significant figures, e.g. -2, +0.125 or +12.5.");
                    string text = $"!adjust {game} {player} {(points > 0 ? "+" : "-")}{Math.Abs(points).ToString("0.##########", CultureInfo.InvariantCulture)}"
                                  + (reason.Length > 0 ? " " + reason : "");
                    // Read back as the mods read it: a name ending in a number could pass for the points.
                    var read = RefereeAdjustment.TryParse(text, "", DateTime.MinValue);
                    if (read == null || read.Game != game || read.Player != player || read.Points != points)
                        return Fail($"\"{player}\" can't be told apart from the points: use the player's colour.");
                    return Done(text);
                }
                case "void":
                case "unvoid":
                {
                    if (!OneWord(game)) return Fail("Pick the game (LJ-3), or the lobby (LJ) for the game it's playing.");
                    string text = kind == "void" ? $"!void {game}" + (reason.Length > 0 ? " " + reason : "") : $"!unvoid {game}";
                    var read = RefereeVoid.TryParse(text, "", DateTime.MinValue);
                    return read != null && read.Game == game ? Done(text) : Fail("That game name can't be used.");
                }
                case "start":
                {
                    if (!Number(f("round"), 1, 99, out int round)) return Fail("Which round? A number from 1 to 99.");
                    string m = f("minutes");
                    if (m.Length == 0 || m == "0") return Done($"!start {round}");
                    if (!Number(m, 1, 120, out int minutes)) return Fail("Start it now (leave the minutes empty) or in 1 to 120 minutes.");
                    return Done($"!start {round} in {minutes}");
                }
                case "lobbies":
                    return Number(f("round"), 2, 99, out int next) ? Done($"!lobbies {next}") : Fail("Lobbies are built for round 2 or later, from the round before.");
                case "move":
                case "add":
                {
                    string lobby = PlanLobby(f("lobby"));
                    if (player.Length == 0 || player.Length > MaxField) return Fail("Which player?");
                    if (!OneWord(lobby)) return Fail("Which lobby? A, B… or Final (or its host's lobby label).");
                    return Done($"!{kind} {player} {lobby}");
                }
                case "swap":
                {
                    string other = f("other");
                    if (player.Length == 0 || other.Length == 0) return Fail("Pick the two players to swap.");
                    if (!OneWord(player) || !OneWord(other)) return Fail("A swap takes one word for each player: the start of a name is enough.");
                    return Done($"!swap {player} {other}");
                }
                case "drop":
                    return player.Length > 0 && player.Length <= MaxField ? Done($"!drop {player}") : Fail("Which player?");
                case "host":
                {
                    string lobby = PlanLobby(f("lobby")), host = f("host");
                    if (!OneWord(lobby)) return Fail("Which lobby? A, B… or Final.");
                    if (host.Length == 0 || host.Length > MaxField) return Fail("Who hosts it? Their lobby label (LJ).");
                    return Done($"!host {lobby} {host}");
                }
                case "reset":
                    return Done(OrganiserReset);
                default:
                    return Fail("The Button doesn't make that change.");
            }
        }

        private static bool OneWord(string s) => s.Length > 0 && s.Length <= MaxField && s.IndexOf(' ') < 0;

        private static bool Number(string s, int min, int max, out int n) =>
            int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n >= min && n <= max;

        /// <summary>The next round's lobbies are "Lobby A", "Lobby B"… or "Final": a lobby is named by its letter.</summary>
        private static string PlanLobby(string s) => s.StartsWith("Lobby ", StringComparison.OrdinalIgnoreCase) ? s.Substring(6).Trim() : s;

        /// <summary>Posts one change from The Button's fields (see <see cref="BuildChange(string, Func{string, string}, out string)"/>).</summary>
        public async Task<(bool Ok, string Message)> PostChangeAsync(string kind, Func<string, string> field)
        {
            string? text = BuildChange(kind, field, out string problem);
            return text == null ? (false, problem) : await PostAsync(text).ConfigureAwait(false);
        }

        /// <summary>Posts a change already written as a command (Red Alert's void of an interrupted game); only <see cref="IsChange"/> shapes go.</summary>
        public async Task<string> CommandAsync(string text) => (await PostAsync(text.Trim()).ConfigureAwait(false)).Message;

        private async Task<(bool Ok, string Message)> PostAsync(string text)
        {
            if (!IsChange(text)) return (false, "That isn't a change The Button makes, so it wasn't posted.");
            var result = await _rest.PostMessageAsync(_token, _code.ResultsChannelId!, text).ConfigureAwait(false);
            if (!result.Ok) return (false, $"Discord didn't take it ({result.Status}). Check the administration bot can send messages in the results channel.");
            _ = Task.Run(async () => { await Task.Delay(4000).ConfigureAwait(false); await LoadStandingsAsync().ConfigureAwait(false); });
            return (true, text == OrganiserReset ? "The standings start over from now. Deleting the reset message in the results channel undoes it." : $"Posted: {text}");
        }

        public void Dispose() => _cts.Cancel();
    }
}
