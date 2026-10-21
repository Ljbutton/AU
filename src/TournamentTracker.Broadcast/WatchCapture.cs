using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// The Watch page's pictures: one small JPEG of each lobby, taken from OBS (which already has every
    /// lobby at full size) a few times a second, in one loop shared by everyone watching. Each watcher
    /// reads the latest picture from here, so however many people watch, the hosts upload nothing more
    /// and OBS is asked once per lobby per round. The loop takes pictures only while someone is
    /// watching (and OBS is connected); otherwise it just checks again now and then.
    /// <para>
    /// Bounded: at most <see cref="MaxFps"/> pictures a second per lobby and <see cref="MaxShotsPerSecond"/>
    /// in all (more lobbies: fewer each), <see cref="MaxLobbies"/> lobbies, pictures over
    /// <see cref="MaxJpegBytes"/> dropped, and one OBS request at a time.
    /// </para>
    /// </summary>
    public sealed class WatchCapture : IDisposable
    {
        public const int MaxFps = 5;
        public const int MaxShotsPerSecond = 30;
        public const int MaxLobbies = 16;
        public const int MaxJpegBytes = 400_000;
        /// <summary>A picture older than this isn't shown (the page says "No picture"; it never falls back to the host's video).</summary>
        public static TimeSpan FreshFor { get; set; } = TimeSpan.FromSeconds(4);
        /// <summary>How often the loop looks again while nobody is watching.</summary>
        internal static TimeSpan IdleCheck { get; set; } = TimeSpan.FromMilliseconds(300);

        private readonly Func<bool> _watching;
        private readonly Func<IReadOnlyList<string>> _lobbies;
        private readonly Func<string, int, Task<byte[]?>> _shot;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly Dictionary<string, (byte[] Jpg, DateTime At)> _frames = new Dictionary<string, (byte[], DateTime)>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>Pictures taken so far (for tests and the log).</summary>
        public long Shots { get; private set; }
        /// <summary>The loop is taking pictures (someone is watching, OBS is connected).</summary>
        public bool Running { get; private set; }

        /// <param name="watching">Someone is watching and OBS is connected: pictures are worth taking.</param>
        /// <param name="lobbies">The lobbies to take, in order.</param>
        /// <param name="shot">A lobby's picture at a width (null: none).</param>
        /// <param name="run">Start the loop (false for tests, which step it themselves).</param>
        public WatchCapture(Func<bool> watching, Func<IReadOnlyList<string>> lobbies, Func<string, int, Task<byte[]?>> shot, bool run = true, Func<DateTime>? clock = null)
        {
            _watching = watching;
            _lobbies = lobbies;
            _shot = shot;
            _clock = clock ?? (() => DateTime.UtcNow);
            if (run) _ = Task.Run(LoopAsync);
        }

        /// <summary>Pictures a second per lobby for this many lobbies (fewer when there are many).</summary>
        public static int FpsFor(int lobbies) => lobbies <= 0 ? MaxFps : Math.Max(1, Math.Min(MaxFps, MaxShotsPerSecond / lobbies));

        /// <summary>How wide each picture is for this many lobbies (smaller when the grid has more).</summary>
        public static int WidthFor(int lobbies) => lobbies <= 1 ? 960 : lobbies <= 4 ? 640 : lobbies <= 9 ? 480 : 400;

        /// <summary>The pictures a second the page should ask for now.</summary>
        public int Fps => FpsFor(Math.Min(MaxLobbies, Math.Max(1, SafeLobbies().Count)));

        /// <summary>A lobby's latest picture while it's fresh, else null.</summary>
        public byte[]? Latest(string lobby)
        {
            var now = _clock();
            lock (_lock) return _frames.TryGetValue(lobby, out var f) && now - f.At < FreshFor ? f.Jpg : null;
        }

        /// <summary>The lobbies with a fresh picture.</summary>
        public HashSet<string> Fresh()
        {
            var now = _clock();
            lock (_lock) return new HashSet<string>(_frames.Where(kv => now - kv.Value.At < FreshFor).Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
        }

        private IReadOnlyList<string> SafeLobbies()
        {
            try { return _lobbies(); } catch (Exception) { return Array.Empty<string>(); }
        }

        /// <summary>
        /// One round: nobody watching, nothing (and the wait before looking again); else one picture of each
        /// lobby, and how long to wait so each lobby is taken <see cref="FpsFor"/> times a second.
        /// </summary>
        internal async Task<TimeSpan> StepAsync()
        {
            bool watching;
            try { watching = _watching(); } catch (Exception) { watching = false; }
            Running = watching;
            if (!watching) return IdleCheck;
            var lobbies = SafeLobbies().Take(MaxLobbies).ToList();
            if (lobbies.Count == 0) return IdleCheck;
            var timer = Stopwatch.StartNew();
            int width = WidthFor(lobbies.Count);
            foreach (var lobby in lobbies)
            {
                byte[]? jpg = null;
                try { jpg = await _shot(lobby, width).ConfigureAwait(false); } catch (Exception) { }
                Shots++;
                if (jpg == null || jpg.Length == 0 || jpg.Length > MaxJpegBytes) continue;
                lock (_lock) _frames[lobby] = (jpg, _clock());
            }
            // Lobbies that have gone are forgotten.
            lock (_lock) foreach (var k in _frames.Keys.Where(k => !lobbies.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) _frames.Remove(k);
            var round = TimeSpan.FromSeconds(1.0 / FpsFor(lobbies.Count));
            var left = round - timer.Elapsed;
            return left > TimeSpan.FromMilliseconds(20) ? left : TimeSpan.FromMilliseconds(20);
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TimeSpan wait;
                try { wait = await StepAsync().ConfigureAwait(false); } catch (Exception) { wait = IdleCheck; }
                try { await Task.Delay(wait, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            lock (_lock) _frames.Clear();
        }
    }

    public sealed partial class ObsDirector
    {
        /// <summary>
        /// A lobby's picture as OBS has it (its TT Lobby source), as a small JPEG for the Watch page; null when
        /// OBS isn't connected or the lobby has no source.
        /// </summary>
        public async Task<byte[]?> LobbyShotAsync(string lobby, int width)
        {
            var obs = _obs;
            if (obs == null || !obs.Connected) return null;
            string? source;
            try { source = Settings.Sources.TryGetValue(lobby, out var s) && s.Length > 0 ? s : null; }
            catch (Exception) { source = null; }
            if (source == null) return null;
            try
            {
                var r = await obs.RequestAsync("GetSourceScreenshot", new { sourceName = source, imageFormat = "jpg", imageWidth = Math.Max(64, Math.Min(1280, width)), imageCompressionQuality = 55 }, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                string data = r.TryGetProperty("imageData", out var d) ? d.GetString() ?? "" : "";
                int comma = data.IndexOf(',');
                return Convert.FromBase64String(comma >= 0 ? data.Substring(comma + 1) : data);
            }
            catch (Exception) { return null; }
        }
    }
}
