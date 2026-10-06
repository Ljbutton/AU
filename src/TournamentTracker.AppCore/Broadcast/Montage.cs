using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>A built montage (a video file) and how it's going.</summary>
    public sealed class Montage
    {
        public string Id { get; set; } = "";
        /// <summary>"game", "round" or "custom".</summary>
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public string? File { get; set; }
        /// <summary>"building", "ready", "failed", or "played".</summary>
        public string State { get; set; } = "building";
        public string? Problem { get; set; }
        public DateTime Created { get; set; }
        public double Duration { get; set; }
        public List<string> ClipIds { get; set; } = new List<string>();
        public string? Lobby { get; set; }
        public int Round { get; set; }
        public string? Sponsor { get; set; }
        /// <summary>When each moment plays (seconds in) and its key player, for player cards during the montage.</summary>
        public List<MontageMoment> Moments { get; set; } = new List<MontageMoment>();
    }

    public sealed class MontageMoment
    {
        public double At { get; set; }
        public double Length { get; set; }
        public string Lobby { get; set; } = "";
        public string Key { get; set; } = "";
    }

    /// <summary>One piece of a montage: a clip (cut around its moment, cropped, with a lower third) or a title card.</summary>
    public sealed class Segment
    {
        public Clip? Clip { get; set; }
        public string? File { get; set; }
        public double Start { get; set; }
        public double Length { get; set; }
        public View View { get; set; } = new View(0.5, 0.5, 1);
        /// <summary>Lower third, with name tags ("[[colour|Name]]").</summary>
        public string? Lower { get; set; }
        /// <summary>A title card instead of a clip.</summary>
        public string? Card { get; set; }
        /// <summary>A small line above the card's title ("PRESENTED BY").</summary>
        public string? CardTop { get; set; }
        public string? CardSmall { get; set; }
        public string? CardLogo { get; set; }
    }

    /// <summary>
    /// Builds montages with ffmpeg: each clip cut to a few seconds around its moment, cropped and
    /// zoomed like replays, with a lower third ("[swatch] Jake → [swatch] Maria, Electrical"),
    /// joined with quick wipes, optionally opened by a "Presented by" sponsor card. Renders to an
    /// MP4 next to the clips; The Button plays it in OBS's replay scene.
    /// </summary>
    public sealed class MontageBuilder
    {
        public const double Transition = 0.4;
        private static readonly string[] Crew = { "c51111", "132ed1", "117f2d", "ed54ba", "ef7d0d", "f5f557", "3f474e", "d6e0f0", "6b2fbb", "71491e", "38fedc", "50ef39", "5f1d2e", "ecc0d3", "f0e7a8", "758593", "918877", "d76464" };
        private readonly Func<ReplaySettings> _settings;
        private readonly Func<string> _folder;
        private readonly string? _toolsFolder;
        /// <summary>This ffmpeg can't draw text (built without freetype): montages go without words.</summary>
        private bool _noText;

        public MontageBuilder(Func<ReplaySettings> settings, Func<string> folder, string? toolsFolder)
        {
            _settings = settings;
            _folder = folder;
            _toolsFolder = toolsFolder;
        }

        // ---- ffmpeg ----------------------------------------------------------------------------

        /// <summary>ffmpeg: the path in the replay settings, The Button's own copy, or one on the PATH.</summary>
        public string? Ffmpeg
        {
            get
            {
                var s = _settings();
                if (!string.IsNullOrEmpty(s.Ffmpeg) && File.Exists(s.Ffmpeg)) return s.Ffmpeg;
                string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
                if (_toolsFolder != null && File.Exists(Path.Combine(_toolsFolder, exe))) return Path.Combine(_toolsFolder, exe);
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                    try { if (dir.Length > 0 && File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe); } catch (Exception) { }
                return null;
            }
        }

        public const string FfmpegZip = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

        /// <summary>Downloads ffmpeg for Windows into The Button's tools folder.</summary>
        public async Task<string> DownloadFfmpegAsync(HttpClient http)
        {
            if (_toolsFolder == null) return "No place to put ffmpeg.";
            Directory.CreateDirectory(_toolsFolder);
            string zip = Path.Combine(_toolsFolder, "ffmpeg.zip");
            using (var resp = await http.GetAsync(FfmpegZip, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using var file = File.Create(zip);
                await resp.Content.CopyToAsync(file).ConfigureAwait(false);
            }
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The download had no ffmpeg.exe in it.");
                entry.ExtractToFile(Path.Combine(_toolsFolder, "ffmpeg.exe"), overwrite: true);
            }
            File.Delete(zip);
            return "ffmpeg is ready.";
        }

        /// <summary>Runs ffmpeg; answers with its error output (the last lines) when it fails.</summary>
        public async Task<(bool Ok, string Output)> RunAsync(IEnumerable<string> args, TimeSpan? timeout = null)
        {
            string ffmpeg = Ffmpeg ?? throw new InvalidOperationException("ffmpeg isn't installed: press Get ffmpeg in the Caster tab.");
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEndAsync();
            var done = Task.Run(() => p.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(6)).TotalMilliseconds));
            bool exited = await done.ConfigureAwait(false);
            if (!exited) { try { p.Kill(true); } catch (Exception) { } return (false, "ffmpeg took too long."); }
            string e = await err.ConfigureAwait(false);
            await outp.ConfigureAwait(false);
            return (p.ExitCode == 0, e);
        }

        /// <summary>A clip's length in seconds (from ffmpeg's description of the file).</summary>
        public async Task<double?> ProbeAsync(string file)
        {
            var (_, output) = await RunAsync(new[] { "-hide_banner", "-i", file }).ConfigureAwait(false);
            var m = Regex.Match(output, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
            return m.Success ? int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : (double?)null;
        }

        /// <summary>A still from a clip (for the Moments library).</summary>
        public Task<(bool Ok, string Output)> ThumbnailAsync(string clip, double at, string jpg) =>
            RunAsync(new[] { "-hide_banner", "-y", "-ss", F(Math.Max(0, at)), "-i", clip, "-frames:v", "1", "-vf", "scale=320:-2", jpg });

        /// <summary>A stand-in clip for simulation mode (no OBS): the lobby and the play over moving colours.</summary>
        public async Task<(bool Ok, string Output)> PlaceholderAsync(string file, string text, double seconds)
        {
            string Args(bool words) => "drawbox=x=0:y=0:w=iw:h=ih:color=black@0.45:t=fill" + (words ? $",drawtext={Font(44)}:text='{Esc(NameTag.Plain(text))}':fontcolor=white:x=(w-text_w)/2:y=(h-text_h)/2,drawtext={Font(30)}:text='SIMULATED %{{pts\\:hms}}':fontcolor=white:x=40:y=40" : "");
            List<string> Cmd(bool words) => new List<string> { "-hide_banner", "-y", "-f", "lavfi", "-i", $"testsrc2=s=1280x720:r=30:d={F(seconds)}",
                "-vf", Args(words), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-an", file };
            var r = await RunAsync(Cmd(!_noText)).ConfigureAwait(false);
            if (!r.Ok && NoText(r.Output)) r = await RunAsync(Cmd(false)).ConfigureAwait(false);
            return r;
        }

        /// <summary>ffmpeg said it has no drawtext: remember, and go without words from now on.</summary>
        private bool NoText(string output)
        {
            if (_noText || !output.Contains("No such filter: 'drawtext'", StringComparison.Ordinal)) return false;
            _noText = true;
            return true;
        }

        /// <summary>
        /// The placeholder swoosh: a green wipe with a gold edge across a transparent picture (WebM with
        /// alpha), covering the whole screen around 0.45 s, and a whoosh of filtered noise.
        /// </summary>
        public async Task<bool> SwooshAsync(string file)
        {
            if (Ffmpeg == null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var (ok, _) = await RunAsync(new[]
            {
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", "color=c=black@0.0:s=1920x1080:r=60:d=0.9,format=rgba",
                "-f", "lavfi", "-i", "color=c=0x1fa143:s=2400x1080:r=60:d=0.9,format=rgba",
                "-f", "lavfi", "-i", "color=c=0xffc15a:s=140x1080:r=60:d=0.9,format=rgba",
                "-f", "lavfi", "-i", "anoisesrc=d=0.9:c=pink:a=0.6",
                "-filter_complex", "[0][1]overlay=x='-2400+t/0.9*4320':y=0:shortest=1[a];[a][2]overlay=x='t/0.9*4320':y=0:shortest=1,format=yuva420p[v];[3]highpass=f=400,lowpass=f=6000,afade=t=in:d=0.4,afade=t=out:st=0.45:d=0.45[au]",
                "-map", "[v]", "-map", "[au]", "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-auto-alt-ref", "0", "-b:v", "1M", "-deadline", "realtime", "-c:a", "libopus", file,
            }).ConfigureAwait(false);
            return ok;
        }

        // ---- Planning ---------------------------------------------------------------------------

        /// <summary>The lower third for a clip: "[swatch] Jake → [swatch] Maria, Electrical" for kills, the play's text otherwise.</summary>
        public static string LowerThird(Clip c)
        {
            var tags = Regex.Matches(c.Title, @"\[\[\d+\|[^\]]*\]\]").Select(m => m.Value).ToList();
            if ((c.Rule == "kill" || c.Rule == "winningKill") && tags.Count >= 2)
                return $"{tags[0]} → {tags[1]}{(c.Room != null ? ", " + c.Room : "")}";
            return c.Title;
        }

        /// <summary>A clip cut to a few seconds around its moment, with its crop (the replay's, at the moment, pushed in).</summary>
        public Segment Cut(Clip c, double duration, double? before = null, double? after = null)
        {
            var s = _settings();
            double b = before ?? s.MontageBefore, a = after ?? s.MontageAfter;
            double at = duration - ((c.SavedAt ?? c.EventAt) - c.EventAt).TotalSeconds - s.VideoDelayMs / 1000.0;
            double start = Math.Max(0, at - b);
            double length = Math.Min(Math.Max(0, at - start) + a, duration - start);
            var clip = new Clip { Samples = c.Samples, Focus = c.Focus, Marks = c.Marks, EventAt = c.EventAt, SavedAt = c.SavedAt, Duration = duration, Pre = c.Pre, Post = c.Post };
            var view = Framing.At(clip, s, Math.Max(0, at), 99);
            return new Segment { Clip = c, File = c.File, Start = start, Length = Math.Max(0.5, length), View = view, Lower = LowerThird(c) };
        }

        /// <summary>Keeps the montage near its target length: the best plays first, then in time order.</summary>
        public static List<Clip> Pick(IEnumerable<Clip> clips, double seconds, double each)
        {
            int max = Math.Max(1, (int)Math.Floor((seconds + Transition) / (each - Transition)));
            var list = clips.ToList();
            if (list.Count <= max) return list.OrderBy(c => c.EventAt).ToList();
            int Rank(Clip c) => c.Rule switch { "winningKill" => 0, "gameEnd" => 1, "witnessedKill" => 2, "eject" => 3, "kill" => 4, _ => 5 };
            return list.OrderBy(Rank).ThenBy(c => c.EventAt).Take(max).OrderBy(c => c.EventAt).ToList();
        }

        // ---- Rendering ----------------------------------------------------------------------------

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Text for ffmpeg's drawtext, with its special characters escaped.</summary>
        public static string Esc(string s) => s.Replace("\\", "\\\\\\\\").Replace("'", "\u2019").Replace(":", "\\:").Replace("%", "\\%").Replace(",", "\\,");

        /// <summary>A monospaced bold font (so swatches line up with the names), and its width per character.</summary>
        private static (string? File, double Width) MonoFont()
        {
            foreach (var (f, w) in new[] { (@"C:\Windows\Fonts\consolab.ttf", 0.55), ("/usr/share/fonts/truetype/dejavu/DejaVuSansMono-Bold.ttf", 0.602), ("/usr/share/fonts/truetype/liberation/LiberationMono-Bold.ttf", 0.6) })
                if (File.Exists(f)) return (f, w);
            return (null, 0.6);
        }

        private static string Font(int size)
        {
            var (file, _) = MonoFont();
            return file == null ? $"fontsize={size}" : $"fontfile='{file.Replace("\\", "/").Replace(":", "\\:")}':fontsize={size}";
        }

        public const int HeadSize = 52;

        /// <summary>
        /// The lower third's drawing: a dark band, then each player's crewmate head (placed with an
        /// overlay afterwards, see <see cref="Command"/>) with their name, and plain text, left to right.
        /// </summary>
        public static (string Filter, List<(int Color, double X, double Y)> Heads) LowerThird(string lower, int w, int h, bool words = true)
        {
            var (_, cw) = MonoFont();
            int size = 46;
            double charW = cw * size, x0 = 110, y = h - 175;
            // One line of text (so every piece sits on the same baseline), with two spaces where each
            // head goes; the font is monospaced, so each head's place is known exactly.
            var text = new System.Text.StringBuilder();
            var heads = new List<(int, double, double)>();
            foreach (var p in Regex.Split(lower, @"(\[\[\d+\|[^\]]*\]\])").Where(p => p.Length > 0))
            {
                var m = Regex.Match(p, @"^\[\[(\d+)\|([^\]]*)\]\]$");
                if (!m.Success) { text.Append(p); continue; }
                int c = int.Parse(m.Groups[1].Value);
                heads.Add((c >= 0 && c < Crew.Length ? c : 15, x0 + text.Length * charW + (2 * charW - HeadSize) / 2, y - 2));
                text.Append("  ").Append(m.Groups[2].Value);
            }
            var parts = new List<string>
            {
                $"drawbox=x=80:y={F(y - 22)}:w={F(Math.Min(w - 160, text.Length * charW + 70))}:h=92:color=0x08090e@0.86:t=fill",
                $"drawbox=x=80:y={F(y - 22)}:w=10:h=92:color=0xffc15a@1:t=fill",
            };
            // ffmpeg drops spaces at the start of a line: start the text after them instead.
            string line = text.ToString();
            int lead = line.Length - line.TrimStart(' ').Length;
            if (words) parts.Add($"drawtext={Font(size)}:text='{Esc(line.TrimStart(' '))}':fontcolor=white:x={F(x0 + lead * charW)}:y={F(y)}");
            return (string.Join(",", parts), heads);
        }

        /// <summary>A player's crewmate head (the same pictures as The Button's), as a file ffmpeg can read.</summary>
        public static string HeadFile(int color)
        {
            string dir = Path.Combine(Path.GetTempPath(), "tt-crew");
            string file = Path.Combine(dir, color + ".png");
            if (File.Exists(file)) return file;
            Directory.CreateDirectory(dir);
            using var res = typeof(MontageBuilder).Assembly.GetManifestResourceStream($"ui/crew/{color}.png")
                ?? throw new InvalidOperationException("No crewmate picture for colour " + color);
            using (var f = File.Create(file + ".part")) res.CopyTo(f);
            File.Move(file + ".part", file, overwrite: true);
            return file;
        }

        /// <summary>The ffmpeg command for a montage: inputs, the filter graph (crop, lower thirds, wipes) and the output.</summary>
        public List<string> Command(IReadOnlyList<Segment> segments, string output, int w = 1920, int h = 1080)
        {
            var args = new List<string> { "-hide_banner", "-y" };
            var filters = new List<string>();
            int input = 0;
            var labels = new List<(string Label, double Length)>();
            foreach (var seg in segments)
            {
                string label = $"s{labels.Count}";
                if (seg.Card != null)
                {
                    args.AddRange(new[] { "-f", "lavfi", "-t", F(seg.Length), "-i", $"color=c=0x0b0e13:s={w}x{h}:r=30" });
                    int bg = input++;
                    var draw = new List<string> { "null", $"drawtext={Font(110)}:text='{Esc(seg.Card)}':fontcolor=white:x=(w-text_w)/2:y=(h-text_h)/2-{(seg.CardLogo != null ? 170 : 40)}" };
                    if (seg.CardTop != null) draw.Add($"drawtext={Font(40)}:text='{Esc(seg.CardTop)}':fontcolor=0x9aa3b2:x=(w-text_w)/2:y=(h-text_h)/2-{(seg.CardLogo != null ? 300 : 150)}");
                    if (_noText) draw.RemoveAll(d => d.StartsWith("drawtext", StringComparison.Ordinal));
                    if (seg.CardSmall != null && !_noText) draw.Add($"drawtext={Font(48)}:text='{Esc(seg.CardSmall)}':fontcolor=0xffc15a:x=(w-text_w)/2:y=(h-text_h)/2+{(seg.CardLogo != null ? 260 : 70)}");
                    if (seg.CardLogo != null)
                    {
                        args.AddRange(new[] { "-loop", "1", "-t", F(seg.Length), "-i", seg.CardLogo });
                        int logo = input++;
                        filters.Add($"[{logo}:v]scale=520:-2,format=rgba[lg{labels.Count}]");
                        filters.Add($"[{bg}:v][lg{labels.Count}]overlay=(W-w)/2:(H-h)/2+40,{string.Join(",", draw)},fps=30,format=yuv420p,setsar=1[{label}]");
                    }
                    else filters.Add($"[{bg}:v]{string.Join(",", draw)},fps=30,format=yuv420p,setsar=1[{label}]");
                }
                else
                {
                    args.AddRange(new[] { "-ss", F(seg.Start), "-t", F(seg.Length), "-i", seg.File! });
                    int i = input++;
                    var v = seg.View;
                    double z = Math.Max(1, v.Zoom);
                    // The crop's top left (in source pixels): the view's centre minus half the visible size.
                    string crop = $"crop=w=iw/{F(z)}:h=ih/{F(z)}:x=iw*{F(v.X)}-iw/{F(z)}/2:y=ih*{F(v.Y)}-ih/{F(z)}/2";
                    var (lower, heads) = seg.Lower != null ? LowerThird(seg.Lower, w, h, !_noText) : ("", new List<(int Color, double X, double Y)>());
                    string chain = $"[{i}:v]{crop},scale={w}:{h},fps=30,format=yuv420p,setsar=1,setpts=PTS-STARTPTS{(lower.Length > 0 ? "," + lower : "")}";
                    if (heads.Count == 0) filters.Add($"{chain}[{label}]");
                    else
                    {
                        // Each player's crewmate head over the band, next to their name.
                        string at = $"b{labels.Count}";
                        filters.Add($"{chain}[{at}]");
                        for (int k = 0; k < heads.Count; k++)
                        {
                            args.AddRange(new[] { "-i", HeadFile(heads[k].Color) });
                            int img = input++;
                            string hd = $"h{labels.Count}_{k}", next = k == heads.Count - 1 ? label : $"b{labels.Count}_{k}";
                            filters.Add($"[{img}:v]scale={HeadSize}:{HeadSize},format=rgba[{hd}]");
                            filters.Add($"[{at}][{hd}]overlay=x={F(heads[k].X)}:y={F(heads[k].Y)}:eof_action=repeat,format=yuv420p[{next}]");
                            at = next;
                        }
                    }
                }
                labels.Add((label, seg.Length));
            }
            // Quick wipes between the pieces.
            string last = labels[0].Label;
            double total = labels[0].Length;
            for (int k = 1; k < labels.Count; k++)
            {
                string outLabel = $"x{k}";
                filters.Add($"[{last}][{labels[k].Label}]xfade=transition=wiperight:duration={F(Transition)}:offset={F(Math.Max(0, total - Transition))}[{outLabel}]");
                total = total + labels[k].Length - Transition;
                last = outLabel;
            }
            args.AddRange(new[] { "-filter_complex", string.Join(";", filters), "-map", $"[{last}]", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-r", "30", "-an", "-movflags", "+faststart", output });
            return args;
        }

        /// <summary>Total length of a montage of these pieces (the wipes overlap).</summary>
        public static double Length(IEnumerable<Segment> segments) { var l = segments.Select(s => s.Length).ToList(); return l.Sum() - Transition * Math.Max(0, l.Count - 1); }

        /// <summary>Renders a montage to a file next to the clips.</summary>
        public async Task<Montage> BuildAsync(Montage m, IReadOnlyList<Segment> segments)
        {
            try
            {
                if (segments.Count == 0) throw new InvalidOperationException("No clips to put in it.");
                string dir = Path.Combine(_folder(), "Montages");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"{m.Kind} {Regex.Replace(NameTag.Plain(m.Title), @"[^\w\- ]", "")} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
                var (ok, output) = await RunAsync(Command(segments, file)).ConfigureAwait(false);
                if (!ok && NoText(output)) (ok, output) = await RunAsync(Command(segments, file)).ConfigureAwait(false);
                if (!ok) throw new InvalidOperationException("ffmpeg: " + string.Join(" ", output.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(3)));
                m.File = file;
                m.Duration = Math.Round(Length(segments), 1);
                double at = 0;
                m.Moments.Clear();
                foreach (var seg in segments)
                {
                    if (seg.Clip?.KeyPlayer is { } key) m.Moments.Add(new MontageMoment { At = at, Length = seg.Length, Lobby = seg.Clip.Lobby, Key = key });
                    at += seg.Length - Transition;
                }
                m.State = "ready";
            }
            catch (Exception e)
            {
                m.State = "failed";
                m.Problem = e.Message;
            }
            return m;
        }
    }
}
