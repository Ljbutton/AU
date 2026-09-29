using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TournamentTracker.Setup
{
    /// <summary>
    /// Everything a host needs, in one line of text the organiser hands out ("TT1-…"). It
    /// replaces editing the config file: the installer asks for it, or the host copies it
    /// and types !setup. Two kinds:
    /// <list type="bullet">
    /// <item>Preliminary codes: a tournament ID, the server name and the organiser's webhook. Safe to hand to anyone running a preliminary.</item>
    /// <item>Tournament host codes: also the organiser's bot, so automute and the shared standings work. Give these only to trusted hosts, privately.</item>
    /// </list>
    /// The code is only encoded, not encrypted: whoever has it can read what's inside.
    /// </summary>
    public sealed class SetupCode
    {
        public const string Prefix = "TT1-";
        public const string FileName = "setup-code.txt";

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>"prelim" or "tournament".</summary>
        [JsonPropertyName("m")] public string Mode { get; set; } = "prelim";
        [JsonPropertyName("id")] public string TournamentId { get; set; } = "";
        [JsonPropertyName("n")] public string TournamentName { get; set; } = "";
        [JsonPropertyName("srv")] public string? Server { get; set; }

        /// <summary>Where game reports go (a webhook in the organiser's server).</summary>
        [JsonPropertyName("wh")] public string Webhook { get; set; } = "";

        // Tournament host codes only.
        [JsonPropertyName("g")] public string? GuildId { get; set; }
        [JsonPropertyName("bt")] public List<string>? BotTokens { get; set; }

        /// <summary>Preliminaries with automute: where the live lobby message (with the colour menu) goes. Optional.</summary>
        [JsonPropertyName("sw")] public string? StatusWebhook { get; set; }
        [JsonPropertyName("rc")] public string? ResultsChannelId { get; set; }
        [JsonPropertyName("pc")] public List<string>? PrelimChannelIds { get; set; }
        [JsonPropertyName("adv")] public int? AdvanceCount { get; set; }
        [JsonPropertyName("gpr")] public int? GamesPerRound { get; set; }

        /// <summary>Game settings the host's lobby is locked to (both kinds of code).</summary>
        [JsonPropertyName("set")] public LobbySettings? Lobby { get; set; }

        /// <summary>Fair impostor rotation within each round.</summary>
        [JsonPropertyName("rot")] public bool? ImpostorRotation { get; set; }

        /// <summary>Tournament host codes: this host's mod answers the results-channel commands. Give it to one host (you).</summary>
        [JsonPropertyName("lead")] public bool? Lead { get; set; }
        [JsonPropertyName("ref")] public List<string>? RefereeUserIds { get; set; }

        /// <summary>Point values, so every host scores the same way. Missing: the defaults.</summary>
        [JsonPropertyName("sc")] public ScoringRules? Scoring { get; set; }

        public bool IsTournament => string.Equals(Mode, "tournament", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// An administration code: unlocks the organiser's view in The Button (every lobby live,
        /// standings, referee actions, the caster overlay). Not a host code: a game ignores it.
        /// </summary>
        public bool IsAdmin => string.Equals(Mode, "admin", StringComparison.OrdinalIgnoreCase);

        public string Describe() =>
            IsAdmin ? $"{TournamentName} (administration)" :
            IsTournament
                ? $"{TournamentName} (tournament host{(BotTokens?.Count > 0 ? ", automute on" : "")})"
                : $"{TournamentName} (preliminary{(string.IsNullOrWhiteSpace(Server) ? "" : " in " + Server)}{(BotTokens?.Count > 0 ? ", automute on" : "")})";

        public string Encode()
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json));
            return Prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>Reads a code, ignoring spaces and line breaks picked up when copying.</summary>
        public static bool TryParse(string? text, out SetupCode code, out string error)
        {
            code = new SetupCode();
            error = "";
            string t = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
            int start = t.IndexOf(Prefix, StringComparison.Ordinal);
            if (start < 0)
            {
                error = "That isn't a setup code (they start with TT1-).";
                return false;
            }
            string body = t.Substring(start + Prefix.Length).Replace('-', '+').Replace('_', '/');
            body = body.PadRight(body.Length + (4 - body.Length % 4) % 4, '=');
            try
            {
                var parsed = JsonSerializer.Deserialize<SetupCode>(Encoding.UTF8.GetString(Convert.FromBase64String(body)), Json);
                if (parsed == null) throw new JsonException("empty");
                code = parsed;
            }
            catch (Exception)
            {
                error = "That setup code is damaged. Copy the whole code again.";
                return false;
            }
            if (code.TournamentId.Trim().Length == 0 || code.TournamentName.Trim().Length == 0)
            {
                error = "That setup code has no tournament in it. Ask the organiser for a new one.";
                return false;
            }
            if (code.IsAdmin)
            {
                if ((code.BotTokens?.Count ?? 0) == 0 || string.IsNullOrWhiteSpace(code.ResultsChannelId))
                {
                    error = "That administration code has no bot or results channel in it. Ask the organiser for a new one.";
                    return false;
                }
                return true;
            }
            if (!code.Webhook.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                error = "That setup code has no Discord channel in it. Ask the organiser for a new one.";
                return false;
            }
            return true;
        }

        /// <summary>Overrides the config file's settings with the code's.</summary>
        public void ApplyTo(TrackerSettings s)
        {
            s.TournamentId = TournamentId;
            s.TournamentName = TournamentName;
            s.StatsWebhookUrl = Webhook;
            if (Scoring != null) s.Scoring = Scoring;
            s.LobbySettings = Lobby;
            s.ImpostorRotation = ImpostorRotation == true;

            if (IsTournament)
            {
                s.Mode = TrackerMode.Tournament;
                if (GuildId != null) s.AutoMute.GuildId = GuildId;
                if (BotTokens?.Count > 0)
                {
                    s.AutoMute.BotTokens = BotTokens.ToList();
                    s.AutoMute.Enabled = true;
                }
                if (ResultsChannelId != null) s.ResultsChannelId = ResultsChannelId;
                if (PrelimChannelIds != null) s.PrelimChannelIds = PrelimChannelIds.ToList();
                if (AdvanceCount.HasValue) s.AdvanceCount = AdvanceCount.Value;
                if (GamesPerRound.HasValue) s.GamesPerRound = GamesPerRound.Value;
                if (RefereeUserIds != null) s.AutoMute.RefereeUserIds = RefereeUserIds.ToList();
                s.Lead = Lead == true;
            }
            else
            {
                // Preliminaries: the report (with the game's data file) goes to the organiser's
                // server and a summary shows in the host's chat. Nothing else is posted there:
                // the combined preliminary leaderboard is kept by the organiser's scheduled job.
                s.Mode = TrackerMode.Preliminary;
                s.ServerName = Server ?? "";
                s.LiveStatus = false;
                s.LiveFeedWebhookUrl = "";
                s.PostLeaderboardAfterEachGame = false;
                s.ResultsChannelId = "";

                // Automute is optional in preliminaries: only when the organiser put a bot in the
                // code. Everything else works the same without it (players can use AutoMuteUs).
                if (BotTokens?.Count > 0 && !string.IsNullOrWhiteSpace(GuildId))
                {
                    s.AutoMute.GuildId = GuildId!;
                    s.AutoMute.BotTokens = BotTokens.ToList();
                    s.AutoMute.Enabled = true;
                    if (RefereeUserIds != null) s.AutoMute.RefereeUserIds = RefereeUserIds.ToList();
                    // A channel for the live lobby message, so players can pick their colour to link.
                    if (StatusWebhook != null && StatusWebhook.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        s.StatusWebhookUrl = StatusWebhook;
                        s.LiveStatus = true;
                    }
                }
            }
        }

        public static SetupCode? Load(string dataDir, ILog log)
        {
            string path = Path.Combine(dataDir, FileName);
            if (!File.Exists(path)) return null;
            if (TryParse(File.ReadAllText(path), out var code, out var error))
            {
                if (!code.IsAdmin) return code;
                error = "it's an administration code (it goes in The Button's Settings → Administration), not a host code";
            }
            log.Warn($"Ignoring {FileName}: {error}");
            return null;
        }

        public static void Save(string dataDir, string text)
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, FileName), text.Trim());
        }

        public static void Clear(string dataDir)
        {
            string path = Path.Combine(dataDir, FileName);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
