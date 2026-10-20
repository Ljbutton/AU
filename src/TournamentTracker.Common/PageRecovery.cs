using System;
using System.Collections.Generic;

namespace TournamentTracker.App
{
    /// <summary>
    /// What the window does when its page stops (WebView2's ProcessFailed; both apps' windows share it): the
    /// page's renderer crashed, the page is loaded again; the browser itself closed, a new one is started; the
    /// page stopped responding (WebView2 says so every few seconds while it lasts), it's loaded again once
    /// that has gone on for <see cref="UnresponsiveFor"/> (a busy PC often catches up by itself). At most
    /// <see cref="MaxPerMinute"/> times in a minute: past that, the window says so in plain words instead of
    /// reloading for ever. A frame inside the page, or a helper process (GPU, utility), WebView2 starts again
    /// by itself (and Red Alert's page reloads a quiet data link by itself): only written in the log.
    /// </summary>
    public sealed class PageRecovery
    {
        public const int MaxPerMinute = 3;
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
        /// <summary>The page reported as not responding for this long: loaded again.</summary>
        public static readonly TimeSpan UnresponsiveFor = TimeSpan.FromSeconds(10);
        /// <summary>No "not responding" report for this long: the hang was over (the next one starts afresh).</summary>
        private static readonly TimeSpan UnresponsiveGap = TimeSpan.FromSeconds(20);

        public enum Step
        {
            /// <summary>Not the page (a frame, a helper): only logged.</summary>
            None,
            /// <summary>The page stopped responding: logged, and waited for a little.</summary>
            Wait,
            /// <summary>The same hang reported again: nothing to say or do yet.</summary>
            Same,
            Reload,
            NewBrowser,
            /// <summary>Too many in a minute: a plain message instead of the page.</summary>
            GiveUp,
        }

        private readonly List<DateTime> _done = new List<DateTime>();
        private readonly object _lock = new object();
        private DateTime? _hangSince, _hangLast;

        /// <summary>The page's own renderer or the browser: the page has gone (white, or frozen).</summary>
        public static bool PageGone(string kind) => kind is "RenderProcessExited" or "RenderProcessUnresponsive" or "BrowserProcessExited";

        /// <summary>What to do for one report (<paramref name="kind"/>: the ProcessFailedKind's name) at <paramref name="now"/>.</summary>
        public Step Next(string kind, DateTime now)
        {
            if (!PageGone(kind)) return Step.None;
            lock (_lock)
            {
                if (kind == "RenderProcessUnresponsive")
                {
                    bool first = _hangSince == null || _hangLast is { } last && now - last > UnresponsiveGap;
                    _hangLast = now;
                    if (first) { _hangSince = now; return Step.Wait; }
                    if (now - _hangSince!.Value < UnresponsiveFor) return Step.Same;
                }
                // Loaded again (or a new browser): a hang after this one starts afresh.
                _hangSince = _hangLast = null;
                _done.RemoveAll(t => now - t >= Window);
                if (_done.Count >= MaxPerMinute) return Step.GiveUp;
                _done.Add(now);
            }
            return kind == "BrowserProcessExited" ? Step.NewBrowser : Step.Reload;
        }

        /// <summary>For the app's log: what stopped, in words.</summary>
        public static string Describe(string kind) => kind switch
        {
            "RenderProcessExited" => "the page's renderer crashed",
            "RenderProcessUnresponsive" => "the page stopped responding",
            "BrowserProcessExited" => "the window's browser (WebView2) closed",
            "FrameRenderProcessExited" => "a frame inside the page crashed",
            _ => "a WebView2 helper process stopped",
        };

        /// <summary>What the window says once it has stopped reloading.</summary>
        public static string GaveUpText(string app) =>
            $"{app}'s page stopped working {MaxPerMinute + 1} times in a minute, so it isn't being loaded again.\n"
            + $"Close {app} and open it again. Your settings are kept.";
    }
}
