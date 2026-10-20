using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Red Alert's own files: its small JSON ones (settings, saved picks, the montage archive) read and written
    /// whole (never half a file: see SafeFile), and clips gone.
    /// </summary>
    internal static class Files
    {
        /// <summary>Where a file that couldn't be read is reported (red-alert.log, once the app's server is up).</summary>
        public static Action<string>? Warn { get; set; }

        /// <summary>
        /// The file's contents; null when there's no path, no file, or it can't be read. One that's there but
        /// broken is kept as name.corrupt-… first, so saving the defaults over it later loses nothing.
        /// </summary>
        public static T? ReadJson<T>(string? path, JsonSerializerOptions? options = null) where T : class
        {
            try { return path == null ? null : SafeFile.ReadOrRecover(path, text => JsonSerializer.Deserialize<T>(text, options), m => Warn?.Invoke(m)); }
            catch (Exception) { return null; }
        }

        /// <summary>Writes the file (its folder made first), whole or not at all. Throws when it can't.</summary>
        public static void WriteJson<T>(string path, T value, JsonSerializerOptions? options = null) =>
            SafeFile.WriteAllText(path, JsonSerializer.Serialize(value, options));

        /// <summary>A file gone, if it's there (nothing said when it can't be).</summary>
        public static void Delete(string? path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }

    /// <summary>
    /// A file people edit by hand (sponsors.json, roster.csv…): made when it's missing, and read again
    /// only when it changes, looked at every 2 s at most (unless forced).
    /// </summary>
    internal sealed class WatchedFile
    {
        private readonly Action<WatchedFile> _create;
        private DateTime _stamp, _nextCheck;
        private bool _broken;
        public string Path { get; }

        /// <param name="create">Makes the file when it's missing (an example, or the defaults).</param>
        public WatchedFile(string path, Action<WatchedFile> create)
        {
            Path = path;
            _create = create;
        }

        /// <summary>The file's text when it changed since it was last read, else null. Throws when it can't be read.</summary>
        public string? Changed(bool force)
        {
            var now = DateTime.UtcNow;
            if (!force && now < _nextCheck) return null;
            _nextCheck = now.AddSeconds(2);
            if (!File.Exists(Path)) _create(this);
            var stamp = File.GetLastWriteTimeUtc(Path);
            if (stamp == _stamp) return null;
            // Read without blocking an editor saving it (tried again while it's busy); a read that fails is tried again next time.
            string text = SafeFile.ReadAllText(Path);
            _stamp = stamp;
            _broken = false;
            return text;
        }

        /// <summary>What was just read couldn't be used (a mistake made editing it): it's kept as name.corrupt-… before the app next writes over it.</summary>
        public void Broken() => _broken = true;

        /// <summary>Writes the file (its folder made first), whole or not at all. <paramref name="inUse"/>: what's written is what's in use already, so it isn't read back.</summary>
        public void Write(string text, Encoding? encoding = null, bool inUse = false)
        {
            if (_broken) SafeFile.KeepCorrupt(Path);
            _broken = false;
            SafeFile.WriteAllText(Path, text, encoding ?? new UTF8Encoding(false));
            if (inUse) _stamp = File.GetLastWriteTimeUtc(Path);
        }
    }
}
