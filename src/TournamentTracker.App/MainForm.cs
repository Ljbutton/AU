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
    /// <summary>
    /// The app's window (The Button's, and Red Alert's: this file is shared): borderless, with its own title bar drawn by the app's page (drag it
    /// to move, double-click to maximise, and minimise, maximise and close buttons). The thin
    /// edge around the page is the window's own, for resizing.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly string _url;
        private readonly string _userData;
        private readonly WebView2 _view = new WebView2 { Dock = DockStyle.Fill };
        private DateTime _lastDrag;
        private Point _lastDragAt;
        private bool _pageReady;

        private int Edge => Math.Max(4, (int)Math.Round(5 * DeviceDpi / 96.0));

        public MainForm(string url, string userData)
        {
            _url = url;
            _userData = userData;
            Text = NativeMethods.Title;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.Black;
            ClientSize = new Size(1180, 780);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Padding = new Padding(Edge);
            DoubleBuffered = true;
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
                    _view.CoreWebView2.WebMessageReceived += OnPageMessage;
                    _view.CoreWebView2.NavigationCompleted += (s, e) => { _pageReady = true; TellPage(); };
                    _view.Source = new Uri(_url);
                }
                catch (Exception)
                {
                    // No WebView2 runtime (rare on Windows 10/11): use the normal browser instead,
                    // and give this window its normal frame back.
                    FormBorderStyle = FormBorderStyle.Sizable;
                    Padding = Padding.Empty;
                    BackColor = SystemColors.Control;
                    Controls.Remove(_view);
                    Controls.Add(new Label
                    {
                        Text = NativeMethods.Title + " is running in your web browser.\nKeep this window open while you use it; closing it quits the app.",
                        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11f),
                    });
                    Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
                }
            };
        }

        /// <summary>The page's title bar: "drag", "min", "max" and "close".</summary>
        private void OnPageMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!e.Source.StartsWith(_url, StringComparison.OrdinalIgnoreCase)) return;
            string message;
            try { message = e.TryGetWebMessageAsString(); } catch (Exception) { return; }
            switch (message)
            {
                case "drag":
                    // A second press in the same place soon after is a double-click: maximise.
                    var now = DateTime.UtcNow;
                    var at = Cursor.Position;
                    bool twice = (now - _lastDrag).TotalMilliseconds <= SystemInformation.DoubleClickTime
                        && Math.Abs(at.X - _lastDragAt.X) <= SystemInformation.DoubleClickSize.Width
                        && Math.Abs(at.Y - _lastDragAt.Y) <= SystemInformation.DoubleClickSize.Height;
                    _lastDrag = twice ? DateTime.MinValue : now;
                    _lastDragAt = at;
                    if (twice) { ToggleMaximised(); return; }
                    // Only while the button is still down, or the window would follow the mouse until the next click.
                    if ((MouseButtons & MouseButtons.Left) == 0) return;
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
                    break;
                case "min": WindowState = FormWindowState.Minimized; break;
                case "max": ToggleMaximised(); break;
                case "close": Close(); break;
            }
        }

        private void ToggleMaximised() =>
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

        private void TellPage()
        {
            if (!_pageReady || _view.CoreWebView2 == null) return;
            try { _view.CoreWebView2.PostWebMessageAsString(WindowState == FormWindowState.Maximized ? "maximized:true" : "maximized:false"); }
            catch (Exception) { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // Keeps the taskbar button's minimise/restore, Win+arrow snapping and the drop shadow.
                cp.Style |= NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX | NativeMethods.WS_SYSMENU;
                cp.ClassStyle |= NativeMethods.CS_DROPSHADOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.RoundCorners(Handle);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState != FormWindowState.Minimized)
                Padding = WindowState == FormWindowState.Maximized ? Padding.Empty : new Padding(Edge);
            Invalidate();
            TellPage();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (WindowState == FormWindowState.Maximized) return;
            using var pen = new Pen(Color.FromArgb(58, 62, 72));
            e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                // The edge around the page resizes the window.
                var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                int edge = Edge + 2;
                bool left = p.X < edge, right = p.X >= ClientSize.Width - edge, top = p.Y < edge, bottom = p.Y >= ClientSize.Height - edge;
                int hit = top && left ? 13 : top && right ? 14 : bottom && left ? 16 : bottom && right ? 17
                    : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 0;
                if (hit != 0) { m.Result = (IntPtr)hit; return; }
            }
            base.WndProc(ref m);
            if (m.Msg == NativeMethods.WM_GETMINMAXINFO)
            {
                // Maximised, a borderless window would cover the taskbar: keep it to the work area.
                var screen = Screen.FromHandle(Handle);
                var info = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(m.LParam);
                info.ptMaxPosition = new NativeMethods.POINT { X = screen.WorkingArea.Left - screen.Bounds.Left, Y = screen.WorkingArea.Top - screen.Bounds.Top };
                info.ptMaxSize = new NativeMethods.POINT { X = screen.WorkingArea.Width, Y = screen.WorkingArea.Height };
                System.Runtime.InteropServices.Marshal.StructureToPtr(info, m.LParam, false);
            }
        }
    }

    internal static class NativeMethods
    {
        /// <summary>The window's title, and the program to bring forward when it's started twice (set by each app).</summary>
        public static string Title { get; set; } = "The Button";
        public static string ProcessName { get; set; } = "TheButton";
        public const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDOWN = 0xA1, WM_GETMINMAXINFO = 0x24, HTCAPTION = 2;
        public const int WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000, WS_SYSMENU = 0x80000, CS_DROPSHADOW = 0x20000;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Windows 11's rounded corners (Windows 10 ignores it).</summary>
        public static void RoundCorners(IntPtr hwnd)
        {
            try { int round = 2; DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); } catch (Exception) { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static void ShowExisting()
        {
            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                if (p.Id == Environment.ProcessId || p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, 9);   // restore if minimised
                SetForegroundWindow(p.MainWindowHandle);
            }
        }
    }
}
