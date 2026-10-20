using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace TournamentTracker.App
{
    /// <summary>
    /// A file attached to a GitHub release, as the release JSON lists it: where to download it, its
    /// size, and its SHA-256 ("digest": "sha256:…"). Every update (The Button, the mod, Red Alert)
    /// is downloaded through <see cref="DownloadAsync"/>, which only keeps a file that matches.
    /// </summary>
    public sealed class ReleaseAsset
    {
        /// <summary>What the apps show and log when a download isn't the file the release lists.</summary>
        public const string Mismatch = "The download didn't match the release (it may be incomplete). Nothing was changed; try again later.";

        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        /// <summary>The size the release lists, in bytes; null when it doesn't say.</summary>
        public long? Size { get; set; }
        /// <summary>The SHA-256 the release lists, in lower-case hex; null when it lists none (older releases).</summary>
        public string? Sha256 { get; set; }

        /// <summary>One entry of a release's "assets"; null when it has no name or download link.</summary>
        public static ReleaseAsset? From(JsonElement asset)
        {
            if (asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String) return null;
            return new ReleaseAsset
            {
                Name = name.GetString()!,
                Url = url.GetString()!,
                Size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var bytes) && bytes >= 0 ? bytes : null,
                Sha256 = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? Sha256Of(d.GetString()!) : null,
            };
        }

        /// <summary>"sha256:&lt;64 hex digits&gt;" gives the hex in lower case; anything else (no digest, or another kind) gives null.</summary>
        public static string? Sha256Of(string digest)
        {
            const string prefix = "sha256:";
            if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            string hex = digest.Substring(prefix.Length).Trim().ToLowerInvariant();
            return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
        }

        /// <summary>
        /// Downloads the file to <paramref name="path"/>, working out its SHA-256 as it arrives, and
        /// keeps it only when its size and SHA-256 are the ones the release lists. Returns "" when the
        /// file is in place. Otherwise the file is deleted and the reason returned:
        /// <paramref name="refused"/>'s message for GitHub's status code, or <see cref="Mismatch"/>.
        /// A release that lists no SHA-256 (older ones) is still downloaded, and the log says so.
        /// </summary>
        public async Task<string> DownloadAsync(HttpClient http, string path, ILog log, Func<int, string> refused)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Url);
                request.Headers.UserAgent.ParseAdd("TournamentTracker-App");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return refused((int)response.StatusCode);
                long length = 0;
                string sha;
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var from = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var file = File.Create(path))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await from.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, n);
                        await file.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                        length += n;
                        if (Size is long max && length > max) break;      // already more than the release lists
                    }
                    sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
                string? wrong = Size is long size && length != size ? $"{length} bytes arrived, the release lists {size}"
                    : Sha256 != null && sha != Sha256 ? $"its SHA-256 is {sha}, the release lists {Sha256}"
                    : null;
                if (wrong != null)
                {
                    Delete(path);
                    log.Warn($"Download of {Name} refused: {wrong}. Deleted it; nothing was changed.");
                    return Mismatch;
                }
                log.Info(Sha256 == null
                    ? $"Downloaded {Name} ({length} bytes): the release has no checksum to check{(Size == null ? "" : ", so only its size was checked")}."
                    : $"Downloaded {Name} ({length} bytes): the size and SHA-256 match the release.");
                return "";
            }
            catch (Exception)
            {
                Delete(path);
                throw;
            }
        }

        private static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }
}
