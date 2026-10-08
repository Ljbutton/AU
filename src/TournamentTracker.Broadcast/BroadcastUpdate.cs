using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Settings → Updates: Red Alert looks for a new version of itself (its releases are tagged
    /// broadcast-v…) but only installs one when the caster presses Update, never by itself, so
    /// nothing changes mid-broadcast. The new version runs from the next start (Restart).
    /// </summary>
    public sealed partial class BroadcastServer
    {
        public const string ExeName = "RedAlert.exe";
        /// <summary>Its releases on GitHub are tagged broadcast-v0.1.0 and so on (The Button's are v0.1.23…).</summary>
        public const string TagPrefix = "broadcast-v";

        private (string Tag, string Url)? _latestRelease;
        private DateTime _releaseChecked = DateTime.MinValue;
        private volatile bool _checking, _updating;
        private volatile string? _updateReady, _updateError;

        private bool UpdateAvailable => _env.ExePath != null && _latestRelease is { } l && _updateReady == null && Newer(l.Tag, _env.Version);

        /// <summary>"broadcast-v0.2.0" is newer than "0.1.0" (or than nothing recorded).</summary>
        public static bool Newer(string tag, string? installed)
        {
            static Version? V(string? t) => Version.TryParse((t ?? "").Replace(TagPrefix, "").TrimStart('v', 'V'), out var v) ? v : null;
            var l = V(tag);
            var i = V(installed);
            return l != null && (i == null || l > i);
        }

        /// <summary>The newest broadcast release with a RedAlert.exe, from the repository's release list.</summary>
        public static (string Tag, string Url)? PickRelease(JsonElement releases)
        {
            foreach (var r in releases.EnumerateArray())
            {
                string tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                if (!tag.StartsWith(TagPrefix, StringComparison.Ordinal) || (r.TryGetProperty("draft", out var d) && d.GetBoolean())) continue;
                if (!r.TryGetProperty("assets", out var assets)) continue;
                foreach (var a in assets.EnumerateArray())
                    if (a.TryGetProperty("name", out var n) && n.GetString() == ExeName && a.TryGetProperty("browser_download_url", out var u))
                        return (tag, u.GetString()!);
            }
            return null;
        }

        private async Task CheckForUpdateAsync()
        {
            if (_checking) return;
            _checking = true;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{GitHubRepo.Name}/releases?per_page=30");
                req.Headers.TryAddWithoutValidation("User-Agent", "RedAlert");
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                using var r = await _http.SendAsync(req).ConfigureAwait(false);
                if (!r.IsSuccessStatusCode) { _updateError = $"Couldn't check for updates (GitHub said {(int)r.StatusCode})."; return; }
                _latestRelease = PickRelease(JsonDocument.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement) ?? _latestRelease;
                _updateError = null;
            }
            catch (Exception) { _updateError = "Couldn't reach GitHub to check for updates."; }
            finally { _checking = false; _releaseChecked = DateTime.UtcNow; }
        }

        private async Task InstallUpdateAsync()
        {
            var release = _latestRelease;
            string? exe = _env.ExePath;
            if (_updating || release == null || exe == null) return;
            _updating = true;
            _updateError = null;
            try
            {
                string error = await AppUpdater.InstallAsync(_http, release.Value.Url, exe, "Red Alert").ConfigureAwait(false);
                if (error.Length > 0) _updateError = error;
                else _updateReady = release.Value.Tag;
            }
            finally { _updating = false; }
        }

        /// <summary>Update pressed (or "Check now" with check=true).</summary>
        private object StartUpdate(bool checkOnly)
        {
            if (_env.ExePath == null) return new { ok = false, message = "This copy of Red Alert can't update itself." };
            if (checkOnly || _latestRelease == null)
            {
                _ = Task.Run(CheckForUpdateAsync);
                return new { ok = true, message = "Checking for a new version…" };
            }
            if (_updateReady != null) return new { ok = false, message = $"{Plain(_updateReady)} is ready: restart Red Alert to use it." };
            if (!UpdateAvailable) return new { ok = false, message = "Red Alert is up to date." };
            _ = Task.Run(InstallUpdateAsync);
            return new { ok = true, message = $"Downloading Red Alert {Plain(_latestRelease.Value.Tag)}…" };
        }

        private static string Plain(string tag) => tag.Replace(TagPrefix, "v");

        /// <summary>For Settings (checked every 30 minutes while Red Alert is open; never installed by itself).</summary>
        private object UpdateState()
        {
            if (_env.ExePath != null && DateTime.UtcNow - _releaseChecked > TimeSpan.FromMinutes(30))
            {
                _releaseChecked = DateTime.UtcNow;
                _ = Task.Run(CheckForUpdateAsync);
            }
            return new
            {
                Supported = _env.ExePath != null,
                Available = UpdateAvailable ? Plain(_latestRelease!.Value.Tag) : null,
                Ready = _updateReady == null ? null : Plain(_updateReady),
                Busy = _updating,
                Checking = _checking,
                Error = _updateError,
            };
        }
    }
}
