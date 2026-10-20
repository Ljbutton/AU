using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Red Alert's broadcast delay: the lobbies reach the stream a few seconds late, done between Red
    /// Alert and OBS (not OBS's own stream delay). Each lobby's picture, voice and camera source in OBS
    /// asks VDO.Ninja for a playout buffer (&amp;buffer=ms), and the on-stream graphics wait the same, so
    /// they stay in step with the picture. The Live desk and its pictures stay live.
    /// </summary>
    public static class BroadcastDelay
    {
        /// <summary>
        /// The most it can be: VDO.Ninja sets the buffer as the browser's jitterBufferTarget (older
        /// Chromium: playoutDelayHint), which Chromium caps at 4000 ms (VDO.Ninja clamps to that too).
        /// </summary>
        public const double Max = 4;
        /// <summary>A few seconds: enough to cut to a lobby before its play reaches the stream, and well inside the cap.</summary>
        public const double Default = 3;
        /// <summary>
        /// Off for now (the owner's call): nothing is held back, whatever obs.json says, and the desk has no
        /// setting for it. Turning this on brings the whole of it back (the setting, the OBS card and the setup check).
        /// </summary>
        public static readonly bool Enabled = false;

        /// <summary>A delay as Red Alert keeps it: 0 to <see cref="Max"/>, in half seconds.</summary>
        public static double Clamp(double seconds) =>
            double.IsFinite(seconds) ? Math.Round(Math.Max(0, Math.Min(Max, seconds)) * 2, MidpointRounding.AwayFromZero) / 2 : 0;

        /// <summary>A VDO.Ninja view link with the playout buffer for the delay (other links, and links that set their own buffer, unchanged).</summary>
        public static string WithBuffer(string url, double seconds)
        {
            int ms = (int)Math.Round(Clamp(seconds) * 1000);
            if (ms <= 0 || !url.Contains("vdo.ninja", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(url, @"[?&]buffer[23]?(=|&|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return url;
            return url + (url.Contains('?') ? "&" : "?") + "buffer=" + ms;
        }

        /// <summary>How the delay is said: "3 s", "1.5 s", "off".</summary>
        public static string Say(double seconds) => seconds <= 0 ? "off" : seconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>
    /// What was live a while ago: each look adds what's live now and answers with the newest one kept
    /// from at least the delay before (or, with too little kept yet, the oldest). Thread-safe.
    /// </summary>
    public sealed class DelayLine<T> where T : class
    {
        private readonly List<(DateTime At, T Value)> _kept = new List<(DateTime, T)>();
        private readonly object _lock = new object();

        public int Count { get { lock (_lock) return _kept.Count; } }

        public T Push(DateTime now, T live, TimeSpan delay)
        {
            lock (_lock)
            {
                if (delay <= TimeSpan.Zero) { _kept.Clear(); return live; }
                // The clock went back: start again.
                if (_kept.Count > 0 && now < _kept[^1].At) _kept.Clear();
                _kept.Add((now, live));
                var due = now - delay;
                // Only the newest one from at or before then is needed (and anything newer).
                while (_kept.Count >= 2 && _kept[1].At <= due) _kept.RemoveAt(0);
                // Nobody looked for a while (OBS closed): what's left from back then is far older than asked; the next one is nearer.
                while (_kept.Count >= 2 && _kept[0].At < due - TimeSpan.FromSeconds(1) && _kept[1].At - _kept[0].At > TimeSpan.FromSeconds(1)) _kept.RemoveAt(0);
                return _kept[0].Value;
            }
        }
    }

    public sealed partial class ObsDirector
    {
        /// <summary>Whether the broadcast delay can be on at all (<see cref="BroadcastDelay.Enabled"/>; tests turn it on for their own director).</summary>
        public bool DelayAllowed { get; set; } = BroadcastDelay.Enabled;

        /// <summary>Red Alert's broadcast delay now, in seconds (0: off).</summary>
        public double DelaySeconds => DelayAllowed ? BroadcastDelay.Clamp(Settings.BroadcastDelay) : 0;

        /// <summary>A host's VDO.Ninja link as OBS opens it for a lobby's picture or camera: its bitrate and the broadcast delay.</summary>
        internal string ForObs(string url) => WithDelay(WithBitrate(url));

        /// <summary>A link with the broadcast delay's playout buffer (VDO.Ninja links only).</summary>
        internal string WithDelay(string url) => BroadcastDelay.WithBuffer(url, DelaySeconds);

        /// <summary>
        /// The broadcast delay changed: saved, and every lobby's picture, camera and voice source in OBS
        /// gets its new link now (each reloads, a moment of black or silence). What it did.
        /// </summary>
        public async Task<string> SetDelayAsync(double seconds)
        {
            if (!DelayAllowed) return "The broadcast delay is off in this version of Red Alert.";
            Settings.BroadcastDelay = BroadcastDelay.Clamp(seconds);
            Save();
            string said = DelaySeconds > 0
                ? $"Broadcast delay {BroadcastDelay.Say(DelaySeconds)}: the lobbies and the graphics reach the stream {BroadcastDelay.Say(DelaySeconds)} late. The Live desk stays live."
                : "Broadcast delay off: the lobbies go to the stream as they come.";
            if (!Connected) return said;
            await AddSourcesAsync(now: true).ConfigureAwait(false);
            await EnsureVoiceAsync().ConfigureAwait(false);
            try { await CheckSetupAsync().ConfigureAwait(false); } catch (Exception) { }
            return said + " Every lobby in OBS reloaded with it.";
        }
    }
}
