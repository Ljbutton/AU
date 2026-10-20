using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TournamentTracker.App
{
    /// <summary>
    /// Red Alert's memory of the lobbies (lobbies-cache.json in its folder): each lobby last read from the
    /// results channel, its live data (without the players or the event feed) with its VDO.Ninja link, and its
    /// live message. Loaded at the start, so the lobbies, their OBS sources and the desk's data links are back
    /// at once after a restart, marked cached until the results channel confirms each one (its message is
    /// read first, by its ID). Written when a lobby or its link changes, and every 5 minutes. The links hold
    /// the hosts' VDO.Ninja passwords: the file stays in Red Alert's folder, is never put in a support bundle,
    /// and is deleted when Red Alert is locked. Only for the same results channel, and only lobbies seen in
    /// the last 18 hours.
    /// </summary>
    public sealed partial class Organizer
    {
        public const string CacheFileName = "lobbies-cache.json";
        /// <summary>A lobby not heard from for this long isn't brought back (the same as the Stream Deck's lobby numbers).</summary>
        public static readonly TimeSpan CacheKeptFor = TimeSpan.FromHours(18);
        private static readonly TimeSpan CacheRefresh = TimeSpan.FromMinutes(5);
        private static readonly JsonSerializerOptions CacheJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly string? _cacheFile;
        /// <summary>Lobbies remembered from before a restart, not confirmed by the results channel yet.</summary>
        private readonly HashSet<string> _cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _cacheKey = "";
        private DateTime _cacheSaved;
        private bool _cacheWarned;

        private sealed class CacheData
        {
            public string Channel { get; set; } = "";
            public DateTime Saved { get; set; }
            public List<CachedLobby> Lobbies { get; set; } = new List<CachedLobby>();
        }

        private sealed class CachedLobby
        {
            public string Lobby { get; set; } = "";
            public long Sent { get; set; }
            /// <summary>When the results channel last showed it (UTC).</summary>
            public DateTime Seen { get; set; }
            public string? MessageId { get; set; }
            public JsonElement Data { get; set; }
        }

        /// <summary>Remembered from before a restart, and not confirmed by the results channel yet.</summary>
        public bool IsCached(string lobby)
        {
            lock (_lock) return _cached.Contains(lobby);
        }

        private void LoadCache()
        {
            if (_cacheFile == null || !File.Exists(_cacheFile)) return;
            CacheData? file;
            // Only a copy of what the channel says: one that can't be read is just not used.
            try { file = JsonSerializer.Deserialize<CacheData>(SafeFile.ReadAllText(_cacheFile), CacheJson); }
            catch (Exception) { return; }
            if (file == null || file.Channel != _code.ResultsChannelId) return;
            var now = _clock();
            int n = 0;
            lock (_lock)
                foreach (var l in file.Lobbies ?? new List<CachedLobby>())
                {
                    if (l.Lobby.Length == 0 || l.Data.ValueKind != JsonValueKind.Object || now - l.Seen > CacheKeptFor || _lobbies.ContainsKey(l.Lobby)) continue;
                    _lobbies[l.Lobby] = (l.Data.Clone(), l.Seen, l.Sent);
                    _cached.Add(l.Lobby);
                    // Read by its ID at the first poll, so it's confirmed (or let go) within seconds.
                    if (!string.IsNullOrEmpty(l.MessageId)) _liveMessages[l.Lobby] = (l.MessageId, DateTime.MinValue);
                    n++;
                }
            _cacheKey = CacheKey();
            _cacheSaved = now;
            if (n > 0) _log.Info($"Organiser: {n} lobb{(n == 1 ? "y" : "ies")} remembered from before the restart, until the results channel confirms {(n == 1 ? "it" : "them")}.");
        }

        /// <summary>What the file is kept for: each lobby, its link and its message.</summary>
        private string CacheKey()
        {
            lock (_lock)
                return string.Join("\n", _lobbies.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => $"{kv.Key}|{VideoUrl(kv.Value.Data)}|{(_liveMessages.TryGetValue(kv.Key, out var m) ? m.MessageId : "")}"));
        }

        private readonly object _cacheWriteLock = new object();

        /// <summary>Stops the poll and waits for a write of the kept lobbies already under way.</summary>
        public void Dispose()
        {
            _cts.Cancel();
            lock (_cacheWriteLock) { }
        }

        /// <summary>After each read of the channel: the file written when a lobby or its link changed, and every 5 minutes.</summary>
        private void SaveCache()
        {
            if (_cacheFile == null || _cts.IsCancellationRequested) return;
            var now = _clock();
            string key = CacheKey();
            if (key == _cacheKey && now - _cacheSaved < CacheRefresh) return;
            var data = new CacheData { Channel = _code.ResultsChannelId ?? "", Saved = now };
            lock (_lock)
                foreach (var kv in _lobbies)
                    data.Lobbies.Add(new CachedLobby
                    {
                        Lobby = kv.Key, Sent = kv.Value.Sent, Seen = kv.Value.SeenUtc, Data = Trimmed(kv.Value.Data),
                        MessageId = _liveMessages.TryGetValue(kv.Key, out var m) ? m.MessageId : null,
                    });
            // Under the write lock, checked again: once Dispose has returned, nothing writes the file (Lock deletes it then).
            lock (_cacheWriteLock)
            try
            {
                if (_cts.IsCancellationRequested) return;
                if (data.Lobbies.Count == 0 && !File.Exists(_cacheFile)) return;
                SafeFile.WriteAllText(_cacheFile, JsonSerializer.Serialize(data, CacheJson));
                _cacheKey = key;
                _cacheSaved = now;
                _cacheWarned = false;
            }
            catch (Exception e)
            {
                // Tried again after the next read; said once until it works.
                if (!_cacheWarned) _log.Warn("Organiser: couldn't keep the lobbies for the next start: " + e.Message);
                _cacheWarned = true;
            }
        }

        /// <summary>
        /// Every VDO.Ninja link in a kept file (whatever its channel or age), so a support bundle can take their
        /// passwords out wherever they turn up. Empty when there's no file or it can't be read.
        /// </summary>
        public static List<string> CachedLinks(string? file)
        {
            var links = new List<string>();
            try
            {
                if (file == null || !File.Exists(file)) return links;
                var data = JsonSerializer.Deserialize<CacheData>(SafeFile.ReadAllText(file), CacheJson);
                foreach (var l in data?.Lobbies ?? new List<CachedLobby>())
                {
                    if (l.Data.ValueKind != JsonValueKind.Object) continue;
                    if (VideoUrl(l.Data) is { } url) links.Add(url);
                    // One that isn't a well-formed link still has its password after the colon.
                    else if (l.Data.TryGetProperty("vdo", out var v) && v.ValueKind == JsonValueKind.String && (v.GetString() ?? "").Split(':') is { Length: 2 } parts)
                        links.Add("https://vdo.ninja/?view=x&password=" + Uri.EscapeDataString(parts[1]));
                }
            }
            catch (Exception) { }
            return links;
        }

        /// <summary>A lobby's live data as kept: the same, without the players and the event feed (they're stale by the next start).</summary>
        internal static JsonElement Trimmed(JsonElement data)
        {
            if (JsonNode.Parse(data.GetRawText()) is not JsonObject o) return data.Clone();
            if (o.ContainsKey("p")) o["p"] = new JsonArray();
            if (o.ContainsKey("f")) o["f"] = new JsonArray();
            return JsonSerializer.SerializeToElement(o);
        }
    }
}
