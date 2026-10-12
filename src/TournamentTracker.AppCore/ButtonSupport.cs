using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TournamentTracker.App
{
    /// <summary>
    /// The Button's support bundle (Settings → Help → Save support bundle): its own log and settings, the
    /// mod's log, config and the state files that explain a problem, Among Us's own log, the versions of
    /// everything, and a summary of what The Button sees right now. Everything goes through
    /// <see cref="SupportBundle.Redact"/>; players' full stats and the setup code itself are never added.
    /// </summary>
    public static class ButtonSupport
    {
        /// <summary>A stats file this small goes in whole; a bigger one (every player of a long tournament) is only listed.</summary>
        public const long SmallStats = 256 * 1024;

        /// <summary>The mod's own config file in BepInEx/config.</summary>
        public const string ModConfigName = "com.ljbutton.tournamenttracker.cfg";

        /// <summary>What goes in, gathered by The Button (all optional: a PC without Among Us still gets a bundle).</summary>
        public sealed class Inputs
        {
            public string AppVersion { get; set; } = "";
            /// <summary>The Button's app.json; its log is next to it.</summary>
            public string? SettingsFile { get; set; }
            public string? GamePath { get; set; }
            public ModState? Mod { get; set; }
            /// <summary>The mod's status as it answered just now (null: Among Us isn't running with the mod).</summary>
            public string? StatusJson { get; set; }
            /// <summary>Among Us's own log folder (…\AppData\LocalLow\Innersloth\Among Us), with Player.log.</summary>
            public string? UnityLogFolder { get; set; }
            /// <summary>Extra lines for summary.txt: the connection check, the Discord bot, the send page.</summary>
            public List<string> Notes { get; set; } = new List<string>();
            public DateTime UtcNow { get; set; } = DateTime.UtcNow;
        }

        public static SupportBundle Build(Inputs i)
        {
            var b = new SupportBundle("The Button " + i.AppVersion);
            JsonElement? status = Parse(i.StatusJson);
            b.AddText("summary.txt", Summary(i, status), "What The Button saw when the bundle was made: the mod, safe mode, the last disconnect, host authority, the connection check");
            b.AddText("versions.txt", Versions(i, status), "Versions: The Button, the mod, Among Us, Windows");

            string? appFolder = string.IsNullOrEmpty(i.SettingsFile) ? null : Path.GetDirectoryName(i.SettingsFile);
            b.AddFile("the-button/the-button.log", appFolder == null ? null : Path.Combine(appFolder, "the-button.log"), "The Button's log");
            string? older = appFolder == null ? null : Path.Combine(appFolder, "the-button.log.1");
            if (older != null && File.Exists(older)) b.AddFile("the-button/the-button.log.1", older, "The Button's log before that");
            b.AddFile("the-button/settings.json", i.SettingsFile, "The Button's settings (codes, bot tokens and webhooks removed)");

            if (i.GamePath != null && GameLocator.IsGameFolder(i.GamePath))
            {
                string bep = Path.Combine(i.GamePath, "BepInEx");
                b.AddFile("mod/LogOutput.log", Path.Combine(bep, "LogOutput.log"), "The mod's log from the last start of Among Us (BepInEx/LogOutput.log)");
                b.AddFile("mod/" + ModConfigName, Path.Combine(bep, "config", ModConfigName), "The mod's config file");
                b.AddFile("mod/BepInEx.cfg", Path.Combine(bep, "config", "BepInEx.cfg"), "BepInEx's own settings");
                AddDataFolder(b, ModInstaller.DataDir(i.GamePath));
            }
            else b.AddText("mod/not-found.txt", "The Button hasn't found Among Us on this PC, so there are no mod files.", "Why there are no mod files");

            if (i.UnityLogFolder != null)
            {
                b.AddFile("among-us/Player.log", Path.Combine(i.UnityLogFolder, "Player.log"), "Among Us's own log from its last start (crashes and disconnects show here)");
                string prev = Path.Combine(i.UnityLogFolder, "Player-prev.log");
                if (File.Exists(prev)) b.AddFile("among-us/Player-prev.log", prev, "Among Us's own log from the start before");
            }
            return b;
        }

        /// <summary>
        /// The mod's data folder: what explains a problem (the install marker, the tournament state, safe mode
        /// and disconnect records, its settings), a small stats file, and only a list of the rest (saved games,
        /// replays). The setup code, the host's Discord, the links and the connection files never go in.
        /// </summary>
        private static void AddDataFolder(SupportBundle b, string data)
        {
            if (!Directory.Exists(data))
            {
                b.AddText("mod/data/not-found.txt", "The mod's data folder isn't there yet (Among Us hasn't run with the mod).", "Why there's no mod data");
                return;
            }
            var never = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                TournamentTracker.Setup.SetupCode.FileName, TournamentTracker.Setup.HostDiscord.FileName,
                TournamentTracker.Control.ControlServer.FileName, TournamentTracker.Discord.ButtonBridge.FileName, "links.json",
            };
            List<FileInfo> files;
            try { files = new DirectoryInfo(data).EnumerateFiles().ToList(); }
            catch (Exception) { files = new List<FileInfo>(); }
            foreach (var f in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (never.Contains(f.Name)) continue;
                string n = f.Name.ToLowerInvariant();
                bool wanted = n == "installed.json" || n.StartsWith("state") || n.Contains("disconnect") || n.Contains("safe")
                    || n.StartsWith("settings") || (n.StartsWith("stats") && f.Length <= SmallStats);
                if (wanted && (n.EndsWith(".json") || n.EndsWith(".txt")))
                    b.AddFile("mod/data/" + f.Name, f.FullName, About(n));
            }
            b.AddFolderListing("mod/data-folder.txt", data, "Every file in the mod's data folder (names, sizes, times; most aren't in the bundle)", newest: 60);
            string games = Path.Combine(data, "games");
            if (Directory.Exists(games))
                b.AddFolderListing("mod/games-list.txt", games, "The saved games and replays (only the list, newest first)", newest: 40);
        }

        private static string About(string name) =>
            name == "installed.json" ? "Which release of the mod The Button installed"
            : name.StartsWith("state") ? "The tournament's saved state (round, referee slot, switches)"
            : name.Contains("disconnect") ? "The mod's record of disconnects"
            : name.Contains("safe") ? "The mod's safe mode record"
            : name.StartsWith("stats") ? "The tournament's stats (small enough to include)"
            : "The mod's settings";

        private static JsonElement? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
            catch (JsonException) { return null; }
        }

        private static string? Str(JsonElement? e, string name) =>
            e is JsonElement s && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public static string Versions(Inputs i, JsonElement? status)
        {
            var safe = ModHealth.SafeMode(status);
            var v = new StringBuilder();
            v.AppendLine("The Button: " + (i.AppVersion.Length > 0 ? i.AppVersion : "unknown"));
            v.AppendLine("Mod installed: " + (i.Mod?.Installed == true ? i.Mod.InstalledVersion ?? "yes (version unknown)" : "no"));
            v.AppendLine("Mod running: " + (Str(status, "version") ?? "not connected (Among Us isn't running with the mod)"));
            v.AppendLine("Among Us: " + (safe?.GameVersion ?? "unknown (the mod reports it while Among Us runs)") + (i.Mod?.GameArch != null ? $", {i.Mod.GameArch}" : ""));
            if (safe?.BuiltFor != null) v.AppendLine("Mod built for Among Us: " + safe.BuiltFor);
            v.AppendLine("Mod loader (BepInEx winhttp.dll): " + (i.Mod?.LoaderArch ?? "not found"));
            v.AppendLine("Among Us folder: " + (i.GamePath ?? "not found"));
            v.AppendLine("Windows: " + RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture);
            v.AppendLine(".NET: " + RuntimeInformation.FrameworkDescription);
            var offset = TimeZoneInfo.Local.GetUtcOffset(i.UtcNow);
            v.AppendLine($"PC time: {i.UtcNow.ToLocalTime():yyyy-MM-dd HH:mm:ss} (UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm})");
            return v.ToString();
        }

        public static string Summary(Inputs i, JsonElement? status)
        {
            var s = new StringBuilder();
            s.AppendLine("Tournament Tracker: what The Button saw");
            s.AppendLine();
            s.AppendLine(status == null ? "Mod: not connected (Among Us isn't running with the mod, or didn't answer)." : $"Mod: connected, version {Str(status, "version") ?? "?"}, {Str(status, "phase") ?? "?"}, lobby {Str(status, "lobby") ?? "?"}.");

            var safe = ModHealth.SafeMode(status);
            s.AppendLine("Safe mode: " + (safe == null ? "not reported (a mod from before safe mode, or not connected)"
                : safe.On ? $"ON. {safe.Title}. {safe.Say}" : safe.Overridden ? $"off, overridden by the host. {safe.Say}" : "off"));

            var d = ModHealth.LastDisconnect(status, i.UtcNow);
            s.AppendLine("Last disconnect: " + (d == null ? "none reported" : $"{d.At:yyyy-MM-dd HH:mm:ss} UTC ({d.MinutesAgo} min ago), {d.Reason}{(d.Detail != null ? " (" + d.Detail + ")" : "")}{(d.InGame ? ", during a game" : "")}. {d.Title}. {d.Explain}"));

            if (status is JsonElement st && st.ValueKind == JsonValueKind.Object && st.TryGetProperty("hostAuthority", out var ha) && ha.ValueKind == JsonValueKind.Object)
            {
                string on = ha.TryGetProperty("on", out var o) && o.ValueKind == JsonValueKind.True ? "on" : "off";
                string active = ha.TryGetProperty("active", out var a) ? a.ValueKind == JsonValueKind.True ? "active in this lobby" : a.ValueKind == JsonValueKind.False ? "NOT active in this lobby (host again from the main menu)" : "not hosting" : "unknown";
                s.AppendLine($"Host authority: {on}, {active}.");
            }
            else s.AppendLine("Host authority: not reported.");

            foreach (var note in i.Notes) s.AppendLine(note);

            s.AppendLine();
            s.AppendLine("The mod's status (secrets removed):");
            s.AppendLine(status is JsonElement js ? SupportBundle.RedactJson(js.GetRawText()) : "(none)");
            return s.ToString();
        }
    }
}
