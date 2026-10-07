using System;
using System.Linq;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// A key that works from any window (Windows' RegisterHotKey), written as "Ctrl+Shift+M":
    /// modifiers Ctrl, Shift, Alt, Win, then one key (A–Z, 0–9, F1–F24, Pause, Home, End, Insert,
    /// Delete, PageUp, PageDown, Space). Needs a modifier, unless it's an F key or Pause.
    /// </summary>
    public static class Hotkey
    {
        public const string DefaultMuteAll = "Ctrl+Shift+M";
        public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8, NoRepeat = 0x4000;

        public static bool TryParse(string? text, out int modifiers, out int key, out string normal)
        {
            modifiers = 0; key = 0; normal = "";
            var parts = (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;
            foreach (var p in parts.Take(parts.Length - 1))
            {
                switch (p.ToLowerInvariant())
                {
                    case "ctrl" or "control": modifiers |= Ctrl; break;
                    case "shift": modifiers |= Shift; break;
                    case "alt": modifiers |= Alt; break;
                    case "win" or "windows" or "meta": modifiers |= Win; break;
                    default: return false;
                }
            }
            string k = parts[^1];
            string name;
            if (k.Length == 1 && char.IsLetterOrDigit(k[0]) && k[0] < 128) { key = char.ToUpperInvariant(k[0]); name = ((char)key).ToString(); }
            else if (k.Length >= 2 && (k[0] == 'F' || k[0] == 'f') && int.TryParse(k.Substring(1), out int f) && f >= 1 && f <= 24) { key = 0x6F + f; name = "F" + f; }
            else
            {
                (key, name) = k.ToLowerInvariant() switch
                {
                    "pause" => (0x13, "Pause"), "space" => (0x20, "Space"), "pageup" => (0x21, "PageUp"), "pagedown" => (0x22, "PageDown"),
                    "end" => (0x23, "End"), "home" => (0x24, "Home"), "insert" => (0x2D, "Insert"), "delete" => (0x2E, "Delete"),
                    _ => (0, ""),
                };
                if (key == 0) return false;
            }
            bool bare = name.StartsWith("F") && name.Length > 1 || name == "Pause";
            if (modifiers == 0 && !bare) return false;
            int mods = modifiers;
            normal = string.Join("+", new[] { (Ctrl, "Ctrl"), (Shift, "Shift"), (Alt, "Alt"), (Win, "Win") }.Where(m => (mods & m.Item1) != 0).Select(m => m.Item2).Append(name));
            return true;
        }
    }
}
