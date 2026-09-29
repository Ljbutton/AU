using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TournamentTracker.Discord;
using TournamentTracker.Overlay;
using TournamentTracker.Setup;
using TournamentTracker.Stats;

namespace TournamentTracker.App
{
    /// <summary>
    /// The organiser's view, unlocked with an administration code: every tournament lobby live
    /// (read from the "Live data" messages the hosts' games keep in the private results
    /// channel), the combined standings, referee actions posted to that channel, and a caster
    /// overlay for OBS that follows whichever lobby is picked.
    /// </summary>
    public sealed class Organizer : IDisposable
    {
        public const int CasterPort = 8767;
        private static readonly TimeSpan LivePoll = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan StandingsPoll = TimeSpan.FromSeconds(30);
        /// <summary>A lobby that hasn't sent anything for this long is shown as gone quiet.</summary>
        public static readonly TimeSpan Stale = TimeSpan.FromSeconds(150);
        private static readonly HashSet<string> PublicEvents = new HashSet<string> { "meeting", "eject", "end" };
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
        private string? _cast;
        private readonly OverlayServer? _caster;

        public Organizer(SetupCode code, HttpClient http, Func<DateTime>? clock = null, int casterPort = CasterPort, bool start = true)
        {
            _code = code;
            _clock = clock ?? (() => DateTime.UtcNow);
            _token = code.BotTokens![0];
            _rest = new DiscordRest(http, NullLog.Instance);
            _shared = new SharedResults(_rest, _token, code.ResultsChannelId!, NullLog.Instance);
            try { _caster = new OverlayServer(casterPort, NullLog.Instance); }
            catch (Exception)
            {
                // Something else has the usual port: take any free one (the page shows which).
                try { _caster = new OverlayServer(0, NullLog.Instance); } catch (Exception e) { CasterProblem = "The caster overlay couldn't start: " + e.Message; }
            }
            if (_caster != null) _caster.Extra = CasterPage;
            if (start) Task.Run(LoopAsync);
        }

        public string Tournament => _code.TournamentName;
        public string? Problem { get; private set; }
        public string? CasterProblem { get; }
        public string? CasterUrl => _caster?.Url;

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
            UpdateCaster();
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

        /// <summary>Picks the lobby the caster overlay follows.</summary>
        public void Cast(string lobby)
        {
            lock (_lock) _cast = lobby;
            UpdateCaster();
        }

        private void UpdateCaster()
        {
            if (_caster == null) return;
            JsonElement? data;
            lock (_lock)
            {
                if (_cast == null || !_lobbies.ContainsKey(_cast))
                    _cast = _lobbies.Where(kv => kv.Value.Data.GetProperty("phase").GetString() != "Menu").OrderByDescending(kv => kv.Value.Sent).Select(kv => kv.Key).FirstOrDefault() ?? _cast;
                data = _cast != null && _lobbies.TryGetValue(_cast, out var d) ? d.Data : (JsonElement?)null;
            }
            if (data == null) return;
            _caster.SafeJson = JsonSerializer.Serialize(OverlayState(data.Value, full: false), Camel);
            _caster.FullJson = JsonSerializer.Serialize(OverlayState(data.Value, full: true), Camel);
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

        /// <summary>What the caster's video pages read: the lobby being cast and every lobby's video. Public for tests.</summary>
        public string FeedsJson()
        {
            List<(string Label, JsonElement Data)> lobbies;
            string? cast;
            lock (_lock)
            {
                lobbies = _lobbies.Where(kv => kv.Value.Data.GetProperty("phase").GetString() != "Menu")
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (kv.Key, kv.Value.Data)).ToList();
                cast = _cast;
            }
            return JsonSerializer.Serialize(new
            {
                Cast = cast,
                Lobbies = lobbies.Select(l =>
                {
                    var players = l.Data.TryGetProperty("p", out var p) ? p.EnumerateArray().ToList() : new List<JsonElement>();
                    return new
                    {
                        l.Label,
                        Video = VideoUrl(l.Data),
                        Phase = l.Data.GetProperty("phase").GetString(),
                        Alive = players.Count(x => x[2].GetInt32() == 0),
                        Total = players.Count,
                        // On stream: only what the players already know.
                        Hot = Hot(l.Label, full: false),
                    };
                }).ToList(),
            }, Camel);
        }

