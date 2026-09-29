using System.Collections.Generic;
using System.Linq;
using System.Text;
using TournamentTracker.Voice;

namespace TournamentTracker.Discord
{
    public sealed class StatusPlayer
    {
        public PlayerSnapshot Player { get; set; } = new PlayerSnapshot();
        public string? DiscordUserId { get; set; }

        /// <summary>Dead as far as everyone in the game knows (a body was found or they were ejected).</summary>
        public bool KnownDead { get; set; }
    }

    public sealed class StatusInfo
    {
        public VoicePhase Phase { get; set; }
        public string LobbyCode { get; set; } = "";
        public string Label { get; set; } = "";
        public int Round { get; set; }
        public string Map { get; set; } = "";
        public List<StatusPlayer> Players { get; set; } = new List<StatusPlayer>();
        public bool? AutoMuteOn { get; set; }
        public int Spectators { get; set; }
        public bool SpectatorsMuted { get; set; }
        public bool RefereeMode { get; set; }

        /// <summary>The referee ghost slot's player, shown apart from the players.</summary>
        public PlayerSnapshot? Referee { get; set; }
        public string CommandPrefix { get; set; } = "!";

        /// <summary>The bot's crewmate-head emoji IDs by colour, when it has them.</summary>
        public IReadOnlyDictionary<int, string>? Emojis { get; set; }

        /// <summary>Adds the "pick your colour" menu with this ID (only when the bot posts the message).</summary>
        public string? LinkMenuId { get; set; }
    }

    /// <summary>The live lobby message: who's playing, who they are on Discord, and what phase the game is in.</summary>
    public static class StatusFormatter
    {
        public static WebhookMessage Build(StatusInfo s)
        {
            var sb = new StringBuilder();
            var header = new List<string>();
            if (s.LobbyCode.Length > 0) header.Add($"**Code:** `{s.LobbyCode}`");
            if (s.Map.Length > 0) header.Add($"**Map:** {s.Map}");
            if (header.Count > 0) sb.AppendLine(string.Join(" · ", header)).AppendLine();

            if (s.RefereeMode && s.Phase != VoicePhase.Menu)
                sb.AppendLine("🎙️ **Referee speaking.** Everyone else is muted until the referee is done.").AppendLine();

            if (s.Phase == VoicePhase.Menu)
            {
                sb.AppendLine("The host closed the lobby.");
            }
            else if (s.Players.Count == 0)
            {
                sb.AppendLine("Waiting for players…");
            }
            else
            {
                foreach (var p in s.Players.OrderBy(p => p.Player.ColorId))
                {
                    string who = $"**{Colors.Name(p.Player.ColorId)}** {Escape(p.Player.Name)}";
                    if (EmojiId(s, p.Player.ColorId) is string emoji) who = $"<:{EmojiName(p.Player.ColorId)}:{emoji}> {who}";
                    if (p.KnownDead) who = $"💀 ~~{who}~~";
                    string discord = p.DiscordUserId != null ? $"<@{p.DiscordUserId}>" : "*not linked*";
                    sb.Append(who).Append(" · ").AppendLine(discord);
                }
            }

            if (s.Referee != null && s.Phase != VoicePhase.Menu)
                sb.AppendLine().AppendLine($"🎥 Referee (ghost): **{Colors.Name(s.Referee.ColorId)}** {Escape(s.Referee.Name)}");

            if (s.Spectators > 0)
            {
                sb.AppendLine().Append($"Spectators in voice: {s.Spectators}");
                if (s.SpectatorsMuted) sb.Append(" (muted during games)");
                sb.AppendLine();
            }

            int linked = s.Players.Count(p => p.DiscordUserId != null);
            var footer = new List<string>();
            if (s.AutoMuteOn.HasValue) footer.Add($"Automute {(s.AutoMuteOn.Value ? "on" : "OFF")}");
            if (s.Players.Count > 0) footer.Add($"{linked}/{s.Players.Count} linked");
            bool menu = s.LinkMenuId != null && s.Phase != VoicePhase.Menu && s.Players.Count > 0;
            if (s.Phase != VoicePhase.Menu && linked < s.Players.Count)
                footer.Add(menu ? "Not linked? Pick your colour below (or type /link)" : "Not linked? Type /link and your in-game name or colour");

            return new WebhookMessage
            {
                Username = ReportFormatter.BotName,
                Embeds = new List<Embed>
                {
                    new Embed
                    {
                        Title = (s.Label.Length > 0 ? s.Label + " — " : "") + (s.Round > 0 && s.Phase != VoicePhase.Menu ? $"Round {s.Round} · " : "") + Title(s.Phase),
                        Color = Color(s.Phase),
                        Description = ReportFormatter.Clip(sb.ToString().TrimEnd(), Embed.DescriptionLimit),
                        Footer = footer.Count > 0 ? new EmbedFooter { Text = string.Join(" · ", footer) } : null,
                    },
                },
                Components = menu ? LinkMenu(s) : null,
            };
        }

        /// <summary>The emoji name for a colour's crewmate head, e.g. "tt_red".</summary>
        public static string EmojiName(int colorId) => "tt_" + Colors.Name(colorId).ToLowerInvariant();

        private static string? EmojiId(StatusInfo s, int colorId) =>
            s.Emojis != null && s.Emojis.TryGetValue(colorId, out var id) && id.Length > 0 ? id : null;

        /// <summary>
        /// A menu of the colours in the lobby right now, like AutoMuteUs: picking one links
        /// whoever clicked to that player (replacing anyone linked to it before).
        /// </summary>
        private static List<object> LinkMenu(StatusInfo s)
        {
            var options = s.Players.OrderBy(p => p.Player.ColorId).Take(24).Select(p =>
            {
                string name = p.Player.Name.Length > 60 ? p.Player.Name.Substring(0, 60) : p.Player.Name;
                var option = new Dictionary<string, object>
                {
                    ["label"] = Colors.Name(p.Player.ColorId),
                    ["value"] = p.Player.ColorId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["description"] = name + (p.DiscordUserId != null ? " · linked" : ""),
                };
                if (EmojiId(s, p.Player.ColorId) is string emoji) option["emoji"] = new { id = emoji, name = EmojiName(p.Player.ColorId) };
                return (object)option;
            }).ToList();
            return new List<object>
            {
                new
                {
                    type = 1,
                    components = new object[]
                    {
                        new { type = 3, custom_id = s.LinkMenuId, placeholder = "Select your in-game colour", min_values = 1, max_values = 1, options },
                    },
                },
            };
        }

        private static string Title(VoicePhase phase) => phase switch
        {
            VoicePhase.Lobby => "Lobby open",
            VoicePhase.Tasks => "Game in progress",
            VoicePhase.Meeting => "Meeting",
            VoicePhase.GameOver => "Game over",
            _ => "Lobby closed",
        };

        private static int Color(VoicePhase phase) => phase switch
        {
            VoicePhase.Lobby => 0x22C55E,
            VoicePhase.Tasks => 0x3B82F6,
            VoicePhase.Meeting => 0xF59E0B,
            _ => 0x6B7280,
        };

        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("~", "\\~").Replace("`", "'").Replace("|", "\\|");
    }
}
