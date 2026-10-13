using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// What OBS is sending out, like vMix's status bar: streaming and recording (with their time), the
    /// stream's bitrate and dropped frames, OBS keeping up with rendering and encoding, and the disk
    /// space left (replay clips, Source Record's buffers and the recording share it). Read every few
    /// seconds, and at once when the stream or the recording starts or stops. Warnings say what's
    /// wrong in plain words for the Live desk and the Stream Deck.
    /// </summary>
    public sealed partial class ObsDirector
    {
        private sealed class Outputs
        {
            public bool Known, Streaming, Reconnecting, Recording, RecordPaused;
            public string? StreamTime, RecordTime;
            public double? Kbps, DroppedPct, Congestion, Cpu, Fps, DiskGb, RenderLagPct, EncoderLagPct;
            public DateTime At;
        }

        private Outputs _out = new Outputs();
        private (long Bytes, DateTime At)? _lastBytes;
        private (long Skipped, long Total)? _lastDropped, _lastRender, _lastEncoder;
        private DateTime? _streamStoppedAt;
        /// <summary>Under this much free disk (GB) the desk warns; under a quarter of it, in red.</summary>
        public static double LowDiskGb { get; set; } = 20;

        /// <summary>The stream or the recording started or stopped (OBS's Outputs events): noted at once.</summary>
        private void OnOutputEvent(string type, JsonElement data)
        {
            bool active = data.Flag("outputActive");
            string state = data.TryGetProperty("outputState", out var s) ? s.GetString() ?? "" : "";
            lock (_outLock)
            {
                if (type == "StreamStateChanged")
                {
                    bool was = _out.Streaming;
                    _out.Streaming = active;
                    _out.Reconnecting = state.EndsWith("RECONNECTING", StringComparison.Ordinal);
                    if (state.EndsWith("STOPPED", StringComparison.Ordinal) && was) _streamStoppedAt = DateTime.UtcNow;
                    if (active) _streamStoppedAt = null;
                }
                else
                {
                    _out.Recording = active;
                    _out.RecordPaused = state.EndsWith("PAUSED", StringComparison.Ordinal);
                }
                _out.Known = true;
            }
            OutputChanged?.Invoke();
        }

        private readonly object _outLock = new object();

        /// <summary>Streaming or recording started or stopped (the Stream Deck's state answers at once).</summary>
        public event Action? OutputChanged;

        /// <summary>Every few seconds (with the upkeep): the stream, the recording and OBS's own figures, in one request.</summary>
        internal async Task OutputsAsync()
        {
            var obs = _obs;
            if (obs == null) return;
            List<ObsResult> r;
            try { r = await obs.BatchAsync(new (string, object?)[] { ("GetStreamStatus", null), ("GetRecordStatus", null), ("GetStats", null) }).ConfigureAwait(false); }
            catch (Exception) { return; }
            var now = DateTime.UtcNow;
            static double? N(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            static bool B(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
            static string? S(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            // A share of frames lost since the last look (not since the stream began, which hides a new problem).
            static double? Rate(ref (long Skipped, long Total)? last, double? skipped, double? total)
            {
                if (skipped is not { } sk || total is not { } tt) return null;
                var was = last;
                last = ((long)sk, (long)tt);
                if (was is not { } w || tt < w.Total) return null;
                long frames = (long)tt - w.Total;
                return frames <= 0 ? 0 : Math.Round(100.0 * Math.Max(0, (long)sk - w.Skipped) / frames, 2);
            }
            bool changed;
            lock (_outLock)
            {
                bool wasStreaming = _out.Streaming, wasRecording = _out.Recording;
                var o = new Outputs { Known = true, At = now };
                if (r.Count > 0 && r[0].Ok)
                {
                    var st = r[0].Data;
                    o.Streaming = B(st, "outputActive");
                    o.Reconnecting = B(st, "outputReconnecting");
                    o.StreamTime = Time(S(st, "outputTimecode"));
                    o.Congestion = N(st, "outputCongestion") is { } cg ? Math.Round(cg * 100, 1) : null;
                    if (N(st, "outputBytes") is { } bytes && o.Streaming)
                    {
                        if (_lastBytes is { } lb && bytes >= lb.Bytes && (now - lb.At).TotalSeconds > 0.5)
                            o.Kbps = Math.Round((bytes - lb.Bytes) * 8 / 1000 / (now - lb.At).TotalSeconds);
                        _lastBytes = ((long)bytes, now);
                    }
                    else _lastBytes = null;
                    o.DroppedPct = o.Streaming ? Rate(ref _lastDropped, N(st, "outputSkippedFrames"), N(st, "outputTotalFrames")) : null;
                    if (wasStreaming && !o.Streaming) _streamStoppedAt ??= now;
                    if (o.Streaming) _streamStoppedAt = null;
                }
                if (r.Count > 1 && r[1].Ok)
                {
                    var rec = r[1].Data;
                    o.Recording = B(rec, "outputActive");
                    o.RecordPaused = B(rec, "outputPaused");
                    o.RecordTime = Time(S(rec, "outputTimecode"));
                }
                if (r.Count > 2 && r[2].Ok)
                {
                    var stats = r[2].Data;
                    o.Cpu = N(stats, "cpuUsage") is { } cpu ? Math.Round(cpu, 1) : null;
                    o.Fps = N(stats, "activeFps") is { } fps ? Math.Round(fps, 1) : null;
                    o.DiskGb = N(stats, "availableDiskSpace") is { } mb ? Math.Round(mb / 1024, 1) : null;
                    o.RenderLagPct = Rate(ref _lastRender, N(stats, "renderSkippedFrames"), N(stats, "renderTotalFrames"));
                    o.EncoderLagPct = Rate(ref _lastEncoder, N(stats, "outputSkippedFrames"), N(stats, "outputTotalFrames"));
                }
                changed = wasStreaming != o.Streaming || wasRecording != o.Recording || _out.Reconnecting != o.Reconnecting;
                _out = o;
            }
            if (changed) OutputChanged?.Invoke();
        }

        /// <summary>"01:23:45" from OBS's "01:23:45.678".</summary>
        private static string? Time(string? timecode) => timecode == null ? null : timecode.Split('.')[0];

        /// <summary>What's wrong with what OBS is sending out: ("bad" or "warn", what to say). Empty when all is well.</summary>
        public List<(string Level, string Text)> OutputWarnings()
        {
            var list = new List<(string, string)>();
            if (!Connected) return list;
            lock (_outLock)
            {
                var o = _out;
                if (!o.Known) return list;
                if (o.Reconnecting) list.Add(("bad", "The stream is reconnecting: OBS lost its connection to the streaming service."));
                else if (_streamStoppedAt is { } stopped && DateTime.UtcNow - stopped < TimeSpan.FromMinutes(10)) list.Add(("bad", $"The stream stopped in OBS at {stopped.ToLocalTime():HH:mm}."));
                if (o.Streaming && !o.Recording) list.Add(("warn", "Streaming, but OBS isn't recording."));
                if (o.DroppedPct is > 1.0) list.Add(("warn", $"The stream is dropping frames ({o.DroppedPct:0.#}%): the upload can't keep up."));
                if (o.RenderLagPct is > 1.0) list.Add(("warn", $"OBS can't keep up drawing the picture ({o.RenderLagPct:0.#}% of frames missed): close other programs or lighten the scenes."));
                if (o.EncoderLagPct is > 1.0) list.Add(("warn", $"The encoder is overloaded ({o.EncoderLagPct:0.#}% of frames skipped): lower the stream's resolution or encoder preset."));
                if (o.DiskGb is { } gb && gb < LowDiskGb) list.Add((gb < LowDiskGb / 4 ? "bad" : "warn", $"Only {gb:0.#} GB free on OBS's disk: replays and the recording need room."));
            }
            return list;
        }

        public bool Streaming { get { lock (_outLock) return Connected && _out.Streaming; } }
        public bool Recording { get { lock (_outLock) return Connected && _out.Recording; } }

        /// <summary>For the Live desk and Setup: LIVE and REC with their time, bitrate, dropped frames, CPU and disk.</summary>
        public object OutputView()
        {
            var warnings = OutputWarnings();
            lock (_outLock)
            {
                var o = _out;
                return new
                {
                    Known = Connected && o.Known,
                    Streaming = Connected && o.Streaming, o.Reconnecting, o.StreamTime, o.Kbps, o.DroppedPct, o.Congestion,
                    Recording = Connected && o.Recording, o.RecordPaused, o.RecordTime,
                    o.Cpu, o.Fps, o.DiskGb, o.RenderLagPct, o.EncoderLagPct,
                    Warnings = warnings.Select(w => new { Level = w.Level, Text = w.Text }).ToList(),
                };
            }
        }

        // ---- The program picture (a confidence monitor for the Live desk) ----------------------------

        private readonly SemaphoreSlim _shotOne = new SemaphoreSlim(1, 1);
        private (byte[] Jpg, DateTime At)? _shot;
        /// <summary>How long a program picture is used again before OBS is asked for a new one.</summary>
        public static TimeSpan ShotCache { get; set; } = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// What's on stream now as a small JPEG (OBS's own screenshot of the program scene, graphics,
        /// replays and swooshes included), or null when OBS isn't connected. One request at a time;
        /// pictures are reused for <see cref="ShotCache"/>, so however often the desk asks, OBS isn't
        /// asked more than about twice a second.
        /// </summary>
        public async Task<byte[]?> ProgramShotAsync(int width = 448)
        {
            var obs = _obs;
            if (obs == null || !obs.Connected) return null;
            if (_shot is { } fresh && DateTime.UtcNow - fresh.At < ShotCache) return fresh.Jpg;
            await _shotOne.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_shot is { } again && DateTime.UtcNow - again.At < ShotCache) return again.Jpg;
                string scene = Scene ?? ProgramName(await obs.RequestAsync("GetCurrentProgramScene").ConfigureAwait(false));
                var r = await obs.RequestAsync("GetSourceScreenshot", new { sourceName = scene, imageFormat = "jpg", imageWidth = Math.Max(8, Math.Min(1920, width)), imageCompressionQuality = 60 }).ConfigureAwait(false);
                string data = r.TryGetProperty("imageData", out var d) ? d.GetString() ?? "" : "";
                int comma = data.IndexOf(',');
                var jpg = Convert.FromBase64String(comma >= 0 ? data.Substring(comma + 1) : data);
                _shot = (jpg, DateTime.UtcNow);
                return jpg;
            }
            catch (Exception) { return _shot?.Jpg; }
            finally { _shotOne.Release(); }
        }
    }
}
