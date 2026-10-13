using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace TournamentTracker.App
{
    /// <summary>
    /// The files built into an app (its pages, and what both apps take from The Button's ui folder: the
    /// fonts, crewmate heads and the pages' shared look and script), served the same way by both.
    /// </summary>
    public static class UiFiles
    {
        private static readonly Regex Font = new Regex(@"^/fonts/([a-z0-9-]+\.woff2)$");
        private static readonly Regex Crew = new Regex(@"^/crew/(\d{1,2})\.png$");

        /// <summary>A text file built into <paramref name="assembly"/>; "" when there's none.</summary>
        public static string Text(Assembly assembly, string name)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) return "";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>A file built into <paramref name="assembly"/>; empty when there's none.</summary>
        public static byte[] Bytes(Assembly assembly, string name)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) return Array.Empty<byte>();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        /// <summary>The font or crewmate head <paramref name="route"/> asks for; null for any other route, or one that isn't built in.</summary>
        public static (string Type, byte[] Body)? Asset(Assembly assembly, string route)
        {
            var font = Font.Match(route);
            var crew = Crew.Match(route);
            if (!font.Success && !crew.Success) return null;
            var bytes = font.Success ? Bytes(assembly, "ui/fonts/" + font.Groups[1].Value) : Bytes(assembly, $"ui/crew/{crew.Groups[1].Value}.png");
            if (bytes.Length == 0) return null;
            return (font.Success ? "font/woff2" : "image/png", bytes);
        }

        /// <summary>/base.css and /base.js, the look and script both apps' own pages share; null for any other route.</summary>
        public static (string Type, byte[] Body)? Base(Assembly assembly, string route)
        {
            var (type, name) = route switch
            {
                "/base.css" => ("text/css; charset=utf-8", "ui/base.css"),
                "/base.js" => ("text/javascript; charset=utf-8", "ui/base.js"),
                _ => (null, null),
            };
            if (name == null) return null;
            var bytes = Bytes(assembly, name);
            return bytes.Length == 0 ? null : (type!, bytes);
        }
    }
}
