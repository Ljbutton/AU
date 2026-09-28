using System;

namespace TournamentTracker
{
    public interface ILog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    /// <summary>Forwards to three delegates, so the plugin can hand over BepInEx's logger.</summary>
    public sealed class DelegateLog : ILog
    {
        private readonly Action<string> _info, _warn, _error;

        public DelegateLog(Action<string> info, Action<string> warn, Action<string> error)
        {
            _info = info;
            _warn = warn;
            _error = error;
        }

        public void Info(string message) => _info(message);
        public void Warn(string message) => _warn(message);
        public void Error(string message) => _error(message);
    }
}
