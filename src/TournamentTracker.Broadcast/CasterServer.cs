using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TournamentTracker.Overlay;

namespace TournamentTracker.App
{
    /// <summary>
    /// The caster's pages on this PC (port 8767, which OBS points at): the overlay that follows the
    /// lobby being cast, /video and /multiview, the simulation stand-ins, the REPLAY tag, and whatever
    /// the broadcast app adds (its graphics). Reads each lobby's live data from the tournament link.
    /// </summary>
    public sealed class CasterServer : IDisposable
    {
        public const int DefaultPort = 8767;
        private static readonly JsonSerializerOptions Camel = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly Organizer _org;
        private readonly OverlayServer? _server;
        private readonly object _lock = new object();
        private string? _cast;

        public CasterServer(Organizer organizer, int port = DefaultPort)
        {
            _org = organizer;
            try { _server = new OverlayServer(port, NullLog.Instance); }
            catch (Exception)
            {
                // Something else has the usual port: take any free one (the page shows which).
                try { _server = new OverlayServer(0, NullLog.Instance); } catch (Exception e) { Problem = "The caster overlay couldn't start: " + e.Message; }
            }
            if (_server != null) _server.Extra = Page;
            organizer.LiveChanged += Update;
        }

        public string? Url => _server?.Url;
        public string? Problem { get; }
        public string? Casting { get { lock (_lock) return _cast; } }

        /// <summary>More pages on the caster port (the broadcast graphics app), by path.</summary>
        public Func<string, (string Type, byte[] Body)?>? MorePages { get; set; }

        /// <summary>What the REPLAY tag on stream says (set by the replays).</summary>
        public string ReplayNow { get; set; } = "{\"on\":false}";

        /// <summary>
        /// What's on stream (the full-screen lobby, or slot 1 of a multi-view): the overlay, video and
        /// multiview pages follow it unless they're pinned.
        /// </summary>
        public Func<string?>? Follow { get; set; }
        /// <summary>A lobby the pages stay on whatever is on stream, or null (they follow the stream).</summary>
        public string? Pinned { get { lock (_lock) return _pin; } }
        private string? _pin;

        /// <summary>Pins the pages to a lobby (null: follow the stream again).</summary>
        public void Pin(string? lobby)
        {
            lock (_lock) _pin = string.IsNullOrWhiteSpace(lobby) ? null : lobby;
            Update();
        }

        /// <summary>What's on stream changed: follow it now.</summary>
        public void Refresh() => Update();

        /// <summary>Picks the lobby the caster overlay shows (tests, and older callers): the same as pinning it.</summary>
        public void Cast(string lobby) => Pin(lobby);

        private void Update()
        {
            if (_server == null) return;
            var lobbies = _org.LiveLobbies();
            JsonElement? data;
            string? want = Pinned ?? Follow?.Invoke();
            lock (_lock)
            {
                if (want != null && lobbies.Any(l => string.Equals(l.Label, want, StringComparison.OrdinalIgnoreCase))) _cast = want;
                else if (_cast == null || !lobbies.Any(l => string.Equals(l.Label, _cast, StringComparison.OrdinalIgnoreCase)))
                    _cast = lobbies.Where(l => l.Data.GetProperty("phase").GetString() != "Menu").OrderByDescending(l => l.Sent).Select(l => l.Label).FirstOrDefault() ?? _cast;
                var hit = lobbies.FirstOrDefault(l => string.Equals(l.Label, _cast, StringComparison.OrdinalIgnoreCase));
                data = hit.Label != null ? hit.Data : (JsonElement?)null;
            }
            if (data == null) return;
            _server.SafeJson = JsonSerializer.Serialize(OverlayState(data.Value, full: false), Camel);
            _server.FullJson = JsonSerializer.Serialize(OverlayState(data.Value, full: true), Camel);
        }

        /// <summary>
        /// Each sending lobby's VDO.Ninja link with neither picture nor sound: the caster tab joins
        /// it only for the lobby's live data, which the host's Button sends alongside the video.
        /// </summary>
        public List<(string Lobby, string Url)> DataLinks() => _org.LiveLobbies()
            .Select(l => (l.Label, Organizer.VideoUrl(l.Data))).Where(x => x.Item2 != null)
            .Select(x => (x.Label, x.Item2!.Replace("&noaudio&cleanoutput", "&novideo&noaudio&cleanoutput"))).ToList();

        /// <summary>Each sending lobby's VDO.Ninja link with picture and sound, for its OBS source (OBS mutes all but one).</summary>
        public List<(string Lobby, string Url)> ObsLinks() => _org.LiveLobbies()
            .Select(l => (l.Label, Organizer.VideoUrl(l.Data))).Where(x => x.Item2 != null)
            .Select(x => (x.Label, x.Item2!.Replace("&noaudio&cleanoutput", "&cleanoutput"))).ToList();

