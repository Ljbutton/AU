using TournamentTracker.App;
using TournamentTracker.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>The host's side of the broadcast feed (docs/broadcast-protocol.md).</summary>
public class SendPageTests
{
    [Fact]
    public void The_hosts_page_stamps_its_own_messages_with_the_same_version()
    {
        Assert.Contains($"const PROTOCOL={FeedProtocol.Version};", SendPage.Html);
        Assert.Contains("v:PROTOCOL,type:'voice'", SendPage.Html);
        Assert.Contains("v:PROTOCOL,type:'health'", SendPage.Html);
        Assert.Contains(FeedProtocol.CommandPattern, SendPage.Html);
    }
}
