using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>A sponsor and where they appear.</summary>
    public sealed class Sponsor
    {
        public string Name { get; set; } = "";
        /// <summary>A logo file on this PC or a web link.</summary>
        public string Logo { get; set; } = "";
        public string Tagline { get; set; } = "";
        /// <summary>Optional video or graphic for the split-screen break (a file on this PC or a web link).</summary>
        public string Video { get; set; } = "";
        /// <summary>Where they appear: killcam, montage, standings, grid, break.</summary>
        public List<string> Placements { get; set; } = new List<string>();
        /// <summary>How long a split-screen break lasts, in seconds.</summary>
        public int BreakSeconds { get; set; } = 30;
    }

    /// <summary>
    /// Sponsors (sponsors.json next to The Button's settings) and proof of delivery: every appearance
    /// (placement, start, seconds on screen, lobby and moment) goes to sponsor-log.jsonl, exported as a
    /// CSV and a short summary to send to sponsors.
    /// </summary>
    public sealed class SponsorBook
    {
        public const string FileName = "sponsors.json";
        public const string LogName = "sponsor-log.jsonl";
        public static readonly string[] AllPlacements = { "killcam", "montage", "standings", "grid", "break" };
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        private readonly string? _path, _logPath;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private DateTime _stamp, _nextCheck;
        private List<Sponsor> _sponsors = new List<Sponsor>();
        private readonly Dictionary<string, int> _turn = new Dictionary<string, int>();
        private readonly Dictionary<string, (Sponsor Sponsor, string Placement, DateTime Start, string? Lobby, string? Moment)> _open = new Dictionary<string, (Sponsor, string, DateTime, string?, string?)>();

        public string? Path => _path;
        public string? Problem { get; private set; }

        public SponsorBook(string? folder, Func<DateTime>? clock = null)
        {
            _clock = clock ?? (() => DateTime.UtcNow);
            _path = folder == null ? null : System.IO.Path.Combine(folder, FileName);
            _logPath = folder == null ? null : System.IO.Path.Combine(folder, LogName);
            Refresh(force: true);
        }

        public List<Sponsor> Sponsors { get { Refresh(); lock (_lock) return _sponsors.ToList(); } }

        public void Refresh(bool force = false)
        {
            if (_path == null) return;
            var now = DateTime.UtcNow;
            if (!force && now < _nextCheck) return;
            _nextCheck = now.AddSeconds(2);
            try
            {
                if (!File.Exists(_path))
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                    // An example with no placements, so nothing shows until it's filled in.
                    File.WriteAllText(_path, JsonSerializer.Serialize(new { sponsors = new[] { new Sponsor { Name = "Example Sponsor", Tagline = "Fill this in, then list its placements: killcam, montage, standings, grid, break", Logo = "C:\\\\Sponsors\\\\example-logo.png" } } }, Json));
                }
                var stamp = File.GetLastWriteTimeUtc(_path);
                if (stamp == _stamp) return;
                _stamp = stamp;
                using var doc = JsonDocument.Parse(File.ReadAllText(_path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var list = doc.RootElement.TryGetProperty("sponsors", out var s) ? JsonSerializer.Deserialize<List<Sponsor>>(s.GetRawText(), Json) ?? new List<Sponsor>() : new List<Sponsor>();
                foreach (var x in list) x.Placements = x.Placements.Select(p => p.Trim().ToLowerInvariant()).ToList();
                lock (_lock) _sponsors = list.Where(x => x.Name.Length > 0).ToList();
                Problem = null;
            }
            catch (Exception e) { Problem = $"{FileName} couldn't be read ({e.Message})."; }
        }

        /// <summary>The next sponsor for a placement (they take turns), or null when nobody has it.</summary>
        public Sponsor? Next(string placement)
        {
            var list = Sponsors.Where(s => s.Placements.Contains(placement)).ToList();
            if (list.Count == 0) return null;
            lock (_lock)
            {
                _turn.TryGetValue(placement, out int i);
                _turn[placement] = i + 1;
                return list[i % list.Count];
            }
        }

        /// <summary>The sponsor already on for a placement (for steady placements like the standings), or the next one.</summary>
        public Sponsor? Current(string placement)
        {
            var list = Sponsors.Where(s => s.Placements.Contains(placement)).ToList();
            if (list.Count == 0) return null;
            lock (_lock)
            {
                var open = _open.Values.FirstOrDefault(o => o.Placement == placement);
                if (open.Sponsor != null && list.Any(s => s.Name == open.Sponsor.Name)) return open.Sponsor;
                _turn.TryGetValue(placement, out int i);
                return list[i % list.Count];
            }
        }

        /// <summary>A sponsor appearance starts (key: anything unique to it).</summary>
        public void Begin(string key, Sponsor sponsor, string placement, string? lobby = null, string? moment = null)
        {
            lock (_lock)
            {
                if (_open.ContainsKey(key)) return;
                _open[key] = (sponsor, placement, _clock(), lobby, moment);
            }
        }

        /// <summary>The appearance ends: it's logged with its seconds on screen.</summary>
        public void End(string key)
        {
            (Sponsor Sponsor, string Placement, DateTime Start, string? Lobby, string? Moment) o;
            lock (_lock)
            {
                if (!_open.Remove(key, out o)) return;
            }
            Log(o.Sponsor.Name, o.Placement, o.Start, _clock(), o.Lobby, o.Moment);
        }

        public bool IsOpen(string key) { lock (_lock) return _open.ContainsKey(key); }

        /// <summary>Ends every appearance whose key starts with this (e.g. all grid tiles).</summary>
        public void EndAll(string prefix)
        {
            List<string> keys;
            lock (_lock) keys = _open.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var k in keys) End(k);
        }

        /// <summary>A whole appearance at once (e.g. a montage's intro card, whose length is known).</summary>
        public void Log(string sponsor, string placement, DateTime start, DateTime end, string? lobby, string? moment)
        {
            if (_logPath == null) return;
            var line = JsonSerializer.Serialize(new { sponsor, placement, start = start.ToString("o"), end = end.ToString("o"), seconds = Math.Round((end - start).TotalSeconds, 1), lobby, moment });
            lock (_lock)
            {
                try { File.AppendAllText(_logPath, line + "\n"); } catch (Exception) { }
            }
        }

        public sealed class Appearance
        {
            public string Sponsor { get; set; } = "";
            public string Placement { get; set; } = "";
            public DateTime Start { get; set; }
            public DateTime End { get; set; }
            public double Seconds { get; set; }
            public string? Lobby { get; set; }
            public string? Moment { get; set; }
        }

        public List<Appearance> Appearances()
        {
            var list = new List<Appearance>();
            if (_logPath == null || !File.Exists(_logPath)) return list;
            foreach (var line in File.ReadAllLines(_logPath))
                try { if (JsonSerializer.Deserialize<Appearance>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) is { } a) list.Add(a); } catch (Exception) { }
            return list;
        }

        /// <summary>Proof of delivery: every appearance as CSV, and a summary per sponsor and placement.</summary>
        public (string Csv, string Summary) Export()
        {
            var all = Appearances();
            var csv = new StringBuilder("sponsor,placement,start (UTC),end (UTC),seconds,lobby,moment\n");
            string Q(string? s) => s == null ? "" : s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
            foreach (var a in all)
                csv.Append(string.Join(",", Q(a.Sponsor), a.Placement, a.Start.ToString("yyyy-MM-dd HH:mm:ss"), a.End.ToString("yyyy-MM-dd HH:mm:ss"),
                    a.Seconds.ToString("0.0", CultureInfo.InvariantCulture), Q(a.Lobby), Q(NameTag.Plain(a.Moment ?? "")))).Append('\n');
            var sum = new StringBuilder();
            foreach (var s in all.GroupBy(a => a.Sponsor).OrderBy(g => g.Key))
            {
                sum.AppendLine($"{s.Key}: {s.Count()} appearances, {Math.Round(s.Sum(a => a.Seconds) / 60, 1)} minutes on screen");
                foreach (var p in s.GroupBy(a => a.Placement).OrderBy(g => g.Key))
                    sum.AppendLine($"  {Placement(p.Key)}: {p.Count()} × , {Math.Round(p.Sum(a => a.Seconds))} s");
                var first = s.Min(a => a.Start); var last = s.Max(a => a.End);
                sum.AppendLine($"  From {first.ToLocalTime():yyyy-MM-dd HH:mm} to {last.ToLocalTime():yyyy-MM-dd HH:mm}");
            }
            if (sum.Length == 0) sum.AppendLine("No sponsor appearances logged yet.");
            return (csv.ToString(), sum.ToString().Replace(" × ,", " times,"));
        }

        /// <summary>Where a page on the caster port gets the sponsor's logo (a web link as it is, a file through The Button).</summary>
        public static string? LogoUrl(Sponsor s) => s.Logo.Length == 0 ? null : s.Logo.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? s.Logo : "/sponsorfile?what=logo&name=" + Uri.EscapeDataString(s.Name);
        public static string? VideoUrl(Sponsor s) => s.Video.Length == 0 ? null : s.Video.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? s.Video : "/sponsorfile?what=video&name=" + Uri.EscapeDataString(s.Name);

        /// <summary>A sponsor's logo or video file on this PC, for the caster port.</summary>
        public (string Type, byte[] Body)? MediaFile(string name, string what)
        {
            var sp = Sponsors.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            string path = sp == null ? "" : what == "video" ? sp.Video : sp.Logo;
            if (path.Length == 0 || path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(path)) return null;
            return (MediaType(path), System.IO.File.ReadAllBytes(path));
        }

        public static string MediaType(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".svg" => "image/svg+xml", ".gif" => "image/gif", ".webp" => "image/webp",
            ".mp4" => "video/mp4", ".webm" => "video/webm", ".mov" => "video/quicktime", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" => "audio/ogg",
            _ => "application/octet-stream",
        };

        public static string Placement(string p) => p switch
        {
            "killcam" => "Kill Cam (replays)",
            "montage" => "Montage intro",
            "standings" => "Standings graphic",
            "grid" => "Grid tile",
            "break" => "Split-screen break",
            _ => p,
        };
    }
}
