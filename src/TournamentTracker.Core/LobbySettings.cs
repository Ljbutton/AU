using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace TournamentTracker
{
    /// <summary>
    /// The tournament's game settings. Carried in the setup code; in tournament and
    /// preliminary mode the host's mod keeps the lobby on them and checks them when a game
    /// starts. Anything left null isn't enforced. Only the host's game is touched, and only
    /// while a tournament or preliminary setup code is in use.
    /// </summary>
    public sealed class LobbySettings
    {
        [JsonPropertyName("imp")] public int? Impostors { get; set; }
        [JsonPropertyName("ce")] public bool? ConfirmEjects { get; set; }
        [JsonPropertyName("em")] public int? EmergencyMeetings { get; set; }
        [JsonPropertyName("av")] public bool? AnonymousVotes { get; set; }
        [JsonPropertyName("ec")] public int? EmergencyCooldown { get; set; }
        [JsonPropertyName("dt")] public int? DiscussionTime { get; set; }
        [JsonPropertyName("vt")] public int? VotingTime { get; set; }
        [JsonPropertyName("ps")] public float? PlayerSpeed { get; set; }
        [JsonPropertyName("cv")] public float? CrewmateVision { get; set; }
        [JsonPropertyName("iv")] public float? ImpostorVision { get; set; }
        [JsonPropertyName("kc")] public float? KillCooldown { get; set; }
        /// <summary>0 short, 1 medium, 2 long.</summary>
        [JsonPropertyName("kd")] public int? KillDistance { get; set; }
        [JsonPropertyName("vis")] public bool? VisualTasks { get; set; }
        [JsonPropertyName("ct")] public int? CommonTasks { get; set; }
        [JsonPropertyName("lt")] public int? LongTasks { get; set; }
        [JsonPropertyName("st")] public int? ShortTasks { get; set; }
        /// <summary>Every special role (Engineer, Scientist, Shapeshifter, Guardian Angel…) at 0, apart from those in <see cref="Roles"/>.</summary>
        [JsonPropertyName("ro")] public bool? RolesOff { get; set; }
        /// <summary>0 normal, 1 only in meetings, 2 never.</summary>
        [JsonPropertyName("tb")] public int? TaskBarMode { get; set; }
        [JsonPropertyName("gdt")] public bool? GhostsDoTasks { get; set; }

        /// <summary>
        /// Roles that are on, by the game's role name ("Engineer", "Shapeshifter"…): [most per game, chance %].
        /// A role that isn't listed is off when <see cref="RolesOff"/> is set.
        /// </summary>
        [JsonPropertyName("rl")] public Dictionary<string, int[]>? Roles { get; set; }

        /// <summary>
        /// Role options by the game's option name ("EngineerCooldown", "ShapeshifterLeaveSkin"…): numbers,
        /// with 1/0 for on/off. Names the game doesn't have are skipped.
        /// </summary>
        [JsonPropertyName("opt")] public Dictionary<string, double>? Options { get; set; }

        /// <summary>The roles the game has (2026.9), with their options, for the setup-code maker and the messages.</summary>
        public static readonly IReadOnlyDictionary<string, string> RoleLabels = new Dictionary<string, string>
        {
            ["Engineer"] = "Engineer", ["Scientist"] = "Scientist", ["GuardianAngel"] = "Guardian Angel", ["Noisemaker"] = "Noisemaker",
            ["Tracker"] = "Tracker", ["Detective"] = "Detective", ["Judge"] = "Judge", ["SpiritGuide"] = "Spirit Guide",
            ["Shapeshifter"] = "Shapeshifter", ["Phantom"] = "Phantom", ["Viper"] = "Viper",
        };

        /// <summary>The settings the tournament uses today.</summary>
        public static LobbySettings TournamentDefaults() => new LobbySettings
        {
            Impostors = 2, ConfirmEjects = false, EmergencyMeetings = 1, AnonymousVotes = true, EmergencyCooldown = 20,
            DiscussionTime = 15, VotingTime = 150, PlayerSpeed = 1.25f, CrewmateVision = 0.25f, ImpostorVision = 1f,
            KillCooldown = 25f, KillDistance = 0, VisualTasks = false, CommonTasks = 2, LongTasks = 3, ShortTasks = 5, RolesOff = true,
        };

        private static readonly string[] Distances = { "Short", "Medium", "Long" };

        /// <summary>
        /// What in <paramref name="actual"/> doesn't match these settings, as lines like
        /// "Kill cooldown 20 (should be 25)". Empty when everything that's set matches.
        /// </summary>
        public List<string> Differences(LobbySettings actual)
        {
            var list = new List<string>();
            void Int(string name, int? want, int? got)
            {
                if (want.HasValue && got.HasValue && want != got) list.Add($"{name} {got} (should be {want})");
            }
            void Float(string name, float? want, float? got, string unit = "")
            {
                if (want.HasValue && got.HasValue && Math.Abs(want.Value - got.Value) > 0.001f)
                    list.Add($"{name} {F(got.Value)}{unit} (should be {F(want.Value)}{unit})");
            }
            void Bool(string name, bool? want, bool? got)
            {
                if (want.HasValue && got.HasValue && want != got) list.Add($"{name} {(got.Value ? "on" : "off")} (should be {(want.Value ? "on" : "off")})");
            }

            Int("Impostors", Impostors, actual.Impostors);
            Bool("Confirm ejects", ConfirmEjects, actual.ConfirmEjects);
            Int("Emergency meetings", EmergencyMeetings, actual.EmergencyMeetings);
            Bool("Anonymous votes", AnonymousVotes, actual.AnonymousVotes);
            Int("Emergency cooldown", EmergencyCooldown, actual.EmergencyCooldown);
            Int("Discussion time", DiscussionTime, actual.DiscussionTime);
            Int("Voting time", VotingTime, actual.VotingTime);
            Float("Player speed", PlayerSpeed, actual.PlayerSpeed, "x");
            Float("Crewmate vision", CrewmateVision, actual.CrewmateVision, "x");
            Float("Impostor vision", ImpostorVision, actual.ImpostorVision, "x");
            Float("Kill cooldown", KillCooldown, actual.KillCooldown, "s");
            if (KillDistance.HasValue && actual.KillDistance.HasValue && KillDistance != actual.KillDistance)
                list.Add($"Kill distance {Distance(actual.KillDistance.Value)} (should be {Distance(KillDistance.Value)})");
            Bool("Visual tasks", VisualTasks, actual.VisualTasks);
            Int("Common tasks", CommonTasks, actual.CommonTasks);
            Int("Long tasks", LongTasks, actual.LongTasks);
            Int("Short tasks", ShortTasks, actual.ShortTasks);
            if (RolesOff == true && actual.RolesOff == false) list.Add("Special roles on (should all be off" + (Roles?.Count > 0 ? " but " + string.Join(", ", Roles.Keys.Select(RoleName)) : "") + ")");
            if (TaskBarMode.HasValue && actual.TaskBarMode.HasValue && TaskBarMode != actual.TaskBarMode)
                list.Add($"Task bar {TaskBar(actual.TaskBarMode.Value)} (should be {TaskBar(TaskBarMode.Value)})");
            Bool("Ghosts do tasks", GhostsDoTasks, actual.GhostsDoTasks);
            if (Roles != null && actual.Roles != null)
                foreach (var role in Roles)
                {
                    if (!actual.Roles.TryGetValue(role.Key, out var got) || role.Value.Length < 2 || got.Length < 2) continue;
                    if (got[0] != role.Value[0] || got[1] != role.Value[1])
                        list.Add($"{RoleName(role.Key)} {got[0]} at {got[1]}% (should be {role.Value[0]} at {role.Value[1]}%)");
                }
            if (Options != null && actual.Options != null)
                foreach (var opt in Options)
                {
                    if (!actual.Options.TryGetValue(opt.Key, out var got) || Math.Abs(got - opt.Value) <= 0.001) continue;
                    list.Add($"{OptionName(opt.Key)} {F((float)got)} (should be {F((float)opt.Value)})");
                }
            return list;
        }

        public string Describe() =>
            $"{Impostors} impostors, kill cooldown {F(KillCooldown ?? 0)}s, {Distance(KillDistance ?? 0).ToLowerInvariant()} kill distance, " +
            $"{CommonTasks}/{LongTasks}/{ShortTasks} tasks";

        private static readonly string[] TaskBars = { "always", "in meetings", "never" };
        private static string TaskBar(int t) => t >= 0 && t < TaskBars.Length ? TaskBars[t] : t.ToString(CultureInfo.InvariantCulture);
        public static string RoleName(string role) => RoleLabels.TryGetValue(role, out var label) ? label : role;

        /// <summary>"EngineerInVentMaxTime" → "Engineer in vent max time".</summary>
        public static string OptionName(string option)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in option)
            {
                if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ').Append(char.ToLowerInvariant(c));
                else sb.Append(c);
            }
            return sb.ToString().Replace("guardian angel", "Guardian Angel").Replace("spirit guide", "Spirit Guide");
        }

        private static string Distance(int d) => d >= 0 && d < Distances.Length ? Distances[d] : d.ToString(CultureInfo.InvariantCulture);
        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
