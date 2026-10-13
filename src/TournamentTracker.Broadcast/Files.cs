using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>Red Alert's own files: its small JSON ones (settings, saved picks, the montage archive) read and written whole, and clips gone.</summary>
    internal static class Files
    {
        /// <summary>The file's contents; null when there's no path, no file, or it can't be read.</summary>
        public static T? ReadJson<T>(string? path, JsonSerializerOptions? options = null) where T : class
        {
            try { return path != null && File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), options) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Writes the file (its folder made first). Throws when it can't.</summary>
        public static void WriteJson<T>(string path, T value, JsonSerializerOptions? options = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, options));
        }

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
            _stamp = stamp;
            return File.ReadAllText(Path);
        }

        /// <summary>Writes the file (its folder made first). <paramref name="inUse"/>: what's written is what's in use already, so it isn't read back.</summary>
        public void Write(string text, Encoding? encoding = null, bool inUse = false)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, text, encoding ?? new UTF8Encoding(false));
            if (inUse) _stamp = File.GetLastWriteTimeUtc(Path);
        }
    }
}
