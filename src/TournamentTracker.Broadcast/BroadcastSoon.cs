using System;
using System.Linq;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>The Starting soon screen on the graphics page (/broadcast/state's "soon").</summary>
    public sealed partial class BroadcastApp
    {
        /// <summary>How long each sponsor stays on the Starting soon screen before the next one's turn.</summary>
        public const int SoonSponsorSeconds = 10;
        private const string SoonKey = "startingsoon";
        private readonly object _soonLock = new object();
        private string? _soonSponsor;

        /// <summary>
        /// While Starting soon is on: the tournament's name (the logo is the state's own), the countdown
        /// (seconds left; "Starting now" once it's 0), the caster's line, and a sponsor. The sponsors are
        /// the ones listed for "startingsoon" in sponsors.json, else the split-screen break's ("break"),
        /// taking turns every <see cref="SoonSponsorSeconds"/> s; each turn is logged as an appearance.
        /// </summary>
        private object? StartingSoon(OnAir air)
        {
            if (air.Layout != "soon")
            {
                SoonSponsor(null);
                return null;
            }
            var all = _desk.Sponsors.Sponsors;
            var list = all.Where(s => s.Placements.Contains(SoonKey)).ToList();
            if (list.Count == 0) list = all.Where(s => s.Placements.Contains("break")).ToList();
            Sponsor? sp = null;
            if (list.Count > 0)
            {
                long turn = (long)Math.Max(0, (_desk.Now - air.Since).TotalSeconds) / SoonSponsorSeconds;
                sp = list[(int)(turn % list.Count)];
            }
            SoonSponsor(sp);
            int? left = _desk.SoonLeft();
            return new
            {
                title = Settings.Current.Theme.TournamentName,
                at = _desk.SoonAt?.ToString("o"),
                left,
                now = left == 0,
                text = _desk.SoonText,
                sponsor = sp == null ? null : new { name = sp.Name, logo = SponsorBook.LogoUrl(sp), tagline = sp.Tagline },
            };
        }

        /// <summary>The sponsor on the Starting soon screen now (null: none): the last one's appearance ends when it changes.</summary>
        private void SoonSponsor(Sponsor? sp)
        {
            lock (_soonLock)
            {
                if (_soonSponsor == sp?.Name) return;
                if (_soonSponsor != null) _desk.Sponsors.End(SoonKey);
                _soonSponsor = sp?.Name;
                if (sp != null) _desk.Sponsors.Begin(SoonKey, sp, SoonKey);
            }
        }
    }
}
