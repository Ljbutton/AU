using TournamentTracker.App;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// Both apps' windows: what they do when the page's renderer crashes or hangs, or WebView2's browser closes
/// (reload, a new browser, at most 3 times a minute, then a plain message).
/// </summary>
public class PageRecoveryTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_crashed_page_is_loaded_again_and_a_closed_browser_is_started_again_three_times_a_minute_then_it_says_so()
    {
        var r = new PageRecovery();
        Assert.Equal(PageRecovery.Step.Reload, r.Next("RenderProcessExited", T0));
        Assert.Equal(PageRecovery.Step.NewBrowser, r.Next("BrowserProcessExited", T0.AddSeconds(10)));
        Assert.Equal(PageRecovery.Step.Reload, r.Next("RenderProcessExited", T0.AddSeconds(20)));
        Assert.Equal(PageRecovery.Step.GiveUp, r.Next("RenderProcessExited", T0.AddSeconds(30)));      // the 4th in a minute

        // A minute after the first, there's room again (for a window that hasn't given up).
        var later = new PageRecovery();
        for (int i = 0; i < 3; i++) Assert.Equal(PageRecovery.Step.Reload, later.Next("RenderProcessExited", T0.AddSeconds(i * 25)));
        Assert.Equal(PageRecovery.Step.Reload, later.Next("RenderProcessExited", T0.AddSeconds(61)));

        string text = PageRecovery.GaveUpText("Red Alert");
        Assert.Contains("Red Alert's page stopped working 4 times in a minute", text);
        Assert.Contains("Close Red Alert and open it again.", text);
    }

    [Fact]
    public void A_frame_or_a_helper_process_is_only_logged()
    {
        var r = new PageRecovery();
        foreach (var kind in new[] { "FrameRenderProcessExited", "GpuProcessExited", "UtilityProcessExited", "SandboxHelperProcessExited", "UnknownProcessExited" })
        {
            Assert.Equal(PageRecovery.Step.None, r.Next(kind, T0));
            Assert.False(PageRecovery.PageGone(kind));
            Assert.False(string.IsNullOrWhiteSpace(PageRecovery.Describe(kind)));
        }
        // None of them counted towards the limit.
        for (int i = 0; i < 3; i++) Assert.Equal(PageRecovery.Step.Reload, r.Next("RenderProcessExited", T0.AddSeconds(i)));
    }

    [Fact]
    public void A_page_that_stops_responding_gets_10_seconds_before_it_is_loaded_again()
    {
        var r = new PageRecovery();
        // WebView2 says so every few seconds while it lasts.
        Assert.Equal(PageRecovery.Step.Wait, r.Next("RenderProcessUnresponsive", T0));
        Assert.Equal(PageRecovery.Step.Same, r.Next("RenderProcessUnresponsive", T0.AddSeconds(4)));
        Assert.Equal(PageRecovery.Step.Same, r.Next("RenderProcessUnresponsive", T0.AddSeconds(8)));
        Assert.Equal(PageRecovery.Step.Reload, r.Next("RenderProcessUnresponsive", T0.AddSeconds(12)));
        // Still hung after the reload: another 10 s before the next.
        Assert.Equal(PageRecovery.Step.Wait, r.Next("RenderProcessUnresponsive", T0.AddSeconds(16)));
        Assert.Equal(PageRecovery.Step.Same, r.Next("RenderProcessUnresponsive", T0.AddSeconds(20)));
        Assert.Equal(PageRecovery.Step.Reload, r.Next("RenderProcessUnresponsive", T0.AddSeconds(27)));

        // A busy moment that passed: the next hang, much later, starts afresh.
        var busy = new PageRecovery();
        Assert.Equal(PageRecovery.Step.Wait, busy.Next("RenderProcessUnresponsive", T0));
        Assert.Equal(PageRecovery.Step.Wait, busy.Next("RenderProcessUnresponsive", T0.AddMinutes(5)));
        Assert.Equal(PageRecovery.Step.Same, busy.Next("RenderProcessUnresponsive", T0.AddMinutes(5).AddSeconds(3)));
    }
}
