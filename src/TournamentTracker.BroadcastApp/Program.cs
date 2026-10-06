using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            NativeMethods.Title = "TT Broadcast";
            NativeMethods.ProcessName = "TTBroadcast";
            // One copy at a time: a second start just brings the first to the front.
            using var single = new Mutex(true, "TTBroadcast.App", out bool first);
            // After an update the old copy is still closing: give it a few seconds.
            if (!first && Environment.GetCommandLineArgs().Contains("--after-update"))
            {
                try { first = single.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { first = true; }
            }
            if (!first)
            {
                NativeMethods.ShowExisting();
                return;
            }

            ApplicationConfiguration.Initialize();
            string? exe = Environment.ProcessPath;       // before any update renames the running copy
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Path.Combine(local, "TTBroadcast");
            string settings = Path.Combine(appData, "settings.json");
            // The first time: the caster's setup from The Button's folder (copied; The Button keeps its own).
            Migration.Run(Path.Combine(local, "TheButton"), settings);
            var env = new BroadcastEnvironment
            {
                SettingsFile = settings,
                Open = target => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }),
                Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "",
                ExePath = exe,
                Restart = () =>
                {
                    if (exe == null) return;
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = "--after-update" });
                    var form = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
                    if (form != null) form.BeginInvoke(new Action(Application.Exit)); else Application.Exit();
                },
            };
            AppUpdater.CleanUp(exe);
            using var server = new BroadcastServer(env, new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
            Application.Run(new MainForm(server.Url, Path.Combine(appData, "WebView2")));
        }
    }
}
