using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace TournamentTracker
{
    /// <summary>
    /// Saving so a crash, a power cut or a full disk part-way through never leaves a file wiped or
    /// half-written: the new contents go to a side file next to it (name.tmp), flushed to the disk,
    /// and only then take the old file's place in one step. Until then the old file is untouched. A
    /// side file left by a crash is never read: the next save writes over it and the next load
    /// removes it. A file that's busy (another program reading it, antivirus) is tried again briefly.
    /// Loading keeps a file that can't be read as name.corrupt-&lt;UTC time&gt; before anything is
    /// written over it, and says so. Used by the mod and by both apps.
    /// </summary>
    public static class SafeFile
    {
        /// <summary>The side file a save is written to first: next to the file, its name plus this.</summary>
        public const string TempSuffix = ".tmp";

        /// <summary>A copy of a file that couldn't be read: its name plus this and the time (UTC, yyyyMMddHHmmss).</summary>
        public const string CorruptMark = ".corrupt-";

        /// <summary>A side file this old is left over from a crash (a save in progress is never this slow).</summary>
        public static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(1);

        private const int Tries = 5;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        // Saves of the same file in this program take turns (several threads save some files); different files never wait on each other.
        private static readonly ConcurrentDictionary<string, object> Locks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static object LockFor(string fullPath) => Locks.GetOrAdd(fullPath, _ => new object());

        /// <summary>The first wait before trying a busy file again (doubling each time; five tries in all).</summary>
        internal static int RetryMs = 15;

        /// <summary>Like File.WriteAllText (UTF-8 without a byte order mark), but safely.</summary>
        public static void WriteAllText(string path, string text) => WriteAllText(path, text, Utf8);

        /// <summary>Like File.WriteAllText with an encoding (its byte order mark first, if it has one), but safely.</summary>
        public static void WriteAllText(string path, string text, Encoding encoding)
        {
            byte[] preamble = encoding.GetPreamble();
            byte[] body = encoding.GetBytes(text);
            Write(path, s =>
            {
                if (preamble.Length > 0) s.Write(preamble, 0, preamble.Length);
                s.Write(body, 0, body.Length);
            });
        }

        /// <summary>Like File.WriteAllBytes, but safely.</summary>
        public static void WriteAllBytes(string path, byte[] bytes) => Write(path, s => s.Write(bytes, 0, bytes.Length));

        /// <summary>
        /// Writes the file through <paramref name="write"/> (its folder made first): into the side file,
        /// flushed to the disk, then put in the old one's place. When anything fails (the disk is full,
        /// <paramref name="write"/> throws) the old file is as it was, the side file goes, and it throws.
        /// </summary>
        public static void Write(string path, Action<Stream> write)
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string temp = full + TempSuffix;
            lock (LockFor(full))
            {
                bool made = false;
                try
                {
                    using (var f = Retry(_ => new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)))
                    {
                        made = true;
                        write(f);
                        f.Flush(true);
                    }
                    Retry(attempt => { Swap(temp, full, attempt == Tries); return true; });
                }
                catch (Exception)
                {
                    if (made) try { File.Delete(temp); } catch (Exception) { }
                    throw;
                }
            }
        }

        /// <summary>The side file takes the file's place: File.Replace when there's an old one (File.Move as the last try, for drives without it).</summary>
        private static void Swap(string temp, string path, bool lastTry)
        {
            if (!File.Exists(path) || lastTry) { File.Move(temp, path, true); return; }
            try { File.Replace(temp, path, null, true); }
            catch (PlatformNotSupportedException) { File.Move(temp, path, true); }
        }

        /// <summary>A busy file (in use by another program, antivirus) is tried again a few times, waiting a little longer each time.</summary>
        private static T Retry<T>(Func<int, T> attempt)
        {
            for (int i = 1; ; i++)
            {
                try { return attempt(i); }
                catch (Exception e) when (i < Tries && Busy(e))
                {
                    Thread.Sleep(RetryMs << (i - 1));
                }
            }
        }

        // Only a file another program has open is worth waiting for (a sharing or lock violation; antivirus can
        // also briefly deny access). A full disk, a missing drive or a read-only file fails at once.
        private const int SharingViolation = 32, LockViolation = 33;

        private static bool Busy(Exception e) =>
            e is UnauthorizedAccessException
            || e is IOException && !(e is FileNotFoundException) && !(e is DirectoryNotFoundException) && !(e is PathTooLongException)
               && (!OperatingSystem.IsWindows() || (e.HResult & 0xFFFF) is SharingViolation or LockViolation);   // elsewhere (tests) the codes differ

        /// <summary>The file's text, read without stopping another program from saving it meanwhile (tried again while it's busy).</summary>
        public static string ReadAllText(string path) => Retry(_ =>
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f, Utf8, detectEncodingFromByteOrderMarks: true);
            return r.ReadToEnd();
        });

        /// <summary>The file's bytes, read the same way as <see cref="ReadAllText"/>.</summary>
        public static byte[] ReadAllBytes(string path) => Retry(_ =>
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var m = new MemoryStream();
            f.CopyTo(m);
            return m.ToArray();
        });

        /// <summary>
        /// Reads and parses the file. Null when there's no file (or <paramref name="parse"/> gives null).
        /// When it's there but can't be parsed (half-written, empty, edited wrong), a copy is kept as
        /// name.corrupt-&lt;time&gt; (once per broken version), <paramref name="warn"/> is told, and it gives
        /// null, so whatever is saved next never silently replaces the only copy. A file that can't be
        /// opened at all (in use for longer than the tries) throws instead: it's still there, as it was.
        /// </summary>
        public static T? ReadOrRecover<T>(string path, Func<string, T?> parse, Action<string>? warn = null) where T : class
        {
            if (string.IsNullOrEmpty(path)) return null;
            RemoveLeftover(path);
            string text;
            try
            {
                if (!File.Exists(path)) return null;
                text = ReadAllText(path);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            try
            {
                if (text.Trim().Length == 0) throw new InvalidDataException("the file is empty");
                return parse(text);
            }
            catch (Exception e)
            {
                string? kept = KeepCorrupt(path);
                warn?.Invoke($"{Path.GetFileName(path)} couldn't be read ({e.Message}). "
                    + (kept != null ? $"A copy is kept as {Path.GetFileName(kept)}." : "It couldn't be copied aside."));
                return null;
            }
        }

        /// <summary>
        /// Keeps a copy of a file that couldn't be read, as name.corrupt-&lt;UTC time&gt; next to it, and gives
        /// its path. A copy of the same contents made earlier is used again rather than made twice.
        /// Null when there's no file or it can't be copied.
        /// </summary>
        public static string? KeepCorrupt(string path, DateTime? utcNow = null)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string dir = Path.GetDirectoryName(Path.GetFullPath(path))!, name = Path.GetFileName(path);
                byte[] now = ReadAllBytes(path);
                var last = Directory.GetFiles(dir, name + CorruptMark + "*").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
                if (last != null && new FileInfo(last).Length == now.Length && ReadAllBytes(last).AsSpan().SequenceEqual(now)) return last;
                string stamp = (utcNow ?? DateTime.UtcNow).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                string to = Path.Combine(dir, name + CorruptMark + stamp);
                for (int i = 2; File.Exists(to); i++) to = Path.Combine(dir, name + CorruptMark + stamp + "-" + i);
                File.WriteAllBytes(to, now);
                return to;
            }
            catch (Exception) { return null; }
        }

        /// <summary>When a copy of a broken file was kept (from its name), or null when it isn't one.</summary>
        public static DateTime? CorruptAt(string fileName)
        {
            int at = fileName.LastIndexOf(CorruptMark, StringComparison.Ordinal);
            if (at < 0) return null;
            string stamp = fileName.Substring(at + CorruptMark.Length);
            if (stamp.Length > 14) stamp = stamp.Substring(0, 14);
            return DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTime?)null;
        }

        /// <summary>The name of a file saving leaves next to the real one for a moment (a side file), or of a kept broken copy.</summary>
        public static bool IsSideFile(string fileName) =>
            fileName.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase) || fileName.IndexOf(CorruptMark, StringComparison.Ordinal) >= 0;

        /// <summary>Removes the file's side file when a crash left it there (one older than <see cref="LeftoverAge"/>).</summary>
        public static void RemoveLeftover(string path)
        {
            try
            {
                var temp = new FileInfo(path + TempSuffix);
                if (temp.Exists && DateTime.UtcNow - temp.LastWriteTimeUtc > LeftoverAge) temp.Delete();
            }
            catch (Exception) { }
        }

        /// <summary>Removes the side files crashes left in a folder (not its subfolders).</summary>
        public static void RemoveLeftovers(string folder)
        {
            try
            {
                var dir = new DirectoryInfo(folder);
                if (!dir.Exists) return;
                // The pattern also matches longer extensions on Windows (".tmpl"): only exact side files go.
                foreach (var f in dir.EnumerateFiles("*" + TempSuffix))
                    if (f.Name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - f.LastWriteTimeUtc > LeftoverAge)
                        try { f.Delete(); } catch (Exception) { }
            }
            catch (Exception) { }
        }
    }
}
