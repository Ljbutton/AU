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
        public string CommandPrefix { get; set; } = "!";
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
                    if (p.KnownDead) who = $"💀 ~~{who}~~";
                    string discord = p.DiscordUserId != null ? $"<@{p.DiscordUserId}>" : "*not linked*";
                    sb.Append(who).Append(" · ").AppendLine(discord);
                }
            }

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
            if (s.Phase != VoicePhase.Menu && linked < s.Players.Count) footer.Add($"Link with {s.CommandPrefix}link <discord name> in the lobby chat");

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
