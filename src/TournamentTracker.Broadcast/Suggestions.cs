using System;
using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>One "Show this" from someone on the Watch page: a lobby (or its camera on a player), why, and who.</summary>
    public sealed class Suggestion
    {
        public string Id { get; set; } = "";
        public string Lobby { get; set; } = "";
        /// <summary>The quick reason picked ("kill", "clutch", "meeting", "sabotage", "funny", "cam"), or "" for text only.</summary>
        public string Reason { get; set; } = "";
        /// <summary>The optional short text (up to <see cref="SuggestionBox.MaxText"/> characters).</summary>
        public string Text { get; set; } = "";
        /// <summary>Who sent it (the name they gave on the Watch page), and anyone who sent the same since.</summary>
        public string Who { get; set; } = "";
        public List<string> Also { get; set; } = new List<string>();
        internal string WatcherId = "";
        public DateTime At { get; set; }
        /// <summary>A camera suggestion: the lobby's player camera following this player.</summary>
        public int? PlayerId { get; set; }
        public string? PlayerName { get; set; }
        public int? PlayerColor { get; set; }
        /// <summary>The caster put it into Preview (it stays until its lobby goes on Program).</summary>
        public bool Previewed { get; set; }
        /// <summary>Why it cleared ("program", "expired", "dismissed"), and when; null while it's live.</summary>
        public string? Cleared { get; set; }
        public DateTime? ClearedAt { get; set; }

        public bool IsCam => PlayerId.HasValue;

        /// <summary>The reason in words: "Kill coming", "Cam on Ann"…</summary>
        public string Label => Reason switch
        {
            "kill" => "Kill coming",
            "clutch" => "Clutch",
            "meeting" => "Meeting",
            "sabotage" => "Sabotage",
            "funny" => "Funny",
            "cam" => "Cam on " + (PlayerName ?? "a player"),
            _ => "Show this",
        };

        internal Suggestion Copy() { var c = (Suggestion)MemberwiseClone(); c.Also = Also.ToList(); return c; }
    }

    /// <summary>
    /// The production team's suggestions from the Watch page, for the caster's Live desk: newest first,
    /// each cleared when it's on Program (its lobby full screen; a camera one: its lobby's camera following
    /// that player), after <see cref="ExpireSeconds"/>, or when the caster dismisses it, then kept a while
    /// under Earlier. In memory only (gone after a restart; ids differ from one run to the next). Bounded:
    /// how many are live and kept, how long the text is, and how often one person can send.
    /// </summary>
    public sealed class SuggestionBox
    {
        public const int MaxText = 60;
        public const int MaxLive = 30;
        public const int MaxEarlier = 20;
        public const double ExpireSeconds = 120;
        /// <summary>One person: at most this many a minute, and one every <see cref="MinGapSeconds"/>.</summary>
        public const int PerMinute = 8;
        public const double MinGapSeconds = 3;
        public static readonly string[] Reasons = { "kill", "clutch", "meeting", "sabotage", "funny", "cam" };

        private readonly object _lock = new object();
        private readonly Func<DateTime> _clock;
        private readonly Func<OnAir> _air;
        private readonly Func<string, int?> _camPick;
        /// <summary>This run's own start for the ids, so a desk page left open over a restart doesn't take new ones for old.</summary>
        private readonly string _run = Guid.NewGuid().ToString("N").Substring(0, 6);
        private readonly List<Suggestion> _live = new List<Suggestion>();
        private readonly List<Suggestion> _earlier = new List<Suggestion>();
        private readonly Dictionary<string, List<DateTime>> _sent = new Dictionary<string, List<DateTime>>();
        private long _seq;

        /// <summary>A suggestion arrived (or someone added their name to one): the desk and the Stream Deck look again.</summary>
        public event Action? Changed;

        /// <param name="camPick">Who a lobby's camera follows, as the desk picked it (null: it picks by itself).</param>
        public SuggestionBox(Func<DateTime> clock, Func<OnAir> air, Func<string, int?>? camPick = null)
        {
            _clock = clock;
            _air = air;
            _camPick = camPick ?? (_ => null);
        }

        /// <summary>The camera picks of the lobbies whose camera is on Program (taken before the lock).</summary>
        private Dictionary<string, int?> Picks(OnAir air)
        {
            var picks = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            if (!air.IsCam) return picks;
            foreach (var lobby in air.Slots)
                if (!string.IsNullOrEmpty(lobby) && !picks.ContainsKey(lobby))
                {
                    try { picks[lobby] = _camPick(lobby); } catch (Exception) { picks[lobby] = null; }
                }
            return picks;
        }

        /// <summary>
        /// On Program as suggested: a lobby full screen; a camera suggestion, that lobby's camera on Program
        /// following the player (as the desk picked). A quad tile, the grid, a sponsor break or the camera on
        /// someone else doesn't count.
        /// </summary>
        private static bool OnProgram(OnAir air, Dictionary<string, int?> picks, string lobby, int? playerId)
        {
            if (playerId is int id)
                return air.IsCam && air.Has(lobby) && picks.TryGetValue(lobby, out var pick) && pick == id;
            return air.Layout == "full" && air.Slots.Count > 0 && string.Equals(air.Slots[0], lobby, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A suggestion from <paramref name="who"/> (watcher <paramref name="watcherId"/>). The same lobby, reason
        /// and player as one that's live adds their name to it instead. Refused (with why, and an HTTP status):
        /// too often (429), text too long or nothing said (400), already on Program as it would be (409: the
        /// lobby full screen; for a camera suggestion, that lobby's camera following that player).
        /// </summary>
        public (int Status, string Message, Suggestion? Made) Add(string watcherId, string who, string lobby, string reason, string? text,
            int? playerId = null, string? playerName = null, int? playerColor = null)
        {
            reason = (reason ?? "").Trim().ToLowerInvariant();
            text = Clean(text);
            if (reason.Length > 0 && !Reasons.Contains(reason)) return (400, "Unknown reason.", null);
            if (reason == "cam" && playerId == null) return (400, "Pick the player for the camera.", null);
            if (reason != "cam") { playerId = null; playerName = null; playerColor = null; }
            if (reason.Length == 0 && text.Length == 0) return (400, "Pick a reason or write a few words.", null);
            if (text.Length > MaxText) return (400, $"Keep it to {MaxText} characters.", null);
            var air = _air();
            var picks = Picks(air);
            if (OnProgram(air, picks, lobby, playerId))
                return (409, playerId == null ? $"{lobby} is on stream already." : $"{lobby}'s camera on {playerName ?? "that player"} is on stream already.", null);
            var now = _clock();
            Suggestion made;
            bool merged = false;
            lock (_lock)
            {
                Tidy(now, air, picks);
                // How often this person sends: one every few seconds, a few a minute.
                if (!_sent.TryGetValue(watcherId, out var times)) _sent[watcherId] = times = new List<DateTime>();
                times.RemoveAll(t => (now - t).TotalSeconds >= 60);
                if (times.Count > 0 && (now - times[^1]).TotalSeconds < MinGapSeconds || times.Count >= PerMinute)
                    return (429, "Slow down: one suggestion every few seconds.", null);
                times.Add(now);
                if (_sent.Count > 200) foreach (var k in _sent.Where(kv => kv.Value.All(t => (now - t).TotalSeconds >= 60)).Select(kv => kv.Key).ToList()) _sent.Remove(k);
                // Already suggested: their name goes on it (the newest text wins when they wrote one).
                var same = _live.FirstOrDefault(s => string.Equals(s.Lobby, lobby, StringComparison.OrdinalIgnoreCase) && s.Reason == reason && s.PlayerId == playerId && (reason.Length > 0 || s.Text == text));
                if (same != null)
                {
                    merged = true;
                    if (same.WatcherId != watcherId && !same.Also.Contains(who) && same.Who != who) same.Also.Add(who);
                    if (text.Length > 0) same.Text = text;
                    made = same.Copy();
                }
                else
                {
                    made = new Suggestion
                    {
                        Id = "s" + _run + "-" + (++_seq), Lobby = lobby, Reason = reason, Text = text, Who = who, WatcherId = watcherId, At = now,
                        PlayerId = playerId, PlayerName = playerName, PlayerColor = playerColor,
                    };
                    _live.Insert(0, made);
                    while (_live.Count > MaxLive) Retire(_live[^1], "expired", now);
                    made = made.Copy();
                }
            }
            try { Changed?.Invoke(); } catch (Exception) { }
            return (200, merged ? $"Added your name to the suggestion for {lobby}." : $"Sent: {lobby}, {made.Label}.", made);
        }

        /// <summary>The live suggestions, newest first (cleared ones moved to Earlier first).</summary>
        public List<Suggestion> Live()
        {
            var now = _clock();
            var air = _air();
            var picks = Picks(air);
            lock (_lock)
            {
                Tidy(now, air, picks);
                return _live.Select(s => s.Copy()).ToList();
            }
        }

        /// <summary>Cleared ones, newest first.</summary>
        public List<Suggestion> Earlier()
        {
            var now = _clock();
            var air = _air();
            var picks = Picks(air);
            lock (_lock)
            {
                Tidy(now, air, picks);
                return _earlier.Select(s => s.Copy()).ToList();
            }
        }

        public Suggestion? Find(string id)
        {
            lock (_lock) return (_live.FirstOrDefault(s => s.Id == id) ?? _earlier.FirstOrDefault(s => s.Id == id))?.Copy();
        }

        /// <summary>For the Stream Deck's suggest/next: the newest not yet put into Preview, else the newest. Null when there's none.</summary>
        public Suggestion? Next()
        {
            var live = Live();
            return live.FirstOrDefault(s => !s.Previewed) ?? live.FirstOrDefault();
        }

        /// <summary>The caster put it into Preview (it stays until its lobby goes on Program).</summary>
        public void MarkPreviewed(string id)
        {
            lock (_lock) { if (_live.FirstOrDefault(s => s.Id == id) is { } s) s.Previewed = true; }
        }

        /// <summary>The caster dismissed it. False when it had gone already.</summary>
        public bool Dismiss(string id)
        {
            var now = _clock();
            lock (_lock)
            {
                var s = _live.FirstOrDefault(x => x.Id == id);
                if (s == null) return false;
                Retire(s, "dismissed", now);
                return true;
            }
        }

        /// <summary>Every suggestion for these lobbies goes (the simulation's, when it stops).</summary>
        public void Forget(Func<string, bool> which)
        {
            lock (_lock) { _live.RemoveAll(s => which(s.Lobby)); _earlier.RemoveAll(s => which(s.Lobby)); }
        }

        /// <summary>For the desk and the Watch page: the live ones and Earlier, with ages.</summary>
        public object State()
        {
            var now = _clock();
            var air = _air();
            var picks = Picks(air);
            List<Suggestion> live, earlier;
            lock (_lock)
            {
                Tidy(now, air, picks);
                live = _live.Select(s => s.Copy()).ToList();
                earlier = _earlier.Select(s => s.Copy()).ToList();
            }
            object View(Suggestion s) => new
            {
                s.Id, s.Lobby, s.Reason, s.Label, s.Text, s.Who, s.Also, s.Previewed,
                Cam = s.PlayerId is int id ? new { Id = id, Name = s.PlayerName, Color = s.PlayerColor } : null,
                At = s.At.ToString("o"),
                Ago = Math.Max(0, (int)(now - s.At).TotalSeconds),
                Left = Math.Max(0, (int)Math.Ceiling(ExpireSeconds - (now - s.At).TotalSeconds)),
                s.Cleared,
                ClearedAgo = s.ClearedAt is { } c ? Math.Max(0, (int)(now - c).TotalSeconds) : (int?)null,
            };
            return new { Live = live.Select(View).ToList(), Earlier = earlier.Select(View).ToList() };
        }

        /// <summary>
        /// Clears what no longer applies (under the lock): one that went on Program as suggested after it was
        /// sent (see <see cref="OnProgram"/>), and one older than <see cref="ExpireSeconds"/>.
        /// </summary>
        private void Tidy(DateTime now, OnAir air, Dictionary<string, int?> picks)
        {
            foreach (var s in _live.ToList())
            {
                bool onProgram = air.Since >= s.At && OnProgram(air, picks, s.Lobby, s.PlayerId);
                if (onProgram) Retire(s, "program", now);
                else if ((now - s.At).TotalSeconds >= ExpireSeconds) Retire(s, "expired", now);
            }
        }

        private void Retire(Suggestion s, string why, DateTime now)
        {
            _live.Remove(s);
            s.Cleared = why;
            s.ClearedAt = now;
            _earlier.Insert(0, s);
            if (_earlier.Count > MaxEarlier) _earlier.RemoveRange(MaxEarlier, _earlier.Count - MaxEarlier);
        }

        /// <summary>The text as it may be shown: one line, no control characters, trimmed.</summary>
        internal static string Clean(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var chars = text.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
            return string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
