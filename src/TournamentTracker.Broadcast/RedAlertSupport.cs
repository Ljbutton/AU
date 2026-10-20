using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TournamentTracker.Setup;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// What goes in Red Alert's support bundle (Settings → Save support bundle): one zip with what it
    /// takes to work out what went wrong at an event without a call. Red Alert's log (and the one before
    /// it), its settings, OBS's newest log and crash report, and whatever the app adds (versions, what the
    /// desk, OBS, the voice and the lobbies' health were doing).
    /// <para>
    /// Nothing secret goes in. The administration code, the Stream Deck key and the OBS password are taken
    /// out of their own settings by name; those values, the bot tokens and webhooks inside the code, the
    /// Twitch sign-in, OBS's stream keys and the VDO.Ninja passwords are then taken out of every file
    /// wherever they turn up; and every file still goes through the shared redactor
    /// (<see cref="SupportBundle.Redact"/>), which knows what tokens, webhooks and codes look like. Two
    /// layers, so one missing a new kind of secret doesn't leak it.
    /// </para>
    /// Logs are read while they're still being written (OBS keeps its log open all session), and every
    /// file is cut to its last 5 MB.
    /// </summary>
    public sealed class RedAlertSupport
    {
        public const long MaxBytes = 5L * 1024 * 1024;
        public const string Removed = "[removed]";
        /// <summary>
        /// A secret shorter than this isn't searched for everywhere (it would take out ordinary words and
        /// numbers); it's still taken out of its own setting.
        /// </summary>
        public const int MinSecretLength = 6;
        public const string LogName = "red-alert.log";

        /// <summary>Red Alert's settings that go in as they are (still through the redactor), with what each is, for README.txt.</summary>
        public static readonly (string File, string About)[] SettingsFiles =
        {
            (PriorityConfig.FileName, "The desk's rules: which plays count, and how much (the desk config)."),
            (BroadcastSettings.FileName, "The on-stream graphics: the theme and which graphics are on."),
            (CasterDesk.NumbersFile, "Lobby numbers: which lobby each Stream Deck lobby key (and 1-9 on the Live desk) means."),
            (HealthSettings.FileName, "Lobby health: when a lobby counts as degraded or down, auto switch, the be-right-back screen."),
            (TwitchSettings.FileName, "Twitch settings. The Twitch sign-in itself (twitch-token.json) is never included."),
        };

        /// <summary>Files that never go in a bundle, whatever happens: the Twitch sign-in, and the lobbies kept for a restart (their VDO.Ninja links carry passwords).</summary>
        public static readonly string[] NeverIncluded = { "twitch-token.json", Organizer.CacheFileName };

        private readonly List<(string Name, string Text, string About)> _entries = new List<(string, string, string)>();
        private readonly List<string> _notes = new List<string>();
        private readonly HashSet<string> _secrets = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Each file for the zip: its name there, its text (as read, before anything is taken out) and what it is.</summary>
        public IReadOnlyList<(string Name, string Text, string About)> Entries => _entries;
        /// <summary>What was left out or not found, for README.txt.</summary>
        public IReadOnlyList<string> Notes => _notes;

        /// <summary>
        /// Red Alert's log and settings from <paramref name="appFolder"/> (its settings folder), and OBS's
        /// newest log and crash report from <paramref name="obsFolder"/> (%APPDATA%\obs-studio). Either can
        /// be null or missing: what isn't there is listed in README.txt instead.
        /// </summary>
        public static RedAlertSupport FromFolders(string? appFolder, string? obsFolder)
        {
            var b = new RedAlertSupport();
            if (!string.IsNullOrEmpty(appFolder) && Directory.Exists(appFolder))
            {
                b.LearnAppSecrets(appFolder);
                b.AddLog(Path.Combine(appFolder, LogName), LogName, "Red Alert's log: what it did, and anything that went wrong (this PC's time).");
                b.AddLog(Path.Combine(appFolder, LogName + ".1"), LogName + ".1", "The log before that one (Red Alert moves it there past 1 MB).", optional: true);
                b.AddJson(Path.Combine(appFolder, "settings.json"), "settings.json",
                    "Red Alert's own settings: player cameras on or off, the mute-all key (also in summary.txt), Hold and the desk's automation. The administration code and the Stream Deck key are removed.",
                    "adminCode", "deckKey");
                b.AddJson(Path.Combine(appFolder, ObsSettings.FileName), ObsSettings.FileName,
                    "OBS: where Red Alert connects (the password is removed), the TT scenes and lobby sources, replays, the swoosh and lobby voice.",
                    "password");
                foreach (var (file, about) in SettingsFiles) b.AddFile(Path.Combine(appFolder, file), file, about);
            }
            else b._notes.Add("Red Alert's settings folder wasn't found, so there's no log or settings.");

            if (!string.IsNullOrEmpty(obsFolder) && Directory.Exists(obsFolder))
            {
                b.LearnObsSecrets(obsFolder);
                var log = Newest(Path.Combine(obsFolder, "logs"), "*.txt");
                if (log != null) b.AddLog(log, "obs/logs/" + Path.GetFileName(log), $"OBS's newest log, last written {When(log)}.");
                else b._notes.Add("No OBS log was found (OBS's logs folder is empty or missing).");
                var crash = Newest(Path.Combine(obsFolder, "crashes"), "*.txt");
                if (crash != null) b.AddLog(crash, "obs/crashes/" + Path.GetFileName(crash), $"OBS's newest crash report, written {When(crash)}. Check its date: it may be from an older crash.");
                else b._notes.Add("No OBS crash report (OBS hasn't crashed on this PC, or its reports were cleared).");
            }
            else b._notes.Add("OBS's folder (%APPDATA%\\obs-studio) wasn't found, so there's no OBS log.");
            return b;
        }

        /// <summary>A text the app made (versions, the summary…), with what it is for README.txt.</summary>
        public void Add(string name, string text, string about) => _entries.Add((name, text ?? "", about));

        /// <summary>A value to take out of every file wherever it turns up (too short, or empty, is ignored).</summary>
        public void AddSecret(string? value)
        {
            string v = (value ?? "").Trim();
            if (v.Length < MinSecretLength) return;
            _secrets.Add(v);
            // As it would look inside JSON or a link, too.
            _secrets.Add(JsonSerializer.Serialize(v).Trim('"'));
            _secrets.Add(Uri.EscapeDataString(v));
        }

        private static readonly Regex LinkSecret = new Regex(@"[?&](?:password|pw|key|token)=([^&#\s""']+)", RegexOptions.IgnoreCase);

        /// <summary>The password (or key) in a link, such as a host's VDO.Ninja link, to take out wherever it turns up.</summary>
        public void AddSecretsFromLink(string? url)
        {
            foreach (Match m in LinkSecret.Matches(url ?? ""))
            {
                AddSecret(m.Groups[1].Value);
                try { AddSecret(Uri.UnescapeDataString(m.Groups[1].Value)); } catch (Exception) { }
            }
        }

        /// <summary><paramref name="text"/> with every known secret taken out.</summary>
        public string Mask(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            // Longest first, so a secret that contains another goes whole.
            foreach (var s in _secrets.OrderByDescending(s => s.Length)) text = text.Replace(s, Removed, StringComparison.Ordinal);
            return text;
        }

        /// <summary>README.txt: what the bundle is, what each file is, and what was left out.</summary>
        public string Readme(string version, DateTime now)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Red Alert support bundle");
            sb.AppendLine($"Red Alert {(version.Length > 0 ? version : "(version unknown)")}, saved {now:yyyy-MM-dd HH:mm:ss} (this PC's time).");
            sb.AppendLine();
            sb.AppendLine("Send this zip to whoever looks after Red Alert, with a line on what went wrong and roughly");
            sb.AppendLine("when (the logs use this PC's time).");
            sb.AppendLine();
            sb.AppendLine("Not in it, on purpose: the administration code, bot tokens, webhooks, the Stream Deck key, the");
            sb.AppendLine("OBS password, stream keys, Twitch sign-in tokens and VDO.Ninja passwords. They're taken out of");
            sb.AppendLine("every file and show as " + Removed + ". The desk's cards and who the impostors are aren't in it");
            sb.AppendLine("either; player and lobby names are.");
            sb.AppendLine();
            sb.AppendLine("What's in it:");
            int width = Math.Max(12, _entries.Select(e => e.Name.Length).DefaultIfEmpty(0).Max()) + 2;
            sb.AppendLine("  " + "README.txt".PadRight(width) + "This list.");
            foreach (var e in _entries) sb.AppendLine("  " + e.Name.PadRight(width) + e.About);
            if (_notes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Left out, or not there:");
                foreach (var n in _notes) sb.AppendLine("  - " + n);
            }
            sb.AppendLine();
            sb.AppendLine("Files over 5 MB are cut to their last 5 MB (it says so at the top of the file).");
            return sb.ToString();
        }

        /// <summary>
        /// Writes the zip, README.txt first: every file with the known secrets taken out, then through the
        /// shared redactor (<see cref="SupportBundle.RedactFile"/>: JSON by property name, everything else as text).
        /// </summary>
        public void Save(string zipPath, string version, DateTime now)
        {
            // Whole or not at all (a side file first): never half a zip on the Desktop.
            SafeFile.Write(zipPath, file =>
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                void Put(string name, string text)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    w.Write(text);
                }
                Put("README.txt", SupportBundle.Redact(Mask(Readme(version, now))));
                foreach (var e in _entries) Put(e.Name, SupportBundle.RedactFile(e.Name, Mask(e.Text)));
            });
        }

        /// <summary>RedAlert-support-2026-10-12-1530.zip in <paramref name="folder"/>, or -2, -3… when that's taken.</summary>
        public static string FreePath(string folder, DateTime now)
        {
            string stem = $"RedAlert-support-{now:yyyy-MM-dd-HHmm}";
            string path = Path.Combine(folder, stem + ".zip");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{stem}-{i}.zip");
            return path;
        }

        /// <summary>The newest file in <paramref name="folder"/> matching <paramref name="pattern"/> (by when it was last written), or null.</summary>
        public static string? Newest(string folder, string pattern)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;
                return new DirectoryInfo(folder).GetFiles(pattern)
                    .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
                    .FirstOrDefault()?.FullName;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// A file's text, read while another program may still be writing it (OBS keeps its log open);
        /// past <paramref name="max"/> bytes only the end is kept, from the first whole line, with a note
        /// at the top saying so.
        /// </summary>
        public static string ReadTail(string path, long max = MaxBytes)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            if (length <= max)
            {
                using var all = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return all.ReadToEnd();
            }
            fs.Seek(length - max, SeekOrigin.Begin);
            var bytes = new byte[max];
            int read = 0, n;
            while (read < bytes.Length && (n = fs.Read(bytes, read, bytes.Length - read)) > 0) read += n;
            // From the first whole line, so the text doesn't start mid-line (or mid-character).
            int nl = Array.IndexOf(bytes, (byte)'\n', 0, Math.Min(read, 64 * 1024));
            int start = nl < 0 ? 0 : nl + 1;
            return $"[Only the end of this file is here: its first {Size(length - read + start)} were left out.]{Environment.NewLine}"
                + Encoding.UTF8.GetString(bytes, start, read - start);
        }

        /// <summary>
        /// A settings file's JSON with each property named in <paramref name="names"/> (anywhere in it, any
        /// case) replaced by "[removed]"; an empty one stays empty (so it shows nothing was set). Null when
        /// it isn't JSON: then it's left out rather than risk what's in it.
        /// </summary>
        public static string? ScrubJson(string json, params string[] names)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
            catch (JsonException) { return null; }
            if (root == null) return json;
            Walk(root);
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

            void Walk(JsonNode node)
            {
                if (node is JsonObject o)
                {
                    foreach (var kv in o.ToList())
                    {
                        if (names.Any(n => string.Equals(n, kv.Key, StringComparison.OrdinalIgnoreCase)))
                        {
                            bool empty = kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0;
                            if (kv.Value != null && !empty) o[kv.Key] = Removed;
                        }
                        else if (kv.Value != null) Walk(kv.Value);
                    }
                }
                else if (node is JsonArray a)
                    foreach (var item in a) if (item != null) Walk(item);
            }
        }

        // ---- Reading the files ------------------------------------------------------------------

        private void AddLog(string path, string name, string about, bool optional = false)
        {
            if (!File.Exists(path)) { if (!optional) _notes.Add($"{name}: not there."); return; }
            try { _entries.Add((name, ReadTail(path), about)); }
            catch (Exception e) { _notes.Add($"{name} couldn't be read ({e.Message})."); }
        }

        private void AddFile(string path, string name, string about)
        {
            if (NeverIncluded.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) return;
            AddLog(path, name, about);
        }

        private void AddJson(string path, string name, string about, params string[] secretNames)
        {
            if (!File.Exists(path)) { _notes.Add($"{name}: not there."); return; }
            string text;
            try { text = ReadTail(path); }
            catch (Exception e) { _notes.Add($"{name} couldn't be read ({e.Message})."); return; }
            var clean = ScrubJson(text, secretNames);
            if (clean == null) { _notes.Add($"{name} isn't valid JSON, so it was left out (it could hold a password)."); return; }
            _entries.Add((name, clean, about));
        }

        /// <summary>The secrets in Red Alert's own files: the administration code (and the bots and webhooks in it), the Stream Deck key, the OBS password, the Twitch sign-in.</summary>
        private void LearnAppSecrets(string folder)
        {
            try
            {
                var settings = BroadcastAppSettings.Load(Path.Combine(folder, "settings.json"));
                AddSecret(settings.DeckKey);
                AddSecret(settings.AdminCode);
                if (settings.AdminCode != null && SetupCode.TryParse(settings.AdminCode, out var code, out _))
                {
                    AddSecret(code.Encode());
                    foreach (var t in code.BotTokens ?? new List<string>()) AddSecret(t);
                    AddSecret(code.Webhook);
                    AddSecret(code.StatusWebhook);
                }
            }
            catch (Exception) { }
            try { AddSecret(ObsSettings.Load(Path.Combine(folder, ObsSettings.FileName)).Password); } catch (Exception) { }
            // The lobbies kept for a restart: their VDO.Ninja passwords, even while Red Alert is locked.
            foreach (var link in Organizer.CachedLinks(Path.Combine(folder, Organizer.CacheFileName))) AddSecretsFromLink(link);
            try
            {
                var token = TwitchToken.Load(Path.Combine(folder, "twitch-token.json"));
                AddSecret(token?.AccessToken);
                AddSecret(token?.RefreshToken);
            }
            catch (Exception) { }
        }

        /// <summary>OBS's own secrets: each profile's stream key (and its server password or token), and obs-websocket's password.</summary>
        private void LearnObsSecrets(string obsFolder)
        {
            try
            {
                string profiles = Path.Combine(obsFolder, "basic", "profiles");
                if (Directory.Exists(profiles))
                    foreach (var service in Directory.GetFiles(profiles, "service.json", SearchOption.AllDirectories))
                        LearnJsonSecrets(service);
                LearnJsonSecrets(Path.Combine(obsFolder, "plugin_config", "obs-websocket", "config.json"));
            }
            catch (Exception) { }
        }

        private void LearnJsonSecrets(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                using var doc = JsonDocument.Parse(ReadTail(path, 1024 * 1024));
                Walk(doc.RootElement);
            }
            catch (Exception) { }

            void Walk(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object)
                    foreach (var p in e.EnumerateObject())
                    {
                        string n = p.Name.ToLowerInvariant();
                        if (p.Value.ValueKind == JsonValueKind.String && (n.Contains("key") || n.Contains("password") || n.Contains("token") || n.Contains("secret")))
                            AddSecret(p.Value.GetString());
                        else Walk(p.Value);
                    }
                else if (e.ValueKind == JsonValueKind.Array)
                    foreach (var item in e.EnumerateArray()) Walk(item);
            }
        }

        private static string When(string path)
        {
            try { return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm"); } catch (Exception) { return "at an unknown time"; }
        }

        private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    }
}
