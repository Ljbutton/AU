using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.Discord
{
    /// <summary>A slash command someone used in the Discord server, as the gateway delivers it.</summary>
    public sealed class Interaction
    {
        public string Id { get; set; } = "";
        public string Token { get; set; } = "";
        public string GuildId { get; set; } = "";
        public string Command { get; set; } = "";
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
        /// <summary>The member's server permissions, as Discord's bit field.</summary>
        public ulong Permissions { get; set; }
        /// <summary>String options by name; user options hold the user's ID.</summary>
        public Dictionary<string, string> Options { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Display names of users picked in user options, by ID.</summary>
        public Dictionary<string, string> ResolvedNames { get; set; } = new Dictionary<string, string>();

        public const ulong MuteMembers = 1UL << 22;
        public const ulong Administrator = 1UL << 3;

        /// <summary>A referee or organiser: someone who can mute members in the server.</summary>
        public bool IsStaff => (Permissions & (MuteMembers | Administrator)) != 0;

        public string? Option(string name) => Options.TryGetValue(name, out var v) && v.Length > 0 ? v : null;

        /// <summary>Reads an INTERACTION_CREATE payload. Null when it isn't a slash command in a server.</summary>
        public static Interaction? Parse(JsonElement d)
        {
            if (Int(d, "type") != 2 || !d.TryGetProperty("data", out var data)) return null;
            if (!d.TryGetProperty("member", out var member) || !member.TryGetProperty("user", out var user)) return null;
            var i = new Interaction
            {
                Id = Str(d, "id") ?? "",
                Token = Str(d, "token") ?? "",
                GuildId = Str(d, "guild_id") ?? "",
                Command = (Str(data, "name") ?? "").ToLowerInvariant(),
                UserId = Str(user, "id") ?? "",
                UserName = Str(member, "nick") ?? Str(user, "global_name") ?? Str(user, "username") ?? "",
            };
            if (ulong.TryParse(Str(member, "permissions"), out var perms)) i.Permissions = perms;
            if (data.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                foreach (var o in options.EnumerateArray())
                    if (Str(o, "name") is string name && o.TryGetProperty("value", out var v))
                        i.Options[name] = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
            if (data.TryGetProperty("resolved", out var resolved))
            {
                if (resolved.TryGetProperty("users", out var users))
                    foreach (var u in users.EnumerateObject())
                        i.ResolvedNames[u.Name] = Str(u.Value, "global_name") ?? Str(u.Value, "username") ?? u.Name;
                if (resolved.TryGetProperty("members", out var members))
                    foreach (var m in members.EnumerateObject())
                        if (Str(m.Value, "nick") is string nick) i.ResolvedNames[m.Name] = nick;
            }
            return i.Id.Length > 0 && i.Token.Length > 0 ? i : null;
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
    }

    /// <summary>The server's slash commands for linking Among Us players to Discord accounts.</summary>
    public static class SlashCommands
    {
        /// <summary>The body for Discord's bulk overwrite of a server's commands.</summary>
        public static object[] Definitions() => new object[]
        {
            new
            {
                name = "link",
                description = "Link your Discord to your Among Us player (join the lobby first)",
                options = new object[]
                {
                    new { type = 3, name = "player", description = "Your in-game name or colour (for example Red)", required = true },
                    new { type = 6, name = "user", description = "Referees: link someone else", required = false },
                },
            },
            new
            {
                name = "unlink",
                description = "Unlink your Discord account from your Among Us player",
                options = new object[]
                {
                    new { type = 6, name = "user", description = "Referees: unlink someone else", required = false },
                },
            },
        };
    }
}
