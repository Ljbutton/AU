using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>Finding a clip Source Record has just written, by its file (when it sends no event).</summary>
    public static class ClipFiles
    {
        public static TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// The first file in <paramref name="folder"/> named <paramref name="prefix"/>… written after
        /// <paramref name="since"/> (UTC), once its size has stopped changing; null if none by <paramref name="timeout"/>.
        /// </summary>
        public static async Task<string?> WaitForNewAsync(string folder, string prefix, DateTime since, TimeSpan timeout, CancellationToken ct = default)
        {
            var until = DateTime.UtcNow + timeout;
            // File times can be a little behind the clock that stamped the request.
            var after = since.AddSeconds(-2);
            string? seen = null;
            long size = -1;
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                try
                {
                    var newest = Directory.Exists(folder)
                        ? new DirectoryInfo(folder).EnumerateFiles(prefix + "*")
                            .Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.LastWriteTimeUtc >= after)
                            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                        : null;
                    if (newest != null)
                    {
                        newest.Refresh();
                        if (newest.FullName == seen && newest.Length > 0 && newest.Length == size) return newest.FullName;
                        seen = newest.FullName;
                        size = newest.Length;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                try { await Task.Delay(Poll, ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }
            }
            return null;
        }
    }
}
