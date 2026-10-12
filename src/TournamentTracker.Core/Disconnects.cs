using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TournamentTracker
{
    /// <summary>One time Among Us disconnected this game, as the mod saw it.</summary>
    public sealed class DisconnectRecord
    {
        /// <summary>When (UTC).</summary>
        public DateTime At { get; set; }
        /// <summary>The game's reason by name (DisconnectReasons: Hacking, Kicked, Error…).</summary>
        public string Reason { get; set; } = "";
        /// <summary>The reason's number, for a reason this mod doesn't know by name.</summary>
        public int Code { get; set; }
        /// <summary>The server's or the game's own words, when the reason carries any.</summary>
        public string? Detail { get; set; }
        /// <summary>A game was being played (not the lobby or the menus).</summary>
        public bool InGame { get; set; }
        /// <summary>The lobby code, when in a lobby or a game.</summary>
        public string? Lobby { get; set; }
        /// <summary>This game was the lobby's host.</summary>
        public bool Host { get; set; }
        /// <summary>This connection told the server it's a host-only mod (host authority, the "+25").</summary>
        public bool ModdedFlag { get; set; }
        /// <summary>Safe mode was on.</summary>
        public bool SafeMode { get; set; }
        /// <summary>The reason in plain English.</summary>
        public string Explanation { get; set; } = "";
        /// <summary>What the mod itself sent to the network in the seconds before ("3.2 s before: …").</summary>
        public List<string> SentBefore { get; set; } = new List<string>();
    }

    /// <summary>The latest disconnect as The Button reads it (status JSON "lastDisconnect").</summary>
    public sealed class DisconnectStatus
    {
        /// <summary>ISO-8601, UTC.</summary>
        public string At { get; set; } = "";
        public string Reason { get; set; } = "";
        public string? Detail { get; set; }
        public bool InGame { get; set; }
    }

    /// <summary>
    /// The last few things the mod itself sent to the network (role messages, the referee's
    /// teleport and record syncs, settings syncs, chat…), so a kick can be tied to what caused it.
    /// A small ring: only the seconds before a disconnect matter.
    /// </summary>
    public sealed class RecentSends
    {
        public const int Capacity = 32;
        private readonly (DateTime At, string What)[] _ring = new (DateTime, string)[Capacity];
        private int _next, _count;
        private readonly object _lock = new object();
        private readonly Func<DateTime> _clock;

        public RecentSends(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

        /// <summary>The mod sent this (called from the game's main thread, as it's sent).</summary>
        public void Add(string what)
        {
            var at = _clock();
            lock (_lock)
            {
                _ring[_next] = (at, what);
                _next = (_next + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        /// <summary>What was sent in the <paramref name="window"/> up to <paramref name="at"/>, oldest first ("3.2 s before: …").</summary>
        public List<string> Before(DateTime at, TimeSpan window)
        {
            var list = new List<string>();
            lock (_lock)
            {
                for (int i = 0; i < _count; i++)
                {
                    var (when, what) = _ring[(_next - _count + i + Capacity) % Capacity];
                    var ago = at - when;
                    if (ago < TimeSpan.Zero || ago > window) continue;
                    list.Add($"{ago.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s before: {what}");
                }
            }
            return list;
        }
    }

    /// <summary>What each of the game's disconnect reasons means, in plain English.</summary>
    public static class DisconnectText
    {
        private static readonly Dictionary<string, (string Label, string Explanation)> Reasons = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["ExitGame"] = ("left", "the game left the lobby (you quit, or Among Us closed the connection itself)."),
            ["IntentionalLeaving"] = ("left", "the game left the lobby on purpose."),
            ["GameFull"] = ("lobby full", "the lobby was full."),
            ["GameStarted"] = ("game already started", "the lobby's game had already started, so it couldn't be joined."),
            ["GameNotFound"] = ("lobby not found", "there's no lobby with that code on this server (it closed, the code is wrong, or it's on another region)."),
            ["IncorrectVersion"] = ("version not accepted", "Among Us's servers didn't accept this game's version number: Among Us may need updating, or the server doesn't take the mod's host authority flag (+25) on this version (turn host authority off and host again)."),
            ["MismatchedVersion"] = ("versions differ", "this game's version doesn't match what the server or lobby expects."),
            ["Banned"] = ("banned", "banned from the room, by the lobby's host or by the server."),
            ["Kicked"] = ("kicked", "kicked from the room, by the lobby's host or by the server for a message it didn't accept."),
            ["Hacking"] = ("server anti-cheat", "Among Us's servers saw a message from this game that they only expect from a cheat. For a host mod that's usually something sent twice or at the wrong time: a role, an exile, a kill, a teleport, a settings change."),
            ["Custom"] = ("server message", "the server (or the game) gave its own reason."),
            ["InvalidName"] = ("name not accepted", "the server didn't accept the player name."),
            ["NotAuthorized"] = ("not signed in", "the server didn't accept this game's sign-in: restart Among Us and sign in again."),
            ["ConnectionLimit"] = ("too many connections", "too many connections from this internet address: wait a minute before hosting again."),
            ["TooManyRequests"] = ("too many requests", "too many requests to the server in a short time (rejoining or hosting over and over): wait a minute."),
            ["Destroy"] = ("game closing", "the connection closed as Among Us shut down."),
            ["Error"] = ("network error", "the connection broke: the internet dropped or lagged, or a message couldn't be sent."),
            ["ClientTimeout"] = ("timed out", "no answer from the server in time: the internet connection dropped or lagged."),
            ["IncorrectGame"] = ("wrong room", "the server mixed up which room this game is in."),
            ["ServerRequest"] = ("server closed it", "the server closed the connection (a restart or maintenance)."),
            ["ServerFull"] = ("server full", "the server is full: try again shortly, or another region."),
            ["ServerError"] = ("server error", "the server had an error: try again."),
            ["ServerNotFound"] = ("server not found", "the server couldn't be reached."),
            ["NoServersAvailable"] = ("no servers", "no game server was free on this region."),
            ["LobbyInactivity"] = ("lobby idle", "the server closed the lobby after it sat idle too long."),
            ["MatchmakerInactivity"] = ("matchmaker idle", "the matchmaker gave up after waiting too long."),
            ["MatchmakerFull"] = ("matchmaker full", "the matchmaker is full: try again shortly."),
            ["InvalidGameOptions"] = ("settings not accepted", "the server didn't accept the lobby's settings (a settings change it thinks is invalid)."),
            ["QuickmatchDisabled"] = ("quick match off", "quick match is switched off on the server."),
            ["TooManyGames"] = ("too many games", "this account has too many games open on the server."),
            ["QuickchatLock"] = ("quick chat only", "this account can only use Quick Chat."),
            ["Sanctions"] = ("account sanctioned", "Innersloth has a sanction (a ban) on this account."),
            ["PlatformLock"] = ("platform lock", "cross-platform play is limited for this lobby."),
            ["SelfPlatformLock"] = ("platform lock", "cross-platform play is limited for this account."),
            ["DuplicateConnectionDetected"] = ("signed in twice", "this account connected again from somewhere else."),
            ["FocusLost"] = ("game in background", "Among Us was in the background (suspended) too long."),
            ["FocusLostBackground"] = ("game in background", "Among Us was in the background (suspended) too long."),
            ["NewConnection"] = ("new connection", "a new connection took over from this one."),
            ["PlatformParentalControlsBlock"] = ("platform block", "the platform's account settings block online play."),
            ["PlatformUserBlock"] = ("platform block", "the platform's account settings block online play."),
            ["PlatformFailedToGetUserBlock"] = ("platform block", "the platform couldn't say whether online play is allowed."),
            ["InternalPlayerMissing"] = ("server-side error", "an internal error on Among Us's servers: try again."),
            ["InternalNonceFailure"] = ("server-side error", "an internal error on Among Us's servers: try again."),
            ["InternalConnectionToken"] = ("server-side error", "an internal error on Among Us's servers: try again."),
            ["ErrorAuthNonceFailure"] = ("sign-in error", "the sign-in check with Among Us's servers failed: try again."),
            ["Unknown"] = ("unknown", "Among Us didn't say why."),
        };

        /// <summary>A short label ("server anti-cheat") and a plain-English explanation for a reason's name.</summary>
        public static (string Label, string Explanation) Explain(string reason) =>
            Reasons.TryGetValue(reason ?? "", out var r) ? r : ("unrecognised reason", "a reason this mod doesn't know (a newer Among Us?).");

        // The game reads its own words only for these: for every other reason it passes on whatever
        // it said last time (a stale message from an earlier disconnect), so that's dropped.
        private static readonly HashSet<string> CarriesWords = new HashSet<string>(StringComparer.Ordinal) { "Custom", "MismatchedVersion", "Error" };
        private static readonly Regex Spaces = new Regex(@"\s+");

        /// <summary>The words that came with a disconnect, on one line, or null when there are none (or they're stale).</summary>
        public static string? Detail(string reason, string? words)
        {
            if (!CarriesWords.Contains(reason ?? "") || string.IsNullOrWhiteSpace(words)) return null;
            string text = Spaces.Replace(words!.Replace("\r\n\r\n", " / ").Replace("\n\n", " / "), " ").Trim();
            return text.Length > 300 ? text.Substring(0, 299) + "…" : text;
        }
    }

    /// <summary>
    /// Why Among Us disconnected this game: each one is logged with a plain-English explanation and
    /// what the mod itself sent just before, and the last 20 are kept in disconnects.json in the mod's
    /// data folder (for the support bundle). The latest is in the status The Button reads.
    /// </summary>
    public sealed class DisconnectHistory
    {
        public const string FileName = "disconnects.json";
        public const int Keep = 20;
        /// <summary>How far back what the mod sent is listed with a disconnect.</summary>
        public static readonly TimeSpan SentWindow = TimeSpan.FromSeconds(10);

        private static readonly JsonSerializerOptions FileJson = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly string _path;
        private readonly ILog _log;
        private readonly Func<DateTime> _clock;
        private readonly List<DisconnectRecord> _records;
        private readonly object _lock = new object();

        public DisconnectHistory(string dataDir, ILog log, RecentSends? sends = null, Func<DateTime>? clock = null)
        {
            _path = Path.Combine(dataDir, FileName);
            _log = log;
            _clock = clock ?? (() => DateTime.UtcNow);
            Sends = sends ?? new RecentSends(_clock);
            _records = Load();
        }

        /// <summary>What the mod sent lately (the plugin adds to it as it sends).</summary>
        public RecentSends Sends { get; }

        public IReadOnlyList<DisconnectRecord> All { get { lock (_lock) return _records.ToList(); } }

        public DisconnectRecord? Latest { get { lock (_lock) return _records.Count == 0 ? null : _records[_records.Count - 1]; } }

        /// <summary>The latest disconnect for The Button, or null when there's been none.</summary>
        public DisconnectStatus? Status()
        {
            var r = Latest;
            if (r == null) return null;
            var at = r.At.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(r.At, DateTimeKind.Utc) : r.At.ToUniversalTime();
            return new DisconnectStatus { At = at.ToString("o", CultureInfo.InvariantCulture), Reason = r.Reason, Detail = r.Detail, InGame = r.InGame };
        }

        /// <summary>A disconnect: logged (with what the mod sent in the 10 s before), kept and saved.</summary>
        /// <param name="words">The words the game had with it (only kept for reasons that really carry any).</param>
        public DisconnectRecord Record(string reason, int code, string? words, bool inGame, string? lobby, bool host, bool moddedFlag, bool safeMode)
        {
            var at = _clock();
            reason = string.IsNullOrWhiteSpace(reason) ? code.ToString(CultureInfo.InvariantCulture) : reason.Trim();
            var record = new DisconnectRecord
            {
                At = at,
                Reason = reason,
                Code = code,
                Detail = DisconnectText.Detail(reason, words),
                InGame = inGame,
                Lobby = string.IsNullOrWhiteSpace(lobby) ? null : lobby,
                Host = host,
                ModdedFlag = moddedFlag,
                SafeMode = safeMode,
                Explanation = DisconnectText.Explain(reason).Explanation,
                SentBefore = Sends.Before(at, SentWindow),
            };
            _log.Warn(Line(record));
            _log.Warn(SentLine(record));
            List<DisconnectRecord> snapshot;
            lock (_lock)
            {
                _records.Add(record);
                if (_records.Count > Keep) _records.RemoveRange(0, _records.Count - Keep);
                snapshot = _records.ToList();
            }
            Save(snapshot);
            return record;
        }

        /// <summary>"Disconnected: Hacking (server anti-cheat) — Among Us's servers saw… (during a game in lobby ABCDEF, hosting)."</summary>
        public static string Line(DisconnectRecord r)
        {
            var (label, explanation) = DisconnectText.Explain(r.Reason);
            var sb = new StringBuilder();
            sb.Append("Disconnected: ").Append(r.Reason).Append(" (").Append(label).Append(") — ").Append(Cap(explanation));
            if (r.Detail != null) sb.Append(" It said: \"").Append(r.Detail).Append("\".");
            var where = new List<string>();
            where.Add(r.InGame ? "during a game" : r.Lobby != null ? "in the lobby" : "not in a lobby");
            if (r.Lobby != null) where[0] += " " + r.Lobby;
            if (r.Host) where.Add("hosting");
            if (r.ModdedFlag) where.Add("host authority on");
            if (r.SafeMode) where.Add("safe mode on");
            sb.Append(" (").Append(string.Join(", ", where)).Append(").");
            return sb.ToString();
        }

        /// <summary>What the mod sent in the 10 s before, on one line.</summary>
        public static string SentLine(DisconnectRecord r) =>
            r.SentBefore.Count == 0
                ? "The mod sent nothing to the network in the 10 s before the disconnect."
                : "The mod sent in the 10 s before the disconnect: " + string.Join("; ", r.SentBefore) + ".";

        private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private List<DisconnectRecord> Load()
        {
            try
            {
                if (!File.Exists(_path)) return new List<DisconnectRecord>();
                var list = JsonSerializer.Deserialize<List<DisconnectRecord>>(File.ReadAllText(_path), FileJson) ?? new List<DisconnectRecord>();
                list.RemoveAll(r => r == null);
                return list.Count > Keep ? list.GetRange(list.Count - Keep, Keep) : list;
            }
            catch (Exception e)
            {
                _log.Warn($"Couldn't read {FileName} ({e.Message}); starting a new one.");
                return new List<DisconnectRecord>();
            }
        }

        private void Save(List<DisconnectRecord> records)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // Written whole to a side file first, so a game closing mid-write never leaves half a file.
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(records, FileJson));
                File.Move(temp, _path, true);
            }
            catch (Exception e)
            {
                _log.Warn($"Couldn't save {FileName}: {e.Message}");
            }
        }
    }

    public sealed partial class TournamentSession
    {
        /// <summary>Why Among Us disconnected this game, the last few times (set by the mod; null in tests that don't need it).</summary>
        public DisconnectHistory? Disconnects { get; set; }

        /// <summary>A disconnect the mod just recorded: in the activity log The Button shows, and in its status.</summary>
        public void DisconnectSeen(DisconnectRecord record)
        {
            Log(DisconnectHistory.Line(record));
            RefreshStatus();
        }
    }
}
