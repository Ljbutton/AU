using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TournamentTracker.App
{
    /// <summary>
    /// A support bundle: one zip a host or caster can send when something went wrong, with the logs,
    /// settings and state that explain it, and a README.txt saying what each file is. Shared by The
    /// Button and Red Alert, so both make the same kind of zip and both use the same <see cref="Redact"/>.
    /// <para>
    /// Every text file goes through <see cref="Redact"/> before it goes in: bot tokens, webhooks, setup and
    /// administration codes, passwords, Twitch tokens, stream keys and Stream Deck keys never leave the PC,
    /// so the zip can be posted in a Discord channel. A file that isn't text is left out (it can't be
    /// checked for secrets), and a file over 5 MB keeps only its last 5 MB (the newest part of a log).
    /// </para>
    /// Use: <c>var b = new SupportBundle("The Button 0.1.63"); b.AddFile("the-button.log", path, "The Button's log");
    /// b.AddText("summary.txt", text, "What The Button saw"); string zip = b.Save(SupportBundle.Folder(desktop, downloads), DateTime.Now);</c>
    /// </summary>
    public sealed class SupportBundle
    {
        /// <summary>The most of any one file that goes in: its last 5 MB.</summary>
        public const long MaxFileBytes = 5L * 1024 * 1024;

        /// <summary>What a secret is replaced with.</summary>
        public const string Removed = "[removed]";

        /// <summary>What a text that couldn't be checked in time becomes: nothing of it is sent.</summary>
        public const string TooSlow = "[This text couldn't be checked for secrets in time, so it was left out.]";

        private readonly string _from;
        private readonly List<(string Name, byte[] Bytes)> _entries = new List<(string, byte[])>();
        private readonly List<string> _index = new List<string>();

        /// <param name="from">The app (and version) making the bundle, for the README: "The Button 0.1.63".</param>
        public SupportBundle(string from) => _from = from;

        /// <summary>The names of the files in the bundle so far (README.txt comes first when it's written).</summary>
        public IReadOnlyList<string> Names => _entries.Select(e => e.Name).ToList();

        /// <summary>
        /// A file from the PC, under <paramref name="name"/> in the zip: redacted, and only its last 5 MB.
        /// A missing or unreadable file is noted in the README instead, so whoever reads the bundle knows
        /// it was looked for.
        /// </summary>
        /// <param name="about">One line for the README: what the file is.</param>
        public void AddFile(string name, string? path, string about)
        {
            name = EntryName(name);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _index.Add($"(not found) {name}: {about}");
                return;
            }
            try
            {
                byte[] bytes = ReadTail(path!, MaxFileBytes, out long size, out bool cut);
                if (LooksBinary(bytes))
                {
                    _index.Add($"(left out: not text) {name}: {about}");
                    return;
                }
                string text = Encoding.UTF8.GetString(bytes);
                if (cut)
                {
                    // Start at a whole line, so the first line isn't half of one.
                    int nl = text.IndexOf('\n');
                    if (nl >= 0 && nl < 4096) text = text.Substring(nl + 1);
                    text = $"[… the first {Size(size - MaxFileBytes)} of this {Size(size)} file were left out: only the last {Size(MaxFileBytes)} are here …]" + Environment.NewLine + text;
                }
                Add(name, RedactFile(name, text), about + (cut ? $" (the last {Size(MaxFileBytes)} of {Size(size)})" : ""));
            }
            catch (Exception e)
            {
                _index.Add($"(couldn't read: {Redact(e.Message)}) {name}: {about}");
            }
        }

        /// <summary>Text made by the app (a summary, a list), under <paramref name="name"/>: redacted like a file.</summary>
        public void AddText(string name, string text, string about)
        {
            name = EntryName(name);
            if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes) text = text.Substring(text.Length - (int)Math.Min(text.Length, MaxFileBytes / 2));
            Add(name, RedactFile(name, text), about);
        }

        /// <summary>
        /// What's in a folder, without the files themselves (names, sizes and times, newest first): for folders
        /// that are too big or too personal to send whole, like every saved game.
        /// </summary>
        public void AddFolderListing(string name, string? folder, string about, int newest = 40)
        {
            name = EntryName(name);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                _index.Add($"(not found) {name}: {about}");
                return;
            }
            var text = new StringBuilder();
            try
            {
                var files = new DirectoryInfo(folder!).EnumerateFiles("*", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                text.AppendLine($"{files.Count} file(s), {Size(files.Sum(f => f.Length))} in all. The newest {Math.Min(newest, files.Count)}:");
                foreach (var f in files.Take(newest))
                    text.AppendLine($"{f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  {Size(f.Length),10}  {Path.GetRelativePath(folder!, f.FullName)}");
            }
            catch (Exception e)
            {
                text.AppendLine("Couldn't list the folder: " + e.Message);
            }
            AddText(name, text.ToString(), about);
        }

        private void Add(string name, string text, string about)
        {
            // Two files with the same name (a log and its older copy, say) both go in.
            string unique = name;
            for (int i = 2; _entries.Any(e => e.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); i++)
                unique = Path.ChangeExtension(name, null) + "-" + i + Path.GetExtension(name);
            _entries.Add((unique, Encoding.UTF8.GetBytes(text)));
            _index.Add($"{unique}: {about}");
        }

        /// <summary>A path inside the zip: forward slashes, no leading slash or "..".</summary>
        private static string EntryName(string name)
        {
            var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != "." && p != "..");
            string clean = string.Join("/", parts);
            return clean.Length > 0 ? clean : "file.txt";
        }

        /// <summary>The README: who made the bundle and when, what was removed, and every file (and every file looked for and not found).</summary>
        public string Readme(DateTime localNow)
        {
            var r = new StringBuilder();
            r.AppendLine("Tournament Tracker support bundle");
            r.AppendLine($"Made by {_from} on {localNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} (this PC's time).");
            r.AppendLine();
            r.AppendLine("Secrets were taken out of every file before it went in: Discord bot tokens, webhook links, setup and");
            r.AppendLine("administration codes, VDO.Ninja and OBS passwords, Twitch tokens, stream keys and Stream Deck keys");
            r.AppendLine($"(each shows as {Removed} or similar). Files over {Size(MaxFileBytes)} keep only their last {Size(MaxFileBytes)}.");
            r.AppendLine();
            r.AppendLine("What's in it:");
            foreach (var line in _index) r.AppendLine("  " + line);
            return Redact(r.ToString());
        }

        /// <summary>The bundle as a zip: README.txt first, then every file in the order it was added.</summary>
        public byte[] ToZip(DateTime localNow)
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Put(string name, byte[] bytes)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    entry.LastWriteTime = localNow;
                    using var s = entry.Open();
                    s.Write(bytes, 0, bytes.Length);
                }
                Put("README.txt", Encoding.UTF8.GetBytes(Readme(localNow)));
                foreach (var (name, bytes) in _entries) Put(name, bytes);
            }
            return memory.ToArray();
        }

        /// <summary>
        /// Writes the zip into <paramref name="folder"/> as TournamentTracker-support-&lt;date&gt;.zip (with a
        /// number added if that name is taken) and gives back its full path.
        /// </summary>
        public string Save(string folder, DateTime localNow)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, FileName(localNow));
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(folder, Path.GetFileNameWithoutExtension(FileName(localNow)) + $"-{i}.zip");
            SafeFile.WriteAllBytes(path, ToZip(localNow));
            return path;
        }

        /// <summary>"TournamentTracker-support-2026-10-12-1930.zip".</summary>
        public static string FileName(DateTime localNow) =>
            "TournamentTracker-support-" + localNow.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture) + ".zip";

        /// <summary>Where a bundle is saved: the Desktop, else Downloads, else the temp folder (whichever exists first).</summary>
        public static string Folder(string? desktop, string? downloads)
        {
            foreach (var f in new[] { desktop, downloads })
                if (!string.IsNullOrWhiteSpace(f) && Directory.Exists(f)) return f!;
            return Path.GetTempPath();
        }

        /// <summary>"3.2 MB", "880 KB", "120 bytes".</summary>
        public static string Size(long bytes) =>
            bytes >= 1024 * 1024 ? (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
            : bytes >= 1024 ? (bytes / 1024) + " KB"
            : bytes + " bytes";

        /// <summary>
        /// The last <paramref name="max"/> bytes of a file (all of it when smaller), read while the app that
        /// writes it (Among Us, The Button) still has it open.
        /// </summary>
        public static byte[] ReadTail(string path, long max, out long size, out bool cut)
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            size = f.Length;
            cut = size > max;
            long take = Math.Min(size, max);
            if (cut) f.Seek(size - take, SeekOrigin.Begin);
            var bytes = new byte[take];
            int got = 0;
            while (got < take)
            {
                int n = f.Read(bytes, got, (int)(take - got));
                if (n <= 0) break;
                got += n;
            }
            return got == take ? bytes : bytes.Take(got).ToArray();
        }

        /// <summary>A NUL byte in the first part means it isn't a text file.</summary>
        private static bool LooksBinary(byte[] bytes)
        {
            int n = Math.Min(bytes.Length, 8192);
            for (int i = 0; i < n; i++) if (bytes[i] == 0) return true;
            return false;
        }

        // ---- Redacting ------------------------------------------------------------------------------

        /// <summary>
        /// A property name that holds a secret, in JSON or a settings file: anything with token, secret,
        /// password, webhook, key or code in it (botTokens, AdminCode, FeedKey, StatusWebhook, DeckKey…).
        /// Some of those aren't secret (a player's key, the lobby code); they go too, as a bundle never needs them.
        /// </summary>
        private const string SecretName = @"[A-Za-z0-9_.\-]{0,60}?(?:token|secret|passw(?:or)?d|pwd|webhook|key|code)[A-Za-z0-9_.\-]{0,60}";

        /// <summary>A property that holds a secret (see SecretName), or "bots": the host's bot tokens in host-discord.json.</summary>
        public static bool IsSecretName(string name) =>
            name.Equals("bots", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(name, "^" + SecretName + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        /// <summary>
        /// The longest one rule may take on one text. Never reached on real logs; a pathological file (a huge
        /// blob with no spaces) is left out instead of being sent unchecked.
        /// </summary>
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

        private static readonly (Regex Pattern, string With)[] Rules =
        {
            // Setup and administration codes ("TT1-" and any later version) carry bot tokens and webhooks inside.
            (new Regex(@"\bTT[0-9A-Z]-[A-Za-z0-9_\-]{16,}", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit), "[removed code]"),
            // Discord bot tokens (user id in base64, a timestamp, an HMAC).
            (new Regex(@"[A-Za-z0-9_\-]{23,28}\.[A-Za-z0-9_\-]{6,7}\.[A-Za-z0-9_\-]{27,}", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit), "[removed token]"),
            // Authorization headers and "Bot …" / "Bearer …" / "OAuth …" values.
            (new Regex(@"\b(Authorization\s*[:=]\s*)(?!(?:(?:Bot|Bearer|OAuth|Basic)\s+)?\[removed)(?:(?:Bot|Bearer|OAuth|Basic)\s+)?[^\s""',;]+", Opt, Limit), "$1" + Removed),
            (new Regex(@"\b(Bot|Bearer|OAuth)\s+[A-Za-z0-9._~+/=\-]{20,}", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit), "$1 " + Removed),
            // Twitch: chat tokens, stream keys, and the key at the end of an RTMP link.
            (new Regex(@"\boauth:[A-Za-z0-9]{10,}", Opt, Limit), "oauth:" + Removed),
            (new Regex(@"\blive_\d+_[A-Za-z0-9]{10,}", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit), "[removed stream key]"),
            (new Regex(@"(rtmps?://[^\s/""']+/[^\s/""']+/)[^\s""'<>]+", Opt, Limit), "$1[removed stream key]"),
            // In links and "name=value" text: VDO.Ninja's password, Stream Deck and The Button's keys and tokens.
            (new Regex(@"(?<![A-Za-z0-9_])(password|passwd|pwd|pw|pass|token|access_token|refresh_token|id_token|client_secret|secret|key|apikey|api_key|streamkey|stream_key|device_code|sig|signature)=(?!\[removed)[^&\s""'<>#]+", Opt, Limit), "$1=" + Removed),
            (new Regex(@"(?<![A-Za-z0-9_])(code)=[A-Za-z0-9_\-.~%]{12,}", Opt, Limit), "$1=" + Removed),
            // "name": value pairs (JSON inside a log line, or a JSON file that couldn't be read as JSON).
            (new Regex(@"(""(?:" + SecretName + @"|bots)"")(\s*:\s*)(?!""\[removed)(?:""(?:[^""\\]|\\.)+""|-?\d[\d.eE+\-]*|\[(?:[^\[\]""]|""(?:[^""\\]|\\.)*"")+\]|\{[^{}]*\})", Opt, Limit), "$1$2\"" + Removed + "\""),
            // Settings-file lines "Name = value" (the mod's BepInEx config: BotTokens, StatsWebhookUrl…).
            (new Regex(@"^([ \t]*" + SecretName + @"[ \t]*=[ \t]*)(?=\S)(?!\[removed)[^\r\n]+", Opt | RegexOptions.Multiline, Limit), "$1" + Removed),
            // "name: value" in log text ("OBS password: hunter2", "X-App-Token: …").
            (new Regex(@"(?<![A-Za-z0-9_\-])([A-Za-z0-9_\-]{0,40}?(?:token|secret|passw(?:or)?d|webhook)[A-Za-z0-9_\-]{0,40})(\s*:[ \t]*)(?!""?\[removed)(?:""[^""\r\n]*""|[^\s,;""]+)", Opt, Limit), "$1$2" + Removed),
        };

        /// <summary>
        /// Takes the secrets out of any text: a log, a settings file, a status, a link. Safe to run twice
        /// (what it puts in is never taken for a secret again).
        /// </summary>
        public static string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string t = RemoveWebhooks(text!);
            try
            {
                foreach (var (pattern, with) in Rules) t = pattern.Replace(t, with);
            }
            catch (RegexMatchTimeoutException)
            {
                return TooSlow;
            }
            return t;
        }

        private static readonly Regex WebhookPath = new Regex(@"\G/api/(?:v\d+/)?webhooks/", Opt);

        /// <summary>
        /// Discord webhooks: the whole link, as whoever has it can post as the tournament. Any host's (discord.com,
        /// ptb., canary., discordapp.com) and links pasted without https://. Found by their "/api/…webhooks/"
        /// and widened to the host before it and the token after it, so a long log is read once.
        /// </summary>
        private static string RemoveWebhooks(string t)
        {
            if (t.IndexOf("webhooks/", StringComparison.OrdinalIgnoreCase) < 0) return t;
            var b = new StringBuilder(t.Length);
            int copied = 0;
            for (int at = t.IndexOf("/api/", StringComparison.OrdinalIgnoreCase); at >= 0; at = t.IndexOf("/api/", Math.Max(at + 1, copied), StringComparison.OrdinalIgnoreCase))
            {
                var m = WebhookPath.Match(t, at);
                if (!m.Success) continue;
                int start = at;
                while (start > copied && (char.IsLetterOrDigit(t[start - 1]) || t[start - 1] == '.' || t[start - 1] == '-')) start--;
                if (start >= copied + 3 && string.Compare(t, start - 3, "://", 0, 3, StringComparison.Ordinal) == 0)
                {
                    int scheme = start - 3;
                    while (scheme > copied && char.IsLetter(t[scheme - 1])) scheme--;
                    string word = t.Substring(scheme, start - 3 - scheme);
                    if (word.Equals("https", StringComparison.OrdinalIgnoreCase) || word.Equals("http", StringComparison.OrdinalIgnoreCase)) start = scheme;
                }
                int end = at + m.Length;
                while (end < t.Length && !char.IsWhiteSpace(t[end]) && "\"'<>`\\)]".IndexOf(t[end]) < 0) end++;
                b.Append(t, copied, start - copied).Append("[removed webhook]");
                copied = end;
            }
            return b.Append(t, copied, t.Length - copied).ToString();
        }

        /// <summary>
        /// A JSON document with its secrets taken out: every property named like a secret (see
        /// <see cref="IsSecretName"/>) has its value replaced with "[removed]", at any depth, and every
        /// other string goes through <see cref="Redact"/> (a link with a password in it, JSON inside a string).
        /// Empty values, nulls and true/false stay, so the bundle still shows whether something was set.
        /// Not JSON: redacted as text.
        /// </summary>
        public static string RedactJson(string json)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
            catch (JsonException) { return Redact(json); }
            if (root == null) return Redact(json);
            if (root is JsonValue v && v.TryGetValue(out string? s)) return JsonValue.Create(RedactString(s ?? ""))!.ToJsonString();
            RedactIn(root);
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        /// <summary>Redacts an object or a list where it is (no copies: a stats file can be megabytes).</summary>
        private static void RedactIn(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[name];
                    if (IsSecretName(name) && HasValue(value)) obj[name] = Removed;
                    else if (value != null) Visit(value, clean => obj[name] = clean);
                }
            }
            else if (node is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    int at = i;
                    if (arr[i] != null) Visit(arr[i]!, clean => arr[at] = clean);
                }
            }
        }

        /// <summary>A string is replaced (through <paramref name="replace"/>) only when redacting changed it; the rest is visited where it is.</summary>
        private static void Visit(JsonNode value, Action<JsonNode> replace)
        {
            if (value is JsonValue v && v.TryGetValue(out string? s))
            {
                string clean = RedactString(s ?? "");
                if (clean != s) replace(JsonValue.Create(clean)!);
            }
            else RedactIn(value);
        }

        /// <summary>A string inside JSON: itself JSON (a status kept as text), or text.</summary>
        private static string RedactString(string s)
        {
            string trimmed = s.TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                try
                {
                    var inner = JsonNode.Parse(s);
                    if (inner != null)
                    {
                        RedactIn(inner);
                        return inner.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    }
                }
                catch (JsonException) { }
            }
            return Redact(s);
        }

        /// <summary>Something worth hiding: not null, not true/false, not an empty string or list.</summary>
        private static bool HasValue(JsonNode? value) => value switch
        {
            null => false,
            JsonValue v when v.TryGetValue(out bool _) => false,
            JsonValue v when v.TryGetValue(out string? s) => !string.IsNullOrEmpty(s),
            JsonArray a => a.Count > 0,
            JsonObject o => o.Count > 0,
            _ => true,
        };

        /// <summary>A file's text with its secrets out: as JSON when it is JSON, else as text.</summary>
        public static string RedactFile(string name, string text)
        {
            string t = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
            bool json = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || t.StartsWith("{") || t.StartsWith("[");
            return json ? RedactJson(text.TrimStart('﻿')) : Redact(text);
        }
    }
}
