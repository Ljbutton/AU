using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>One player of the tournament, as the caster calls them.</summary>
    public sealed class RosterEntry
    {
        public string Name { get; set; } = "";
        public string? DiscordId { get; set; }
        public List<string> InGameNames { get; set; } = new List<string>();
        public List<string> FriendCodes { get; set; } = new List<string>();
        public string? Pronunciation { get; set; }
    }

    /// <summary>How a player was matched to the roster.</summary>
    public enum MatchHow { None, Manual, Discord, FriendCode, InGameName }

    /// <summary>
    /// The tournament's players (roster.csv next to The Button's settings, editable in Excel or a
    /// text editor) and how lobby players are matched to them: a manual pick from the caster tab
    /// first, then the Discord account the automute links them to, then the friend code, then the
    /// in-game name. Manual picks are kept in roster-overrides.json.
    /// </summary>
    public sealed class Roster
    {
        public const string FileName = "roster.csv";
        public const string OverridesName = "roster-overrides.json";
        public const string Header = "name,discord_id,in_game_names,friend_codes,pronunciation";

        private readonly string? _path, _overridesPath;
        private readonly object _lock = new object();
        private DateTime _stamp, _nextCheck;
        private List<RosterEntry> _entries = new List<RosterEntry>();
        private Dictionary<string, string> _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string? Path => _path;
        public string? Problem { get; private set; }
        /// <summary>Extra entries used in simulation mode (the fake players), never written to the file.</summary>
        public List<RosterEntry> Extra { get; } = new List<RosterEntry>();

        public Roster(string? path)
        {
            _path = path;
            _overridesPath = path == null ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? ".", OverridesName);
            try
            {
                if (_overridesPath != null && File.Exists(_overridesPath))
                    _overrides = new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_overridesPath)) ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception) { }
            Refresh(force: true);
        }

        public IReadOnlyList<RosterEntry> Entries
        {
            get { Refresh(); lock (_lock) return _entries.Concat(Extra).ToList(); }
        }

        /// <summary>Re-reads roster.csv when it changes (writing an example the first time).</summary>
        public void Refresh(bool force = false)
        {
            if (_path == null) return;
            var now = DateTime.UtcNow;
            if (!force && now < _nextCheck) return;
            _nextCheck = now.AddSeconds(2);
            try
            {
                if (!File.Exists(_path))
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                    File.WriteAllText(_path, Header + "\n" +
                        "# One player per line. in_game_names and friend_codes can hold several, split with ;\n" +
                        "# Example (the # makes a line a note; delete it to use the line):\n" +
                        "# Jake Rivera,123456789012345678,JakeR;jakey,coolfox#1234,JAKE rivv-AIR-uh\n", Encoding.UTF8);
                }
                var stamp = File.GetLastWriteTimeUtc(_path);
                if (stamp == _stamp) return;
                _stamp = stamp;
                var (entries, problem) = Parse(File.ReadAllText(_path));
                lock (_lock) _entries = entries;
                Problem = problem;
            }
            catch (Exception e) { Problem = $"{FileName} couldn't be read ({e.Message}); using the last good copy."; }
        }

        /// <summary>Reads the CSV: a header line, then one player per line; # lines are notes.</summary>
        public static (List<RosterEntry> Entries, string? Problem) Parse(string text)
        {
            var entries = new List<RosterEntry>();
            var bad = new List<int>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int[] col = { 0, 1, 2, 3, 4 };
            bool header = false;
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var cells = SplitCsv(line);
                if (!header)
                {
                    header = true;
                    var names = cells.Select(c => c.Trim().ToLowerInvariant().Replace(" ", "_")).ToList();
                    if (names.Contains("name") || names.Contains("display_name"))
                    {
                        int Find(params string[] keys) => names.FindIndex(x => keys.Contains(x));
                        col = new[] { Find("name", "display_name"), Find("discord_id", "discord"), Find("in_game_names", "in_game_name", "ign"), Find("friend_codes", "friend_code"), Find("pronunciation", "say") };
                        continue;
                    }
                }
                string Cell(int i) => i >= 0 && i < cells.Count ? cells[i].Trim() : "";
                string name = Cell(col[0]);
                if (name.Length == 0) { bad.Add(n + 1); continue; }
                string discord = Regex.Replace(Cell(col[1]), "[^0-9]", "");
                entries.Add(new RosterEntry
                {
                    Name = name,
                    DiscordId = discord.Length > 0 ? discord : null,
                    InGameNames = Cell(col[2]).Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(),
                    FriendCodes = Cell(col[3]).Split(';').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToList(),
                    Pronunciation = Cell(col[4]).Length > 0 ? Cell(col[4]) : null,
                });
            }
            return (entries, bad.Count > 0 ? $"{FileName}: line {string.Join(", ", bad)} has no name and was skipped." : null);
        }

        private static List<string> SplitCsv(string line)
        {
            var cells = new List<string>();
            var cur = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cur.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { cells.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            cells.Add(cur.ToString());
            return cells;
        }

        /// <summary>The roster entry for a lobby player, and how it was found.</summary>
        public (RosterEntry? Entry, MatchHow How) Match(string key, string? discordId, string inGameName)
        {
            var all = Entries;
            string? manual;
            lock (_lock) _overrides.TryGetValue(key, out manual);
            if (manual != null)
            {
                if (manual.Length == 0) return (null, MatchHow.Manual);          // picked "nobody"
                var m = all.FirstOrDefault(e => string.Equals(e.Name, manual, StringComparison.OrdinalIgnoreCase));
                if (m != null) return (m, MatchHow.Manual);
            }
            if (!string.IsNullOrEmpty(discordId))
            {
                var d = all.FirstOrDefault(e => e.DiscordId == discordId);
                if (d != null) return (d, MatchHow.Discord);
            }
            // Keys are the friend code (or "name:…" for players without one).
            if (!key.StartsWith("name:", StringComparison.Ordinal))
            {
                var f = all.FirstOrDefault(e => e.FriendCodes.Contains(key.ToLowerInvariant()));
                if (f != null) return (f, MatchHow.FriendCode);
            }
            var g = all.Where(e => e.InGameNames.Any(n => string.Equals(n, inGameName.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
            if (g.Count == 1) return (g[0], MatchHow.InGameName);
            return (null, MatchHow.None);
        }

        /// <summary>Picks the roster entry for a player by hand ("" for nobody, null to go back to automatic).</summary>
        public void Override(string key, string? rosterName)
        {
            lock (_lock)
            {
                if (rosterName == null) _overrides.Remove(key);
                else _overrides[key] = rosterName;
                try
                {
                    if (_overridesPath != null) File.WriteAllText(_overridesPath, JsonSerializer.Serialize(_overrides, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception) { }
            }
        }

    }

    /// <summary>Names in caster texts: "[[colour|Name]]" tokens become a colour swatch and the name on screen.</summary>
    public static class NameTag
    {
        private static readonly Regex Token = new Regex(@"\[\[(\d+)\|([^\]]*)\]\]");

        public static string Make(int colour, string name) => $"[[{colour}|{name.Replace("]", ")").Replace("|", "/")}]]";

        /// <summary>The text with names only (for messages and logs).</summary>
        public static string Plain(string text) => Token.Replace(text, m => m.Groups[2].Value);
    }
}
