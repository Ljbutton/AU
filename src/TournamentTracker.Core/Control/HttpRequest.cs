using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace TournamentTracker.Control
{
    /// <summary>Just enough HTTP/1.1 for the local API: one request per connection.</summary>
    public static class HttpRequest
    {
        /// <summary>The longest header taken (request line, headers and the blank line).</summary>
        private const int MaxHeader = 16 * 1024;

        public static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)> ReadAsync(NetworkStream stream)
        {
            var (method, path, headers, body) = await ReadRawAsync(stream, 1_000_000).ConfigureAwait(false);
            return (method, path, headers, Encoding.UTF8.GetString(body));
        }

        /// <summary>The same, with the body as bytes (files, up to <paramref name="limit"/>).</summary>
        /// <remarks>
        /// The header is read in chunks (a browser's usually arrives in one), not a byte at a time:
        /// read byte by byte, with the whole header turned back into text after every byte to look
        /// for its end, a 700-byte browser header took 700 reads and half a megabyte of garbage, on
        /// every request to the mod, The Button and Red Alert. Whatever arrived after the blank line
        /// is the start of the body; the rest of Content-Length is read after it.
        /// </remarks>
        public static async Task<(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)> ReadRawAsync(Stream stream, int limit)
        {
            var buffer = new byte[4096];
            int filled = 0, end = -1;
            while (true)
            {
                if (filled == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
                int n = await stream.ReadAsync(buffer, filled, buffer.Length - filled).ConfigureAwait(false);
                if (n == 0) break;                                  // closed before the blank line: what came is the header
                int from = Math.Max(0, filled - 3);                 // the blank line can straddle two reads
                filled += n;
                end = HeaderEnd(buffer, from, filled);
                if (end >= 0 || filled > MaxHeader) break;
            }
            if (end > MaxHeader || (end < 0 && filled > MaxHeader)) throw new IOException("header too long");
            int headLength = end >= 0 ? end : filled;

            // Each byte is one character (the header is ASCII; anything else reads as Latin-1, as before).
            var chars = new char[headLength];
            for (int i = 0; i < headLength; i++) chars[i] = (char)buffer[i];
            var lines = new string(chars).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) headers[lines[i].Substring(0, colon).Trim().ToLowerInvariant()] = lines[i].Substring(colon + 1).Trim();
            }
            byte[] body = Array.Empty<byte>();
            if (headers.TryGetValue("content-length", out var len) && int.TryParse(len, out int length) && length > 0 && length < limit)
            {
                var data = new byte[length];
                // The body's first bytes may have come with the header.
                int read = Math.Min(filled - headLength, length);
                if (read > 0) Buffer.BlockCopy(buffer, headLength, data, 0, read);
                while (read < length)
                {
                    int n = await stream.ReadAsync(data, read, length - read).ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }
                body = read == length ? data : data.AsSpan(0, read).ToArray();
            }
            return (first.Length > 0 ? first[0] : "", first.Length > 1 ? first[1] : "/", headers, body);
        }

        /// <summary>Where the header ends (just past its blank line, "\r\n\r\n"), looking from <paramref name="from"/>; -1 if it hasn't yet.</summary>
        private static int HeaderEnd(byte[] buffer, int from, int filled)
        {
            for (int i = from; i + 3 < filled; i++)
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i + 4;
            return -1;
        }

        public static async Task WriteAsync(NetworkStream stream, int status, string type, byte[] body)
        {
            string reason = status switch { 200 => "OK", 204 => "No Content", 401 => "Unauthorized", 404 => "Not Found", _ => "Error" };
            string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
        }

        public static string Query(string query, string name)
        {
            foreach (var pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == name) return Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            return "";
        }
    }
}
