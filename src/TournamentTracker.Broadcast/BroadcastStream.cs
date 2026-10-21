using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// The graphics app's state with the broadcast delay: the lobbies' pictures reach the stream that
    /// many seconds late, so what the graphics say about the games (impostor tags, sabotage timers,
    /// counts, the stats ticker, top 3, alerts, player cards, the after-game tables) is
    /// what it was that long ago. What's on screen stays as it is now (the layout, where each lobby is,
    /// the theme, which graphics are switched on, intermission, Starting soon, sponsors and Twitch): OBS switches scene
    /// at once, only the pictures inside are late. Replays, intermission, Starting soon and the be-right-back screen
    /// have no lobby picture: they're live. The Live desk reads its own state: always live.
    /// </summary>
    public sealed partial class BroadcastApp
    {
        private readonly DelayLine<JsonObject> _line = new DelayLine<JsonObject>();
        private readonly object _streamLock = new object();
        /// <summary>The clock the delay is kept by (tests set their own).</summary>
        public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

        /// <summary>What's on screen now, whatever the delay.</summary>
        private static readonly string[] LiveKeys = { "canvas", "layout", "scene", "slots", "theme", "logo", "elements", "codeBox", "intermission", "soon", "extras" };

        /// <summary>What /broadcast/state serves: <see cref="State"/>, with its game data as it was the broadcast delay ago.</summary>
        public JsonObject OnStream(JsonSerializerOptions json)
        {
            var live = JsonSerializer.SerializeToNode(State(), json)!.AsObject();
            double delay = _obs()?.DelaySeconds ?? 0;
            if (delay <= 0)
            {
                _line.Push(Clock(), live, TimeSpan.Zero);
                return live;
            }
            // What's kept is only read here, one look at a time: each answer is its own copy.
            lock (_streamLock)
            {
                var past = _line.Push(Clock(), live, TimeSpan.FromSeconds(delay));
                return Delayed(live, past, (kind, lobby) => CanShow(new Graphic { Kind = kind, Lobby = lobby }));
            }
        }

        /// <summary>
        /// The state the stream gets: <paramref name="past"/>'s game data on <paramref name="live"/>'s screen.
        /// Each lobby's number and place are where it is now; a big graphic about a lobby that can't
        /// show on what's on now is left out, and one that can goes where that lobby is now; a replay's
        /// own player card doesn't outlast the replay. A new copy (neither is changed). Public for tests.
        /// </summary>
        public static JsonObject Delayed(JsonObject live, JsonObject past, Func<string, string?, bool> canShow)
        {
            if (Str(live, "layout") is "replay" or "intermission" or "slate" or "soon" or "none") return (JsonObject)live.DeepClone();
            var o = (JsonObject)past.DeepClone();
            foreach (var k in LiveKeys) o[k] = live[k]?.DeepClone();
            var now = live["lobbies"] as JsonObject;
            if (o["lobbies"] is JsonObject lobbies)
                foreach (var (name, node) in lobbies)
                    if (node is JsonObject l && now?[name] is JsonObject n)
                        foreach (var k in new[] { "number", "order", "onScreen" }) l[k] = n[k]?.DeepClone();
            if (o["alerts"] is JsonArray alerts)
                foreach (var a in alerts.OfType<JsonObject>())
                    a["number"] = Str(a, "lobby") is { } al && now?[al] is JsonObject n ? n["number"]?.DeepClone() : null;
            if (o["big"] is JsonObject big)
            {
                string? lobby = Str(big, "lobby");
                if (lobby != null && !canShow(Str(big, "kind") ?? "", lobby)) { o["big"] = null; o["standingsChange"] = null; }
                else if (o["standingsChange"] is JsonObject sc) sc["at"] = Place(live, lobby);
            }
            if (o["playerCard"] is JsonObject card)
            {
                if (Str(past, "layout") == "replay") o["playerCard"] = null;
                else
                {
                    var at = Place(live, Str(card, "lobby"));
                    foreach (var k in new[] { "x", "y", "w", "h", "full" }) card[k] = at[k]?.DeepClone();
                }
            }
            return o;
        }

        /// <summary>Where a graphic about a lobby goes on what's on now: its tile in a multi-view, else the whole screen.</summary>
        private static JsonObject Place(JsonObject live, string? lobby)
        {
            if (lobby != null && Str(live, "layout") is "2up" or "4up" or "grid" && live["slots"] is JsonArray slots)
                foreach (var s in slots.OfType<JsonObject>())
                    if (string.Equals(Str(s, "lobby"), lobby, StringComparison.OrdinalIgnoreCase))
                        return new JsonObject { ["x"] = s["x"]?.DeepClone(), ["y"] = s["y"]?.DeepClone(), ["w"] = s["w"]?.DeepClone(), ["h"] = s["h"]?.DeepClone(), ["full"] = false };
            var c = live["canvas"] as JsonObject;
            return new JsonObject { ["x"] = 0, ["y"] = 0, ["w"] = c?["w"]?.DeepClone(), ["h"] = c?["h"]?.DeepClone(), ["full"] = true };
        }

        private static string? Str(JsonObject o, string key) =>
            o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[key] is JsonValue e && e.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}
