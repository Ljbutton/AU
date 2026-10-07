using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>One thing that makes a lobby worth showing, and how much.</summary>
    public sealed class PriorityRule
    {
        /// <summary>What it adds to the lobby's score (0–100).</summary>
        public double Points { get; set; }
        /// <summary>For one-off plays: how long it stays at full points before it starts to fade.</summary>
        public double HoldSeconds { get; set; }
        /// <summary>For rules with a limit (seconds left, task %, players left, gap between teams).</summary>
        public double? Threshold { get; set; }
        public bool On { get; set; } = true;
        /// <summary>What the rule is, for whoever edits the file.</summary>
        public string Note { get; set; } = "";

        public PriorityRule() { }
        public PriorityRule(double points, double hold, double? threshold, string note)
        {
            Points = points; HoldSeconds = hold; Threshold = threshold; Note = note;
        }
    }

    /// <summary>The score where each tier starts.</summary>
    public sealed class PriorityTiers
    {
        public double MustShow { get; set; } = 90;
        public double VeryHigh { get; set; } = 70;
        public double High { get; set; } = 50;
        public double Medium { get; set; } = 25;
    }

    /// <summary>
    /// Every weight and limit the caster's lobby ranking uses, in caster-priority.json next to
    /// The Button's settings. Written with these defaults the first time; edit it and The Button
    /// picks the changes up within a few seconds.
    /// </summary>
    public sealed class PriorityConfig
    {
        public const string FileName = "caster-priority.json";

        public string Note { get; set; } =
            "Lobby ranking for the caster tab. Each rule adds Points (0-100) to a lobby's score. One-off plays (kills, ejections) " +
            "stay at full points for HoldSeconds, then halve every HalfLifeSeconds. States (danger, meeting) count while they last, " +
            "then fade the same way. A lobby's score is its biggest rule plus OthersShare of the rest, but never into a higher tier than its biggest rule.";

        /// <summary>After a play's hold, its points halve every this many seconds.</summary>
        public double HalfLifeSeconds { get; set; } = 6;
        /// <summary>The share of every rule but the biggest that's added on top (so two things at once rank above one).</summary>
        public double OthersShare { get; set; } = 0.15;
        /// <summary>A lobby that hasn't sent anything for this long is offline.</summary>
        public double OfflineAfterSeconds { get; set; } = 6;
        public PriorityTiers Tiers { get; set; } = new PriorityTiers();

        public Dictionary<string, PriorityRule> Rules { get; set; } = Defaults();

        public static Dictionary<string, PriorityRule> Defaults() => new Dictionary<string, PriorityRule>
        {
            // MUST SHOW
            ["eject"] = new PriorityRule(100, 6, null, "Must show: an ejection (the reveal). A skipped or tied vote uses ejectSkipped."),
            ["gameEnd"] = new PriorityRule(100, 8, null, "Must show: the game ending."),
            ["winningKill"] = new PriorityRule(100, 6, null, "Must show: the kill that wins the game for the impostors."),
            // VERY HIGH
            ["danger"] = new PriorityRule(80, 0, null, "Very high: an impostor with kill ready next to a crewmate who's alone with them."),
            ["criticalSabotage"] = new PriorityRule(80, 0, 15, "Very high: reactor/O2 (or the like) with fewer than Threshold seconds left."),
            ["witnessedKill"] = new PriorityRule(80, 10, null, "Very high: a crewmate saw a kill (a meeting is probably coming)."),
            // HIGH
            ["oneKillFromWin"] = new PriorityRule(60, 0, null, "High: one more kill and the impostors win."),
            ["taskBar"] = new PriorityRule(60, 0, 85, "High: the task bar is at Threshold % or more."),
            ["finalPlayers"] = new PriorityRule(60, 0, 3, "High: Threshold or fewer players left alive."),
            ["meeting"] = new PriorityRule(60, 0, null, "High: a meeting is on."),
            ["witnessedVent"] = new PriorityRule(60, 6, null, "High: a crewmate saw an impostor vent."),
            // MEDIUM
            ["sabotage"] = new PriorityRule(35, 0, null, "Medium: any sabotage is on."),
            ["kill"] = new PriorityRule(40, 5, null, "Medium: a kill just happened (replay-worthy)."),
            ["closeCounts"] = new PriorityRule(35, 0, 2, "Medium: the crew are only Threshold players ahead of the impostors."),
            ["ejectSkipped"] = new PriorityRule(35, 4, null, "Medium: a vote with nobody ejected."),
            ["vent"] = new PriorityRule(25, 3, null, "Medium-low: an impostor vented."),
            ["gameStart"] = new PriorityRule(30, 4, null, "Medium-low: a game just started (the teams are set)."),
            // LOW
            ["inGame"] = new PriorityRule(12, 0, null, "Low: a game is running, nothing special."),
            ["lobby"] = new PriorityRule(3, 0, null, "Low: in the lobby, before the game or between rounds."),
        };

        public PriorityRule Rule(string key) =>
            Rules.TryGetValue(key, out var r) ? r : Defaults().TryGetValue(key, out var d) ? d : new PriorityRule { On = false };

        /// <summary>The highest score a lobby can reach when its top play scores <paramref name="top"/>: just under the next tier.</summary>
        public double CeilingFor(double top) =>
            top >= Tiers.MustShow ? 100 : top >= Tiers.VeryHigh ? Tiers.MustShow - 0.1 : top >= Tiers.High ? Tiers.VeryHigh - 0.1 : top >= Tiers.Medium ? Tiers.High - 0.1 : Tiers.Medium - 0.1;

        public string TierOf(double score) =>
            score >= Tiers.MustShow ? "must" : score >= Tiers.VeryHigh ? "veryHigh" : score >= Tiers.High ? "high" : score >= Tiers.Medium ? "medium" : "low";

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        public static PriorityConfig Parse(string json)
        {
            var c = JsonSerializer.Deserialize<PriorityConfig>(json, Json) ?? new PriorityConfig();
            // Rules missing from an older file get their defaults.
            var rules = new Dictionary<string, PriorityRule>(c.Rules ?? new Dictionary<string, PriorityRule>(), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Defaults()) if (!rules.ContainsKey(kv.Key)) rules[kv.Key] = kv.Value;
            c.Rules = rules;
            c.Tiers ??= new PriorityTiers();
            return c;
        }
    }

    /// <summary>The config file: written with defaults if missing, re-read when it changes. A broken file keeps the last good one.</summary>
    public sealed class PriorityConfigFile
    {
        private readonly string _path;
        private DateTime _stamp;
        private DateTime _nextCheck;
        public PriorityConfig Current { get; private set; } = new PriorityConfig();
        public string? Problem { get; private set; }

        public PriorityConfigFile(string path)
        {
            _path = path;
            Refresh(force: true);
        }

        public string Path => _path;

        public PriorityConfig Refresh(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && now < _nextCheck) return Current;
            _nextCheck = now.AddSeconds(2);
            try
            {
                if (!File.Exists(_path))
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                    File.WriteAllText(_path, new PriorityConfig().ToJson());
                }
                var stamp = File.GetLastWriteTimeUtc(_path);
                if (stamp == _stamp) return Current;
                _stamp = stamp;
                Current = PriorityConfig.Parse(File.ReadAllText(_path));
                Problem = null;
            }
            catch (Exception e)
            {
                Problem = $"{PriorityConfig.FileName} couldn't be read ({e.Message}); using the last good settings.";
            }
            return Current;
        }
    }
}
