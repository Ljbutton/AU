using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App
{
    /// <summary>How quickly a server answered: the middle of several tries, and how much they varied.</summary>
    public sealed class PingResult
    {
        public string Name { get; set; } = "";
        /// <summary>The middle answer time (ms); null when it never answered.</summary>
        public double? Ms { get; set; }
        /// <summary>How much one answer differed from the next, on average (ms): what makes a picture or voice stutter.</summary>
        public double? JitterMs { get; set; }
        public int Tries { get; set; }
        public int Failed { get; set; }
        public bool Reached => Ms != null;

        /// <summary>The middle value and the average step between neighbours, from answer times in the order they came.</summary>
        public static PingResult From(string name, IReadOnlyList<double> times, int failed)
        {
            var r = new PingResult { Name = name, Tries = times.Count + failed, Failed = failed };
            if (times.Count == 0) return r;
            var sorted = times.OrderBy(t => t).ToList();
            r.Ms = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
            r.JitterMs = times.Count < 2 ? 0 : Enumerable.Range(1, times.Count - 1).Average(i => Math.Abs(times[i] - times[i - 1]));
            return r;
        }
    }

    /// <summary>One run of Check my connection.</summary>
    public sealed class ConnectionResult
    {
        public DateTime At { get; set; }
        /// <summary>Upload speed (megabits a second); null when it couldn't be measured.</summary>
        public double? UploadMbps { get; set; }
        /// <summary>What the check sent, in bytes (never more than <see cref="ConnectionCheck.Options.Budget"/>).</summary>
        public long BytesSent { get; set; }
        public PingResult? Discord { get; set; }
        public PingResult? Vdo { get; set; }
        /// <summary>Discord's answer time while the upload ran (ms): a connection that lags as soon as it sends video shows here.</summary>
        public double? LoadedMs { get; set; }
        /// <summary>Why the upload couldn't be measured.</summary>
        public string? Problem { get; set; }
        public ConnectionScore Score { get; set; } = new ConnectionScore();
    }

    /// <summary>The verdict: a colour, one line, the measured facts, and what to do.</summary>
    public sealed class ConnectionScore
    {
        /// <summary>"good", "ok" (enough, little to spare), "warn" or "bad".</summary>
        public string Level { get; set; } = "good";
        public string Headline { get; set; } = "";
        public List<string> Facts { get; set; } = new List<string>();
        public List<string> Advice { get; set; } = new List<string>();
    }

    /// <summary>
    /// Check my connection: before an event, how fast the host's upload is (several parallel uploads to
    /// Cloudflare's speed test for about 7 seconds, never more than 30 MB) and how quickly and steadily
    /// Discord and VDO.Ninja answer, compared with what sending the game needs. Runs off the window's
    /// thread and can be cancelled.
    /// </summary>
    public static class ConnectionCheck
    {
        /// <summary>
        /// What sending the game uses (Mbps): the picture at 1080p, the player camera (when the caster turns it
        /// on), the lobby voice. Together about 9, so 10 is recommended; under 8 is tight and under 5 too little.
        /// </summary>
        public const double VideoLow = 4, VideoHigh = 6, CamLow = 2, CamHigh = 3, Voice = 0.1;
        public const double Recommended = 10, WarnUnder = 8, BadUnder = 5;

        /// <summary>Answer times (ms): slower than this is a warning, then a problem. Jitter the same.</summary>
        public const double SlowMs = 200, VerySlowMs = 400, JitterWarnMs = 30, JitterBadMs = 80;

        /// <summary>Discord answering this much slower while uploading means the connection lags under load.</summary>
        public const double LoadedWarnMs = 150, LoadedBadMs = 400;

        public sealed class Options
        {
            /// <summary>
            /// Cloudflare's own speed test sends its uploads here (POST, any body; @cloudflare/speedtest's
            /// default uploadApiUrl). It answers as soon as the body has arrived.
            /// </summary>
            public string UploadUrl { get; set; } = "https://speed.cloudflare.com/__up";
            /// <summary>Discord's API gateway endpoint: answers without a token, from the same network as the bot.</summary>
            public string DiscordUrl { get; set; } = "https://discord.com/api/v10/gateway";
            public string VdoUrl { get; set; } = "https://vdo.ninja/";
            public int Streams { get; set; } = 4;
            public long RequestBytes { get; set; } = 2L * 1024 * 1024;
            /// <summary>The most one run sends.</summary>
            public long Budget { get; set; } = 30L * 1024 * 1024;
            public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(7);
            /// <summary>The first part of the upload is left out of the speed (connections are still speeding up).</summary>
            public TimeSpan Warmup { get; set; } = TimeSpan.FromSeconds(1.5);
            public TimeSpan Sample { get; set; } = TimeSpan.FromMilliseconds(200);
            public int Pings { get; set; } = 5;
            public TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(5);
            public TimeSpan LoadedPingEvery { get; set; } = TimeSpan.FromMilliseconds(500);
        }

        /// <summary>
        /// Runs the check: Discord, then VDO.Ninja, then the upload (with Discord asked again while it runs).
        /// <paramref name="progress"/> gets what it's doing and how far along (0–1). Cancelling throws
        /// <see cref="OperationCanceledException"/>; nothing more is sent after it.
        /// </summary>
        public static async Task<ConnectionResult> RunAsync(HttpClient http, Options o, Action<string, double>? progress, CancellationToken ct)
        {
            var result = new ConnectionResult { At = DateTime.UtcNow };
            progress?.Invoke("Checking Discord…", 0.02);
            result.Discord = await PingAsync(http, "Discord", o.DiscordUrl, HttpMethod.Get, o.Pings, o.PingTimeout, ct).ConfigureAwait(false);
            progress?.Invoke("Checking VDO.Ninja…", 0.12);
            result.Vdo = await PingAsync(http, "VDO.Ninja", o.VdoUrl, HttpMethod.Head, o.Pings, o.PingTimeout, ct).ConfigureAwait(false);
            progress?.Invoke("Measuring your upload…", 0.22);
            var up = await UploadAsync(http, o, done => progress?.Invoke("Measuring your upload…", 0.22 + 0.78 * done), ct).ConfigureAwait(false);
            result.UploadMbps = up.Mbps;
            result.BytesSent = up.Sent;
            result.LoadedMs = up.LoadedMs;
            result.Problem = up.Problem;
            result.Score = Score(result.UploadMbps, result.Discord, result.Vdo, result.LoadedMs, result.Problem);
            return result;
        }

        /// <summary>
        /// One untimed ask (it sets up the connection), then <paramref name="tries"/> timed ones. Any answer
        /// counts, even an error page. A server that doesn't answer twice at the start is given up on, so a
        /// blocked one costs seconds, not half a minute.
        /// </summary>
        public static async Task<PingResult> PingAsync(HttpClient http, string name, string url, HttpMethod method, int tries, TimeSpan timeout, CancellationToken ct)
        {
            if (await AskAsync(http, url, method, timeout, ct).ConfigureAwait(false) == null
                && await AskAsync(http, url, method, timeout, ct).ConfigureAwait(false) == null)
                return PingResult.From(name, Array.Empty<double>(), 2);
            var times = new List<double>();
            int failed = 0;
            for (int i = 0; i < tries; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (await AskAsync(http, url, method, timeout, ct).ConfigureAwait(false) is double ms) times.Add(ms); else failed++;
            }
            return PingResult.From(name, times, failed);
        }

        /// <summary>How long a server took to answer (ms), or null when it didn't in time.</summary>
        private static async Task<double?> AskAsync(HttpClient http, string url, HttpMethod method, TimeSpan timeout, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var sw = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.UserAgent.ParseAdd("TournamentTracker-App");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                return sw.Elapsed.TotalMilliseconds;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return null; }
        }

        private sealed record Upload(double? Mbps, long Sent, double? LoadedMs, string? Problem);

        private static async Task<Upload> UploadAsync(HttpClient http, Options o, Action<double> progress, CancellationToken ct)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sw = Stopwatch.StartNew();
            var block = new byte[64 * 1024];
            for (int i = 0; i < block.Length; i++) block[i] = (byte)'0';    // what Cloudflare's own test sends
            long reserved = 0, sent = 0, lastWriteTicks = 0;
            string? problem = null;
            // Hands the budget out as each request body is written, and counts what went.
            bool Take(int n)
            {
                if (stop.IsCancellationRequested) return false;
                if (Interlocked.Add(ref reserved, n) > o.Budget) { try { stop.Cancel(); } catch (ObjectDisposedException) { } return false; }
                return true;
            }
            void Sent(int n) { Interlocked.Add(ref sent, n); Interlocked.Exchange(ref lastWriteTicks, sw.Elapsed.Ticks); }

            async Task Worker()
            {
                while (!stop.IsCancellationRequested)
                {
                    long size = Math.Min(o.RequestBytes, o.Budget - Interlocked.Read(ref reserved));
                    if (size <= 0) return;
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, o.UploadUrl + (o.UploadUrl.Contains('?') ? "&" : "?") + "bytes=" + size)
                        {
                            Content = new UploadContent(size, block, Take, Sent),
                        };
                        request.Headers.UserAgent.ParseAdd("TournamentTracker-App");
                        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            Interlocked.CompareExchange(ref problem, $"the speed test server answered {(int)response.StatusCode}", null);
                            return;
                        }
                    }
                    catch (Exception) when (stop.IsCancellationRequested) { return; }
                    catch (Exception e)
                    {
                        Interlocked.CompareExchange(ref problem, e is HttpRequestException ? "the speed test server couldn't be reached (" + e.Message + ")" : e.Message, null);
                        return;
                    }
                }
            }

            // Discord asked again while the upload runs: does the connection lag when it's busy?
            var loaded = new List<double>();
            async Task LoadedPings()
            {
                while (!stop.IsCancellationRequested)
                {
                    try { await Task.Delay(o.LoadedPingEvery, stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                    double? ms;
                    try { ms = await AskAsync(http, o.DiscordUrl, HttpMethod.Get, TimeSpan.FromSeconds(3), stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    if (ms is double m && sw.Elapsed >= o.Warmup && !stop.IsCancellationRequested) lock (loaded) loaded.Add(m);
                }
            }

            var workers = Enumerable.Range(0, Math.Max(1, o.Streams)).Select(_ => Task.Run(Worker)).ToList();
            var pings = Task.Run(LoadedPings);
            var samples = new List<(double T, long Bytes)> { (0, 0) };
            try
            {
                while (true)
                {
                    await Task.Delay(o.Sample, ct).ConfigureAwait(false);
                    double t = sw.Elapsed.TotalSeconds;
                    samples.Add((t, Interlocked.Read(ref sent)));
                    progress(Math.Min(1, t / o.Duration.TotalSeconds));
                    if (t >= o.Duration.TotalSeconds || stop.IsCancellationRequested || workers.All(w => w.IsCompleted)) break;
                }
            }
            finally
            {
                try { stop.Cancel(); } catch (ObjectDisposedException) { }
                try { await Task.WhenAll(workers.Append(pings)).ConfigureAwait(false); } catch (Exception) { }
            }
            ct.ThrowIfCancellationRequested();

            // The speed ends at the last byte written (not the next sample, which may come after the budget ran out).
            long total = Interlocked.Read(ref sent);
            double end = TimeSpan.FromTicks(Interlocked.Read(ref lastWriteTicks)).TotalSeconds;
            samples = samples.Where(s => s.T < end).ToList();
            if (total > 0) samples.Add((end, total));
            double? mbps = total > 0 ? Speed(samples, o.Warmup.TotalSeconds) : null;
            double? loadedMs;
            lock (loaded) loadedMs = loaded.Count > 0 ? PingResult.From("", loaded, 0).Ms : null;
            if (mbps == null && problem == null) problem = "nothing could be sent to the speed test server";
            return new Upload(mbps, total, loadedMs, mbps == null ? problem : null);
        }

        /// <summary>
        /// Megabits a second from (seconds, bytes sent so far) samples: from the first sample after the warm-up
        /// to the last. A run too short for that (a very fast upload used the whole budget) is measured from the start.
        /// </summary>
        public static double? Speed(IReadOnlyList<(double T, long Bytes)> samples, double warmup)
        {
            if (samples.Count < 2) return null;
            var last = samples[^1];
            var start = samples.FirstOrDefault(s => s.T >= warmup);
            if (start == default || last.T - start.T < 1.0) start = samples[0];
            double seconds = last.T - start.T;
            if (seconds <= 0) return null;
            return Math.Max(0, last.Bytes - start.Bytes) * 8.0 / seconds / 1_000_000;
        }

        /// <summary>
        /// The verdict on a run: upload against what sending the game needs (10 Mbps recommended, under 8 a
        /// warning, under 5 a problem), answer times and jitter for Discord and VDO.Ninja, and lag under load.
        /// The worst of them sets the colour.
        /// </summary>
        public static ConnectionScore Score(double? uploadMbps, PingResult? discord, PingResult? vdo, double? loadedMs, string? problem = null)
        {
            var score = new ConnectionScore();
            int level = 0;                                       // 0 good, 1 ok, 2 warn, 3 bad
            void Worse(int l) => level = Math.Max(level, l);
            var advice = new List<string>();
            void Say(string a) { if (!advice.Contains(a)) advice.Add(a); }
            const string Wired = "Use a wired (Ethernet) connection instead of Wi-Fi if you can.";
            const string Quiet = "Pause big uploads and cloud backups (OneDrive, Google Drive, Dropbox, game clips), and ask others on your network not to stream or video-call during the event.";
            const string Lower = "Turn on Lower quality (Settings → Your connection, or the send page): 720p at 30 fps, about 2.5 Mbps instead of up to 6.";

            if (uploadMbps is double up)
            {
                score.Facts.Add($"Upload: {Mbps(up)} ({Recommended:0} Mbps recommended)");
                if (up < BadUnder)
                {
                    Worse(3);
                    Say(Wired); Say(Quiet); Say(Lower);
                    Say("Tell the organiser before the event: at this speed someone else may need to send your lobby's video.");
                }
                else if (up < WarnUnder) { Worse(2); Say(Wired); Say(Quiet); Say(Lower); }
                else if (up < Recommended) { Worse(1); Say(Quiet); Say(Lower); }
            }
            else
            {
                Worse(3);
                score.Facts.Add("Upload: couldn't be measured" + (problem != null ? $" ({problem})" : ""));
                Say("The upload test uses speed.cloudflare.com: a firewall, VPN or school/work network may block it. Try again, or check your upload on any speed test website (10 Mbps recommended).");
            }

            foreach (var p in new[] { discord, vdo })
            {
                if (p == null) continue;
                if (!p.Reached)
                {
                    Worse(3);
                    score.Facts.Add($"{p.Name}: didn't answer");
                    Say(p.Name == "Discord"
                        ? "Discord didn't answer: check your internet, firewall or VPN. The bot and automute need Discord."
                        : $"{p.Name} didn't answer: sending your game needs it. Check your firewall, VPN or school/work network filtering.");
                    continue;
                }
                double ms = p.Ms!.Value, jitter = p.JitterMs ?? 0;
                score.Facts.Add($"{p.Name}: {ms:0} ms (jitter {jitter:0} ms){(p.Failed > 0 ? $", {p.Failed} of {p.Tries} didn't answer" : "")}");
                bool shaky = ms >= SlowMs || jitter >= JitterWarnMs || p.Failed > 0;
                if (ms >= VerySlowMs || jitter >= JitterBadMs) Worse(3);
                else if (shaky) Worse(2);
                if (shaky)
                {
                    Say(Wired);
                    Say("Close downloads, game updates and streams while you play; restart your router if it's been a while.");
                }
            }

            if (loadedMs is double loadedValue && discord?.Ms is double idle)
            {
                double more = loadedValue - idle;
                score.Facts.Add($"While uploading: Discord answered in {loadedValue:0} ms");
                if (more >= LoadedWarnMs)
                {
                    Worse(more >= LoadedBadMs ? 3 : 2);
                    Say("Your connection lags while it uploads, so Among Us may lag while you send video. Lower quality helps; so does a router setting called SQM or QoS, or a wired connection.");
                }
            }

            score.Level = level switch { 0 => "good", 1 => "ok", 2 => "warn", _ => "bad" };
            score.Headline = level switch
            {
                0 => "Your connection is ready to send your game.",
                1 => "Enough to send your game, with little to spare.",
                2 => "Your connection is weak: the caster may get a blurry or choppy picture, and Among Us may lag.",
                _ => uploadMbps == null || discord?.Reached == false || vdo?.Reached == false
                    ? "The check found a problem: see below."
                    : "Your connection is too weak to send your game reliably.",
            };
            score.Advice = advice;
            return score;
        }

        /// <summary>"23.4 Mbps", "over 100 Mbps".</summary>
        public static string Mbps(double v) => v >= 100 ? "over 100 Mbps" : v.ToString(v >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + " Mbps";

        /// <summary>What sending the game needs, in one line.</summary>
        public static string Needs =>
            $"Sending your game uses about {VideoLow:0}–{VideoHigh:0} Mbps for the picture (1080p), {CamLow:0}–{CamHigh:0} Mbps more for the player camera when the caster turns it on, and {Voice.ToString(CultureInfo.InvariantCulture)} Mbps for the lobby voice: {Recommended:0} Mbps upload is recommended.";

        /// <summary>
        /// A request body of <c>size</c> bytes written 64 KB at a time, each piece only once the budget allows
        /// it (<c>take</c>; else the request is dropped), so a run never sends more than its budget.
        /// </summary>
        private sealed class UploadContent : HttpContent
        {
            private readonly long _size;
            private readonly byte[] _block;
            private readonly Func<int, bool> _take;
            private readonly Action<int> _sent;

            public UploadContent(long size, byte[] block, Func<int, bool> take, Action<int> sent)
            {
                _size = size;
                _block = block;
                _take = take;
                _sent = sent;
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                long left = _size;
                while (left > 0)
                {
                    int n = (int)Math.Min(_block.Length, left);
                    if (!_take(n)) throw new OperationCanceledException("The upload check stopped.");
                    await stream.WriteAsync(_block, 0, n).ConfigureAwait(false);
                    _sent(n);
                    left -= n;
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _size;
                return true;
            }
        }
    }
}
