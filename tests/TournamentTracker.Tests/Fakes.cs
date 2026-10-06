using System.Text.Json;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using TournamentTracker.Discord;
using TournamentTracker.Stats;
using TournamentTracker.Voice;

namespace TournamentTracker.Tests;

public static class Players
{
    public static PlayerSnapshot Make(byte id, string name, int color, bool impostor = false, int tasks = 4) => new()
    {
        PlayerId = id,
        Key = PlayerSnapshot.MakeKey($"{name.ToLowerInvariant()}#{1000 + id}", name),
        Name = name,
        ColorId = color,
        Role = impostor ? "Impostor" : "Crewmate",
        IsImpostor = impostor,
        TasksTotal = impostor ? 0 : tasks,
    };

    /// <summary>Red and Blue are the impostors.</summary>
    public static List<PlayerSnapshot> Lobby() => new()
    {
        Make(0, "Alice", 0, impostor: true),
        Make(1, "Bob", 1, impostor: true),
        Make(2, "Carl", 2),
        Make(3, "Dana", 3),
        Make(4, "Eve", 4),
        Make(5, "Finn", 5),
    };
}

public sealed class FakeClock
{
    public DateTime Now { get; set; } = new DateTime(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc);
    public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
}

public sealed record Sent(HttpMethod Method, string Url, string Body, string? Auth);

/// <summary>Records requests and answers them from a queue of canned responses (200 {} when empty).</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public ConcurrentQueue<Sent> Requests { get; } = new();
    public ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> Responses { get; } = new();
    public Func<HttpRequestMessage, HttpResponseMessage>? Default { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        request.Headers.TryGetValues("Authorization", out var auth);
        Requests.Enqueue(new Sent(request.Method, request.RequestUri!.AbsoluteUri, body, auth?.FirstOrDefault()));
        if (Responses.TryDequeue(out var respond)) return respond(request);
        return Default?.Invoke(request) ?? Json(HttpStatusCode.OK, "{}");
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json, params (string, string)[] headers)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }
}

public sealed class FakeVoiceApi : IVoiceApi
{
    public ConcurrentQueue<(string Token, string User, VoiceState State)> Calls { get; } = new();
    public Func<string, VoiceState, DiscordResult>? Respond { get; set; }
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;

    public async Task<DiscordResult> SetVoiceStateAsync(string botToken, string userId, VoiceState state, CancellationToken ct)
    {
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ct);
        Calls.Enqueue((botToken, userId, state));
        return Respond?.Invoke(userId, state) ?? new DiscordResult { Ok = true, Status = 204 };
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tt-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public static class Wait
{
    public static async Task Until(Func<Task<bool>> condition, int timeoutMs = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time");
            await Task.Delay(50);
        }
    }

    public static async Task Until(Func<bool> condition, int timeoutMs = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time");
            await Task.Delay(10);
        }
    }
}

/// <summary>A small fake of Discord: channels with messages and files, webhooks, reactions and the CDN.</summary>
public sealed class FakeDiscord
{
    public sealed record Msg(string Id, string Channel, string Content, string? File, string? Json, bool Bot, JsonElement? Embeds);
    public readonly List<Msg> Messages = new();                    // oldest first
    public readonly List<(string Url, JsonElement Payload, string? File)> Webhooks = new();
    public readonly List<(string Channel, string Message, string Emoji)> Reactions = new();
    private long _next = 1_300_000_000_000_000_000;
    public int Edits;

    private string NextId(DateTime? at = null)
    {
        if (at.HasValue)
        {
            long ms = new DateTimeOffset(at.Value).ToUnixTimeMilliseconds() - 1420070400000L;
            return ((ms << 22) + Interlocked.Increment(ref _next) % 1000).ToString();
        }
        // Posted now: after everything already in the channel.
        long last = Messages.Count == 0 ? 0 : Messages.Max(m => long.Parse(m.Id));
        return Math.Max(last + 1, Interlocked.Increment(ref _next)).ToString();
    }

    public void Say(string channel, string text, DateTime at) => Messages.Add(new Msg(NextId(at), channel, text, null, null, false, null));
    public void AddGame(string channel, GameRecord g) =>
        Messages.Add(new Msg(NextId(g.EndedUtc), channel, $"Game {g.Name}", SharedResults.FileNameFor(g), JsonSerializer.Serialize(g), true, null));

