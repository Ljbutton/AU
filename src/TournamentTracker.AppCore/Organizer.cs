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
                if (_lobbies.TryGetValue(lobby, out var have) && have.Sent >= sent) return;
                _lobbies[lobby] = (data, _clock(), sent);
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
