using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace TournamentTracker.App
{
    /// <summary>
    /// Puts a new version of an app (TheButton.exe, RedAlert.exe) in place of the running one.
    /// Windows won't overwrite a running program but lets it be renamed, so the old one becomes
    /// *.old.exe (deleted on the next start) and the new one takes its name. The new version runs
    /// from the next start.
    /// </summary>
    public static class AppUpdater
    {
        public static string OldPath(string exePath) => Path.ChangeExtension(exePath, ".old.exe");

        /// <summary>Downloads and swaps in the new version. Returns "" when done, or what went wrong.</summary>
        public static async Task<string> InstallAsync(HttpClient http, string url, string exePath, string appName = "The Button")
        {
            string incoming = exePath + ".download";
            try
            {
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return $"Couldn't download the new version (GitHub said {(int)response.StatusCode}).";
                    using var file = File.Create(incoming);
                    await response.Content.CopyToAsync(file).ConfigureAwait(false);
                }
                // A Windows program starts with "MZ"; anything else (an error page) isn't one.
                var head = new byte[2];
                using (var check = File.OpenRead(incoming))
                    if (check.Length < 1024 * 1024 || check.Read(head, 0, 2) < 2 || head[0] != 'M' || head[1] != 'Z')
                        return $"The download wasn't a complete copy of {appName}. It'll try again later.";

                string old = OldPath(exePath);
                if (File.Exists(old)) File.Delete(old);
                File.Move(exePath, old);
                try { File.Move(incoming, exePath); }
                catch (Exception) { File.Move(old, exePath); throw; }
                return "";
            }
            catch (Exception e)
            {
                return "Couldn't install the new version: " + e.Message;
            }
            finally
            {
                try { if (File.Exists(incoming)) File.Delete(incoming); } catch (Exception) { }
            }
        }

        /// <summary>Removes the previous version left behind by an update (call at start).</summary>
        public static void CleanUp(string? exePath)
        {
            if (exePath == null) return;
            try { var old = OldPath(exePath); if (File.Exists(old)) File.Delete(old); } catch (Exception) { }
        }
    }
}
