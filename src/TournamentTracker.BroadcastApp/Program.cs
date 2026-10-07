using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows.Forms;
using TournamentTracker.App.Broadcast;

namespace TournamentTracker.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            NativeMethods.Title = "Red Alert";
            // An install from before the rename may still run as TTBroadcast.exe.
            NativeMethods.ProcessName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "RedAlert";
            // One copy at a time: a second start just brings the first to the front.
            using var single = new Mutex(true, "RedAlert.App", out bool first);
            if (!first)
            {
                NativeMethods.ShowExisting();
                return;
            }

            ApplicationConfiguration.Initialize();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Path.Combine(local, "RedAlert");
            string settings = Path.Combine(appData, "settings.json");
            // The app was called TT Broadcast until 0.2: its folder comes over once.
            Migration.FromOldName(Path.Combine(local, "TTBroadcast"), appData);
            // The first time: the caster's setup from The Button's folder (copied; The Button keeps its own).
            Migration.Run(Path.Combine(local, "TheButton"), settings);
            var env = new BroadcastEnvironment
            {
                SettingsFile = settings,
                Open = target => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }),
                Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "",
            };
            using var server = new BroadcastServer(env, new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
            var form = new MainForm(server.Url, Path.Combine(appData, "WebView2"));
            // Mute all lobby voice from anywhere in Windows (default Ctrl+Shift+M, changed in Lobby voice).
            using var hotkey = new GlobalHotkey(server.MuteHotkeyPressed);
            void Register(string key) => server.HotkeyProblem = hotkey.Set(key) ? null : $"{key} is in use by another app: pick another Mute all key in Lobby voice.";
            Register(server.MuteHotkey);
            server.MuteHotkeyChanged += key =>
            {
                if (form.IsHandleCreated) form.BeginInvoke(new Action(() => Register(key)));
            };
            Application.Run(form);
        }
    }
}
