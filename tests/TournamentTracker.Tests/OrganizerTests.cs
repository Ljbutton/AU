using System.Net;
using System.Text.Json;
using TournamentTracker.App;
using TournamentTracker.Discord;
using TournamentTracker.Setup;
using TournamentTracker.Voice;
using Xunit;

namespace TournamentTracker.Tests;

public class OrganizerTests : IDisposable
{
    private const string Webhook = "https://discord.test/api/webhooks/1/abc";
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly FakeDiscord _discord = new();
    private readonly FakeHttp _http = new();
    private readonly List<IDisposable> _owned = new();

    public OrganizerTests() => _http.Default = _discord.Handle;

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _dir.Dispose();
    }

    private TournamentSession Host(string label)
    {
        var code = new SetupCode
        {
            Mode = "tournament", TournamentId = "fall-cup", TournamentName = "Fall Cup", Webhook = Webhook,
            GuildId = "g1", BotTokens = new() { "tok-" + label }, ResultsChannelId = "results", AdvanceCount = 5, GamesPerRound = 3,
        };
        var dir = Path.Combine(_dir.Path, label);
        Directory.CreateDirectory(dir);
        var s = new TournamentSession(new TrackerSettings { LiveStatus = false, ControlPort = -1, LobbyLabel = label }, dir, NullLog.Instance,
            new HttpClient(_http), () => _clock.Now, new FakeVoiceApi(), new VoicePresenceState("g1"), code);
        _owned.Add(s);
        return s;
    }

    private Organizer Organiser()
    {
        var admin = new SetupCode { Mode = "admin", TournamentId = "fall-cup", TournamentName = "Fall Cup", BotTokens = new() { "admin" }, ResultsChannelId = "results", AdvanceCount = 5, GamesPerRound = 3 };
        Assert.True(SetupCode.TryParse(admin.Encode(), out var read, out var error), error);
        var o = new Organizer(read, new HttpClient(_http), () => DateTime.UtcNow, start: false);
        _owned.Add(o);
        return o;
    }

    private bool HasLive(string label) =>
        _discord.Messages.ToList().Any(m => m.Embeds.HasValue && m.Embeds.Value.GetArrayLength() > 0 && FakeDiscord.Title(JsonDocument.Parse("{\"embeds\":" + m.Embeds.Value.GetRawText() + "}").RootElement) == TournamentSession.LiveTitlePrefix + label);

    [Theory]
    [InlineData(401, 0, "bot token in the administration code is invalid")]
    [InlineData(403, 50001, "add the bot with View Channel and Read Message History")]
    [InlineData(404, 10003, "results channel in the administration code doesn't exist")]
    public async Task A_channel_discord_refuses_says_what_to_fix(int status, int code, string says)
    {
        _http.Default = r => r.RequestUri!.AbsolutePath.Contains("/channels/results/messages")
            ? FakeHttp.Json((HttpStatusCode)status, $"{{\"message\":\"no\",\"code\":{code}}}")
            : FakeHttp.Json(HttpStatusCode.OK, "{}");
        var o = Organiser();
        await o.PollLiveAsync();
        Assert.Contains(says, o.Problem);

        // Fixed: the problem goes away on the next read.
        _http.Default = r => FakeHttp.Json(HttpStatusCode.OK, "[]");
        await o.PollLiveAsync();
        Assert.Null(o.Problem);
    }

    [Fact]
    public async Task Empty_messages_from_the_hosts_bots_mean_the_message_content_intent_is_off()
    {
        // With the intent off, other bots' messages come back with no content, embeds or files.
        _http.Default = r => r.RequestUri!.AbsolutePath.EndsWith("/users/@me")
            ? FakeHttp.Json(HttpStatusCode.OK, "{\"id\":\"9\",\"username\":\"Fall Cup Admin\"}")
            : FakeHttp.Json(HttpStatusCode.OK, "[{\"id\":\"1\",\"content\":\"\",\"embeds\":[],\"attachments\":[],\"author\":{\"id\":\"5\",\"bot\":true}}]");
        var o = Organiser();
        await o.PollLiveAsync();
        Assert.Contains("turn on Message Content Intent for Fall Cup Admin", o.Problem);
    }

    [Fact]
    public void A_bot_id_is_read_from_its_token_without_the_secret()
    {
        string id = "123456789012345678";
        string token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(id)).TrimEnd('=') + ".abc.def";
        Assert.Equal(id, SetupCode.BotIdOf(token));
        Assert.Equal(id, SetupCode.BotIdOf("Bot " + token));
        Assert.Equal("", SetupCode.BotIdOf("not-a-token"));
    }

    [Fact]
    public void Awards_list_the_top_three_in_each_category()
    {
        var t = new TournamentTracker.Stats.GameTracker(new TournamentTracker.ScoringRules());
        var lobby = Players.Lobby();
        var start = new DateTime(2026, 10, 3, 19, 0, 0, DateTimeKind.Utc);
        var g = t.Start(1, "Cup", "X", "Polus", lobby, start);
        t.Kill(0, 2, start.AddSeconds(30));                          // Alice (impostor) kills Carl
        lobby[2].IsDead = true;
        var game = t.End("ImpostorByKill", TournamentTracker.Stats.Outcome.Impostors, lobby, start.AddMinutes(5))!;
        game.Round = 1;

        var awards = JsonSerializer.SerializeToElement(Organizer.Awards(new[] { game }), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var byTitle = awards.EnumerateArray().ToDictionary(a => a.GetProperty("title").GetString()!, a => a.GetProperty("top"));
        Assert.Equal(7, byTitle.Count);
        Assert.Equal("Alice", byTitle["Most kills"][0].GetProperty("name").GetString());
        Assert.Equal("1 kill", byTitle["Most kills"][0].GetProperty("value").GetString());
        Assert.Equal(1, byTitle["Most kills"].GetArrayLength());     // nobody else killed
        Assert.True(byTitle["Top score"].GetArrayLength() <= 3);
        Assert.Empty(byTitle["Sharpest voter"].EnumerateArray());   // no meetings, no votes
    }

    [Fact]
    public void Only_a_well_formed_video_link_is_used()
    {
        JsonElement D(string vdo) => JsonDocument.Parse(JsonSerializer.Serialize(new { vdo })).RootElement;
        Assert.NotNull(Organizer.VideoUrl(D("ttabc:key123")));
        // The sound is the same link without the picture; the picture never carries sound.
        Assert.Equal("https://vdo.ninja/?view=ttabc&password=key123&novideo&cleanoutput", Organizer.SoundUrl(D("ttabc:key123")));
        Assert.Contains("&noaudio", Organizer.VideoUrl(D("ttabc:key123")));
        Assert.Null(Organizer.SoundUrl(D("nocolon")));
        Assert.Null(Organizer.VideoUrl(D("tt&x=1:key")));
        Assert.Null(Organizer.VideoUrl(D("nocolon")));
        Assert.Null(Organizer.VideoUrl(JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public async Task Changes_from_the_organiser_are_posted_as_the_bot_and_the_hosts_act_on_them()
    {
        var lj = Host("LJ");
        var org = Organiser();
        var (ok, message) = await org.PostChangeAsync("start", F(("round", "2")));
        Assert.True(ok);
        Assert.Equal("Posted: !start 2", message);
        var posted = _discord.Messages.ToList().Last(m => m.Content == "!start 2");
        Assert.True(posted.Bot);
        lj.HandleChannelMessage(new ChannelMessage { Id = posted.Id, ChannelId = "results", Content = posted.Content, AuthorIsBot = true });
        Assert.Equal(2, lj.Round);                                                             // a bot's !start counts
        lj.HandleChannelMessage(new ChannelMessage { Id = "4", ChannelId = "results", Content = "!start 3", AuthorIsBot = false });
        Assert.Equal(2, lj.Round);                                                             // the same, typed by a person: nothing
        lj.HandleChannelMessage(new ChannelMessage { Id = "5", ChannelId = "results", Content = "!resetleaderboard\n(reset)", AuthorIsBot = true });
        Assert.Equal(2, lj.Round);
    }

    [Fact]
    public async Task Only_the_changes_the_button_builds_are_posted_never_free_text()
    {
        var org = Organiser();
        int before = _discord.Messages.Count;
        Assert.StartsWith("That isn't a change The Button makes", await org.CommandAsync("hello"));
        Assert.StartsWith("That isn't a change The Button makes", await org.CommandAsync("!start 2\n!lobbies 3"));
        Assert.StartsWith("That isn't a change The Button makes", await org.CommandAsync("!lead"));
        Assert.False((await org.PostChangeAsync("text", F(("text", "!start 2")))).Ok);          // the old "any command" box
        Assert.False((await org.PostChangeAsync("swap", F(("player", "Soggy Dingus"), ("other", "Fred")))).Ok);
        Assert.Equal(before, _discord.Messages.Count);                                           // nothing reached Discord

        // Red Alert's void of an interrupted game is one of them.
        Assert.Equal("Posted: !void B-2 interrupted, to be replayed", await org.CommandAsync("!void B-2 interrupted, to be replayed"));

        // The organiser's reset: as the bot, so it counts; the standings reload.
        var (ok, message) = await org.PostChangeAsync("reset", F());
        Assert.True(ok);
        Assert.StartsWith("The standings start over from now.", message);
        var reset = _discord.Messages.ToList().Last();
        Assert.True(reset.Bot);
        Assert.True(TournamentTracker.Stats.SharedResults.IsReset(reset.Content));
        Assert.Equal("!resetleaderboard\n(Leaderboard reset by the organiser. Delete this message to undo it.)", reset.Content);
    }

    private static Func<string, string> F(params (string Name, string Value)[] fields) =>
        name => fields.FirstOrDefault(f => f.Name == name).Value ?? "";

    [Theory]
    [InlineData("adjust", "game=LJ|player=red|points=-2|reason=meta call", "!adjust LJ red -2 meta call")]
    [InlineData("adjust", "game=LJ-3|player=Soggy Dingus|points=+0.125", "!adjust LJ-3 Soggy Dingus +0.125")]
    [InlineData("adjust", "game=LJ|player=red|points=1,5|reason=  left \n early ", "!adjust LJ red +1.5 left early")]
    [InlineData("void", "game=LJ-3|reason=lobby restarted", "!void LJ-3 lobby restarted")]
    [InlineData("void", "game=LJ", "!void LJ")]
    [InlineData("unvoid", "game=LJ-3|reason=ignored", "!unvoid LJ-3")]
    [InlineData("start", "round=3", "!start 3")]
    [InlineData("start", "round=3|minutes=0", "!start 3")]
    [InlineData("start", "round=3|minutes=10", "!start 3 in 10")]
    [InlineData("lobbies", "round=2", "!lobbies 2")]
    [InlineData("move", "player=Soggy Dingus|lobby=B", "!move Soggy Dingus B")]
    [InlineData("move", "player=Soggy|lobby=Lobby B", "!move Soggy B")]
    [InlineData("add", "player=Fred|lobby=Final", "!add Fred Final")]
    [InlineData("swap", "player=Soggy|other=Fred", "!swap Soggy Fred")]
    [InlineData("drop", "player=Soggy Dingus", "!drop Soggy Dingus")]
    [InlineData("host", "lobby=A|host=LJ", "!host A LJ")]
    public void Each_change_is_built_from_its_fields(string kind, string fields, string command)
    {
        var map = fields.Split('|').Select(f => f.Split('=', 2)).ToDictionary(f => f[0], f => f[1]);
        Assert.Equal(command, Organizer.BuildChange(kind, n => map.TryGetValue(n, out var v) ? v : "", out var problem));
        Assert.Equal("", problem);
        Assert.True(Organizer.IsChange(command));
    }

    [Theory]
    [InlineData("adjust", "game=LJ|player=red", "Use a number of points")]
    [InlineData("adjust", "game=LJ|player=red|points=1.234", "Use a number of points")]         // 4 significant figures
    [InlineData("adjust", "game=LJ|player=red|points=0", "Use a number of points")]
    [InlineData("adjust", "game=LJ|points=2", "Pick the player")]
    [InlineData("adjust", "game=L J|player=red|points=2", "Pick the lobby")]
    [InlineData("adjust", "game=LJ|player=Red 2|points=+1", "can't be told apart from the points")]   // would be read as 2 points
    [InlineData("void", "reason=x", "Pick the game")]
    [InlineData("start", "round=x", "Which round?")]
    [InlineData("start", "round=3|minutes=500", "1 to 120 minutes")]
    [InlineData("lobbies", "round=1", "round 2 or later")]
    [InlineData("move", "player=Soggy", "Which lobby?")]
    [InlineData("swap", "player=Soggy Dingus|other=Fred", "one word for each player")]
    [InlineData("host", "lobby=A", "Who hosts it?")]
    [InlineData("lead", "", "doesn't make that change")]
    public void A_change_that_doesnt_fit_says_why_and_builds_nothing(string kind, string fields, string says)
    {
        var map = fields.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Split('=', 2)).ToDictionary(f => f[0], f => f[1]);
        Assert.Null(Organizer.BuildChange(kind, n => map.TryGetValue(n, out var v) ? v : "", out var problem));
        Assert.Contains(says, problem);
    }

    [Fact]
    public void A_long_reason_is_refused()
    {
        Assert.Null(Organizer.BuildChange("void", n => n == "game" ? "LJ-3" : n == "reason" ? new string('x', 101) : "", out var problem));
        Assert.Contains("under 100 characters", problem);
    }

    [Theory]
    [InlineData("!resetleaderboard")]                 // only the organiser's two-line reset
    [InlineData("!adjust LJ red")]
    [InlineData("!start")]
    [InlineData("!lobbies 3 now")]
    [InlineData("!swap a b c")]
    [InlineData("please !start 2")]
    [InlineData("!START 2")]
    public void Anything_else_is_not_a_change(string text) => Assert.False(Organizer.IsChange(text));

    [Fact]
    public void Admin_codes_unlock_the_organiser_and_are_not_host_codes()
    {
        var admin = new SetupCode { Mode = "admin", TournamentId = "fall-cup", TournamentName = "Fall Cup", BotTokens = new() { "a.b.c" }, ResultsChannelId = "results" };
        Assert.True(SetupCode.TryParse(admin.Encode(), out var read, out _));
        Assert.True(read.IsAdmin);
        Assert.Equal("Fall Cup (administration)", read.Describe());
        var missing = new SetupCode { Mode = "admin", TournamentId = "x", TournamentName = "X" };
        Assert.False(SetupCode.TryParse(missing.Encode(), out _, out var error));
        Assert.Contains("no bot or results channel", error);

        // Saved as a host's setup code by mistake: the game ignores it.
        SetupCode.Save(_dir.Path, admin.Encode());
        Assert.Null(SetupCode.Load(_dir.Path, NullLog.Instance));
    }
}
