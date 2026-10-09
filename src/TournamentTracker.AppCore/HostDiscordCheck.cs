using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TournamentTracker.Setup;

namespace TournamentTracker.App
{
    /// <summary>
    /// Checks the host's own bots and channels with Discord before The Button saves them
    /// (Settings → Your Discord): each token must log in and its bot must be in the server, each
    /// webhook must exist. Only what passes is kept, so a typo never takes a working bot offline.
    /// </summary>
    public static class HostDiscordCheck
    {
        public const string Api = "https://discord.com/api/v10";

        public sealed class Result
        {
            public HostDiscord Saved { get; set; } = new HostDiscord();
            public List<string> BotNames { get; set; } = new List<string>();
            public string PublicName { get; set; } = "";
            public string StatusName { get; set; } = "";
            public List<string> Problems { get; set; } = new List<string>();
            public bool Ok => Problems.Count == 0;
        }

        /// <param name="typed">What the host typed; an empty token slot keeps the saved one in that slot.</param>
        /// <param name="saved">What was saved before (may be null).</param>
        /// <param name="remove">Token slots (0–2) to take out.</param>
        /// <param name="codeGuild">The setup code's server, used when the host leaves theirs empty.</param>
        public static async Task<Result> CheckAsync(HttpClient http, HostDiscord typed, HostDiscord? saved, ICollection<int> remove, string? codeGuild = null, string api = Api)
        {
            var r = new Result();
            string guild = (typed.GuildId ?? "").Trim();
            if (guild.Length > 0 && !HostDiscord.IsId(guild)) r.Problems.Add("The server ID is a long number: right-click the server → Copy Server ID (Developer Mode on).");
            else r.Saved.GuildId = guild;
            string checkGuild = r.Saved.GuildId.Length > 0 ? r.Saved.GuildId : HostDiscord.IsId(codeGuild) ? codeGuild! : "";

            for (int slot = 0; slot < HostDiscord.MaxBots; slot++)
            {
                if (remove.Contains(slot)) continue;
                string token = slot < typed.BotTokens.Count ? (typed.BotTokens[slot] ?? "").Trim() : "";
                bool fresh = token.Length > 0;
                if (!fresh) token = saved != null && slot < saved.BotTokens.Count ? saved.BotTokens[slot] : "";
                if (token.Length == 0) continue;
                if (token.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase)) token = token.Substring(4).Trim();
                string label = $"Bot {slot + 1}";
                if (!HostDiscord.LooksLikeToken(token)) { r.Problems.Add($"{label}: that isn't a bot token. Developer Portal → your app → Bot → Reset Token, then copy it."); continue; }

                var me = await GetAsync(http, $"{api}/users/@me", token).ConfigureAwait(false);
                if (me.Status == 401) { r.Problems.Add($"{label}: Discord refused that token (it was reset or mistyped). Copy a new one from the Developer Portal."); continue; }
                if (me.Json == null)
                {
                    // Discord unreachable: keep a token that was already saved, refuse a new one we couldn't check.
                    if (!fresh) { r.Saved.BotTokens.Add(token); r.BotNames.Add(label); }
                    r.Problems.Add($"{label}: couldn't reach Discord to check it. Try again in a minute.");
                    continue;
                }
                string name = Str(me.Json.Value, "username") ?? label;
                if (checkGuild.Length > 0)
                {
                    var guilds = await GetAsync(http, $"{api}/users/@me/guilds", token).ConfigureAwait(false);
                    bool inServer = guilds.Json?.ValueKind == JsonValueKind.Array && guilds.Json.Value.EnumerateArray().Any(g => Str(g, "id") == checkGuild);
                    if (guilds.Json != null && !inServer) { r.Problems.Add($"{label} ({name}) isn't in that server yet: invite it with the link in the README, then Save again."); continue; }
                }
                if (r.Saved.BotTokens.Contains(token)) continue;
                r.Saved.BotTokens.Add(token);
                r.BotNames.Add(name);
            }
            if (r.Saved.BotTokens.Count > 0 && checkGuild.Length == 0 && !r.Problems.Any(p => p.StartsWith("The server ID")))
                r.Problems.Add("Add the server ID too: the bots need to know which server to mute in (unless your setup code already has one).");

            (r.Saved.PublicWebhook, r.PublicName) = await WebhookAsync(http, typed.PublicWebhook, saved?.PublicWebhook, "Public report channel", r.Problems).ConfigureAwait(false);
            (r.Saved.StatusWebhook, r.StatusName) = await WebhookAsync(http, typed.StatusWebhook, saved?.StatusWebhook, "Live lobby channel", r.Problems).ConfigureAwait(false);
            return r;
        }

        /// <summary>"" to remove, null to keep what was saved.</summary>
        private static async Task<(string Url, string Name)> WebhookAsync(HttpClient http, string? typed, string? saved, string what, List<string> problems)
        {
            if (typed == null) return (saved ?? "", saved != null && saved.Length > 0 ? what : "");
            string url = typed.Trim();
            if (url.Length == 0) return ("", "");
            if (!HostDiscord.IsWebhook(url)) { problems.Add($"{what}: that isn't a webhook URL. Channel settings → Integrations → Webhooks → Copy Webhook URL."); return (saved ?? "", ""); }
            var hook = await GetAsync(http, url, null).ConfigureAwait(false);
            if (hook.Status == 401 || hook.Status == 404) { problems.Add($"{what}: Discord says that webhook doesn't exist (deleted?). Make a new one and copy it again."); return (saved ?? "", ""); }
            string name = hook.Json != null ? Str(hook.Json.Value, "name") ?? what : what;
            return (url, name);
        }

        private static async Task<(int Status, JsonElement? Json)> GetAsync(HttpClient http, string url, string? botToken)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (botToken != null) req.Headers.TryAddWithoutValidation("Authorization", "Bot " + botToken);
                using var res = await http.SendAsync(req).ConfigureAwait(false);
                string body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) return ((int)res.StatusCode, null);
                return ((int)res.StatusCode, JsonDocument.Parse(body).RootElement.Clone());
            }
            catch (Exception) { return (0, null); }
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
