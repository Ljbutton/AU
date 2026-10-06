using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>Twitch's own limits (Helix), so nothing is sent that it would turn down.</summary>
    public static class TwitchLimits
    {
        public const int PollTitle = 60, PollChoice = 25, PollMinChoices = 2, PollMaxChoices = 5, PollMinSeconds = 15, PollMaxSeconds = 1800;
        public const int PredictionTitle = 45, Outcome = 25, MinOutcomes = 2, MaxOutcomes = 10, PredictionMinSeconds = 30, PredictionMaxSeconds = 1800;
        public const int RewardTitle = 45, RewardPrompt = 200, ChatMessage = 500;

        public static string Cut(string text, int max) => text.Length <= max ? text : text.Substring(0, Math.Max(1, max - 1)).TrimEnd() + "…";
        public static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));

        /// <summary>Choice titles cut to fit and kept apart (Twitch refuses two the same).</summary>
        public static List<string> Choices(IEnumerable<string> titles, int max)
        {
            var list = new List<string>();
            foreach (var t in titles)
            {
                string c = Cut(t, max);
                for (int n = 2; list.Contains(c, StringComparer.OrdinalIgnoreCase); n++) c = Cut(t, max - 2 - n.ToString().Length) + " " + n;
                list.Add(c);
            }
            return list;
        }
    }

    public sealed class TwitchUser
    {
        public string Id { get; set; } = "";
        public string Login { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>"partner", "affiliate" or "" (polls, predictions and channel points need one of the first two).</summary>
        public string BroadcasterType { get; set; } = "";
    }

    public sealed class TwitchChoice
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
    }

    /// <summary>What The Button asks of Twitch (Helix). The real one, and a fake for test mode.</summary>
    public interface ITwitchApi
    {
        Task<TwitchUser> MeAsync();
        Task<(string Id, List<TwitchChoice> Choices)> CreatePollAsync(string title, IList<string> choices, int seconds);
        Task EndPollAsync(string id);
        Task<(string Id, List<TwitchChoice> Outcomes)> CreatePredictionAsync(string title, IList<string> outcomes, int seconds);
        /// <summary>"LOCKED", "RESOLVED" (with the winner) or "CANCELED" (everyone gets their points back).</summary>
        Task EndPredictionAsync(string id, string status, string? winningOutcomeId = null);
        Task<List<TwitchChoice>> RewardsAsync();
        Task<string> CreateRewardAsync(string title, int cost, string prompt, bool inputRequired);
        /// <summary>FULFILLED, or CANCELED (the points go back).</summary>
        Task UpdateRedemptionAsync(string rewardId, string redemptionId, bool fulfilled);
        Task SendChatAsync(string text);
        Task SubscribeAsync(string type, string version, object condition, string sessionId);
    }

    public sealed class TwitchException : Exception
    {
        public HttpStatusCode Status { get; }
        public TwitchException(HttpStatusCode status, string message) : base(message) => Status = status;
    }

    /// <summary>The broadcaster's sign-in: kept in twitch-token.json on this PC only.</summary>
    public sealed class TwitchToken
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        public string UserId { get; set; } = "";
        public string Login { get; set; } = "";
        public List<string> Scopes { get; set; } = new List<string>();

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public static TwitchToken? Load(string? path)
        {
            try { return path != null && File.Exists(path) ? JsonSerializer.Deserialize<TwitchToken>(File.ReadAllText(path), Json) : null; }
            catch (Exception) { return null; }
        }
        public void Save(string? path)
        {
            if (path == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
    }

    /// <summary>
    /// Signing in with the broadcaster account: Twitch's device code flow (no web server needed). The
    /// Button shows a code; you enter it at twitch.tv/activate signed in as the channel; The Button
    /// waits for it, then keeps the sign-in fresh by itself.
    /// </summary>
    public sealed class TwitchAuth
    {
        public static readonly string[] Scopes =
        {
            "channel:manage:polls", "channel:read:polls", "channel:manage:predictions", "channel:read:predictions",
            "channel:manage:redemptions", "channel:read:redemptions", "user:read:chat", "user:write:chat",
        };
        public const string IdBase = "https://id.twitch.tv/oauth2/";
        private readonly HttpClient _http;
        private readonly Func<string> _clientId;
        private readonly string? _path;
        private readonly Func<DateTime> _clock;
        public TwitchToken? Token { get; private set; }

        public TwitchAuth(HttpClient http, Func<string> clientId, string? tokenPath, Func<DateTime>? clock = null)
        {
            _http = http;
            _clientId = clientId;
            _path = tokenPath;
            _clock = clock ?? (() => DateTime.UtcNow);
            Token = TwitchToken.Load(tokenPath);
        }

        private async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> form)
        {
            using var r = await _http.PostAsync(url, new FormUrlEncodedContent(form)).ConfigureAwait(false);
            string body = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonElement json = default;
            try { json = JsonDocument.Parse(body.Length > 0 ? body : "{}").RootElement.Clone(); } catch (JsonException) { }
            if (!r.IsSuccessStatusCode)
            {
                string msg = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("message", out var m) ? m.GetString() ?? "" : body;
                throw new TwitchException(r.StatusCode, msg);
            }
            return json;
        }

        /// <summary>Starts signing in: the code to enter and where.</summary>
        public async Task<(string DeviceCode, string UserCode, string Url, int Interval, int ExpiresIn)> StartAsync()
        {
            var j = await PostFormAsync(IdBase + "device", new Dictionary<string, string> { ["client_id"] = _clientId(), ["scopes"] = string.Join(" ", Scopes) }).ConfigureAwait(false);
            return (j.GetProperty("device_code").GetString()!, j.GetProperty("user_code").GetString()!, j.GetProperty("verification_uri").GetString()!,
                j.TryGetProperty("interval", out var i) ? i.GetInt32() : 5, j.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 1800);
        }

        /// <summary>Once: has the code been entered? null while waiting.</summary>
        public async Task<TwitchToken?> PollAsync(string deviceCode)
        {
            try
            {
                var j = await PostFormAsync(IdBase + "token", new Dictionary<string, string>
                {
                    ["client_id"] = _clientId(), ["scopes"] = string.Join(" ", Scopes), ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }).ConfigureAwait(false);
                Token = FromJson(j, null);
                await ValidateAsync().ConfigureAwait(false);
                Token.Save(_path);
                return Token;
            }
            catch (TwitchException e) when (e.Message.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("slow_down", StringComparison.OrdinalIgnoreCase)) { return null; }
        }

        private TwitchToken FromJson(JsonElement j, TwitchToken? was) => new TwitchToken
        {
            AccessToken = j.GetProperty("access_token").GetString()!,
            RefreshToken = j.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : was?.RefreshToken ?? "",
            ExpiresAt = _clock().AddSeconds(j.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number ? ex.GetInt32() : 3600),
            Scopes = j.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Array ? sc.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : was?.Scopes ?? new List<string>(),
            UserId = was?.UserId ?? "", Login = was?.Login ?? "",
        };

        /// <summary>Who the sign-in is for (Twitch asks apps to check this every hour).</summary>
        public async Task ValidateAsync()
        {
            if (Token == null) return;
            using var req = new HttpRequestMessage(HttpMethod.Get, IdBase + "validate");
            req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + Token.AccessToken);
            using var r = await _http.SendAsync(req).ConfigureAwait(false);
            if (r.StatusCode == HttpStatusCode.Unauthorized) { await RefreshAsync().ConfigureAwait(false); return; }
            var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
            if (j.TryGetProperty("user_id", out var id)) Token.UserId = id.GetString() ?? "";
            if (j.TryGetProperty("login", out var l)) Token.Login = l.GetString() ?? "";
            if (j.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array) Token.Scopes = sc.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        }

        public async Task RefreshAsync()
        {
            if (Token == null || Token.RefreshToken.Length == 0) throw new TwitchException(HttpStatusCode.Unauthorized, "Signed out of Twitch: connect again.");
            try
            {
                var j = await PostFormAsync(IdBase + "token", new Dictionary<string, string>
                {
                    ["client_id"] = _clientId(), ["grant_type"] = "refresh_token", ["refresh_token"] = Token.RefreshToken,
                }).ConfigureAwait(false);
                Token = FromJson(j, Token);
                Token.Save(_path);
            }
            catch (TwitchException e) when (e.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                SignOut();
                throw new TwitchException(HttpStatusCode.Unauthorized, "Signed out of Twitch: connect again.");
            }
        }

        public void SignOut()
        {
            Token = null;
            try { if (_path != null && File.Exists(_path)) File.Delete(_path); } catch (Exception) { }
        }

        /// <summary>Scopes this sign-in is missing (an older sign-in, before a feature was added).</summary>
        public List<string> Missing() => Token == null ? Scopes.ToList() : Scopes.Where(s => !Token.Scopes.Contains(s)).ToList();
    }

    /// <summary>Helix, as the broadcaster: polls, predictions, channel points, chat and EventSub subscriptions.</summary>
    public sealed class HelixApi : ITwitchApi
    {
        public const string Base = "https://api.twitch.tv/helix/";
        private readonly HttpClient _http;
        private readonly TwitchAuth _auth;
        private readonly Func<string> _clientId;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        public HelixApi(HttpClient http, TwitchAuth auth, Func<string> clientId)
        {
            _http = http;
            _auth = auth;
            _clientId = clientId;
        }

        private string Me => _auth.Token?.UserId ?? throw new TwitchException(HttpStatusCode.Unauthorized, "Not signed in to Twitch.");

        private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body = null, bool retry = true)
        {
            var token = _auth.Token ?? throw new TwitchException(HttpStatusCode.Unauthorized, "Not signed in to Twitch.");
            using var req = new HttpRequestMessage(method, Base + path);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.AccessToken);
            req.Headers.TryAddWithoutValidation("Client-Id", _clientId());
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            using var r = await _http.SendAsync(req).ConfigureAwait(false);
            if (r.StatusCode == HttpStatusCode.Unauthorized && retry)
            {
                await _auth.RefreshAsync().ConfigureAwait(false);
                return await SendAsync(method, path, body, false).ConfigureAwait(false);
            }
            string text = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonElement json = default;
            try { json = JsonDocument.Parse(text.Length > 0 ? text : "{}").RootElement.Clone(); } catch (JsonException) { }
            if (!r.IsSuccessStatusCode)
                throw new TwitchException(r.StatusCode, json.ValueKind == JsonValueKind.Object && json.TryGetProperty("message", out var m) ? m.GetString() ?? text : text);
            return json;
        }

        private static JsonElement First(JsonElement j) => j.GetProperty("data")[0];
        private static List<TwitchChoice> Items(JsonElement e, string prop) => e.GetProperty(prop).EnumerateArray()
            .Select(x => new TwitchChoice { Id = x.GetProperty("id").GetString()!, Title = x.GetProperty("title").GetString()! }).ToList();

        public async Task<TwitchUser> MeAsync()
        {
            var u = First(await SendAsync(HttpMethod.Get, "users").ConfigureAwait(false));
            return new TwitchUser { Id = u.GetProperty("id").GetString()!, Login = u.GetProperty("login").GetString()!, Name = u.GetProperty("display_name").GetString()!, BroadcasterType = u.GetProperty("broadcaster_type").GetString() ?? "" };
        }

        public async Task<(string Id, List<TwitchChoice> Choices)> CreatePollAsync(string title, IList<string> choices, int seconds)
        {
            var list = TwitchLimits.Choices(choices.Take(TwitchLimits.PollMaxChoices), TwitchLimits.PollChoice);
            if (list.Count < TwitchLimits.PollMinChoices) throw new ArgumentException("A poll needs at least two choices.");
            var p = First(await SendAsync(HttpMethod.Post, "polls", new
            {
                BroadcasterId = Me, Title = TwitchLimits.Cut(title, TwitchLimits.PollTitle),
                Choices = list.Select(c => new { Title = c }).ToList(),
                Duration = TwitchLimits.Clamp(seconds, TwitchLimits.PollMinSeconds, TwitchLimits.PollMaxSeconds),
            }).ConfigureAwait(false));
            return (p.GetProperty("id").GetString()!, Items(p, "choices"));
        }

        public Task EndPollAsync(string id) => SendAsync(HttpMethod.Patch, "polls", new { BroadcasterId = Me, Id = id, Status = "TERMINATED" });

        public async Task<(string Id, List<TwitchChoice> Outcomes)> CreatePredictionAsync(string title, IList<string> outcomes, int seconds)
        {
            var list = TwitchLimits.Choices(outcomes.Take(TwitchLimits.MaxOutcomes), TwitchLimits.Outcome);
            if (list.Count < TwitchLimits.MinOutcomes) throw new ArgumentException("A prediction needs at least two outcomes.");
            var p = First(await SendAsync(HttpMethod.Post, "predictions", new
            {
                BroadcasterId = Me, Title = TwitchLimits.Cut(title, TwitchLimits.PredictionTitle),
                Outcomes = list.Select(c => new { Title = c }).ToList(),
                PredictionWindow = TwitchLimits.Clamp(seconds, TwitchLimits.PredictionMinSeconds, TwitchLimits.PredictionMaxSeconds),
            }).ConfigureAwait(false));
            return (p.GetProperty("id").GetString()!, Items(p, "outcomes"));
        }

        public Task EndPredictionAsync(string id, string status, string? winningOutcomeId = null) =>
            SendAsync(HttpMethod.Patch, "predictions", winningOutcomeId == null
                ? new { BroadcasterId = Me, Id = id, Status = status }
                : (object)new { BroadcasterId = Me, Id = id, Status = status, WinningOutcomeId = winningOutcomeId });

        public async Task<List<TwitchChoice>> RewardsAsync()
        {
            var j = await SendAsync(HttpMethod.Get, $"channel_points/custom_rewards?broadcaster_id={Me}&only_manageable_rewards=true").ConfigureAwait(false);
            return j.GetProperty("data").EnumerateArray().Select(x => new TwitchChoice { Id = x.GetProperty("id").GetString()!, Title = x.GetProperty("title").GetString()! }).ToList();
        }

        public async Task<string> CreateRewardAsync(string title, int cost, string prompt, bool inputRequired)
        {
            var r = First(await SendAsync(HttpMethod.Post, $"channel_points/custom_rewards?broadcaster_id={Me}", new
            {
                Title = TwitchLimits.Cut(title, TwitchLimits.RewardTitle), Cost = Math.Max(1, cost),
                Prompt = TwitchLimits.Cut(prompt, TwitchLimits.RewardPrompt), IsUserInputRequired = inputRequired,
            }).ConfigureAwait(false));
            return r.GetProperty("id").GetString()!;
        }

        public Task UpdateRedemptionAsync(string rewardId, string redemptionId, bool fulfilled) =>
            SendAsync(HttpMethod.Patch, $"channel_points/custom_rewards/redemptions?broadcaster_id={Me}&reward_id={Uri.EscapeDataString(rewardId)}&id={Uri.EscapeDataString(redemptionId)}",
                new { Status = fulfilled ? "FULFILLED" : "CANCELED" });

        public Task SendChatAsync(string text) => SendAsync(HttpMethod.Post, "chat/messages", new { BroadcasterId = Me, SenderId = Me, Message = TwitchLimits.Cut(text, TwitchLimits.ChatMessage) });

        public Task SubscribeAsync(string type, string version, object condition, string sessionId) =>
            SendAsync(HttpMethod.Post, "eventsub/subscriptions", new { Type = type, Version = version, Condition = condition, Transport = new { Method = "websocket", SessionId = sessionId } });
    }
}
