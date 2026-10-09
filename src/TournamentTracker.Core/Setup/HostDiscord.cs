using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TournamentTracker.Setup
{
    /// <summary>
    /// The host's own Discord, set in The Button (Settings → Your Discord) instead of the setup
    /// code: their bots, their server, their public report channel and live lobby channel. The
    /// organiser no longer needs anyone's bot token, and a host can change bots without a new
    /// code. Saved next to the setup code, where the mod reads it; what's set here wins over the
    /// code's bot (the code's channels for the organiser stay as they are).
    /// </summary>
    public sealed class HostDiscord
    {
        public const string FileName = "host-discord.json";
        public const int MaxBots = 3;

        [JsonPropertyName("guild")] public string GuildId { get; set; } = "";
        [JsonPropertyName("bots")] public List<string> BotTokens { get; set; } = new List<string>();
        /// <summary>The host's own channel for game reports (players can read it).</summary>
        [JsonPropertyName("public")] public string PublicWebhook { get; set; } = "";
        /// <summary>The host's channel for the live lobby message with the colour menu.</summary>
        [JsonPropertyName("status")] public string StatusWebhook { get; set; } = "";

        [JsonIgnore] public bool HasBot => BotTokens.Count > 0 && IsId(GuildId);

        public static bool IsId(string? s) => !string.IsNullOrWhiteSpace(s) && s!.Length >= 15 && s.Length <= 21 && s.All(char.IsDigit);
        public static bool IsWebhook(string? s) => s != null && System.Text.RegularExpressions.Regex.IsMatch(s.Trim(), @"^https://[\w.-]+/api/webhooks/\d+/[\w-]+");
        public static bool LooksLikeToken(string? s) => s != null && s.Trim().Split('.').Length >= 3 && s.Trim().Length >= 50;

        /// <summary>Only what's usable: up to three tokens, real IDs and webhooks.</summary>
        public HostDiscord Clean() => new HostDiscord
        {
            GuildId = IsId(GuildId?.Trim()) ? GuildId!.Trim() : "",
            BotTokens = (BotTokens ?? new List<string>()).Select(t => (t ?? "").Trim()).Where(LooksLikeToken).Distinct().Take(MaxBots).ToList(),
            PublicWebhook = IsWebhook(PublicWebhook) ? PublicWebhook.Trim() : "",
            StatusWebhook = IsWebhook(StatusWebhook) ? StatusWebhook.Trim() : "",
        };

        [JsonIgnore] public bool IsEmpty => BotTokens.Count == 0 && GuildId.Length == 0 && PublicWebhook.Length == 0 && StatusWebhook.Length == 0;

        /// <summary>
        /// On top of the setup code: the host's bots and server replace the code's bot; their live
        /// lobby channel is used when the code has none; their public report channel takes the
        /// reports when the code has nowhere for them, and in preliminaries gets a copy of each
        /// report (the code's channel is the organiser's private one).
        /// </summary>
        public void ApplyTo(TrackerSettings s)
        {
            var me = Clean();
            // No server typed in: the code's (a tournament host's bots join the organiser's server).
            if (me.GuildId.Length == 0 && IsId(s.AutoMute.GuildId)) me.GuildId = s.AutoMute.GuildId;
            if (me.HasBot)
            {
                s.AutoMute.GuildId = me.GuildId;
                s.AutoMute.BotTokens = me.BotTokens.ToList();
                s.AutoMute.Enabled = true;
            }
            if (me.StatusWebhook.Length > 0 && string.IsNullOrWhiteSpace(s.StatusWebhookUrl))
                s.StatusWebhookUrl = me.StatusWebhook;
            if (s.Mode == TrackerMode.Preliminary)
            {
                s.PublicWebhookUrl = me.PublicWebhook;
                // The live lobby message (to pick a colour and link) needs the bot and a channel.
                if (s.AutoMute.IsConfigured && !string.IsNullOrWhiteSpace(s.StatusWebhookUrl)) s.LiveStatus = true;
            }
            else if (me.PublicWebhook.Length > 0 && string.IsNullOrWhiteSpace(s.StatsWebhookUrl))
            {
                s.StatsWebhookUrl = me.PublicWebhook;
            }
        }

        public static HostDiscord? Load(string dataDir)
        {
            try
            {
                string path = Path.Combine(dataDir, FileName);
                if (!File.Exists(path)) return null;
                var read = JsonSerializer.Deserialize<HostDiscord>(File.ReadAllText(path));
                return read?.Clean();
            }
            catch (Exception) { return null; }
        }

        public void Save(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            string path = Path.Combine(dataDir, FileName);
            var clean = Clean();
            if (clean.IsEmpty) { if (File.Exists(path)) File.Delete(path); return; }
            File.WriteAllText(path, JsonSerializer.Serialize(clean));
        }
    }
}
