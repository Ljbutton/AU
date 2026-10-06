using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// What happens on the channel, as it happens: EventSub over a WebSocket (no web server needed).
    /// Votes and points on polls and predictions, channel point redemptions and chat messages (for
    /// !sus). Reconnects by itself (when Twitch asks to, or after a drop, waiting longer each time).
    /// </summary>
    public sealed class TwitchEventSub : IDisposable
    {
        public const string Url = "wss://eventsub.wss.twitch.tv/ws";
        private readonly ITwitchApi _api;
        private readonly Func<string> _userId;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task? _loop;

        /// <summary>A notification: its subscription type and the event.</summary>
        public event Action<string, JsonElement>? Event;
        public string State { get; private set; } = "off";
        public string? Problem { get; private set; }

        /// <summary>The subscriptions The Button needs (type, version, needs the user too).</summary>
        public static readonly (string Type, string Version, bool User)[] Types =
        {
            ("channel.poll.progress", "1", false), ("channel.poll.end", "1", false),
            ("channel.prediction.progress", "1", false), ("channel.prediction.lock", "1", false), ("channel.prediction.end", "1", false),
            ("channel.channel_points_custom_reward_redemption.add", "1", false),
            ("channel.chat.message", "1", true),
        };

        public TwitchEventSub(ITwitchApi api, Func<string> userId)
        {
            _api = api;
            _userId = userId;
        }

        public void Start() => _loop ??= Task.Run(RunAsync);

        private async Task RunAsync()
        {
            string url = Url;
            int wait = 2;
            while (!_cts.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                string? next = null;
                try
                {
                    State = "connecting";
                    await ws.ConnectAsync(new Uri(url), _cts.Token).ConfigureAwait(false);
                    var buffer = new byte[64 * 1024];
                    while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                    {
                        using var ms = new MemoryStream();
                        WebSocketReceiveResult r;
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(40));        // keepalives come every 10 s
                        do
                        {
                            r = await ws.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
                            ms.Write(buffer, 0, r.Count);
                        } while (!r.EndOfMessage);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        next = await HandleAsync(Encoding.UTF8.GetString(ms.ToArray())).ConfigureAwait(false);
                        if (next != null) break;
                        wait = 2;
                    }
                }
                catch (Exception e) when (!_cts.IsCancellationRequested) { Problem = "Twitch events: " + e.Message; }
                catch (Exception) { break; }
                if (next != null) { url = next; continue; }                      // Twitch moved us: straight there
                url = Url;
                State = "reconnecting";
                try { await Task.Delay(TimeSpan.FromSeconds(wait), _cts.Token).ConfigureAwait(false); } catch (TaskCanceledException) { break; }
                wait = Math.Min(60, wait * 2);
            }
            State = "off";
        }

        /// <summary>One message from Twitch; returns where to reconnect when Twitch asks.</summary>
        public async Task<string?> HandleAsync(string text)
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string type = root.GetProperty("metadata").GetProperty("message_type").GetString() ?? "";
            var payload = root.GetProperty("payload");
            switch (type)
            {
                case "session_welcome":
                {
                    string session = payload.GetProperty("session").GetProperty("id").GetString()!;
                    string me = _userId();
                    var failed = new List<string>();
                    foreach (var (t, v, user) in Types)
                    {
                        try { await _api.SubscribeAsync(t, v, user ? new { broadcaster_user_id = me, user_id = me } : (object)new { broadcaster_user_id = me }, session).ConfigureAwait(false); }
                        catch (TwitchException e) { failed.Add($"{t} ({e.Message})"); }
                    }
                    State = "connected";
                    Problem = failed.Count == 0 ? null : "Twitch didn't allow: " + string.Join("; ", failed);
                    return null;
                }
                case "session_reconnect":
                    return payload.GetProperty("session").GetProperty("reconnect_url").GetString();
                case "notification":
                    Event?.Invoke(payload.GetProperty("subscription").GetProperty("type").GetString() ?? "", payload.GetProperty("event").Clone());
                    return null;
                case "revocation":
                    Problem = "Twitch stopped sending " + payload.GetProperty("subscription").GetProperty("type").GetString() + ": connect again.";
                    return null;
                default:
                    return null;                                                    // session_keepalive
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
        }
    }
}
