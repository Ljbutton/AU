using Xunit;

namespace TournamentTracker.Tests;

public class BackgroundWorkTests
{
    [Fact]
    public void Inline_until_started_then_in_order_on_another_thread()
    {
        using var work = new BackgroundWork(NullLog.Instance);
        int caller = Environment.CurrentManagedThreadId;
        int ranOn = 0;
        work.Post(() => ranOn = Environment.CurrentManagedThreadId);
        Assert.Equal(caller, ranOn);                      // tests and tools: done at once

        work.Start();
        var order = new List<int>();
        var threads = new HashSet<int>();
        for (int i = 0; i < 200; i++)
        {
            int n = i;
            work.Post(() => { lock (order) order.Add(n); lock (threads) threads.Add(Environment.CurrentManagedThreadId); });
        }
        Assert.True(work.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(Enumerable.Range(0, 200), order);     // in the order given
        Assert.DoesNotContain(caller, threads);
        Assert.Single(threads);                            // one job at a time
    }

    [Fact]
    public void A_failing_job_does_not_stop_the_ones_after_it()
    {
        using var work = new BackgroundWork(NullLog.Instance);
        work.Start();
        bool after = false;
        work.Post(() => throw new InvalidOperationException("boom"));
        work.Post(() => after = true);
        Assert.True(work.Flush(TimeSpan.FromSeconds(5)));
        Assert.True(after);
    }

    [Fact]
    public void The_tick_runs_one_step_a_frame_on_consecutive_frames()
    {
        // Time in ms, so the sums are exact: a frame every 10 ms, a round every 120 ms.
        var stagger = new TickStagger(120, 3);
        var ran = new List<(int Frame, int Step)>();
        for (int frame = 0; frame < 60; frame++)
        {
            int step = stagger.Next(frame * 10);
            if (step >= 0) ran.Add((frame, step));
        }
        // Every 12 frames: steps 0, 1, 2 on three frames in a row, never two in one frame.
        Assert.Equal(new[] { 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2 }, ran.Select(r => r.Step));
        Assert.Equal(ran.Count, ran.Select(r => r.Frame).Distinct().Count());
        var starts = ran.Where(r => r.Step == 0).Select(r => r.Frame).ToList();
        Assert.Equal(new[] { 0, 12, 24, 36, 48 }, starts);
        foreach (var s in starts) Assert.Equal(new[] { s, s + 1, s + 2 }, ran.Where(r => r.Frame >= s && r.Frame <= s + 2).Select(r => r.Frame));
    }

    [Fact]
    public void A_slow_frame_rate_still_finishes_every_round()
    {
        // 5 fps: a round is due every frame; each still gets all its steps before the next starts.
        var stagger = new TickStagger(0.2, 3);
        var steps = new List<int>();
        for (int frame = 0; frame < 9; frame++) steps.Add(stagger.Next(frame * 0.2));
        Assert.Equal(new[] { 0, 1, 2, 0, 1, 2, 0, 1, 2 }, steps);
    }
}
