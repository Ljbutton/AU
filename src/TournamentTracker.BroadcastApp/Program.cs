using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
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
            using var single = AppStart.SingleCopy("RedAlert.App");
            if (single == null) return;

            // Red Alert's page carries every host's data link and sends their commands: it keeps running at
            // full speed when the window is minimised or covered (by an OBS projector, say). Chromium
            // otherwise slows a hidden page's timers to once a second or less. Only this app's WebView2
            // reads it (the variable is set for this process alone, added to any already there).
            const string KeepAwake = "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows";
            string? extra = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", string.IsNullOrWhiteSpace(extra) ? KeepAwake : extra + " " + KeepAwake);

            ApplicationConfiguration.Initialize();
            // A problem in the window's own code is written to red-alert.log and Red Alert carries on,
            // rather than stopping the show on an error box (set before any window is made).
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            string? exe = Environment.ProcessPath;       // before an update renames the running copy
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
                Open = AppStart.Open,
                Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "",
                ExePath = exe,
                Restart = () => AppStart.Restart(exe),
            };
            AppUpdater.CleanUp(exe);
            using var server = new BroadcastServer(env, new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
            // What went wrong, for afterwards: in red-alert.log next to the settings.
            Application.ThreadException += (_, e) => server.Log.Error("Unhandled error in the window: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => server.Log.Error((e.IsTerminating ? "Red Alert stopped: " : "Unhandled error: ") + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) => { server.Log.Warn("Background task failed: " + e.Exception.GetBaseException().Message); e.SetObserved(); };
            server.Log.Info($"Red Alert {env.Version} started on port {server.Port}.");
            // Maximised the first time (the Live desk is laid out for the whole screen), then as it was left.
            var form = new MainForm(server.Url, Path.Combine(appData, "WebView2"), Path.Combine(appData, "window.txt"));
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
