using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>A big graphic waiting for (or taking) its turn on stream.</summary>
    public sealed class Graphic
    {
        public string Id { get; set; } = "";
        /// <summary>"afterGame" (a lobby's table after a game), "playerCard", "storyline" or "standings" (on while switched on).</summary>
        public string Kind { get; set; } = "";
        /// <summary>The lobby it's about (it goes on that lobby's picture), or null: the whole screen.</summary>
        public string? Lobby { get; set; }
        public string Title { get; set; } = "";
        public object? Data { get; set; }
        /// <summary>Stays until it's taken off (the standings switch), rather than for the hold time.</summary>
        public bool Persistent { get; set; }
        public DateTime Queued { get; set; }
        public DateTime? Started { get; set; }
    }

    /// <summary>
    /// Only one big graphic on stream at a time (tables, player cards, storyline notes): each is held
    /// for <see cref="HoldSeconds"/>, then the next. One about a lobby only shows while that lobby is
    /// on screen with room for it (full screen, a big enough tile, or a break); otherwise it waits,
    /// and the next one that can show goes first. Skip and Clear from the Live desk.
    /// </summary>
    public sealed class GraphicsQueue
    {
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly List<Graphic> _waiting = new List<Graphic>();
        private Graphic? _now;
        private long _seq;

        public GraphicsQueue(Func<DateTime> clock) => _clock = clock;

        public double HoldSeconds { get; set; } = 8;
        /// <summary>A graphic that's waited this long without a place on screen is dropped.</summary>
        public TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(10);
        /// <summary>Whether a graphic can be on stream now (set by the graphics app, which knows the layout).</summary>
        public Func<Graphic, bool> CanShow { get; set; } = _ => true;

        /// <summary>Queues a graphic; one of the same kind about the same lobby that's still waiting is replaced.</summary>
        public Graphic Add(string kind, string? lobby, string title, object? data = null, bool persistent = false)
        {
            var g = new Graphic { Id = "g" + Interlocked.Increment(ref _seq), Kind = kind, Lobby = lobby, Title = title, Data = data, Persistent = persistent, Queued = _clock() };
            lock (_lock)
            {
                int same = _waiting.FindIndex(x => x.Kind == kind && string.Equals(x.Lobby, lobby, StringComparison.OrdinalIgnoreCase) && (kind != "playerCard" || Equals(x.Data, data)));
                if (same >= 0) _waiting[same] = g; else _waiting.Add(g);
            }
            return g;
        }

        public bool Has(string kind) { lock (_lock) return _now?.Kind == kind || _waiting.Any(x => x.Kind == kind); }

        /// <summary>Takes every graphic of a kind off (on stream or waiting).</summary>
        public void Remove(string kind) => Remove(g => g.Kind == kind);

        public void Remove(Func<Graphic, bool> which)
        {
            lock (_lock)
            {
                _waiting.RemoveAll(x => which(x));
                if (_now != null && which(_now)) _now = null;
            }
        }

        /// <summary>The graphic on stream now (moving the queue along first).</summary>
        public Graphic? Current()
        {
            var now = _clock();
            lock (_lock)
            {
                if (_now != null && (!_now.Persistent && (now - _now.Started!.Value).TotalSeconds >= HoldSeconds || !CanShow(_now))) _now = null;
                _waiting.RemoveAll(x => now - x.Queued > MaxWait);
                if (_now == null)
                {
                    var next = _waiting.FirstOrDefault(CanShow);
                    if (next != null) { _waiting.Remove(next); next.Started = now; _now = next; }
                }
                return _now;
            }
        }

        /// <summary>Off now; the next one comes up.</summary>
        public void Skip() { lock (_lock) _now = null; }

        public void Clear() { lock (_lock) { _waiting.Clear(); _now = null; } }

        /// <summary>For the Live desk: what's showing, for how much longer, and what's next (and why it's waiting).</summary>
        public object State()
        {
            var cur = Current();
            var now = _clock();
            lock (_lock)
                return new
                {
                    Hold = HoldSeconds,
                    Now = cur == null ? null : new { cur.Id, cur.Kind, cur.Lobby, cur.Title, Left = cur.Persistent ? (int?)null : Math.Max(0, (int)Math.Ceiling(HoldSeconds - (now - cur.Started!.Value).TotalSeconds)) },
                    Next = _waiting.Select(x => new { x.Id, x.Kind, x.Lobby, x.Title, Ready = CanShow(x) }).ToList(),
                };
        }
    }
}
