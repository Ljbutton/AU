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

        /// <summary>Posts a message with one file attached, as the bot.</summary>
        public Task<DiscordResult> PostFileAsync(string botToken, string channelId, string content, string fileName, byte[] file, CancellationToken ct = default)
        {
            string payload = JsonSerializer.Serialize(new
            {
                content,
                allowed_mentions = new { parse = Array.Empty<string>() },
                attachments = new[] { new { id = 0, filename = fileName } },
            });
            return SendAsync(() =>
            {
                var form = new MultipartFormDataContent();
                form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
                var part = new ByteArrayContent(file);
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                form.Add(part, "files[0]", fileName);
                var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/channels/{channelId}/messages") { Content = form };
                Authorize(req, botToken);
                return req;
            }, "channel-post:" + botToken.GetHashCode() + ":" + channelId, ct);
        }

        public Task<DiscordResult> PostMessageAsync(string botToken, string channelId, string content, CancellationToken ct = default)
        {
            string payload = JsonSerializer.Serialize(new { content, allowed_mentions = new { parse = Array.Empty<string>() } });
            return SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/channels/{channelId}/messages")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                Authorize(req, botToken);
                return req;
            }, "channel-post:" + botToken.GetHashCode() + ":" + channelId, ct);
        }

        /// <summary>Posts embeds as the bot (used by the scheduled preliminary job).</summary>
        public Task<DiscordResult> PostEmbedsAsync(string botToken, string channelId, WebhookMessage message, CancellationToken ct = default)
        {
            string json = JsonSerializer.Serialize(new { embeds = message.Embeds, allowed_mentions = new { parse = Array.Empty<string>() } }, WebhookMessage.JsonOptions);
            return SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/channels/{channelId}/messages") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                Authorize(req, botToken);
                return req;
            }, "channel-post:" + botToken.GetHashCode() + ":" + channelId, ct);
        }

        public Task<DiscordResult> EditEmbedsAsync(string botToken, string channelId, string messageId, WebhookMessage message, CancellationToken ct = default)
        {
            string json = JsonSerializer.Serialize(new { embeds = message.Embeds }, WebhookMessage.JsonOptions);
            return SendAsync(() =>
            {
                var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{_apiBase}/channels/{channelId}/messages/{messageId}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                Authorize(req, botToken);
                return req;
            }, "channel-edit:" + botToken.GetHashCode() + ":" + channelId, ct);
        }

        /// <summary>Up to 100 messages, newest first, older than <paramref name="before"/> when given.</summary>
        public async Task<IReadOnlyList<ChannelMessage>?> GetMessagesAsync(string botToken, string channelId, string? before = null, CancellationToken ct = default)
        {
            var result = await SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{_apiBase}/channels/{channelId}/messages?limit=100" + (before == null ? "" : "&before=" + before));
                Authorize(req, botToken);
                return req;
            }, "channel-read:" + botToken.GetHashCode() + ":" + channelId, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                _log.Warn($"Could not read Discord channel {channelId}: {result}");
                return null;
            }
            using var doc = JsonDocument.Parse(result.Body);
            return doc.RootElement.EnumerateArray().Select(ChannelMessage.Parse).ToList();
        }

        /// <summary>Downloads an attachment from Discord's CDN (the URL carries its own signature).</summary>
        public async Task<string?> DownloadAsync(string url, CancellationToken ct = default)
        {
            try
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                _log.Warn("Download failed: " + e.Message);
                return null;
            }
        }

        /// <summary>Edits a message this webhook posted earlier.</summary>
        public Task<DiscordResult> EditWebhookMessageAsync(string webhookUrl, string messageId, WebhookMessage message, CancellationToken ct = default)
        {
            string json = JsonSerializer.Serialize(message, WebhookMessage.JsonOptions);
            return SendAsync(() => new HttpRequestMessage(new HttpMethod("PATCH"), MessageUrl(webhookUrl, messageId))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            }, "webhook-edit:" + webhookUrl, ct);
        }

        public Task<DiscordResult> DeleteWebhookMessageAsync(string webhookUrl, string messageId, CancellationToken ct = default) =>
            SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, MessageUrl(webhookUrl, messageId)), "webhook-delete:" + webhookUrl, ct);

        /// <summary>The ID of the message a webhook post (sent with wait=true) created.</summary>
        public static string? MessageIdOf(DiscordResult result)
        {
            try
            {
                using var doc = JsonDocument.Parse(result.Body);
                return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string MessageUrl(string webhookUrl, string messageId)
        {
            int q = webhookUrl.IndexOf('?');
            string path = q < 0 ? webhookUrl : webhookUrl.Substring(0, q);
            string query = q < 0 ? "" : webhookUrl.Substring(q);
            return path.TrimEnd('/') + "/messages/" + messageId + query;
        }

        /// <summary>A webhook post with one file attached (the game's data, for the organiser's scheduled job).</summary>
        public Task<DiscordResult> ExecuteWebhookWithFileAsync(string webhookUrl, WebhookMessage message, string fileName, byte[] file, CancellationToken ct = default)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(message, WebhookMessage.JsonOptions))!.AsObject();
            node["attachments"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = 0, ["filename"] = fileName });
            string payload = node.ToJsonString();
            return SendAsync(() =>
            {
                var form = new MultipartFormDataContent();
                form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
                var part = new ByteArrayContent(file);
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                form.Add(part, "files[0]", fileName);
                return new HttpRequestMessage(HttpMethod.Post, webhookUrl + (webhookUrl.Contains("?") ? "&" : "?") + "wait=true") { Content = form };
            }, "webhook:" + webhookUrl, ct);
        }

        public Task<DiscordResult> AddReactionAsync(string botToken, string channelId, string messageId, string emoji, CancellationToken ct = default) =>
            SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Put,
                    $"{_apiBase}/channels/{channelId}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}/@me");
                Authorize(req, botToken);
                return req;
            }, "reaction:" + botToken.GetHashCode() + ":" + channelId, ct);

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

    public sealed class ChannelMessage
    {
        public string Id { get; set; } = "";
        public string Content { get; set; } = "";
        public string AuthorId { get; set; } = "";
        public bool AuthorIsBot { get; set; }
        public int Embeds { get; set; }
        public string? EmbedTitle { get; set; }
        public string? EmbedDescription { get; set; }
        public string? EmbedFooter { get; set; }
        public List<(string FileName, string Url)> Attachments { get; set; } = new List<(string, string)>();

        internal static ChannelMessage Parse(JsonElement e)
        {
            var m = new ChannelMessage
            {
                Id = e.GetProperty("id").GetString() ?? "",
                Content = e.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "",
                Embeds = e.TryGetProperty("embeds", out var em) && em.ValueKind == JsonValueKind.Array ? em.GetArrayLength() : 0,
            };
            if (m.Embeds > 0)
            {
                var first = e.GetProperty("embeds")[0];
                m.EmbedTitle = first.TryGetProperty("title", out var t) ? t.GetString() : null;
                m.EmbedDescription = first.TryGetProperty("description", out var d) ? d.GetString() : null;
                m.EmbedFooter = first.TryGetProperty("footer", out var f) && f.TryGetProperty("text", out var ft) ? ft.GetString() : null;
            }
            if (e.TryGetProperty("author", out var a))
            {
                m.AuthorId = a.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                m.AuthorIsBot = a.TryGetProperty("bot", out var b) && b.ValueKind == JsonValueKind.True;
            }
            if (e.TryGetProperty("attachments", out var att) && att.ValueKind == JsonValueKind.Array)
                foreach (var f in att.EnumerateArray())
                    m.Attachments.Add((f.GetProperty("filename").GetString() ?? "", f.GetProperty("url").GetString() ?? ""));
            return m;
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