        /// <summary>
        /// Each sending lobby's voice stream (Part 11): its referee's Discord and game sound, sent by
        /// their Button as its own VDO.Ninja stream (the video's id plus "v"), no picture.
        /// </summary>
        /// <summary>
        /// Each lobby's player camera link, for every lobby sending its game: OBS keeps it connected, and
        /// it shows pictures once the player camera is on (Red Alert turns it on; the host's page then sends it).
        /// </summary>
        public List<(string Lobby, string Url)> CamLinks() => _org.LiveLobbies()
            .Select(l => (l.Label, Organizer.CamUrl(l.Data))).Where(x => x.Item2 != null)
            .Select(x => (x.Label, x.Item2!)).ToList();

        public List<(string Lobby, string Url)> VoiceLinks() => _org.LiveLobbies()
            .Select(l => (l.Label, Organizer.VoiceUrl(l.Data))).Where(x => x.Item2 != null)
            .Select(x => (x.Label, x.Item2!)).ToList();

        /// <summary>What the caster's video pages read: the lobby being cast and every lobby's video. Public for tests.</summary>
        public string FeedsJson()
        {
            var lobbies = _org.LiveLobbies().Where(l => l.Data.GetProperty("phase").GetString() != "Menu")
                .OrderBy(l => l.Label, StringComparer.OrdinalIgnoreCase).ToList();
            return JsonSerializer.Serialize(new
            {
                Cast = Casting,
                Lobbies = lobbies.Select(l =>
                {
                    var players = l.Data.TryGetProperty("p", out var p) ? p.EnumerateArray().ToList() : new List<JsonElement>();
                    return new
                    {
                        l.Label,
                        Video = Organizer.VideoUrl(l.Data),
                        Sound = Organizer.SoundUrl(l.Data),
                        Phase = l.Data.GetProperty("phase").GetString(),
                        Alive = players.Count(x => x[2].GetInt32() == 0),
                        Total = players.Count,
                        // On stream: only what the players already know.
                        Hot = _org.Hot(l.Label, full: false),
                    };
                }).ToList(),
            }, Camel);
        }

        private (string Type, byte[] Body)? Page(string path)
        {
            string route = path.Split('?')[0];
            static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);
            return route switch
            {
                "/feeds" => ("application/json", B(FeedsJson())),
                "/video" => ("text/html; charset=utf-8", B(CasterPages.Video)),
                "/multiview" => ("text/html; charset=utf-8", B(CasterPages.Multiview)),
                "/sim" => ("text/html; charset=utf-8", B(CasterPages.SimFeed)),
                "/simvoice" => ("text/html; charset=utf-8", B(CasterPages.SimVoice)),
                "/replaytag" => ("text/html; charset=utf-8", B(CasterPages.ReplayTag)),
                "/replaynow" => ("application/json", B(ReplayNow)),
                _ => MorePages?.Invoke(path),
            };
        }

        /// <summary>A lobby's live data in the stream overlay's format. Public for tests.</summary>
        public static object OverlayState(JsonElement d, bool full)
        {
            string S(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int I(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            string phase = S("phase");
            bool playing = phase == "Tasks" || phase == "Meeting";
            return new
            {
                Tournament = S("tour"),
                Lobby = S("lobby"),
                Round = I("round"),
                Played = (int?)I("played"),
                PerRound = I("per"),
                Phase = phase,
                Map = S("map"),
                Players = d.GetProperty("p").EnumerateArray().Select(p => new
                {
                    Name = p[0].GetString(),
                    Color = p[1].GetInt32(),
                    Dead = (full ? p[3] : p[2]).GetInt32() == 1,
                    Impostor = full && playing && p[4].GetInt32() == 1,
                    Tasks = full && playing && p[6].GetInt32() > 0 ? new[] { p[5].GetInt32(), p[6].GetInt32() } : null,
                }).ToList(),
                StandingsTitle = I("round") > 0 ? $"Round {I("round")} standings" : "Standings",
                Advance = I("adv"),
                Standings = d.GetProperty("st").EnumerateArray().Select(s => new { Name = s[0].GetString(), Points = s[1].GetString() }).ToList(),
                Feed = d.GetProperty("f").EnumerateArray().Where(f => full || Organizer.PublicEvents.Contains(f[1].GetString() ?? ""))
                    .TakeLast(6).Select(f => new { At = f[0].GetString(), Text = f[2].GetString() }).ToList(),
            };
        }

        public void Dispose()
        {
            _org.LiveChanged -= Update;
            _server?.Dispose();
        }
    }
}
