using System;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Hold and the desk's automation as the caster left them, kept in Red Alert's settings.json ("Desk") so a
    /// restart mid-show keeps them: auto grid, the next-round countdown, the win counter's
    /// and the standings' scope, Starting soon's countdown and line, and Hold (until released, or until a time). The rest of the automation is
    /// kept in its own file already: auto switch and the be-right-back screen (health.json), the after-game
    /// table and how long each graphic stays (broadcast.json), off-screen alerts (alerts.json), lobby voice
    /// and the swoosh (obs.json), player cameras (settings.json).
    /// </summary>
    public sealed record DeskAutomation
    {
        public bool AutoGrid { get; set; }
        /// <summary>Auto intermission was removed (people switch the stream): read from older files, ignored, written as false.</summary>
        public bool AutoIntermission { get; set; }
        /// <summary>When the next round starts (UTC), for the countdown; null: none.</summary>
        public DateTime? NextRoundAt { get; set; }
        public string WinScope { get; set; } = "today";
        public string StandingsScope { get; set; } = "lobby";
        /// <summary>Held until released.</summary>
        public bool Hold { get; set; }
        /// <summary>Held until then (UTC); null: not for a while.</summary>
        public DateTime? HoldUntil { get; set; }
        /// <summary>Why it's held ("Held from the Stream Deck", "Panic"…).</summary>
        public string? HoldWhy { get; set; }
        /// <summary>When the show starts (UTC), for Starting soon's countdown; null: none.</summary>
        public DateTime? SoonAt { get; set; }
        /// <summary>Starting soon's line of text ("": none).</summary>
        public string SoonText { get; set; } = "";

        /// <summary>What the desk is set to now.</summary>
        public static DeskAutomation From(CasterDesk desk)
        {
            var (forever, until, why) = desk.HoldNow();
            return new DeskAutomation
            {
                AutoGrid = desk.AutoGrid,
                NextRoundAt = desk.NextRoundAt,
                WinScope = desk.WinScope,
                StandingsScope = desk.StandingsScope,
                Hold = forever,
                HoldUntil = until,
                HoldWhy = why,
                SoonAt = desk.SoonAt,
                SoonText = desk.SoonText,
            };
        }

        /// <summary>
        /// Puts it back on the desk (at the start). A countdown or a timed hold that has run out meanwhile
        /// isn't brought back; a scope that isn't one is left as it is.
        /// </summary>
        public void ApplyTo(CasterDesk desk, DateTime utcNow)
        {
            desk.AutoGrid = AutoGrid;
            desk.NextRoundAt = NextRoundAt is { } at && at.ToUniversalTime() > utcNow ? at.ToUniversalTime() : null;
            if (WinScope is "today" or "round") desk.WinScope = WinScope;
            if (StandingsScope is "lobby" or "round" or "overall") desk.StandingsScope = StandingsScope;
            if (Hold || HoldUntil != null) desk.RestoreHold((Hold, HoldUntil?.ToUniversalTime(), HoldWhy));
            if (SoonAt is { } soon && soon.ToUniversalTime() > utcNow) desk.SetSoonAt(soon.ToUniversalTime());
            if (!string.IsNullOrEmpty(SoonText)) desk.SetSoonText(SoonText);
        }
    }

    public sealed partial class BroadcastServer
    {
        private readonly object _deskMemoryLock = new object();
        // The desk whose saved automation has been put back: only it is saved (never a new one's defaults over what was kept).
        private CasterDesk? _restoredDesk;

        /// <summary>The desk as it was left (at the start, and for a new administration code).</summary>
        private void RestoreDesk(CasterDesk desk)
        {
            lock (_deskMemoryLock)
            {
                try { _settings.Desk?.ApplyTo(desk, DateTime.UtcNow); }
                catch (Exception e) { Log.Warn("The desk's saved automation couldn't be put back: " + e.Message); }
                _restoredDesk = desk;
            }
        }

        /// <summary>After an action (the desk page's or the Stream Deck's): Hold and the automation saved when they changed.</summary>
        private void KeepDesk()
        {
            try
            {
                var desk = _desk;
                if (desk == null) return;
                lock (_deskMemoryLock)
                {
                    if (!ReferenceEquals(desk, _restoredDesk)) return;
                    var now = DeskAutomation.From(desk);
                    if (now == (_settings.Desk ?? new DeskAutomation())) return;
                    _settings.Desk = now;
                    TrySave();
                }
            }
            catch (Exception) { }
        }
    }
}
