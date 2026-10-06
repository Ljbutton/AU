using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// The first time TT Broadcast starts, it takes the caster's setup from The Button's folder, so
    /// nothing has to be set up again: OBS (obs.json, with the key bindings and the replay folder),
    /// the priority rules, roster, graphics, sponsors, alerts, health, Twitch, the stats database, the
    /// tools (ffmpeg) and the administration code. Copied, not moved: The Button's folder is left as it
    /// was. Files TT Broadcast already has are never overwritten.
    /// </summary>
    public static class Migration
    {
        public const string Marker = ".migrated-from-the-button";

        public static readonly string[] Files =
        {
            "obs.json", PriorityConfig.FileName, Roster.FileName, Roster.OverridesName, BroadcastSettings.FileName, SponsorBook.FileName, SponsorBook.LogName,
            "alerts.json", "notes-state.json",
            HealthSettings.FileName, TwitchSettings.FileName, "twitch-token.json", "twitch-detectives.json",
        };

        public static readonly string[] Folders = { "broadcast-games", "tools", "Sponsor reports" };

        /// <summary>
        /// Copies from <paramref name="buttonFolder"/> (The Button's settings folder) to
        /// <paramref name="settingsFile"/>'s folder, once. Returns what it copied.
        /// </summary>
        public static List<string> Run(string buttonFolder, string settingsFile)
        {
            var copied = new List<string>();
            string to = Path.GetDirectoryName(settingsFile)!;
            string marker = Path.Combine(to, Marker);
            if (File.Exists(marker) || !Directory.Exists(buttonFolder)) return copied;
            Directory.CreateDirectory(to);
            foreach (var name in Files)
            {
                string src = Path.Combine(buttonFolder, name), dst = Path.Combine(to, name);
                if (!File.Exists(src) || File.Exists(dst)) continue;
                try { File.Copy(src, dst); copied.Add(name); } catch (Exception) { }
            }
            foreach (var swoosh in Directory.GetFiles(buttonFolder, "tt-swoosh*.webm"))
            {
                string dst = Path.Combine(to, Path.GetFileName(swoosh));
                if (!File.Exists(dst)) try { File.Copy(swoosh, dst); copied.Add(Path.GetFileName(swoosh)); } catch (Exception) { }
            }
            foreach (var folder in Folders)
            {
                string src = Path.Combine(buttonFolder, folder);
                if (Directory.Exists(src) && CopyFolder(src, Path.Combine(to, folder)) > 0) copied.Add(folder + "/");
            }
            // The administration code, so it opens already unlocked.
            var settings = BroadcastAppSettings.Load(settingsFile);
            if (settings.AdminCode == null)
            {
                try
                {
                    string app = Path.Combine(buttonFolder, "app.json");
                    if (File.Exists(app) && JsonDocument.Parse(File.ReadAllText(app)).RootElement.TryGetProperty("AdminCode", out var code) && code.ValueKind == JsonValueKind.String)
                    {
                        settings.AdminCode = code.GetString();
                        settings.Save(settingsFile);
                        copied.Add("administration code");
                    }
                }
                catch (Exception) { }
            }
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o") + "\n" + string.Join("\n", copied));
            return copied;
        }

        private static int CopyFolder(string from, string to)
        {
            int n = 0;
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string dst = Path.Combine(to, Path.GetRelativePath(from, file));
                if (File.Exists(dst)) continue;
                try { Directory.CreateDirectory(Path.GetDirectoryName(dst)!); File.Copy(file, dst); n++; } catch (Exception) { }
            }
            return n;
        }
    }
}
