using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.Control
{
    /// <summary>
    /// What the local web servers share (the mod's control API and overlay, The Button and its Discord
    /// bridge, Red Alert): listening on this computer only (127.0.0.1), each connection on its own task,
    /// and the random token that keeps other programs and web pages out.
    /// </summary>
    public static class LocalServer
    {
        /// <summary>Listens on the first of <paramref name="ports"/> that's free (0 or out of range: skipped), else on any free port.</summary>
        public static TcpListener Listen(params int[] ports)
        {
            foreach (int port in ports)
            {
                if (port <= 0 || port > 65535) continue;
                var listener = new TcpListener(IPAddress.Loopback, port);
                try { listener.Start(); return listener; }
                catch (SocketException) { }
            }
            var any = new TcpListener(IPAddress.Loopback, 0);
            any.Start();
            return any;
        }

        /// <summary>Takes connections until <paramref name="stop"/> (or the listener stops), each served on its own task.</summary>
        public static async Task AcceptLoop(TcpListener listener, CancellationToken stop, Func<TcpClient, Task> serve)
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                _ = Task.Run(() => serve(client));
            }
        }

        /// <summary>
        /// Takes connections (see <see cref="AcceptLoop"/>) and answers each one's request with what
        /// <paramref name="route"/> gives for it. A request that fails (the other end closed mid-request)
        /// goes to <paramref name="failed"/>, or is dropped quietly.
        /// </summary>
        public static Task Serve(TcpListener listener, CancellationToken stop,
            Func<string, string, Dictionary<string, string>, string, Task<(int Status, string Type, byte[] Body)>> route, Action<Exception>? failed = null) =>
            AcceptLoop(listener, stop, async client =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var (method, path, headers, body) = await HttpRequest.ReadAsync(stream).ConfigureAwait(false);
                        var (status, type, bytes) = await route(method, path, headers, body).ConfigureAwait(false);
                        await HttpRequest.WriteAsync(stream, status, type, bytes).ConfigureAwait(false);
                    }
                    catch (Exception e) { failed?.Invoke(e); }
                }
            });

        /// <summary>A new random token (<paramref name="bytes"/> random bytes), safe in a link: base64 with - and _.</summary>
        public static string NewToken(int bytes = 18)
        {
            var random = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            return Convert.ToBase64String(random).Replace('+', '-').Replace('/', '_');
        }
    }
}
