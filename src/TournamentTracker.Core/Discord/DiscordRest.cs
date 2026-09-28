using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.Discord
{
    public sealed class DiscordResult
    {
        public bool Ok { get; set; }
        public int Status { get; set; }

        /// <summary>Discord's JSON error code, e.g. 40032 "Target user is not connected to voice".</summary>
        public int ErrorCode { get; set; }
        public string Body { get; set; } = "";

        public const int NotInVoice = 40032;
        public const int UnknownMember = 10007;

        public bool IsNotInVoice => ErrorCode == NotInVoice;
        public override string ToString() => Ok ? "OK" : $"HTTP {Status} ({ErrorCode}) {Body}";
    }

    public sealed class GuildMember
    {
        public string Id { get; set; } = "";
        public string Username { get; set; } = "";
        public string? GlobalName { get; set; }
        public string? Nick { get; set; }
        public string DisplayName => Nick ?? GlobalName ?? Username;
    }

    /// <summary>
    /// The handful of Discord REST calls the mod needs. No gateway connection: server mute,
    /// member lookup and webhooks are all plain HTTPS. Rate limits are honoured per bucket
    /// from Discord's headers, and 429s are retried after the advertised delay.
    /// </summary>
    public sealed class DiscordRest
    {
        public const string DefaultApiBase = "https://discord.com/api/v10";
        private const int MaxAttempts = 5;

        private readonly HttpClient _http;
        private readonly string _apiBase;
        private readonly ILog _log;
        private readonly ConcurrentDictionary<string, DateTime> _bucketResetAt = new ConcurrentDictionary<string, DateTime>();
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public DiscordRest(HttpClient http, ILog log, string apiBase = DefaultApiBase,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _http = http;
            _log = log;
            _apiBase = apiBase.TrimEnd('/');
            _delay = delay ?? Task.Delay;
        }

        public static HttpClient CreateHttpClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://github.com/Ljbutton/AU, 1.0)");
            return http;
        }

        /// <summary>Server-mutes and/or server-deafens a member who is in a voice channel.</summary>
        public Task<DiscordResult> SetVoiceStateAsync(string botToken, string guildId, string userId,
            bool mute, bool deaf, CancellationToken ct = default)
        {
            string body = "{\"mute\":" + (mute ? "true" : "false") + ",\"deaf\":" + (deaf ? "true" : "false") + "}";
            return SendAsync(() =>
            {
                var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{_apiBase}/guilds/{guildId}/members/{userId}")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                Authorize(req, botToken);
                req.Headers.TryAddWithoutValidation("X-Audit-Log-Reason", "Among Us automute");
                return req;
            }, "member:" + botToken.GetHashCode() + ":" + guildId, ct);
        }

        public async Task<GuildMember?> GetMemberAsync(string botToken, string guildId, string userId, CancellationToken ct = default)
        {
            var result = await SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"{_apiBase}/guilds/{guildId}/members/{userId}");
                Authorize(req, botToken);
                return req;
            }, "get-member:" + botToken.GetHashCode(), ct).ConfigureAwait(false);
            return result.Ok ? ParseMember(JsonDocument.Parse(result.Body).RootElement) : null;
        }

        /// <summary>Members whose username or server nickname starts with <paramref name="query"/>.</summary>
        public async Task<IReadOnlyList<GuildMember>> SearchMembersAsync(string botToken, string guildId, string query, CancellationToken ct = default)
        {
            var result = await SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{_apiBase}/guilds/{guildId}/members/search?limit=10&query={Uri.EscapeDataString(query)}");
                Authorize(req, botToken);
                return req;
            }, "search:" + botToken.GetHashCode(), ct).ConfigureAwait(false);
            if (!result.Ok) return Array.Empty<GuildMember>();
            using var doc = JsonDocument.Parse(result.Body);
            return doc.RootElement.EnumerateArray().Select(ParseMember).Where(m => m != null).Select(m => m!).ToList();
        }

        public Task<DiscordResult> ExecuteWebhookAsync(string webhookUrl, WebhookMessage message, CancellationToken ct = default)
        {
            string json = JsonSerializer.Serialize(message, WebhookMessage.JsonOptions);
            return SendAsync(() => new HttpRequestMessage(HttpMethod.Post, webhookUrl + (webhookUrl.Contains("?") ? "&" : "?") + "wait=true")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            }, "webhook:" + webhookUrl, ct);
        }

        private static void Authorize(HttpRequestMessage req, string botToken) =>
            req.Headers.TryAddWithoutValidation("Authorization", "Bot " + botToken);

        private async Task<DiscordResult> SendAsync(Func<HttpRequestMessage> makeRequest, string bucket, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                if (_bucketResetAt.TryGetValue(bucket, out var resetAt))
                {
                    var wait = resetAt - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await _delay(wait, ct).ConfigureAwait(false);
                }

                HttpResponseMessage response;
                string body;
                try
                {
                    using var request = makeRequest();
                    response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                    body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
                {
                    _log.Warn($"Discord request failed ({e.GetType().Name}: {e.Message}), retrying");
                    await _delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                catch (Exception e)
                {
                    return new DiscordResult { Ok = false, Status = 0, Body = e.Message };
                }

                using (response)
                {
                    TrackBucket(bucket, response);

                    if ((int)response.StatusCode == 429 && attempt < MaxAttempts)
                    {
                        var retry = RetryAfter(response, body);
                        _log.Warn($"Discord rate limit hit, waiting {retry.TotalSeconds:0.##}s");
                        await _delay(retry, ct).ConfigureAwait(false);
                        continue;
                    }

                    if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
                    {
                        await _delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                        continue;
                    }

                    return new DiscordResult
                    {
                        Ok = response.IsSuccessStatusCode,
                        Status = (int)response.StatusCode,
                        ErrorCode = response.IsSuccessStatusCode ? 0 : ErrorCodeOf(body),
                        Body = body,
                    };
                }
            }
        }

        private void TrackBucket(string bucket, HttpResponseMessage response)
        {
            if (Header(response, "X-RateLimit-Remaining") == "0"
                && double.TryParse(Header(response, "X-RateLimit-Reset-After"), NumberStyles.Float, CultureInfo.InvariantCulture, out var after))
            {
                _bucketResetAt[bucket] = DateTime.UtcNow + TimeSpan.FromSeconds(after);
            }
            else
            {
                _bucketResetAt.TryRemove(bucket, out _);
            }
        }

        private static TimeSpan RetryAfter(HttpResponseMessage response, string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("retry_after", out var ra) && ra.TryGetDouble(out var seconds))
                    return TimeSpan.FromSeconds(Math.Max(0.05, seconds));
            }
            catch (JsonException) { }

            if (double.TryParse(Header(response, "Retry-After"), NumberStyles.Float, CultureInfo.InvariantCulture, out var header))
                return TimeSpan.FromSeconds(header);
            return TimeSpan.FromSeconds(1);
        }

        private static string? Header(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

        private static int ErrorCodeOf(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("code", out var code) && code.TryGetInt32(out var value))
                    return value;
            }
            catch (JsonException) { }
            return 0;
        }

        private static GuildMember? ParseMember(JsonElement e)
        {
            if (!e.TryGetProperty("user", out var user)) return null;
            return new GuildMember
            {
                Id = user.GetProperty("id").GetString() ?? "",
                Username = user.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "",
                GlobalName = user.TryGetProperty("global_name", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null,
                Nick = e.TryGetProperty("nick", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
            };
        }
    }

    public sealed class WebhookMessage
    {
        internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("embeds")] public List<Embed>? Embeds { get; set; }

        /// <summary>Stops a player named "@everyone" from pinging the server.</summary>
        [JsonPropertyName("allowed_mentions")] public AllowedMentions AllowedMentions { get; set; } = new AllowedMentions();
    }

    public sealed class AllowedMentions
    {
        [JsonPropertyName("parse")] public List<string> Parse { get; set; } = new List<string>();
    }

    public sealed class Embed
    {
        public const int TitleLimit = 256;
        public const int DescriptionLimit = 4096;
        public const int FieldValueLimit = 1024;

        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("color")] public int? Color { get; set; }
        [JsonPropertyName("fields")] public List<EmbedField>? Fields { get; set; }
        [JsonPropertyName("footer")] public EmbedFooter? Footer { get; set; }
        [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
    }

    public sealed class EmbedField
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("value")] public string Value { get; set; } = "";
        [JsonPropertyName("inline")] public bool Inline { get; set; }
    }

    public sealed class EmbedFooter
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
    }
}
