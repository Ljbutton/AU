using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.Voice
{
    public sealed class PlayerLink
    {
        public string PlayerKey { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public string DiscordUserId { get; set; } = "";
        public string DiscordName { get; set; } = "";
    }

    /// <summary>
    /// Which Discord account belongs to which Among Us player, keyed by friend code so a
    /// link survives name and colour changes. Stored in links.json, which the host can
    /// also fill in by hand before the tournament.
    /// </summary>
    public sealed class LinkRegistry
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };
        private readonly Dictionary<string, PlayerLink> _byKey = new Dictionary<string, PlayerLink>(StringComparer.OrdinalIgnoreCase);
        private readonly string? _path;

        public LinkRegistry(string? path = null)
        {
            _path = path;
        }

        public IReadOnlyCollection<PlayerLink> All => _byKey.Values;

        /// <summary>Goes up on every change, so views built from the links know to rebuild.</summary>
        public int Version { get; private set; }

        public PlayerLink? Find(string playerKey) => _byKey.TryGetValue(playerKey, out var link) ? link : null;

        public PlayerLink? FindByDiscordId(string discordUserId) =>
            _byKey.Values.FirstOrDefault(l => l.DiscordUserId == discordUserId);

        /// <summary>Links a player. A Discord account can only belong to one player at a time.</summary>
        public void Link(string playerKey, string playerName, string discordUserId, string discordName)
        {
            foreach (var stale in _byKey.Values.Where(l => l.DiscordUserId == discordUserId && !string.Equals(l.PlayerKey, playerKey, StringComparison.OrdinalIgnoreCase)).ToList())
                _byKey.Remove(stale.PlayerKey);

            _byKey[playerKey] = new PlayerLink
            {
                PlayerKey = playerKey,
                PlayerName = playerName,
                DiscordUserId = discordUserId,
                DiscordName = discordName,
            };
            Save();
        }

        public bool Unlink(string playerKey)
        {
            bool removed = _byKey.Remove(playerKey);
            if (removed) Save();
            return removed;
        }

        /// <summary>
        /// Adds the links (from a backup) for players and Discord accounts it has no link for yet; the links
        /// it has stay as they are. Saved once. Gives how many were added.
        /// </summary>
        public int AddMissing(IEnumerable<PlayerLink> links)
        {
            int added = 0;
            foreach (var l in links)
            {
                if (l == null || l.PlayerKey.Trim().Length == 0 || l.DiscordUserId.Length == 0) continue;
                string key = l.PlayerKey.Trim().ToLowerInvariant();
                if (_byKey.ContainsKey(key) || _byKey.Values.Any(x => x.DiscordUserId == l.DiscordUserId)) continue;
                _byKey[key] = new PlayerLink { PlayerKey = key, PlayerName = l.PlayerName, DiscordUserId = l.DiscordUserId, DiscordName = l.DiscordName };
                added++;
            }
            if (added > 0) Save();
            return added;
        }

        /// <summary>Keeps the stored in-game name fresh so links.json stays readable.</summary>
        public void Touch(string playerKey, string playerName)
        {
            if (_byKey.TryGetValue(playerKey, out var link) && link.PlayerName != playerName)
            {
                link.PlayerName = playerName;
                Save();
            }
        }

        public static LinkRegistry Load(string path, ILog log)
        {
            var registry = new LinkRegistry(path);
            try
            {
                // A broken file is kept as links.json.corrupt-… before the first new link writes over it.
                var links = SafeFile.ReadOrRecover(path, text => JsonSerializer.Deserialize<List<PlayerLink>>(text, Json),
                    m => log.Error(m + " Starting with no links.")) ?? new List<PlayerLink>();
                foreach (var l in links.Where(l => l != null && l.PlayerKey.Length > 0 && l.DiscordUserId.Length > 0))
                {
                    l.PlayerKey = l.PlayerKey.Trim().ToLowerInvariant();
                    registry._byKey[l.PlayerKey] = l;
                }
            }
            catch (Exception e)
            {
                log.Error($"Could not read {path}: {e.Message}. Starting with no links.");
            }
            return registry;
        }

        private void Save()
        {
            Version++;
            if (_path == null) return;
            SafeFile.WriteAllText(_path, JsonSerializer.Serialize(_byKey.Values.OrderBy(l => l.PlayerName).ToList(), Json));
        }
    }
}
