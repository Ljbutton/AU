using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>Part 23: what the Twitch side does, in twitch.json next to The Button's other files.</summary>
    public sealed class TwitchSettings
    {
        public const string FileName = "twitch.json";

        /// <summary>The master switch: nothing goes to Twitch (and nothing starts by itself).</summary>
        public bool Off { get; set; }
        /// <summary>Your Twitch application's Client ID (dev.twitch.tv → Your Console → Register, client type Public).</summary>
        public string ClientId { get; set; } = "";
        /// <summary>Fake Twitch: polls, predictions, chat votes and redemptions are made up here; nothing is sent.</summary>
        public bool TestMode { get; set; }
        /// <summary>Your stream delay: every action waits this long after what it follows (0: no delay).</summary>
        public double DelaySeconds { get; set; }
        /// <summary>The lobby predictions are about: one picked here, or empty for the one on stream (else the top one).</summary>
        public string Featured { get; set; } = "";

        /// <summary>A game's prediction stays open this long after it starts (Twitch allows 30–1800 s).</summary>
        public int PredictionSeconds { get; set; } = 90;
        /// <summary>The "who's the impostor?" poll and the !sus vote run this long into a meeting (polls: 15–1800 s).</summary>
        public int MeetingSeconds { get; set; } = 40;
        /// <summary>"auto" (a poll with up to 5 players alive, else !sus), "poll", "chat" or "both".</summary>
        public string MeetingMode { get; set; } = "auto";
        /// <summary>Say whether chat was right straight after the ejection. Only when the lobbies confirm ejects; otherwise it's said when the game ends.</summary>
        public bool RevealAtEject { get; set; }
        public int LobbyPollSeconds { get; set; } = 60;
        /// <summary>"Which lobby next?" at most this often (minutes), in a calm moment.</summary>
        public int LobbyPollEveryMinutes { get; set; } = 8;
        public int MvpPollSeconds { get; set; } = 90;
        public int ReplayCost { get; set; } = 2000;
        public int ShoutoutCost { get; set; } = 1000;
        /// <summary>More words the shoutout filter turns away (a short list is built in).</summary>
        public List<string> BannedWords { get; set; } = new List<string>();

        /// <summary>Each feature on or off.</summary>
        public Dictionary<string, bool> Features { get; set; } = DefaultFeatures();
        /// <summary>Which start by themselves (the rest from the caster tab).</summary>
        public Dictionary<string, bool> Auto { get; set; } = DefaultAuto();

        public static Dictionary<string, bool> DefaultFeatures() => new Dictionary<string, bool>
        {
            ["predictions"] = true, ["roundPredictions"] = true, ["meetingPolls"] = true, ["chatVote"] = true,
            ["lobbyPoll"] = true, ["mvpPoll"] = true, ["replayReward"] = true, ["shoutoutReward"] = true, ["detectives"] = true,
        };

        public static Dictionary<string, bool> DefaultAuto() => new Dictionary<string, bool>
        {
            ["predictions"] = true, ["meetings"] = true, ["lobbyPoll"] = false, ["mvpPoll"] = true,
        };

        public static readonly Dictionary<string, string> FeatureNames = new Dictionary<string, string>
        {
            ["predictions"] = "Game predictions", ["roundPredictions"] = "Round predictions", ["meetingPolls"] = "Who's the impostor? poll",
            ["chatVote"] = "!sus chat vote", ["lobbyPoll"] = "Which lobby next?", ["mvpPoll"] = "Round MVP poll",
            ["replayReward"] = "Request a replay (points)", ["shoutoutReward"] = "Shoutout a player (points)", ["detectives"] = "Chat Detective",
        };

        public bool Feature(string name) => !Off && Features.TryGetValue(name, out var on) && on;
        public bool AutoOn(string name) => !Off && Auto.TryGetValue(name, out var on) && on;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        public static TwitchSettings Load(string? path)
        {
            TwitchSettings s;
            try
            {
                s = path != null && File.Exists(path) ? JsonSerializer.Deserialize<TwitchSettings>(File.ReadAllText(path), Json) ?? new TwitchSettings() : new TwitchSettings();
            }
            catch (Exception) { s = new TwitchSettings(); }
            s.Features ??= DefaultFeatures();
            s.Auto ??= DefaultAuto();
            foreach (var kv in DefaultFeatures()) if (!s.Features.ContainsKey(kv.Key)) s.Features[kv.Key] = kv.Value;
            foreach (var kv in DefaultAuto()) if (!s.Auto.ContainsKey(kv.Key)) s.Auto[kv.Key] = kv.Value;
            s.BannedWords ??= new List<string>();
            if (path != null && !File.Exists(path)) s.Save(path);
            return s;
        }

        public void Save(string? path)
        {
            if (path == null) return;
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(this, Json)); }
            catch (Exception) { }
        }
    }
}
