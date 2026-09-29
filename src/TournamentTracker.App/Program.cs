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

    internal sealed class MainForm : Form
    {
        private readonly string _url;
        private readonly string _userData;
        private readonly WebView2 _view = new WebView2 { Dock = DockStyle.Fill };

        public MainForm(string url, string userData)
        {
            _url = url;
            _userData = userData;
            Text = NativeMethods.Title;
            ClientSize = new Size(1180, 780);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            Controls.Add(_view);
            Load += async (_, __) =>
            {
                try
                {
                    var environment = await CoreWebView2Environment.CreateAsync(null, _userData);
                    await _view.EnsureCoreWebView2Async(environment);
                    _view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    _view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    // Links to the web (GitHub, the overlay) open in the normal browser; the replay viewer opens in its own window.
                    _view.CoreWebView2.NewWindowRequested += (s, e) =>
                    {
                        if (e.Uri.StartsWith(_url, StringComparison.OrdinalIgnoreCase)) return;
                        e.Handled = true;
                        Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
                    };
                    _view.Source = new Uri(_url);
                }
                catch (Exception)
                {
                    // No WebView2 runtime (rare on Windows 10/11): use the normal browser instead.
                    Controls.Remove(_view);
                    Controls.Add(new Label
                    {
                        Text = "The Button is running in your web browser.\nKeep this window open while you use it; closing it quits the app.",
                        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11f),
                    });
                    Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
                }
            };
        }
    }

    internal static class NativeMethods
    {
        public const string Title = "The Button";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static void ShowExisting()
        {
            foreach (var p in Process.GetProcessesByName("TheButton"))
            {
                if (p.Id == Environment.ProcessId || p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, 9);   // restore if minimised
                SetForegroundWindow(p.MainWindowHandle);
            }
        }
    }
}
