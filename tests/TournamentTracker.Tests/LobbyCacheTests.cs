using System.Net;
using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// Red Alert's memory of the lobbies across a restart (lobbies-cache.json): loaded at the start, marked
/// cached until the results channel confirms each one, replaced by what the channel says.
/// </summary>
public class LobbyCacheTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly List<IDisposable> _owned = new();
    // What the results channel holds now: the lobby's live message (id 42), or nothing.
    private object? _message;
    private int _ownReads;

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _dir.Dispose();
    }

    private string CacheFile => Path.Combine(_dir.Path, Organizer.CacheFileName);

    private static object Live(string lobby, string vdo, long sent) => new
    {
        id = "42",
        content = "",
        author = new { id = "host-bot", bot = true },
        embeds = new[]
        {
            new
            {
                title = TournamentSession.LiveTitlePrefix + lobby,
                description = "```json\n" + JsonSerializer.Serialize(new
                {
                    v = 1, lobby, phase = "Tasks", t = sent, vdo, round = 1,
                    p = new[] { new object[] { "Soggy", 3, 0, 0, 1, 2, 5 } },
                    f = new[] { new object[] { "19:00", "kill", "Soggy killed Jake" } },
                    st = Array.Empty<object>(),
                }) + "\n```",
            },
        },
        attachments = Array.Empty<object>(),
    };

    private HttpResponseMessage Handle(HttpRequestMessage r)
    {
        string path = r.RequestUri!.AbsolutePath;
        if (path.EndsWith("/channels/results/messages/42"))
        {
            Interlocked.Increment(ref _ownReads);
            return _message == null ? FakeHttp.Json(HttpStatusCode.NotFound, "{\"message\":\"Unknown Message\",\"code\":10008}") : FakeHttp.Json(HttpStatusCode.OK, JsonSerializer.Serialize(_message));
        }
        if (path.EndsWith("/channels/results/messages"))
            return FakeHttp.Json(HttpStatusCode.OK, JsonSerializer.Serialize(_message == null ? Array.Empty<object>() : new[] { _message }));
        return FakeHttp.Json(HttpStatusCode.OK, "{}");
    }

    private Organizer Organiser(string channel = "results", bool cache = true)
    {
        var admin = new SetupCode { Mode = "admin", TournamentId = "fall-cup", TournamentName = "Fall Cup", BotTokens = new() { "admin" }, ResultsChannelId = channel, AdvanceCount = 5, GamesPerRound = 3 };
        Assert.True(SetupCode.TryParse(admin.Encode(), out var read, out var error), error);
        var o = new Organizer(read, new HttpClient(new FakeHttp { Default = Handle }) { Timeout = TimeSpan.FromSeconds(20) }, () => _clock.Now, start: false, cacheFile: cache ? CacheFile : null);
        _owned.Add(o);
        return o;
    }

    private static JsonElement Lobby(Organizer o, string label) =>
        JsonSerializer.SerializeToElement(o.State(), TestJson.Camel).GetProperty("lobbies").EnumerateArray().Single(l => l.GetProperty("label").GetString() == label);

    [Fact]
    public async Task The_lobbies_and_their_links_come_back_at_the_start_marked_cached_until_the_channel_confirms_them()
    {
        long sent = DateTimeOffset.Parse("2026-09-01T17:59:50Z").ToUnixTimeSeconds();
        _message = Live("LJ", "ttabc:pw123", sent);
        var before = Organiser();
        await before.PollLiveAsync();
        Assert.True(File.Exists(CacheFile));
        // Kept without the players or the event feed (stale by the next start, and who the impostors are).
        string kept = File.ReadAllText(CacheFile);
        Assert.DoesNotContain("Soggy", kept);
        Assert.Contains("ttabc:pw123", kept);
        before.Dispose();

        // Restarted: LJ is there at once, with its link, before the channel has been read.
        _message = null;
        var after = Organiser();
        var lj = Assert.Single(after.LiveLobbies());
        Assert.Equal("LJ", lj.Label);
        Assert.Equal("https://vdo.ninja/?view=ttabc&password=pw123&noaudio&cleanoutput", Organizer.VideoUrl(lj.Data));
        Assert.True(after.IsCached("LJ"));
        Assert.True(Lobby(after, "LJ").GetProperty("cached").GetBoolean());
        Assert.Null(after.Hot("LJ", full: true));                     // no stale meeting or kill marks
        Assert.Equal(0, lj.Data.GetProperty("p").GetArrayLength());

        // The channel has the same message: confirmed, with its players back.
        _message = Live("LJ", "ttabc:pw123", sent);
        await after.PollLiveAsync();
        Assert.False(after.IsCached("LJ"));
        Assert.False(Lobby(after, "LJ").GetProperty("cached").GetBoolean());
        Assert.Equal(1, Assert.Single(after.LiveLobbies()).Data.GetProperty("p").GetArrayLength());
    }

    [Fact]
    public async Task Fresh_data_from_the_channel_replaces_what_was_kept()
    {
        long sent = DateTimeOffset.Parse("2026-09-01T17:59:50Z").ToUnixTimeSeconds();
        _message = Live("LJ", "ttabc:pw123", sent);
        await Organiser().PollLiveAsync();

        // The host restarted their send page meanwhile: a new link.
        _message = Live("LJ", "ttnew:pw999", sent + 30);
        var o = Organiser();
        Assert.True(o.IsCached("LJ"));
        await o.PollLiveAsync();
        var lj = Assert.Single(o.LiveLobbies());
        Assert.False(o.IsCached("LJ"));
        Assert.Equal(sent + 30, lj.Sent);
        Assert.Contains("view=ttnew&password=pw999", Organizer.VideoUrl(lj.Data));
        // And kept so for the next start.
        Assert.Contains("ttnew:pw999", File.ReadAllText(CacheFile));
        Assert.DoesNotContain("ttabc", File.ReadAllText(CacheFile));
    }

    [Fact]
    public async Task A_kept_lobby_whose_message_is_gone_is_let_go_and_its_message_is_read_first()
    {
        long sent = DateTimeOffset.Parse("2026-09-01T17:59:50Z").ToUnixTimeSeconds();
        _message = Live("LJ", "ttabc:pw123", sent);
        await Organiser().PollLiveAsync();

        _message = null;                                               // deleted from the channel
        var o = Organiser();
        Assert.Single(o.LiveLobbies());
        await o.PollLiveAsync();
        Assert.Equal(1, _ownReads);                                    // read by its ID at the first poll
        Assert.Empty(o.LiveLobbies());
    }

    [Fact]
    public async Task Only_for_the_same_results_channel_only_lobbies_seen_in_the_last_18_hours_and_never_for_The_Button()
    {
        long sent = DateTimeOffset.Parse("2026-09-01T17:59:50Z").ToUnixTimeSeconds();
        _message = Live("LJ", "ttabc:pw123", sent);
        await Organiser().PollLiveAsync();
        _message = null;

        Assert.Empty(Organiser(channel: "another").LiveLobbies());     // another tournament's code
        Assert.Single(Organiser().LiveLobbies());
        _clock.Advance(19 * 3600);
        Assert.Empty(Organiser().LiveLobbies());                       // yesterday's lobbies

        // Without a cache file (The Button's Organiser tab) nothing is kept or loaded.
        File.Delete(CacheFile);
        _message = Live("LJ", "ttabc:pw123", sent);
        await Organiser(cache: false).PollLiveAsync();
        Assert.False(File.Exists(CacheFile));
    }

    [Fact]
    public void A_broken_file_is_just_not_used()
    {
        File.WriteAllText(CacheFile, "{ not json");
        Assert.Empty(Organiser().LiveLobbies());
        Assert.Empty(Organizer.CachedLinks(CacheFile));
        Assert.Empty(Organizer.CachedLinks(Path.Combine(_dir.Path, "missing.json")));
    }
}