    public HttpResponseMessage Handle(HttpRequestMessage r)
    {
        string url = r.RequestUri!.AbsoluteUri;
        if (url.StartsWith("https://cdn.test/"))
            return FakeHttp.Json(HttpStatusCode.OK, Messages.Single(m => m.Id == url.Split('/')[3]).Json!);
        if (url.Contains("/reactions/"))
        {
            var parts = r.RequestUri.AbsolutePath.Split('/');
            Reactions.Add((parts[^6], parts[^4], Uri.UnescapeDataString(parts[^2])));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (url.Contains("/webhooks/"))
        {
            JsonElement payload;
            string? file = null;
            if (r.Content is MultipartFormDataContent form)
            {
                var parts = form.ToList();
                payload = JsonDocument.Parse(parts[0].ReadAsStringAsync().Result).RootElement.Clone();
                file = parts[1].ReadAsStringAsync().Result;
            }
            else payload = JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result).RootElement.Clone();
            Webhooks.Add((url, payload, file));
            return FakeHttp.Json(HttpStatusCode.OK, """{"id":"42"}""");
        }
        var m = System.Text.RegularExpressions.Regex.Match(url, @"/channels/(\w+)/messages(\?|$)");
        if (m.Success && r.Method == HttpMethod.Get)
        {
            string channel = m.Groups[1].Value;
            List<Msg> snapshot;
            lock (Messages) snapshot = Messages.ToList();
            var items = snapshot.Where(x => x.Channel == channel).Reverse().Select(x => new Dictionary<string, object?>
            {
                ["id"] = x.Id,
                ["content"] = x.Content,
                ["author"] = new { id = x.Bot ? "bot" : "ref", bot = x.Bot },
                ["embeds"] = x.Embeds.HasValue ? x.Embeds.Value : (object)Array.Empty<object>(),
                ["attachments"] = x.File == null ? Array.Empty<object>() : new object[] { new { filename = x.File, url = $"https://cdn.test/{x.Id}/{x.File}" } },
            });
            return FakeHttp.Json(HttpStatusCode.OK, JsonSerializer.Serialize(items));
        }
        if (m.Success && r.Method == HttpMethod.Post)
        {
            string channel = m.Groups[1].Value;
            string id;
            lock (Messages)
            {
                id = NextId();
                if (r.Content is MultipartFormDataContent form)
                {
                    var parts = form.ToList();
                    var payload = JsonDocument.Parse(parts[0].ReadAsStringAsync().Result).RootElement;
                    Messages.Add(new Msg(id, channel, payload.GetProperty("content").GetString()!,
                        payload.GetProperty("attachments")[0].GetProperty("filename").GetString(), parts[1].ReadAsStringAsync().Result, true, null));
                }
                else
                {
                    var body = JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result).RootElement.Clone();
                    Messages.Add(new Msg(id, channel, body.TryGetProperty("content", out var c) ? c.GetString()! : "", null, null, true,
                        body.TryGetProperty("embeds", out var e) ? e : null));
                }
            }
            return FakeHttp.Json(HttpStatusCode.OK, "{\"id\":\"" + id + "\"}");
        }
        var edit = System.Text.RegularExpressions.Regex.Match(url, @"/channels/(\w+)/messages/(\d+)$");
        if (edit.Success && r.Method.Method == "PATCH")
        {
            lock (Messages)
            {
                int i = Messages.FindIndex(x => x.Id == edit.Groups[2].Value);
                if (i < 0) return FakeHttp.Json(HttpStatusCode.NotFound, "{}");
                var body = JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result).RootElement.Clone();
                Messages[i] = Messages[i] with { Embeds = body.GetProperty("embeds") };
                Edits++;
            }
            return FakeHttp.Json(HttpStatusCode.OK, "{}");
        }
        if (edit.Success && r.Method == HttpMethod.Delete)
        {
            lock (Messages) Messages.RemoveAll(x => x.Id == edit.Groups[2].Value);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        return FakeHttp.Json(HttpStatusCode.OK, "{}");
    }

    public static string Title(JsonElement payload) => payload.GetProperty("embeds")[0].GetProperty("title").GetString()!;
    public static string Description(JsonElement payload) => payload.GetProperty("embeds")[0].GetProperty("description").GetString()!;
}
