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
        /// <summary>BepInEx's loader is the 32-bit one Among Us needs (false: 64-bit, won't load; null: not there).</summary>
        public bool? LoaderIs32Bit { get; set; }
        public bool NeedsRepair => Installed && LoaderIs32Bit == false;
    }

    public sealed class Release
    {
        public string Tag { get; set; } = "";
        public string BundleUrl { get; set; } = "";
        /// <summary>The Button itself (TheButton.exe) in this release, if it has one.</summary>
        public string? AppUrl { get; set; }
    }

    /// <summary>Installs and updates the mod (with its BepInEx loader) in the Among Us folder.</summary>
    public sealed class ModInstaller
    {
        public const string Repo = "Ljbutton/AU";
        public const string BundleName = "TournamentTracker-Full.zip";
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
            string loader = Path.Combine(gameDir!, "winhttp.dll");
            if (File.Exists(loader)) state.LoaderIs32Bit = Is32Bit(loader);
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

        /// <summary>Reads a DLL's machine type from its PE header: 0x14C is 32-bit x86.</summary>
        public static bool? Is32Bit(string dll)
        {
            try
            {
                using var f = File.OpenRead(dll);
                using var r = new BinaryReader(f);
                f.Seek(0x3C, SeekOrigin.Begin);
                int pe = r.ReadInt32();
                f.Seek(pe + 4, SeekOrigin.Begin);
                ushort machine = r.ReadUInt16();
                return machine == 0x14C;
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
                var asset = json.GetProperty("assets").EnumerateArray()
                    .FirstOrDefault(a => a.GetProperty("name").GetString() == BundleName);
                if (asset.ValueKind != JsonValueKind.Object) return null;
                var app = json.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == AppName);
                return new Release
                {
                    Tag = json.GetProperty("tag_name").GetString() ?? "",
                    BundleUrl = asset.GetProperty("browser_download_url").GetString() ?? "",
                    AppUrl = app.ValueKind == JsonValueKind.Object ? app.GetProperty("browser_download_url").GetString() : null,
                };
            }
            catch (Exception) { return null; }
        }

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
            RemoveSetAside(gameDir);
            string temp = Path.Combine(Path.GetTempPath(), "tt-install-" + Guid.NewGuid().ToString("N"));
            try
            {
                progress("Downloading " + release.Tag + "…");
                Directory.CreateDirectory(temp);
                string zip = Path.Combine(temp, BundleName);
                var request = new HttpRequestMessage(HttpMethod.Get, release.BundleUrl);
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
                if (!state.Installed || state.LoaderIs32Bit != true) return "The files didn't all land. Try again, or run the app as administrator if the game is in Program Files.";
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
