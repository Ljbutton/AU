using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TournamentTracker.Discord;
using TournamentTracker.Stats;

namespace TournamentTracker.Sheets
{
    /// <summary>
    /// Sends each finished game to a Google Sheet through the Apps Script web app in
    /// sheets/TournamentSheet.gs. Sends run one at a time off the game thread. A game that
    /// can't be delivered (offline, script not deployed yet) waits in an outbox folder and is
    /// retried after the next game or with !sheetsync. The script replaces rows by game ID,
    /// so sending a game twice never duplicates it.
    /// </summary>
    public sealed class SheetsSync
    {
        internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly string _url;
        private readonly string _secret;
        private readonly HttpClient _http;
        private readonly string _outboxDir;
        private readonly ILog _log;
        private readonly object _lock = new object();
        private Task _chain = Task.CompletedTask;

        public SheetsSync(string url, string secret, HttpClient http, string outboxDir, ILog log)
        {
            _url = url.Trim();
            _secret = secret;
            _http = http;
            _outboxDir = outboxDir;
            _log = log;
        }

        public static HttpClient CreateHttpClient() =>
            // Apps Script can take a while to wake up; its reply arrives through a redirect.
            new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

        /// <summary>Completes when everything queued so far has been attempted.</summary>
        public Task Pending
        {
            get { lock (_lock) return _chain; }
        }

        /// <summary>Queues a game, then retries anything left in the outbox.</summary>
        public void Enqueue(GameRecord game)
        {
            var payload = Build(game);
            Chain(async () =>
            {
                if (await SendAsync(payload).ConfigureAwait(false)) await FlushOutboxAsync().ConfigureAwait(false);
            });
        }

        /// <summary>Retries games that failed to send earlier.</summary>
        public void Flush() => Chain(FlushOutboxAsync);

        public int OutboxCount => Directory.Exists(_outboxDir) ? Directory.GetFiles(_outboxDir, "*.json").Length : 0;

        private void Chain(Func<Task> work)
        {
            lock (_lock)
            {
                _chain = _chain.ContinueWith(async _ =>
                {
                    try { await work().ConfigureAwait(false); }
                    catch (Exception e) { _log.Error("Google Sheets sync failed: " + e.Message); }
                }, TaskScheduler.Default).Unwrap();
            }
        }

