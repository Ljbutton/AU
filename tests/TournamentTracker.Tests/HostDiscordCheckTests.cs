using System.Net;
using TournamentTracker.App;
using TournamentTracker.Setup;
using Xunit;

namespace TournamentTracker.Tests;

public class HostDiscordCheckTests
{
    private const string Good = "test-token-good.not-real.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Reset = "test-token-reset.not-real.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Outside = "test-token-outside.not-real.cccccccccccccccccccccccccccccccccccccccc";
    private const string Server = "118000000000000000";

    private static HttpClient Discord() => new(new FakeHttp
    {
        Default = r =>
        {
            string url = r.RequestUri!.AbsoluteUri;
            string auth = r.Headers.Authorization?.ToString() ?? "";
            if (url.Contains("/webhooks/"))
                return url.Contains("/gone") ? FakeHttp.Json(HttpStatusCode.NotFound, "{}") : FakeHttp.Json(HttpStatusCode.OK, """{"name":"Results","channel_id":"5"}""");
            if (auth.Contains(Reset)) return FakeHttp.Json(HttpStatusCode.Unauthorized, """{"message":"401: Unauthorized"}""");
            if (url.EndsWith("/users/@me")) return FakeHttp.Json(HttpStatusCode.OK, auth.Contains(Good) ? """{"username":"TT Lobby 1"}""" : """{"username":"Stray"}""");
            if (url.EndsWith("/users/@me/guilds")) return FakeHttp.Json(HttpStatusCode.OK, auth.Contains(Good) ? $$"""[{"id":"{{Server}}"}]""" : "[]");
            return FakeHttp.Json(HttpStatusCode.NotFound, "{}");
        },
    });

    [Fact]
    public async Task Only_bots_that_log_in_and_are_in_the_server_are_saved()
    {
        var typed = new HostDiscord { GuildId = Server, BotTokens = new() { "Bot " + Good, Reset, Outside }, PublicWebhook = "https://discord.com/api/webhooks/1/ok", StatusWebhook = "https://discord.com/api/webhooks/2/gone" };
        var r = await HostDiscordCheck.CheckAsync(Discord(), typed, null, new List<int>());
        Assert.Equal(new[] { Good }, r.Saved.BotTokens);
        Assert.Equal(new[] { "TT Lobby 1" }, r.BotNames);
        Assert.Equal("Results", r.PublicName);
        Assert.Equal("", r.Saved.StatusWebhook);
        Assert.Contains(r.Problems, p => p.StartsWith("Bot 2: Discord refused"));
        Assert.Contains(r.Problems, p => p.StartsWith("Bot 3 (Stray) isn't in that server"));
        Assert.Contains(r.Problems, p => p.StartsWith("Live lobby channel: Discord says"));
    }

    [Fact]
    public async Task Empty_slots_keep_what_was_saved_and_removed_slots_go()
    {
        var saved = new HostDiscord { GuildId = Server, BotTokens = new() { Good }, PublicWebhook = "https://discord.com/api/webhooks/1/ok" };
        var keep = await HostDiscordCheck.CheckAsync(Discord(), new HostDiscord { GuildId = Server, BotTokens = new() { "" }, PublicWebhook = null!, StatusWebhook = null! }, saved, new List<int>());
        Assert.True(keep.Ok);
        Assert.Equal(new[] { Good }, keep.Saved.BotTokens);
        Assert.Equal(saved.PublicWebhook, keep.Saved.PublicWebhook);

        var gone = await HostDiscordCheck.CheckAsync(Discord(), new HostDiscord { GuildId = Server, PublicWebhook = "", StatusWebhook = null! }, saved, new List<int> { 0 });
        Assert.Empty(gone.Saved.BotTokens);
        Assert.Equal("", gone.Saved.PublicWebhook);
        Assert.True(gone.Saved.IsEmpty == false);   // the server ID stays
    }

    [Fact]
    public async Task A_tournament_codes_server_is_used_when_the_host_leaves_theirs_empty()
    {
        var r = await HostDiscordCheck.CheckAsync(Discord(), new HostDiscord { BotTokens = new() { Good }, PublicWebhook = null!, StatusWebhook = null! }, null, new List<int>(), codeGuild: Server);
        Assert.True(r.Ok, string.Join(" ", r.Problems));
        Assert.Equal("", r.Saved.GuildId);
        var s = new TrackerSettings();
        new SetupCode { Mode = "tournament", TournamentId = "c", TournamentName = "C", Webhook = "https://discord.com/api/webhooks/1/x", GuildId = Server }.ApplyTo(s);
        Assert.False(s.AutoMute.IsConfigured);
        r.Saved.ApplyTo(s);
        Assert.True(s.AutoMute.IsConfigured);
        Assert.Equal(Server, s.AutoMute.GuildId);
        var bot = DiscordBridge.FromSetupCode(new SetupCode { Mode = "tournament", TournamentId = "c", TournamentName = "C", Webhook = "https://discord.com/api/webhooks/1/x", GuildId = Server }.Encode(), r.Saved);
        Assert.Equal(Good, bot!.Token);
    }
}
