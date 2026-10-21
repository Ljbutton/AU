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
    /// each cleared when its lobby goes on Program, after <see cref="ExpireSeconds"/>, or when the caster
    /// dismisses it, then kept a while under Earlier. In memory only (gone after a restart). Bounded: how
    /// many are live and kept, how long the text is, and how often one person can send.
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
        private readonly List<Suggestion> _live = new List<Suggestion>();
        private readonly List<Suggestion> _earlier = new List<Suggestion>();
        private readonly Dictionary<string, List<DateTime>> _sent = new Dictionary<string, List<DateTime>>();
        private long _seq;

        /// <summary>A suggestion arrived (or someone added their name to one): the desk and the Stream Deck look again.</summary>
        public event Action? Changed;

        public SuggestionBox(Func<DateTime> clock, Func<OnAir> air)
        {
            _clock = clock;
            _air = air;
        }

        /// <summary>
        /// A suggestion from <paramref name="who"/> (watcher <paramref name="watcherId"/>). The same lobby, reason
        /// and player as one that's live adds their name to it instead. Refused (with why, and an HTTP status):
        /// too often (429), text too long or nothing said (400), a lobby already on stream (409, unless it's a
        /// camera suggestion).
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
            if (playerId == null && air.Layout != "replay" && air.Has(lobby)) return (409, $"{lobby} is on stream already.", null);
            var now = _clock();
            Suggestion made;
            bool merged = false;
            lock (_lock)
            {
                Tidy(now, air);
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
                        Id = "s" + (++_seq), Lobby = lobby, Reason = reason, Text = text, Who = who, WatcherId = watcherId, At = now,
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
            lock (_lock)
            {
                Tidy(now, air);
                return _live.Select(s => s.Copy()).ToList();
            }
        }

        /// <summary>Cleared ones, newest first.</summary>
        public List<Suggestion> Earlier()
        {
            var now = _clock();
            var air = _air();
            lock (_lock)
            {
                Tidy(now, air);
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
            List<Suggestion> live, earlier;
            lock (_lock)
            {
                Tidy(now, air);
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
        /// Clears what no longer applies (under the lock): a lobby that went on Program after it was suggested
        /// (a camera suggestion: its camera did), and one older than <see cref="ExpireSeconds"/>.
        /// </summary>
        private void Tidy(DateTime now, OnAir air)
        {
            foreach (var s in _live.ToList())
            {
                bool onProgram = air.Layout != "replay" && air.Has(s.Lobby) && air.Since >= s.At && (!s.IsCam || air.IsCam);
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
