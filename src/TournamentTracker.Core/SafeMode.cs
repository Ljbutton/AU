using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TournamentTracker
{
    /// <summary>
    /// An Among Us version by its date. Every Among Us release is named after a day (2026.9.29), and
    /// so is the game-libraries package the mod is built against, so comparing the two dates says
    /// whether the game is newer than anything the mod was checked with.
    /// </summary>
    public readonly struct GameDate : IComparable<GameDate>, IEquatable<GameDate>
    {
        // Year, month and day anywhere in the text, so "2026.9.29", "v2026.9.29s", "2026.9.29.1" and a
        // package version such as "2026.9.29-patch" all read the same.
        private static readonly Regex DatePattern = new Regex(@"(?<!\d)(\d{4})[._\-](\d{1,2})[._\-](\d{1,2})(?!\d)", RegexOptions.CultureInvariant);

        public GameDate(int year, int month, int day)
        {
            Year = year;
            Month = month;
            Day = day;
        }

        public int Year { get; }
        public int Month { get; }
        public int Day { get; }

        /// <summary>The date in a version text, or null when it has none (or an impossible one).</summary>
        public static GameDate? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = DatePattern.Match(text);
            if (!m.Success) return null;
            return Make(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The date in the number the game sends to Among Us's servers (year × 25000 + month × 1800 +
        /// day × 50 + revision, the game's own Constants.GetVersion). The revision is left out, so the
        /// host authority flag (+25 on the revision) doesn't change it.
        /// </summary>
        public static GameDate? FromBroadcastVersion(int version)
        {
            if (version <= 0) return null;
            int year = version / 25000;
            version -= year * 25000;
            int month = version / 1800;
            version -= month * 1800;
            int day = version / 50;
            return Make(year, month, day);
        }

        /// <summary>
        /// The running game's date, read the way the game-libraries packages are named: from
        /// Application.version, else (no date in it) from the network version. The network version's
        /// date (the game's Constants) isn't moved on every release: one game said 2026.7.20 there while
        /// its Application.version was 2026.8.18 or later and its game libraries 2026.9.29, so compared
        /// on its own an update would never look newer than the mod.
        /// </summary>
        public static GameDate? OfRunningGame(string? applicationVersion, int broadcastVersion) =>
            Parse(applicationVersion) ?? FromBroadcastVersion(broadcastVersion);

        private static GameDate? Make(int year, int month, int day) =>
            year >= 2018 && year <= 2100 && month >= 1 && month <= 12 && day >= 1 && day <= 31 ? new GameDate(year, month, day) : (GameDate?)null;

        public int CompareTo(GameDate other) =>
            Year != other.Year ? Year.CompareTo(other.Year) : Month != other.Month ? Month.CompareTo(other.Month) : Day.CompareTo(other.Day);

        public bool Equals(GameDate other) => CompareTo(other) == 0;
        public override bool Equals(object? obj) => obj is GameDate d && Equals(d);
        public override int GetHashCode() => (Year * 13 + Month) * 32 + Day;
        public override string ToString() => $"{Year}.{Month}.{Day}";
    }

    /// <summary>What safe mode decided: on or off, why, and whether the host chose to run normally anyway.</summary>
    public sealed class SafeModeDecision
    {
        public SafeModeDecision(bool on, bool automatic, string? reason, bool overridden)
        {
            On = on;
            Automatic = automatic;
            Reason = reason;
            Overridden = overridden;
        }

        /// <summary>Safe mode is on: the risky features are off.</summary>
        public bool On { get; }
        /// <summary>What it would be without the host's choice.</summary>
        public bool Automatic { get; }
        /// <summary>Why it's (or would be) on; null when there's no reason to be.</summary>
        public string? Reason { get; }
        /// <summary>It would be on, but the host said to run normally on this game version.</summary>
        public bool Overridden { get; }
    }

    /// <summary>
    /// Safe mode, the pure decision. On Among Us update day the game can change under the mod: a
    /// message the host sends that was fine yesterday can get the host kicked or banned by the
    /// server's anti-cheat today. So when the game is newer than the game libraries the mod was
    /// built against, or a hook that belongs to one of those features didn't go in, the features
    /// that send things to the server are off until an update.
    /// </summary>
    public static class SafeModeRules
    {
        /// <param name="running">The game's version (null when it couldn't be read).</param>
        /// <param name="builtFor">The game libraries' version the mod was built against (null when unknown).</param>
        /// <param name="riskyHooksFailed">Hooks of the risky features that the startup self-check couldn't put in.</param>
        /// <param name="runNormally">The host said to run normally on this game version.</param>
        public static SafeModeDecision Decide(GameDate? running, GameDate? builtFor, IReadOnlyCollection<string>? riskyHooksFailed, bool runNormally)
        {
            var failed = riskyHooksFailed ?? Array.Empty<string>();
            // Only a newer game counts: the same day is what the mod was built for, and an older game
            // with this mod is caught by the hooks' own self-check.
            bool newer = running.HasValue && builtFor.HasValue && running.Value.CompareTo(builtFor.Value) > 0;
            string? reason = null;
            if (newer)
                reason = $"Among Us {running} is newer than this mod (built for {builtFor}): risky features are off until an update"
                         + (failed.Count > 0 ? $" (hooks failed too: {Names(failed)})" : "");
            else if (failed.Count > 0)
                reason = $"hooks failed: {Names(failed)} {(failed.Count == 1 ? "isn't" : "aren't")} in place, so risky features are off until an update";
            bool automatic = reason != null;
            return new SafeModeDecision(automatic && !runNormally, automatic, reason, automatic && runNormally);
        }

        private static string Names(IReadOnlyCollection<string> names)
        {
            var shown = names.Take(4).ToList();
            string list = string.Join(", ", shown);
            return names.Count > shown.Count ? $"{list} and {names.Count - shown.Count} more" : list;
        }
    }

    /// <summary>Safe mode as The Button reads it (status JSON "safeMode").</summary>
    public sealed class SafeModeStatus
    {
        public bool On { get; set; }
        public string? Reason { get; set; }
        public string? GameVersion { get; set; }
        public string? BuiltFor { get; set; }
        public bool Overridden { get; set; }
    }

    /// <summary>
    /// Safe mode for this run of the game: the versions it was decided from, and the host's
    /// "run normally anyway" (safemode off), which is saved for this game version only, so the next
    /// Among Us update turns safe mode back on by itself.
    /// </summary>
    public sealed class SafeMode
    {
        public const string FileName = "safemode.json";

        private sealed class Saved
        {
            /// <summary>The game version the host said to run normally on.</summary>
            public string? RunNormallyOn { get; set; }
        }

        private readonly string _path;
        private readonly ILog _log;
        private readonly GameDate? _running, _builtFor;
        private volatile SafeModeDecision _decision;

        /// <param name="gameVersion">The running game's version text (its date, or what the game said when that isn't a date).</param>
        public SafeMode(string dataDir, ILog log, string? gameVersion, string? builtFor, IReadOnlyCollection<string>? riskyHooksFailed = null)
        {
            _path = Path.Combine(dataDir, FileName);
            _log = log;
            _running = GameDate.Parse(gameVersion);
            _builtFor = GameDate.Parse(builtFor);
            GameVersion = _running?.ToString() ?? Clean(gameVersion);
            BuiltFor = _builtFor?.ToString() ?? Clean(builtFor);
            RiskyHooksFailed = (riskyHooksFailed ?? Array.Empty<string>()).ToList();
            _decision = Decide(LoadRunNormallyOn() == VersionKey);
        }

        /// <summary>The running game's version ("2026.10.20"), or null when it couldn't be read.</summary>
        public string? GameVersion { get; }
        /// <summary>The game libraries the mod was built against ("2026.9.29"), or null when unknown.</summary>
        public string? BuiltFor { get; }
        public IReadOnlyList<string> RiskyHooksFailed { get; }

        public bool On => _decision.On;

        // Safe mode as it stood when the game being played began: 1 on, 0 off, -1 no game held.
        private volatile int _game = -1;

        /// <summary>
        /// Safe mode for the game being played: as it stood when the game began (<see cref="HoldForGame"/>),
        /// until the lobby or the menus (<see cref="ReleaseGame"/>); between games, <see cref="On"/>. The
        /// host's choice mid-game applies from the next game, so the referee slot never switches on or off
        /// half way through one (a player made referee mid-game couldn't be killed or voted out and saw the
        /// impostors; a referee made a player again could be voted out, which got the host banned).
        /// </summary>
        public bool OnThisGame
        {
            get
            {
                int game = _game;
                return game < 0 ? _decision.On : game == 1;
            }
        }

        /// <summary>A game is being played and safe mode is held for it.</summary>
        public bool HeldForGame => _game >= 0;

        /// <summary>A game begins (its roles are handed out): safe mode is held as it is now until <see cref="ReleaseGame"/>.</summary>
        public void HoldForGame() => _game = _decision.On ? 1 : 0;

        /// <summary>Back in the lobby or the menus: safe mode follows the decision again.</summary>
        public void ReleaseGame() => _game = -1;

        public bool Overridden => _decision.Overridden;
        /// <summary>On without the host's choice.</summary>
        public bool WouldBeOn => _decision.Automatic;
        public string? Reason => _decision.Reason;

        /// <summary>What the host's choice is saved under: this game version.</summary>
        private string VersionKey => GameVersion ?? "unknown";

        public SafeModeStatus Status()
        {
            var d = _decision;
            return new SafeModeStatus { On = d.On, Reason = d.Reason, GameVersion = GameVersion, BuiltFor = BuiltFor, Overridden = d.Overridden };
        }

        /// <summary>
        /// "safemode off" (true): run normally on this game version, saved for it only.
        /// "safemode on" (false): back to automatic.
        /// </summary>
        public void RunNormally(bool yes)
        {
            try
            {
                if (yes) SafeFile.WriteAllText(_path, JsonSerializer.Serialize(new Saved { RunNormallyOn = VersionKey }));
                else if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception e)
            {
                _log.Warn("Safe mode: couldn't save the choice (" + e.Message + "); it lasts until Among Us closes.");
            }
            _decision = Decide(yes);
        }

        /// <summary>One line for the log: the versions, and what that means.</summary>
        public string Describe()
        {
            string versions = $"Among Us {GameVersion ?? "(version unknown)"}, mod built for {BuiltFor ?? "(unknown)"}";
            var d = _decision;
            if (d.On) return $"Safe mode ON ({versions}): {d.Reason}. Off: host authority, the referee ghost slot, the impostor rotation, the settings lock and the host-only message guard. Tracking, stats, overlays and Discord keep working. The Button (or the safemode off command) can run everything anyway on this version.";
            if (d.Overridden) return $"Safe mode would be on ({d.Reason}), but the host chose to run normally on Among Us {VersionKey} ({versions}).";
            if (!_running.HasValue || !_builtFor.HasValue) return $"Safe mode off ({versions}): the versions can't be compared, so nothing is held back.";
            return $"Safe mode off ({versions}).";
        }

        private SafeModeDecision Decide(bool runNormally) => SafeModeRules.Decide(_running, _builtFor, RiskyHooksFailed, runNormally);

        private string? LoadRunNormallyOn()
        {
            try
            {
                if (!File.Exists(_path)) return null;
                return JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path))?.RunNormallyOn;
            }
            catch (Exception e)
            {
                _log.Warn("Safe mode: couldn't read the saved choice (" + e.Message + "); deciding by itself.");
                return null;
            }
        }

        private static string? Clean(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text!.Trim();
            return text.Length > 40 ? text.Substring(0, 40) : text;
        }
    }

    public sealed partial class TournamentSession
    {
        /// <summary>
        /// Safe mode for this run of the game (set by the mod, which reads the versions and the hooks'
        /// self-check; null in tests that don't need it).
        /// </summary>
        public SafeMode? SafeMode { get; set; }

        /// <summary>Safe mode for the game being played (held from its start until the lobby); between games, as it is now.</summary>
        public bool SafeModeThisGame => SafeMode?.OnThisGame ?? false;

        private readonly HashSet<string> _safeModeSaid = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// The mod didn't do something because safe mode is on (the referee slot, the rotation, the
        /// settings lock…): in the log, and on Home once a session for each.
        /// <paramref name="advice"/>: what that means for the host, when it isn't obvious.
        /// </summary>
        public void SafeModeHeldBack(string what, string? advice = null)
        {
            if (!_safeModeSaid.Add(what)) return;
            _log.Warn($"Safe mode: {what} is off ({SafeMode?.Reason ?? "safe mode is on"}).");
            Note($"Safe mode is on, so {what} is off this time: {SafeMode?.Reason ?? "the game is newer than the mod"}." +
                 (advice != null ? " " + advice : "") +
                 " Tracking, stats, overlays and Discord work as usual. To run everything anyway on this Among Us version, turn safe mode off in The Button.");
        }

        /// <summary>
        /// Changed during a game: that game carries on as it started (the referee slot can't switch on or
        /// off half way through), and the change applies from the next one. Empty otherwise.
        /// </summary>
        private static string ThisGameCarriesOn(SafeMode safe) =>
            !safe.HeldForGame || safe.OnThisGame == safe.On ? ""
            : safe.OnThisGame
                ? "This game carries on in safe mode (the game's own roles, no referee ghost); the change applies from the next game. "
                : "This game carries on as it started (the referee ghost and the rotation as they were); safe mode applies from the next game. ";

        /// <summary>!safemode off|on: run normally on this game version anyway, or back to automatic.</summary>
        private void SafeModeCommand(string[] args)
        {
            var safe = SafeMode;
            if (safe == null)
            {
                Reply("Safe mode isn't set up: the mod hasn't checked the game's version.", false);
                return;
            }
            string arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
            string version = safe.GameVersion ?? "this version";
            if (arg == "off")
            {
                if (!safe.WouldBeOn)
                {
                    Reply($"Safe mode is already off: everything runs normally on Among Us {version}.", false);
                    return;
                }
                safe.RunNormally(true);
                _safeModeSaid.Clear();
                _log.Warn($"Safe mode: the host chose to run normally on Among Us {version} ({safe.Reason}).");
                Reply($"Safe mode OFF for Among Us {version}: everything runs as normal, at your own risk ({safe.Reason}). " +
                      "Host authority applies from the next lobby you host (it's sent as Among Us connects). " +
                      ThisGameCarriesOn(safe) +
                      "Safe mode comes back by itself when Among Us updates again.", false);
            }
            else if (arg == "on")
            {
                safe.RunNormally(false);
                _safeModeSaid.Clear();
                Reply((safe.On
                    ? $"Safe mode is ON: {safe.Reason}. Host authority, the referee ghost slot, the impostor rotation and the settings lock are off; tracking, stats, overlays and Discord keep working."
                    : $"Safe mode is back to automatic, and off: Among Us {version} is what this mod was built for (or the versions can't be compared).")
                    + (ThisGameCarriesOn(safe) is { Length: > 0 } note ? " " + note.TrimEnd() : ""), false);
            }
            else
            {
                Reply(safe.OnThisGame != safe.On ? $"Safe mode is {(safe.On ? "ON" : "OFF")} from the next game; this game carries on {(safe.OnThisGame ? "in safe mode" : "without it")}."
                    : safe.On ? $"Safe mode is ON: {safe.Reason}."
                    : safe.Overridden ? $"Safe mode is OFF on Among Us {version} because you chose to run normally ({safe.Reason})."
                    : "Safe mode is off.", false);
            }
        }
    }
}
