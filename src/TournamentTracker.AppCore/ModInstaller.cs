using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace TournamentTracker.App
{
    public sealed class ModState
    {
        public bool GameFound { get; set; }
        public bool Installed { get; set; }
        public string? InstalledVersion { get; set; }
        /// <summary>What Among Us.exe is built for: "x86" (32-bit, before the 29 September 2026 update), "x64", or null if unreadable.</summary>
        public string? GameArch { get; set; }
        /// <summary>What BepInEx's loader (winhttp.dll) is built for, or null when it isn't there.</summary>
        public string? LoaderArch { get; set; }
        /// <summary>The loader can load in this game: it's there and built for the same bitness (an unreadable game is given the benefit of the doubt).</summary>
        public bool LoaderMatchesGame => LoaderArch != null && LoaderArch == (GameArch ?? LoaderArch);
    }

    public sealed class Release
    {
        public string Tag { get; set; } = "";
        /// <summary>The full bundle (mod and BepInEx) for each game bitness: "x86" and "x64".</summary>
        public Dictionary<string, string> BundleUrls { get; set; } = new Dictionary<string, string>();
        public string? BundleFor(string arch) => BundleUrls.TryGetValue(arch, out var url) ? url : null;
        /// <summary>The Button itself (TheButton.exe) in this release, if it has one.</summary>
        public string? AppUrl { get; set; }
    }

    /// <summary>Installs and updates the mod (with its BepInEx loader) in the Among Us folder.</summary>
    public sealed class ModInstaller
    {
        public const string Repo = "Ljbutton/AU";
        /// <summary>The full bundle for one bitness: TournamentTracker-Full-x86.zip or -x64.zip.</summary>
        public static string BundleName(string arch) => $"TournamentTracker-Full-{arch}.zip";
        /// <summary>The bundle's name before 64-bit Among Us (always x86). Releases keep it for older copies of The Button.</summary>
        public const string LegacyBundleName = "TournamentTracker-Full.zip";
        /// <summary>When the game can't be read, assume today's 64-bit Among Us.</summary>
        public const string DefaultArch = "x64";
        public const string AppName = "TheButton.exe";
        private const string Marker = "installed.json";
        private readonly HttpClient _http;

        public ModInstaller(HttpClient http) => _http = http;

        public static string DataDir(string gameDir) => Path.Combine(gameDir, "BepInEx", "config", "TournamentTracker");

        public static ModState State(string? gameDir)
        {
            var state = new ModState { GameFound = GameLocator.IsGameFolder(gameDir) };
            if (!state.GameFound) return state;
            state.Installed = File.Exists(Path.Combine(gameDir!, "BepInEx", "plugins", "TournamentTracker.dll"));
            state.GameArch = GameArch(gameDir!);
            string loader = Path.Combine(gameDir!, "winhttp.dll");
            if (File.Exists(loader)) state.LoaderArch = Machine(loader);
            try
            {
                string marker = Path.Combine(DataDir(gameDir!), Marker);
                if (File.Exists(marker)) state.InstalledVersion = JsonDocument.Parse(File.ReadAllText(marker)).RootElement.GetProperty("tag").GetString();
            }
            catch (Exception) { }
            // The mod's own version wins: the marker can be missing or behind (a copy installed by hand).
            try
            {
                if (state.Installed && System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(gameDir!, "BepInEx", "plugins", "TournamentTracker.dll")).Version is Version v && v.Major + v.Minor + v.Build > 0)
                    state.InstalledVersion = "v" + v.ToString(3);
            }
            catch (Exception) { }
            return state;
        }

        /// <summary>What Among Us.exe in this folder is built for ("x86" or "x64"), or null.</summary>
        public static string? GameArch(string gameDir) => Machine(Path.Combine(gameDir, GameLocator.Exe));

        /// <summary>
        /// Reads an exe's or DLL's machine type from its PE header: 0x14C is 32-bit x86, 0x8664
        /// is x64. Null when the file is missing, isn't a PE file, or is something else.
        /// </summary>
        public static string? Machine(string file)
        {
            try
            {
                using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new BinaryReader(f);
                if (f.Length < 0x40 || r.ReadUInt16() != 0x5A4D) return null;          // "MZ"
                f.Seek(0x3C, SeekOrigin.Begin);
                int pe = r.ReadInt32();
                if (pe <= 0 || pe + 6 > f.Length) return null;
                f.Seek(pe, SeekOrigin.Begin);
                if (r.ReadUInt32() != 0x00004550) return null;                          // "PE\0\0"
                return r.ReadUInt16() switch { 0x14C => "x86", 0x8664 => "x64", _ => null };
            }
            catch (Exception) { return null; }
        }

        public async Task<Release?> LatestAsync()
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
                request.Headers.UserAgent.ParseAdd("TournamentTracker-App");
                using var response = await _http.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
                var assets = json.GetProperty("assets").EnumerateArray()
                    .Where(a => a.TryGetProperty("name", out _) && a.TryGetProperty("browser_download_url", out _))
                    .ToDictionary(a => a.GetProperty("name").GetString() ?? "", a => a.GetProperty("browser_download_url").GetString() ?? "");
                var bundles = new Dictionary<string, string>();
                foreach (var arch in new[] { "x86", "x64" })
                    if (assets.TryGetValue(BundleName(arch), out var url)) bundles[arch] = url;
                // Releases from before 64-bit Among Us only have the x86 bundle, under its old name.
                if (!bundles.ContainsKey("x86") && assets.TryGetValue(LegacyBundleName, out var legacy)) bundles["x86"] = legacy;
                if (bundles.Count == 0) return null;
                return new Release
                {
                    Tag = json.GetProperty("tag_name").GetString() ?? "",
                    BundleUrls = bundles,
                    AppUrl = assets.TryGetValue(AppName, out var app) ? app : null,
                };
            }
            catch (Exception) { return null; }
        }

        private static string Bits(string? arch) => arch == "x64" ? "64-bit" : arch == "x86" ? "32-bit" : "unknown";

        public static bool GameRunning() =>
            Process.GetProcesses().Any(p => { try { return p.ProcessName == "Among Us"; } catch (Exception) { return false; } });

        /// <summary>
        /// Downloads the release's bundle and copies it over the game folder. Settings, links,
        /// stats and the setup code live in BepInEx/config, which the bundle never contains.
        /// </summary>
        public async Task<string> InstallAsync(string gameDir, Release release, Action<string> progress)
        {
            if (!GameLocator.IsGameFolder(gameDir)) return "That folder doesn't have Among Us in it.";
            if (GameRunning()) return "Close Among Us first: its files are in use while it runs.";
            // BepInEx's loader has to match the game: 32-bit Among Us needs the x86 build, 64-bit the x64 one.
            string arch = GameArch(gameDir) ?? DefaultArch;
            string? bundleUrl = release.BundleFor(arch);
            if (bundleUrl == null)
                return $"{release.Tag} has no mod download for {(arch == "x64" ? "64-bit" : "32-bit")} Among Us yet. Update The Button, or wait for the next release.";
            RemoveSetAside(gameDir);
            string temp = Path.Combine(Path.GetTempPath(), "tt-install-" + Guid.NewGuid().ToString("N"));
            try
            {
                progress($"Downloading {release.Tag} ({(arch == "x64" ? "64-bit" : "32-bit")})…");
                Directory.CreateDirectory(temp);
                string zip = Path.Combine(temp, BundleName(arch));
                var request = new HttpRequestMessage(HttpMethod.Get, bundleUrl);
                request.Headers.UserAgent.ParseAdd("TournamentTracker-App");
                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return $"The download failed ({(int)response.StatusCode}). Check your internet connection and try again.";
                    using var file = File.Create(zip);
                    await response.Content.CopyToAsync(file).ConfigureAwait(false);
                }
                progress("Installing…");
                string bundle = Path.Combine(temp, "bundle");
                ZipFile.ExtractToDirectory(zip, bundle);
                CopyOver(bundle, gameDir);
                Directory.CreateDirectory(DataDir(gameDir));
                File.WriteAllText(Path.Combine(DataDir(gameDir), Marker), JsonSerializer.Serialize(new { tag = release.Tag, at = DateTime.UtcNow }));
                var state = State(gameDir);
                if (!state.Installed || state.LoaderArch == null) return "The files didn't all land. Try again, or run the app as administrator if the game is in Program Files.";
                if (!state.LoaderMatchesGame)
                    return $"The mod loader that was downloaded is {Bits(state.LoaderArch)} but Among Us is {Bits(state.GameArch)}. Try again; if it keeps happening, the release was packaged wrong.";
                return "";
            }
            catch (FileLockedException e)
            {
                return e.Message;
            }
            catch (UnauthorizedAccessException)
            {
                return "Windows wouldn't let the app write to the game folder. Close Among Us, or run the app as administrator.";
            }
            catch (Exception e)
            {
                return "Install failed: " + e.Message;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch (Exception) { }
            }
        }

        /// <summary>What a file in use is renamed to so the new one can go in its place.</summary>
        public const string SetAsideSuffix = ".tt-old";

        /// <summary>
        /// Copies the bundle over the game folder. Files that are already the same are left alone
        /// (so a mod update only touches what changed), a file that's briefly busy (antivirus,
        /// the game still closing) is tried again, and one that stays in use is renamed out of
        /// the way (Windows allows that even for a loaded DLL) and removed on the next install.
        /// </summary>
        public static void CopyOver(string from, string to) => CopyOver(from, to, TimeSpan.FromMilliseconds(400), 6);

        public static void CopyOver(string from, string to, TimeSpan wait, int tries)
        {
            foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(from, file);
                string target = Path.Combine(to, relative);
                if (SameFile(file, target)) continue;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Copy(file, target, overwrite: true);
                        break;
                    }
                    catch (IOException) when (attempt < tries)
                    {
                        System.Threading.Thread.Sleep(wait);
                    }
                    catch (IOException)
                    {
                        if (SetAside(target))
                        {
                            File.Copy(file, target, overwrite: true);
                            break;
                        }
                        var users = FileLocks.Users(target);
                        throw new FileLockedException(relative, users);
                    }
                }
            }
        }

        private static bool SameFile(string a, string b)
        {
            try
            {
                var fa = new FileInfo(a);
                var fb = new FileInfo(b);
                if (!fb.Exists || fa.Length != fb.Length) return false;
                using var sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var ba = new byte[81920];
                var bb = new byte[81920];
                while (true)
                {
                    int na = sa.Read(ba, 0, ba.Length);
                    int nb = sb.Read(bb, 0, bb.Length);
                    if (na != nb) return false;
                    if (na == 0) return true;
                    if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
                }
            }
            catch (Exception) { return false; }
        }

        private static bool SetAside(string target)
        {
            try
            {
                string aside = target + SetAsideSuffix;
                if (File.Exists(aside))
                {
                    try { File.Delete(aside); }
                    catch (Exception) { aside = target + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + SetAsideSuffix; }
                }
                File.Move(target, aside);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Removes files an earlier install had to rename out of the way.</summary>
        public static void RemoveSetAside(string gameDir)
        {
            foreach (var root in new[] { gameDir, Path.Combine(gameDir, "BepInEx") })
            {
                if (!Directory.Exists(root)) continue;
                var option = root == gameDir ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*" + SetAsideSuffix, option).ToList(); }
                catch (Exception) { continue; }
                foreach (var f in files)
                    try { File.Delete(f); } catch (Exception) { }
            }
        }
    }

    /// <summary>A file in the game folder that something else has open, so the mod can't be installed.</summary>
    public sealed class FileLockedException : IOException
    {
        public FileLockedException(string file, IReadOnlyList<string> users)
            : base(users.Count > 0
                ? $"{file} in the Among Us folder is in use by {string.Join(", ", users)}. Close {(users.Count == 1 ? "it" : "them")} (or restart your PC) and try again."
                : $"{file} in the Among Us folder is in use by another program. Make sure Among Us is fully closed (check Task Manager), or restart your PC, and try again.")
        { }
    }
}
