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
    /// member lookup and webhooks are all plain HTTPS (in the mod, through The Button: see
    /// <see cref="ButtonBridge"/>). Rate limits are honoured per bucket
    /// from Discord's headers, and 429s are retried after the advertised delay.
    /// </summary>
    public sealed class DiscordRest
    {
        public const string DefaultApiBase = "https://discord.com/api/v10";
        private const int MaxAttempts = 5;

        /// <summary>
        /// On The Button's answer to the mod: Discord was never reached (no connection), so the
        /// request can be sent again without any risk of it happening twice.
        /// </summary>
        public const string NotSentHeader = "X-TT-Not-Sent";

        private readonly HttpClient _http;
        private readonly string _apiBase;
        private readonly ILog _log;
        private readonly ConcurrentDictionary<string, DateTime> _bucketResetAt = new ConcurrentDictionary<string, DateTime>();
        /// <summary>Discord's global limit (every route at once) holds until then (UTC ticks).</summary>
        private long _globalResetAt;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public DiscordRest(HttpClient http, ILog log, string apiBase = DefaultApiBase,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _http = http;
            _log = log;
            _apiBase = apiBase.TrimEnd('/');
            _delay = delay ?? Task.Delay;
        }

        /// <summary>
        /// How long one request may take before it counts as failed (null: the HttpClient's own
        /// timeout). The desktop apps share an HttpClient made for big downloads (10 minutes),
        /// which is far too long to wait on Discord.
        /// </summary>
        public TimeSpan? RequestTimeout { get; set; }

        public static HttpClient CreateHttpClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://github.com/Ljbutton/AU, 1.0)");
            return http;
        }

        /// <summary>Server-mutes and/or server-deafens a member who is in a voice channel.</summary>
        public Task<DiscordResult> SetVoiceStateAsync(string botToken, string guildId, string userId,
            bool mute, bool deaf, CancellationToken ct = default) =>
            SetVoiceStateAsync(botToken, guildId, userId, () => (mute, deaf), "Among Us automute", ct);

        /// <summary>
        /// Server-mutes and/or server-deafens a member, asking for the state right before each try:
        /// a rate-limit wait can last seconds, and by then the game may want something else (a
        /// meeting ended), so the stale state is never sent.
        /// </summary>
        public Task<DiscordResult> SetVoiceStateAsync(string botToken, string guildId, string userId,
            Func<(bool Mute, bool Deaf)> state, string reason, CancellationToken ct = default) =>
            SendAsync(() =>
            {
                var (mute, deaf) = state();
                var req = BotRequest(HttpMethod.Patch, $"/guilds/{guildId}/members/{userId}", botToken,
                    Json("{\"mute\":" + (mute ? "true" : "false") + ",\"deaf\":" + (deaf ? "true" : "false") + "}"));
                req.Headers.TryAddWithoutValidation("X-Audit-Log-Reason", reason);
                return req;
            }, Bucket("member", botToken, guildId), ct);

        /// <summary>Members whose username or server nickname starts with <paramref name="query"/>.</summary>
        public async Task<IReadOnlyList<GuildMember>> SearchMembersAsync(string botToken, string guildId, string query, CancellationToken ct = default)
        {
            var result = await SendAsync(() => BotRequest(HttpMethod.Get, $"/guilds/{guildId}/members/search?limit=10&query={Uri.EscapeDataString(query)}", botToken),
                "search:" + botToken.GetHashCode(), ct).ConfigureAwait(false);
            if (!result.Ok) return Array.Empty<GuildMember>();
            using var doc = JsonDocument.Parse(result.Body);
            return doc.RootElement.EnumerateArray().Select(ParseMember).Where(m => m != null).Select(m => m!).ToList();
        }

        public Task<DiscordResult> ExecuteWebhookAsync(string webhookUrl, WebhookMessage message, CancellationToken ct = default)
        {
            string json = JsonSerializer.Serialize(message, WebhookMessage.JsonOptions);
            return SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Waiting(webhookUrl)) { Content = Json(json) }, "webhook:" + webhookUrl, ct, idempotent: false);
        }

        /// <summary>
        /// A one-off tag for a message the bot posts. With enforce_nonce, Discord answers a second
        /// try with the message the first one made instead of posting it again, so a post whose
        /// answer was lost (a timeout, a 5xx) can safely be sent again.
        /// </summary>
        private static string NewNonce() => Guid.NewGuid().ToString("N").Substring(0, 25);

        /// <summary>Posts a message with one file attached, as the bot.</summary>
        public Task<DiscordResult> PostFileAsync(string botToken, string channelId, string content, string fileName, byte[] file, CancellationToken ct = default)
        {
            string payload = JsonSerializer.Serialize(new
            {
                content,
                allowed_mentions = new { parse = Array.Empty<string>() },
                attachments = new[] { new { id = 0, filename = fileName } },
                nonce = NewNonce(),
                enforce_nonce = true,
            });
            return PostToChannelAsync(botToken, channelId, () => FileForm(payload, fileName, file), ct);
        }

        public Task<DiscordResult> PostMessageAsync(string botToken, string channelId, string content, CancellationToken ct = default)
        {
            string payload = JsonSerializer.Serialize(new { content, allowed_mentions = new { parse = Array.Empty<string>() }, nonce = NewNonce(), enforce_nonce = true });
            return PostToChannelAsync(botToken, channelId, () => Json(payload), ct);
        }

        /// <summary>Posts embeds as the bot (used by the scheduled preliminary job).</summary>
        public Task<DiscordResult> PostEmbedsAsync(string botToken, string channelId, WebhookMessage message, CancellationToken ct = default)
        {
            string json = JsonSerializer.Serialize(new { embeds = message.Embeds, components = message.Components, allowed_mentions = new { parse = Array.Empty<string>() }, nonce = NewNonce(), enforce_nonce = true }, WebhookMessage.JsonOptions);
            return PostToChannelAsync(botToken, channelId, () => Json(json), ct);
        }

        private Task<DiscordResult> PostToChannelAsync(string botToken, string channelId, Func<HttpContent> content, CancellationToken ct) =>
            SendAsync(() => BotRequest(HttpMethod.Post, $"/channels/{channelId}/messages", botToken, content()), Bucket("channel-post", botToken, channelId), ct);

        public Task<DiscordResult> EditEmbedsAsync(string botToken, string channelId, string messageId, WebhookMessage message, CancellationToken ct = default)
        {
            string json = JsonSerializer.Serialize(new { embeds = message.Embeds, components = message.Components ?? new List<object>() }, WebhookMessage.JsonOptions);
            return SendAsync(() => BotRequest(HttpMethod.Patch, $"/channels/{channelId}/messages/{messageId}", botToken, Json(json)), Bucket("channel-edit", botToken, channelId), ct);
        }

        public Task<DiscordResult> DeleteMessageAsync(string botToken, string channelId, string messageId, CancellationToken ct = default) =>
            SendAsync(() => BotRequest(HttpMethod.Delete, $"/channels/{channelId}/messages/{messageId}", botToken), Bucket("channel-delete", botToken, channelId), ct);

        /// <summary>The channel a webhook posts into (the webhook URL is enough to ask). Null if Discord doesn't say.</summary>
        public async Task<string?> WebhookChannelAsync(string webhookUrl, CancellationToken ct = default)
        {
            int q = webhookUrl.IndexOf('?');
            string url = q < 0 ? webhookUrl : webhookUrl.Substring(0, q);
            var result = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), "webhook-info:" + url, ct).ConfigureAwait(false);
            if (!result.Ok) return null;
            try
            {
                using var doc = JsonDocument.Parse(result.Body);
                return doc.RootElement.Str("channel_id");
            }
            catch (JsonException) { return null; }
        }

        /// <summary>The bot's own emojis (usable in any server), by name.</summary>
        public async Task<Dictionary<string, string>?> AppEmojisAsync(string botToken, string applicationId, CancellationToken ct = default)
        {
            var result = await SendAsync(() => BotRequest(HttpMethod.Get, $"/applications/{applicationId}/emojis", botToken), "app-emojis:" + applicationId, ct).ConfigureAwait(false);
            if (!result.Ok) return null;
            var emojis = new Dictionary<string, string>();
            try
            {
                using var doc = JsonDocument.Parse(result.Body);
                if (doc.RootElement.TryGetProperty("items", out var items))
                    foreach (var e in items.EnumerateArray())
                        if (e.TryGetProperty("name", out var n) && e.TryGetProperty("id", out var id))
                            emojis[n.GetString() ?? ""] = id.GetString() ?? "";
            }
            catch (JsonException) { return null; }
            return emojis;
        }

        /// <summary>Uploads a PNG as one of the bot's own emojis. Returns its ID.</summary>
        public async Task<string?> CreateAppEmojiAsync(string botToken, string applicationId, string name, byte[] png, CancellationToken ct = default)
        {
            string json = JsonSerializer.Serialize(new { name, image = "data:image/png;base64," + Convert.ToBase64String(png) });
            var result = await SendAsync(() => BotRequest(HttpMethod.Post, $"/applications/{applicationId}/emojis", botToken, Json(json)), "app-emojis:" + applicationId, ct).ConfigureAwait(false);
            return result.Ok ? MessageIdOf(result) : null;
        }

        /// <summary>Up to 100 messages, newest first, older than <paramref name="before"/> when given.</summary>
        public async Task<IReadOnlyList<ChannelMessage>?> GetMessagesAsync(string botToken, string channelId, string? before = null, CancellationToken ct = default) =>
            (await ReadMessagesAsync(botToken, channelId, before, ct).ConfigureAwait(false)).Messages;

        /// <summary>The channel's newest messages, or null with Discord's answer saying why not.</summary>
        public async Task<(IReadOnlyList<ChannelMessage>? Messages, DiscordResult Result)> ReadMessagesAsync(string botToken, string channelId, string? before = null, CancellationToken ct = default)
        {
            var result = await SendAsync(() => BotRequest(HttpMethod.Get, $"/channels/{channelId}/messages?limit=100" + (before == null ? "" : "&before=" + before), botToken),
                Bucket("channel-read", botToken, channelId), ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                _log.Warn($"Could not read Discord channel {channelId}: {result}");
                return (null, result);
            }
            using var doc = JsonDocument.Parse(result.Body);
            return (doc.RootElement.EnumerateArray().Select(ChannelMessage.Parse).ToList(), result);
        }

        /// <summary>One message by its ID, or null with Discord's answer saying why not (404: it was deleted).</summary>
        public async Task<(ChannelMessage? Message, DiscordResult Result)> GetMessageAsync(string botToken, string channelId, string messageId, CancellationToken ct = default)
        {
            var result = await SendAsync(() => BotRequest(HttpMethod.Get, $"/channels/{channelId}/messages/{messageId}", botToken), Bucket("channel-read", botToken, channelId), ct).ConfigureAwait(false);
            if (!result.Ok) return (null, result);
            try
            {
                using var doc = JsonDocument.Parse(result.Body);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out _)
                    ? (ChannelMessage.Parse(doc.RootElement), result)
                    : (null, result);
            }
            catch (Exception e) when (e is JsonException || e is KeyNotFoundException || e is InvalidOperationException)
            {
                return (null, result);
            }
        }

        /// <summary>The bot's own name (for messages), or null.</summary>
        public async Task<string?> BotNameAsync(string botToken, CancellationToken ct = default)
        {
            var result = await SendAsync(() => BotRequest(HttpMethod.Get, "/users/@me", botToken), "me:" + botToken.GetHashCode(), ct).ConfigureAwait(false);
            if (!result.Ok) return null;
            try
            {
                using var doc = JsonDocument.Parse(result.Body);
                return doc.RootElement.Str("username");
            }
            catch (JsonException) { return null; }
        }

        /// <summary>Downloads an attachment from Discord's CDN (the URL carries its own signature).</summary>
        public async Task<string?> DownloadAsync(string url, CancellationToken ct = default)
        {
            try
            {
                using var timeout = Timeout(ct);
                var token = timeout?.Token ?? ct;
                using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
                return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token).ConfigureAwait(false) : null;
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
            return SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, MessageUrl(webhookUrl, messageId)) { Content = Json(json) }, "webhook-edit:" + webhookUrl, ct);
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

        /// <summary>
        /// A webhook post with one file attached (the game's data, for the organiser's scheduled job).
        /// Unlike a plain webhook post it is sent again after a lost answer: every reader of the
        /// channel counts a game file once by its ID, so a second copy changes nothing, while a
        /// lost one would leave the game out of the standings.
        /// </summary>
        public Task<DiscordResult> ExecuteWebhookWithFileAsync(string webhookUrl, WebhookMessage message, string fileName, byte[] file, CancellationToken ct = default)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(message, WebhookMessage.JsonOptions))!.AsObject();
            node["attachments"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = 0, ["filename"] = fileName });
            string payload = node.ToJsonString();
            return SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Waiting(webhookUrl)) { Content = FileForm(payload, fileName, file) }, "webhook:" + webhookUrl, ct);
        }

        public Task<DiscordResult> AddReactionAsync(string botToken, string channelId, string messageId, string emoji, CancellationToken ct = default) =>
            SendAsync(() => BotRequest(HttpMethod.Put, $"/channels/{channelId}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}/@me", botToken),
                Bucket("reaction", botToken, channelId), ct);

        /// <summary>Replaces the bot's slash commands in one server with <paramref name="commands"/>.</summary>
        public Task<DiscordResult> PutGuildCommandsAsync(string botToken, string applicationId, string guildId, object[] commands, CancellationToken ct = default)
        {
            string json = JsonSerializer.Serialize(commands);
            return SendAsync(() => BotRequest(HttpMethod.Put, $"/applications/{applicationId}/guilds/{guildId}/commands", botToken, Json(json)), "commands:" + applicationId + ":" + guildId, ct);
        }

        /// <summary>Answers a slash command; with <paramref name="onlyThem"/>, only the person who used it sees the answer.</summary>
        public Task<DiscordResult> RespondToInteractionAsync(string interactionId, string interactionToken, string content, bool onlyThem = true, CancellationToken ct = default)
        {
            string json = JsonSerializer.Serialize(new
            {
                type = 4,
                data = new { content, flags = onlyThem ? 64 : 0, allowed_mentions = new { parse = Array.Empty<string>() } },
            });
            return SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/interactions/{interactionId}/{interactionToken}/callback") { Content = Json(json) },
                "interaction:" + interactionId, ct);
        }

        /// <summary>A request to Discord's API as the bot (<paramref name="path"/> goes after the API base).</summary>
        private HttpRequestMessage BotRequest(HttpMethod method, string path, string botToken, HttpContent? content = null)
        {
            var req = new HttpRequestMessage(method, _apiBase + path) { Content = content };
            req.Headers.TryAddWithoutValidation("Authorization", "Bot " + botToken);
            return req;
        }

        /// <summary>One bot's requests of one kind to one channel (or server) share a rate-limit bucket.</summary>
        private static string Bucket(string kind, string botToken, string id) => kind + ":" + botToken.GetHashCode() + ":" + id;

        private static StringContent Json(string json) => new StringContent(json, Encoding.UTF8, "application/json");

        /// <summary>A webhook address that makes Discord answer with the message it posted.</summary>
        private static string Waiting(string webhookUrl) => webhookUrl + (webhookUrl.Contains("?") ? "&" : "?") + "wait=true";

        /// <summary>A post's JSON payload with one file attached.</summary>
        private static MultipartFormDataContent FileForm(string payload, string fileName, byte[] file)
        {
            var form = new MultipartFormDataContent();
            form.Add(Json(payload), "payload_json");
            var part = new ByteArrayContent(file);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            form.Add(part, "files[0]", fileName);
            return form;
        }

        private CancellationTokenSource? Timeout(CancellationToken ct)
        {
            if (RequestTimeout is not TimeSpan limit) return null;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(limit);
            return cts;
        }

        /// <param name="idempotent">
        /// False for a post that would show twice if sent again after Discord got it (a plain
        /// webhook message: a public game report, a leaderboard). Such a request is only sent again
        /// when it surely never got there (no connection, a rate limit, Discord saying it was too
        /// busy to take it); after a timeout or an unclear error it is reported as failed rather
        /// than risk posting it twice. The bot's own posts carry a nonce instead, so they can always
        /// be sent again.
        /// </param>
        private async Task<DiscordResult> SendAsync(Func<HttpRequestMessage> makeRequest, string bucket, CancellationToken ct, bool idempotent = true)
        {
            bool waited = false;
            for (int attempt = 1; ; attempt++)
            {
                if (!waited)
                {
                    var wait = HoldFor(bucket);
                    if (wait > TimeSpan.Zero) await _delay(wait, ct).ConfigureAwait(false);
                }
                waited = false;

                HttpResponseMessage response;
                string body;
                try
                {
                    using var timeout = Timeout(ct);
                    var token = timeout?.Token ?? ct;
                    using var request = makeRequest();
                    response = await _http.SendAsync(request, token).ConfigureAwait(false);
                    try { body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false); }
                    catch (Exception) { response.Dispose(); throw; }
                }
                catch (Exception e) when (!ct.IsCancellationRequested && attempt < MaxAttempts && (idempotent || NeverSent(e)))
                {
                    _log.Warn($"Discord request failed ({e.GetType().Name}: {e.Message}), retrying");
                    await _delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                catch (Exception e)
                {
                    if (!idempotent && !ct.IsCancellationRequested && !NeverSent(e))
                        _log.Warn($"Discord didn't answer a post ({e.GetType().Name}: {e.Message}). It isn't sent again: it may have gone through, and twice would show it twice.");
                    return new DiscordResult { Ok = false, Status = 0, Body = e.Message };
                }

                using (response)
                {
                    // The Button isn't open: nothing went to Discord, and trying again won't help.
                    if (response.Headers.Contains(ButtonBridge.OfflineHeader))
                        return new DiscordResult { Ok = false, Status = 503, Body = "The Button isn't open, so Discord is off." };
                    TrackBucket(bucket, response);
                    int status = (int)response.StatusCode;

                    if (status == 429 && attempt < MaxAttempts)
                    {
                        var retry = RetryAfter(response, body);
                        bool global = IsGlobal(response, body);
                        // Every request in the bucket (or, for Discord's global limit, every request)
                        // waits it out, not only this one: the other lanes would just be turned away too.
                        if (global) HoldAll(retry);
                        else Hold(bucket, retry);
                        _log.Warn($"Discord rate limit hit{(global ? " (global)" : "")}, waiting {retry.TotalSeconds:0.##}s");
                        await _delay(retry, ct).ConfigureAwait(false);
                        waited = true;
                        continue;
                    }

                    // 503: Discord was too busy to take it in; The Button marks the answers where it
                    // never reached Discord. Any other 5xx (a 502 or 504 timeout on the way, a 500)
                    // may have come after the post was made.
                    bool safe = idempotent || status == 503 || response.Headers.Contains(NotSentHeader);
                    if (status >= 500 && attempt < MaxAttempts && safe)
                    {
                        await _delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (status >= 500 && !safe)
                        _log.Warn($"Discord answered a post with HTTP {status}. It isn't sent again: it may have gone through, and twice would show it twice.");

                    return new DiscordResult
                    {
                        Ok = response.IsSuccessStatusCode,
                        Status = status,
                        ErrorCode = response.IsSuccessStatusCode ? 0 : ErrorCodeOf(body),
                        Body = body,
                    };
                }
            }
        }

        /// <summary>The request failed before anything reached Discord (no connection, no address).</summary>
        private static bool NeverSent(Exception e) =>
            e is HttpRequestException && e.InnerException is System.Net.Sockets.SocketException;

        /// <summary>How long a request in this bucket has to wait now (Discord's global limit included).</summary>
        private TimeSpan HoldFor(string bucket)
        {
            var now = DateTime.UtcNow;
            var until = new DateTime(Interlocked.Read(ref _globalResetAt), DateTimeKind.Utc);
            if (_bucketResetAt.TryGetValue(bucket, out var resetAt) && resetAt > until) until = resetAt;
            return until - now;
        }

        /// <summary>
        /// Holds the bucket until at least <paramref name="wait"/> from now. A hold only ever gets
        /// longer and runs out by itself: with several requests on the wire at once, an older
        /// answer that still had room must not lift the hold a newer one set.
        /// </summary>
        private void Hold(string bucket, TimeSpan wait)
        {
            var until = DateTime.UtcNow + wait;
            _bucketResetAt.AddOrUpdate(bucket, until, (_, old) => old > until ? old : until);
        }

        private void HoldAll(TimeSpan wait)
        {
            long until = (DateTime.UtcNow + wait).Ticks;
            long old;
            do
            {
                old = Interlocked.Read(ref _globalResetAt);
                if (old >= until) return;
            }
            while (Interlocked.CompareExchange(ref _globalResetAt, until, old) != old);
        }

        private void TrackBucket(string bucket, HttpResponseMessage response)
        {
            if (Header(response, "X-RateLimit-Remaining") == "0"
                && double.TryParse(Header(response, "X-RateLimit-Reset-After"), NumberStyles.Float, CultureInfo.InvariantCulture, out var after))
            {
                Hold(bucket, TimeSpan.FromSeconds(after));
            }
        }

        private static bool IsGlobal(HttpResponseMessage response, string body)
        {
            if (string.Equals(Header(response, "X-RateLimit-Global"), "true", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("global", out var g) && g.ValueKind == JsonValueKind.True;
            }
            catch (JsonException) { return false; }
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
                GlobalName = user.Str("global_name"),
                Nick = e.Str("nick"),
            };
        }
    }

    public sealed class ChannelMessage
    {
        public string Id { get; set; } = "";
        public string ChannelId { get; set; } = "";
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
                ChannelId = e.Str("channel_id") ?? "",
                Content = e.Str("content") ?? "",
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

    /// <summary>Reading Discord's JSON.</summary>
    internal static class JsonRead
    {
        /// <summary>A string field, or null when it's missing or not a string.</summary>
        public static string? Str(this JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
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

        /// <summary>Buttons and menus. Only messages the bot posts itself can have them (not webhooks).</summary>
        [JsonPropertyName("components")] public List<object>? Components { get; set; }

        /// <summary>Stops a player named "@everyone" from pinging the server.</summary>
        [JsonPropertyName("allowed_mentions")] public AllowedMentions AllowedMentions { get; set; } = new AllowedMentions();
    }

    public sealed class AllowedMentions
    {
        [JsonPropertyName("parse")] public List<string> Parse { get; set; } = new List<string>();
        /// <summary>The only users this message may ping.</summary>
        [JsonPropertyName("users")] public List<string>? Users { get; set; }
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
