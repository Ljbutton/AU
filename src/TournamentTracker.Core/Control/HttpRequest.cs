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
        public static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)> ReadAsync(NetworkStream stream)
        {
            var (method, path, headers, body) = await ReadRawAsync(stream, 1_000_000).ConfigureAwait(false);
            return (method, path, headers, Encoding.UTF8.GetString(body));
        }

        /// <summary>The same, with the body as bytes (files, up to <paramref name="limit"/>).</summary>
        public static async Task<(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)> ReadRawAsync(Stream stream, int limit)
        {
            var head = new StringBuilder();
            var buffer = new byte[1];
            // Read the header byte by byte up to the blank line, then exactly Content-Length bytes.
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                int n = await stream.ReadAsync(buffer, 0, 1).ConfigureAwait(false);
                if (n == 0) break;
                head.Append((char)buffer[0]);
                if (head.Length > 16 * 1024) throw new IOException("header too long");
            }
            var lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
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
                int read = 0;
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

        public static async Task WriteAsync(NetworkStream stream, int status, string type, byte[] body)
        {
            string reason = status switch { 200 => "OK", 401 => "Unauthorized", 404 => "Not Found", _ => "Error" };
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
