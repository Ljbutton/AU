using System;
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

        public static void CopyOver(string from, string to)
        {
            foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }
}