        private async Task FlushOutboxAsync()
        {
            if (!Directory.Exists(_outboxDir)) return;
            foreach (var file in Directory.GetFiles(_outboxDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var payload = JsonSerializer.Deserialize<SheetsPayload>(File.ReadAllText(file), Json);
                if (payload == null) continue;
                if (!await SendAsync(payload, saveOnFailure: false).ConfigureAwait(false)) return;
                File.Delete(file);
            }
        }

        private async Task<bool> SendAsync(SheetsPayload payload, bool saveOnFailure = true)
        {
            string error;
            try
            {
                payload.Secret = _secret;
                string json = JsonSerializer.Serialize(payload, Json);
                payload.Secret = null;
                using var response = await _http.PostAsync(_url, new StringContent(json, Encoding.UTF8, "application/json")).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                error = ErrorIn(response, body);
                if (error.Length == 0)
                {
                    _log.Info($"Game {payload.Game.Number} sent to Google Sheets");
                    return true;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            _log.Error($"Could not send game {payload.Game.Number} to Google Sheets: {error}");
            if (saveOnFailure)
            {
                Directory.CreateDirectory(_outboxDir);
                File.WriteAllText(Path.Combine(_outboxDir, payload.Game.Id + ".json"), JsonSerializer.Serialize(payload, Json));
            }
            return false;
        }

        /// <summary>The script answers {"ok":true}; anything else (an HTML error page, a bad secret) is a failure.</summary>
        private static string ErrorIn(HttpResponseMessage response, string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return "";
                return root.TryGetProperty("error", out var e) ? e.GetString() ?? "unknown error" : "unexpected reply";
            }
            catch (JsonException)
            {
                return response.IsSuccessStatusCode
                    ? "the web app didn't answer with JSON; check the deployment URL and that access is set to Anyone"
                    : $"HTTP {(int)response.StatusCode}";
            }
        }

        public static string GameId(GameRecord game) =>
            $"{Slug(game.Tournament)}-{game.GameNumber:0000}-{game.StartedUtc:yyyyMMddHHmmss}";

        public static SheetsPayload Build(GameRecord game)
        {
            var mvp = game.Players.Where(p => p.Won).OrderByDescending(p => p.Points).FirstOrDefault();
            return new SheetsPayload
            {
                Game = new SheetsGame
                {
                    Id = GameId(game),
                    Tournament = game.Tournament,
                    Number = game.GameNumber,
                    StartedUtc = game.StartedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    Lobby = game.LobbyCode,
                    Map = game.Map,
                    Winner = game.Winner ?? "",
                    Result = game.Counted ? Outcome.Describe(game.EndReason) : "No result",
                    Length = ReportFormatter.Clock(game.DurationSeconds),
                    Impostors = string.Join(", ", game.Players.Where(p => p.IsImpostor).Select(p => p.Label)),
                    Meetings = game.Meetings.Count,
                    Mvp = mvp == null ? "" : mvp.Name,
                    Counted = game.Counted,
                },
                Players = game.Players.Select(p => new SheetsPlayer
                {
                    Key = p.Key,
                    Name = p.Name,
                    Color = Colors.Name(p.ColorId),
                    Role = p.Role,
                    Team = p.IsImpostor ? "Impostor" : "Crewmate",
                    Result = !game.Counted ? "" : p.Won ? "Won" : "Lost",
                    Death = p.DeathCause ?? "Alive",
                    Kills = p.Kills,
                    FirstBlood = p.FirstBlood,
                    CorrectVotes = p.CorrectVotes,
                    WrongVotes = p.IncorrectVotes,
                    Skips = p.Skips,
                    Missed = p.MissedVotes,
                    CorrectVoteOuts = p.EjectVotesOnImpostor,
                    WrongVoteOuts = p.IsImpostor ? 0 : p.EjectVotesOnCrewmate,
                    MeetingsCalled = p.MeetingsCalled,
                    BodiesReported = p.BodiesReported,
                    TasksDone = p.TasksCompleted,
                    TasksTotal = p.TasksTotal,
                    Sabotages = p.Sabotages,
                    Points = p.Points,
                    Breakdown = p.PointBreakdown.Select(l => new SheetsPointLine { Rule = l.Rule, Points = l.Points }).ToList(),
                }).ToList(),
            };
        }

        private static string Slug(string name)
        {
            var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
            string slug = new string(chars).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return slug.Length == 0 ? "tournament" : slug;
        }
    }

    public sealed class SheetsPayload
    {
        public string? Secret { get; set; }
        public int Version { get; set; } = 1;
        public SheetsGame Game { get; set; } = new SheetsGame();
        public List<SheetsPlayer> Players { get; set; } = new List<SheetsPlayer>();
    }

    public sealed class SheetsGame
    {
        public string Id { get; set; } = "";
        public string Tournament { get; set; } = "";
        public int Number { get; set; }
        public string StartedUtc { get; set; } = "";
        public string Lobby { get; set; } = "";
        public string Map { get; set; } = "";
        public string Winner { get; set; } = "";
        public string Result { get; set; } = "";
        public string Length { get; set; } = "";
        public string Impostors { get; set; } = "";
        public int Meetings { get; set; }
        public string Mvp { get; set; } = "";
        public bool Counted { get; set; }
    }

    public sealed class SheetsPlayer
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Color { get; set; } = "";
        public string Role { get; set; } = "";
        public string Team { get; set; } = "";
        public string Result { get; set; } = "";
        public string Death { get; set; } = "";
        public int Kills { get; set; }
        public bool FirstBlood { get; set; }
        public int CorrectVotes { get; set; }
        public int WrongVotes { get; set; }
        public int Skips { get; set; }
        public int Missed { get; set; }
        public int CorrectVoteOuts { get; set; }
        public int WrongVoteOuts { get; set; }
        public int MeetingsCalled { get; set; }
        public int BodiesReported { get; set; }
        public int TasksDone { get; set; }
        public int TasksTotal { get; set; }
        public int Sabotages { get; set; }
        public double Points { get; set; }
        public List<SheetsPointLine> Breakdown { get; set; } = new List<SheetsPointLine>();
    }

    public sealed class SheetsPointLine
    {
        public string Rule { get; set; } = "";
        public double Points { get; set; }
    }
}
