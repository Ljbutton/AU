using System.Collections.Concurrent;
using System.Net;
using System.Text;
using TournamentTracker.Discord;
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
