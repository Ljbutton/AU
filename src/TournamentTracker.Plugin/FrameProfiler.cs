using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// Times the mod's own work each frame, to find hitches. Off by default: F10 switches it on and
    /// off, or set FrameProfiler = true under [Debug] in the config file. While on, any frame where
    /// the mod took more than <see cref="SlowMs"/> ms is written to the BepInEx log (LogOutput.log)
    /// with the time of each part, at most once a second, with how many garbage collections ran
    /// since the last line. Every 10 s a summary line gives each part's average and worst time.
    /// Only the log: nothing shows on screen.
    /// </summary>
    internal static class FrameProfiler
    {
        public const double SlowMs = 8;

        public enum Part
        {
            Nameplates, OverlayUpdate, OverlayCheck, OverlayLate, OverlayDim, OverlayRings, OverlayAnimate,
            Zoom, Referee, Pump, Replay, Players, Voice, FeedRead, FeedTick, Publish, Theatre,
        }

        private static readonly int Count = Enum.GetValues(typeof(Part)).Length;
        private static readonly double[] Frame = new double[Count];
        private static readonly double[] Sum = new double[Count];
        private static readonly double[] Worst = new double[Count];
        private static readonly int[] Calls = new int[Count];
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        private static readonly StringBuilder Line = new StringBuilder(512);

        private static int _frame = -1;
        private static int _frames;
        private static double _frameTotal, _worstFrame;
        private static float _nextLog, _nextSummary;
        private static int _gc0, _gc1, _gc2, _il2cppGc = -1;
        private static int _frameGc0;
        private static bool _il2cppGcFailed;

        public static bool Enabled { get; set; }

        /// <summary>Times one part: <c>using (FrameProfiler.Time(Part.X)) { … }</c>. Costs nothing while off.</summary>
        public static Section Time(Part part) => Enabled ? new Section(part, Stopwatch.GetTimestamp()) : default;

        public readonly struct Section : IDisposable
        {
            private readonly Part _part;
            private readonly long _start;
            public Section(Part part, long start) { _part = part; _start = start; }
            public void Dispose()
            {
                if (_start == 0) return;
                Add(_part, (Stopwatch.GetTimestamp() - _start) * TicksToMs);
            }
        }

        private static void Add(Part part, double ms)
        {
            NewFrame();
            Frame[(int)part] += ms;
        }

        /// <summary>Called first thing every frame: F10, and the report for the frame before.</summary>
        public static void BeginFrame()
        {
            if (Input.GetKeyDown(KeyCode.F10))
            {
                Enabled = !Enabled;
                TournamentPlugin.Logger.Info($"Frame profiler {(Enabled ? "ON: frames where the mod takes over " + SlowMs + " ms are logged here (F10 to stop)" : "off")}.");
                if (Enabled) Reset();
            }
            if (Enabled) NewFrame();
        }

        private static void Reset()
        {
            Array.Clear(Frame, 0, Count);
            Array.Clear(Sum, 0, Count);
            Array.Clear(Worst, 0, Count);
            Array.Clear(Calls, 0, Count);
            _frames = 0;
            _frameTotal = _worstFrame = 0;
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            _il2cppGc = Il2CppCollections();
            _frameGc0 = _gc0;
            _frame = UnityEngine.Time.frameCount;
            _nextSummary = UnityEngine.Time.unscaledTime + 10f;
        }

        /// <summary>When the frame changes: the last frame's parts are added up and reported if slow.</summary>
        private static void NewFrame()
        {
            int now = UnityEngine.Time.frameCount;
            if (now == _frame) return;
            if (_frame >= 0) EndFrame();
            _frame = now;
            _frameGc0 = GC.CollectionCount(0);
        }

        private static void EndFrame()
        {
            double total = 0;
            for (int i = 0; i < Count; i++)
            {
                double ms = Frame[i];
                if (ms <= 0) continue;
                total += ms;
                Sum[i] += ms;
                Calls[i]++;
                if (ms > Worst[i]) Worst[i] = ms;
            }
            _frames++;
            _frameTotal += total;
            if (total > _worstFrame) _worstFrame = total;
            float t = UnityEngine.Time.unscaledTime;
            bool gcHere = GC.CollectionCount(0) != _frameGc0;
            if (total > SlowMs && t >= _nextLog)
            {
                _nextLog = t + 1f;
                Line.Clear();
                Line.Append("Frame profiler: slow frame ").Append(total.ToString("0.0")).Append(" ms in the mod (whole frame ")
                    .Append((UnityEngine.Time.unscaledDeltaTime * 1000).ToString("0.0")).Append(" ms):");
                for (int i = 0; i < Count; i++)
                    if (Frame[i] >= 0.05) Line.Append(' ').Append((Part)i).Append('=').Append(Frame[i].ToString("0.00"));
                if (gcHere) Line.Append(" [a .NET collection ran this frame]");
                AppendGc(Line);
                TournamentPlugin.Logger.Warn(Line.ToString());
            }
            if (t >= _nextSummary && _frames > 0)
            {
                _nextSummary = t + 10f;
                Line.Clear();
                Line.Append("Frame profiler: last ").Append(_frames).Append(" frames, mod average ").Append((_frameTotal / _frames).ToString("0.00"))
                    .Append(" ms, worst ").Append(_worstFrame.ToString("0.0")).Append(" ms. Average/worst per part:");
                for (int i = 0; i < Count; i++)
                    if (Calls[i] > 0) Line.Append(' ').Append((Part)i).Append('=').Append((Sum[i] / _frames).ToString("0.00")).Append('/').Append(Worst[i].ToString("0.0"));
                AppendGc(Line);
                TournamentPlugin.Logger.Info(Line.ToString());
                Array.Clear(Sum, 0, Count);
                Array.Clear(Worst, 0, Count);
                Array.Clear(Calls, 0, Count);
                _frames = 0;
                _frameTotal = _worstFrame = 0;
            }
            Array.Clear(Frame, 0, Count);
        }

        /// <summary>Collections since the last line: .NET (the mod's side) by generation, and the game's own.</summary>
        private static void AppendGc(StringBuilder sb)
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            sb.Append(" | GC since last line: .NET gen0 +").Append(g0 - _gc0).Append(" gen1 +").Append(g1 - _gc1).Append(" gen2 +").Append(g2 - _gc2);
            _gc0 = g0; _gc1 = g1; _gc2 = g2;
            int game = Il2CppCollections();
            if (game >= 0 && _il2cppGc >= 0) sb.Append(", game +").Append(game - _il2cppGc);
            _il2cppGc = game;
        }

        private static int Il2CppCollections()
        {
            if (_il2cppGcFailed) return -1;
            try { return Il2CppSystem.GC.CollectionCount(0); }
            catch (Exception) { _il2cppGcFailed = true; return -1; }
        }
    }
}
