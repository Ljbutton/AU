using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TournamentTracker.App
{
    public sealed class GameInstall
    {
        public string Path { get; set; } = "";
        public string Store { get; set; } = "";
    }

    /// <summary>Finds Among Us: every Steam library, Epic's install manifests, then the usual folders.</summary>
    public static class GameLocator
    {
        public const string Exe = "Among Us.exe";

        public static bool IsGameFolder(string? path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && File.Exists(System.IO.Path.Combine(path, Exe)); }
            catch (Exception) { return false; }
        }

        public static List<GameInstall> Find(string? steamRoot, string epicManifests, IEnumerable<string> fallbacks)
        {
            var found = new List<GameInstall>();
            foreach (var library in SteamLibraries(steamRoot))
                found.Add(new GameInstall { Store = "Steam", Path = System.IO.Path.Combine(library, "steamapps", "common", "Among Us") });
            foreach (var path in EpicInstalls(epicManifests))
                found.Add(new GameInstall { Store = "Epic Games", Path = path });
            foreach (var path in fallbacks)
                found.Add(new GameInstall { Store = path.Contains("Epic", StringComparison.OrdinalIgnoreCase) ? "Epic Games" : "Steam", Path = path });
            return found.Where(g => IsGameFolder(g.Path))
                .GroupBy(g => System.IO.Path.GetFullPath(g.Path).TrimEnd('\\', '/').ToLowerInvariant())
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>The Steam folder plus every library listed in steamapps/libraryfolders.vdf.</summary>
        public static List<string> SteamLibraries(string? steamRoot)
        {
            var libraries = new List<string>();
            if (string.IsNullOrWhiteSpace(steamRoot)) return libraries;
            libraries.Add(steamRoot);
            string vdf = System.IO.Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch (Exception) { /* unreadable: the Steam folder itself still counts */ }
            return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Epic writes one JSON manifest (*.item) per installed game.</summary>
        public static List<string> EpicInstalls(string manifestDir)
        {
            var found = new List<string>();
            try
            {
                if (!Directory.Exists(manifestDir)) return found;
                foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
                {
                    try
                    {
                        var item = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
                        if (item.TryGetProperty("DisplayName", out var name) && (name.GetString() ?? "").StartsWith("Among Us", StringComparison.OrdinalIgnoreCase)
                            && item.TryGetProperty("InstallLocation", out var location) && location.GetString() is string path)
                            found.Add(path);
                    }
                    catch (Exception) { /* not a manifest we can read */ }
                }
            }
            catch (Exception) { }
            return found;
        }

        public static readonly string[] Fallbacks =
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Among Us",
            @"C:\Program Files\Epic Games\AmongUs",
        };

        public const string EpicManifests = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
    }
}
