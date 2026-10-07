using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TournamentTracker.App.Broadcast;

namespace TournamentTracker.App
{
    /// <summary>
    /// A key that works while Red Alert isn't the window in front (the caster is in OBS or the
    /// game): Windows' RegisterHotKey on a hidden message window.
    /// </summary>
    internal sealed class GlobalHotkey : NativeWindow, IDisposable
    {
        private const int WmHotkey = 0x0312, Id = 0x5241;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly Action _pressed;
        private bool _registered;

        public GlobalHotkey(Action pressed)
        {
            _pressed = pressed;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });     // HWND_MESSAGE: never shown
        }

        /// <summary>Registers the key ("Ctrl+Shift+M"); false when it's not a key or another app has it.</summary>
        public bool Set(string text)
        {
            if (_registered) { UnregisterHotKey(Handle, Id); _registered = false; }
            if (!Hotkey.TryParse(text, out int mods, out int key, out _)) return false;
            _registered = RegisterHotKey(Handle, Id, (uint)(mods | Hotkey.NoRepeat), (uint)key);
            return _registered;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == Id)
                try { _pressed(); } catch (Exception) { }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_registered) UnregisterHotKey(Handle, Id);
            _registered = false;
            DestroyHandle();
        }
    }
}
