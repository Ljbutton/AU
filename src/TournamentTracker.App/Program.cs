using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace TournamentTracker.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // One copy at a time: a second start just brings the first to the front.
            using var single = new Mutex(true, "TheButton.App", out bool first);
            // After an update the old copy is still closing: give it a few seconds.
            if (!first && Environment.GetCommandLineArgs().Contains("--after-update"))
            {
                try { first = single.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { first = true; }     // the old copy closed without letting go: it's ours now
            }
            if (!first)
            {
                NativeMethods.ShowExisting();
                return;
            }

            ApplicationConfiguration.Initialize();
            string? exe = Environment.ProcessPath;       // before any update renames the running copy
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheButton");
            var env = new AppEnvironment
            {
                SettingsFile = Path.Combine(appData, "app.json"),
                SteamRoot = SteamRoot(),
                Downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Open = target => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }),
                Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "",
                ExePath = exe,
                Restart = () =>
                {
                    // Start the new version (it waits for this one to let go of the single-copy lock), then close.
                    if (exe == null) return;
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = "--after-update" });
                    var form = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
                    if (form != null) form.BeginInvoke(new Action(Application.Exit)); else Application.Exit();
                },
            };
            AppUpdater.CleanUp(exe);
            using var server = new AppServer(env, new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
            Application.Run(new MainForm(server.Url, Path.Combine(appData, "WebView2")));
        }

        private static string? SteamRoot()
        {
            try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; }
            catch (Exception) { return null; }
        }
    }
}
