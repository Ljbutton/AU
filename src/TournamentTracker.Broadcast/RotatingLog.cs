using System;
using System.IO;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Red Alert's log (red-alert.log next to its settings): one line per message with the time, the
    /// same as The Button's. When it passes 1 MB it moves to red-alert.log.1 (the one before that goes)
    /// and a new one starts, so what led up to a problem mid-event is still there to read.
    /// </summary>
    public sealed class RotatingLog : ILog
    {
        public const long MaxBytes = 1_000_000;
        private readonly string? _path;
        private readonly long _max;
        private readonly object _lock = new object();

        /// <param name="folder">Where the app keeps its settings; null logs nowhere.</param>
        public RotatingLog(string? folder, string fileName, long maxBytes = MaxBytes)
        {
            _max = maxBytes;
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
                    if (file.Exists && file.Length > _max) File.Move(_path, _path + ".1", overwrite: true);
                    File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
                }
                catch (Exception) { }
            }
        }
    }
}
