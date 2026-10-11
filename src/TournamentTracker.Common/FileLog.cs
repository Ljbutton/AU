using System;
using System.IO;

namespace TournamentTracker.App
{
    /// <summary>
    /// A plain log file next to an app's settings (red-alert.log, the-button.log): one line per
    /// message with the time. Kept small: when it passes 1 MB it starts again.
    /// </summary>
    public sealed class FileLog : ILog
    {
        private const long MaxBytes = 1_000_000;
        private readonly string? _path;
        private readonly object _lock = new object();

        /// <param name="folder">Where the app keeps its settings; null logs nowhere.</param>
        public FileLog(string? folder, string fileName)
        {
            if (string.IsNullOrEmpty(folder)) return;
            try { Directory.CreateDirectory(folder); _path = Path.Combine(folder, fileName); }
            catch (Exception) { _path = null; }
        }

        public string? FilePath => _path;

        public void Info(string message) => Write("INFO", message);
        public void Warn(string message) => Write("WARN", message);
        public void Error(string message) => Write("ERROR", message);

        private void Write(string level, string message)
        {
            if (_path == null) return;
            lock (_lock)
            {
                try
                {
                    var file = new FileInfo(_path);
                    if (file.Exists && file.Length > MaxBytes) file.Delete();
                    File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
                }
                catch (Exception) { }
            }
        }
    }
}