        private (string Type, byte[] Body)? CasterPage(string path)
        {
            string route = path.Split('?')[0];
            static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);
            return route switch
            {
                "/feeds" => ("application/json", B(FeedsJson())),
                "/video" => ("text/html; charset=utf-8", B(CasterPages.Video)),
                "/multiview" => ("text/html; charset=utf-8", B(CasterPages.Multiview)),
                _ => null,
            };
        }

        /// <summary>A lobby's live data in the stream overlay's format. Public for tests.</summary>
        public static object OverlayState(JsonElement d, bool full)
        {
            string S(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int I(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            string phase = S("phase");
            bool playing = phase == "Tasks" || phase == "Meeting";
            return new
            {
                Tournament = S("tour"),
                Lobby = S("lobby"),
                Round = I("round"),
                Played = (int?)I("played"),
                PerRound = I("per"),
                Phase = phase,
                Map = S("map"),
                Players = d.GetProperty("p").EnumerateArray().Select(p => new
                {
                    Name = p[0].GetString(),
                    Color = p[1].GetInt32(),
                    Dead = (full ? p[3] : p[2]).GetInt32() == 1,
                    Impostor = full && playing && p[4].GetInt32() == 1,
                    Tasks = full && playing && p[6].GetInt32() > 0 ? new[] { p[5].GetInt32(), p[6].GetInt32() } : null,
                }).ToList(),
                StandingsTitle = I("round") > 0 ? $"Round {I("round")} standings" : "Standings",
                Advance = I("adv"),
                Standings = d.GetProperty("st").EnumerateArray().Select(s => new { Name = s[0].GetString(), Points = s[1].GetString() }).ToList(),
                Feed = d.GetProperty("f").EnumerateArray().Where(f => full || PublicEvents.Contains(f[1].GetString() ?? ""))
                    .TakeLast(6).Select(f => new { At = f[0].GetString(), Text = f[2].GetString() }).ToList(),
            };
        }

        /// <summary>Everything the organiser tab shows.</summary>
        public object State()
        {
            var now = _clock();
            List<(string Label, JsonElement Data, DateTime Seen, long Sent)> lobbies;
            SharedLoad? load;
            string? cast;
            lock (_lock)
            {
                lobbies = _lobbies.Select(kv => (kv.Key, kv.Value.Data, kv.Value.SeenUtc, kv.Value.Sent)).ToList();
                load = _load;
                cast = _cast;
            }
            int adv = _code.AdvanceCount ?? 5, per = _code.GamesPerRound ?? 3;
            int round = lobbies.Select(l => l.Data.TryGetProperty("round", out var r) ? r.GetInt32() : 0).DefaultIfEmpty(0).Max();
            if (load != null && round == 0) round = load.GameRecords.Select(g => g.Round).DefaultIfEmpty(0).Max();
            return new
            {
                Tournament = _code.TournamentName,
                Problem,
                CasterUrl,
                CasterProblem,
                Cast = cast,
                Round = round,
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
                Standings = load == null || round == 0 ? null : new
                {
                    Games = load.GameRecords.Count(g => g.Counted && g.Round == round),
                    Lobbies = load.GameRecords.Where(g => g.Round == round).Select(g => g.Host).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                        .Select(h => new
                        {
                            Lobby = h,
                            Played = Stats.Standings.GamesPlayed(load.GameRecords, h, round),
                            Rows = Rows(Stats.Standings.Lobby(load.GameRecords, h, round, adv, per)),
                        }).ToList(),
                    All = Rows(Stats.Standings.Round(load.GameRecords, round, adv, per)),
                },
                Advance = adv,
                PerRound = per,
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

        public void Dispose()
        {
            _cts.Cancel();
            _caster?.Dispose();
        }
    }
}
