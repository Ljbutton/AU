using System;
using System.IO;
using System.Net.Http;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TournamentTracker.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // One copy at a time: a second start just brings the first to the front.
            using var single = AppStart.SingleCopy("TheButton.App");
            if (single == null) return;

            ApplicationConfiguration.Initialize();
            string? exe = Environment.ProcessPath;       // before any update renames the running copy
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheButton");
            var env = new AppEnvironment
            {
                SettingsFile = Path.Combine(appData, "app.json"),
                SteamRoot = SteamRoot(),
                Downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                // Support bundles go on the Desktop; Among Us's own log (Player.log) goes in them too.
                Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                UnityLogs = UnityLogFolder(),
                Open = AppStart.Open,
                Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "",
                ExePath = exe,
                Restart = () => AppStart.Restart(exe),
            };
            AppUpdater.CleanUp(exe);
            using var server = new AppServer(env, new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
            // The page crashing or hanging (and the window bringing it back) is written to the-button.log.
            Application.Run(new MainForm(server.Url, Path.Combine(appData, "WebView2")) { Log = m => server.Log.Warn(m) });
        }

        /// <summary>Where Among Us (a Unity game) writes Player.log: …\AppData\LocalLow\Innersloth\Among Us.</summary>
        private static string? UnityLogFolder()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string? appData = Path.GetDirectoryName(local.TrimEnd('\\', '/'));
                return appData == null ? null : Path.Combine(appData, "LocalLow", "Innersloth", "Among Us");
            }
            catch (Exception) { return null; }
        }

        private static string? SteamRoot()
        {
            try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; }
            catch (Exception) { return null; }
        }
    }
}
