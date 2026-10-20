using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// "Starting soon": the screen before the show (the TT Starting Soon scene). A countdown to a start time
    /// the caster sets (a time, or in so many minutes), the tournament's name and logo, the break sponsors
    /// taking turns and a line of the caster's own. At zero it says "Starting now" and stays: it never
    /// switches by itself (nothing automatic runs while it's on); the caster Takes a lobby to start.
    /// </summary>
    public sealed partial class CasterDesk
    {
        /// <summary>The longest line of text on the screen.</summary>
        public const int SoonTextMax = 120;

        private DateTime? _soonAt;
        private string _soonText = "";
        private OnAir? _beforeSoon;

        /// <summary>The desk's clock (the graphics' sponsor turns go by it).</summary>
        internal DateTime Now => _clock();

        /// <summary>When the show starts (UTC), for the countdown; null: no countdown.</summary>
        public DateTime? SoonAt { get { lock (_lock) return _soonAt; } }
        /// <summary>The caster's line on the screen ("" for none).</summary>
        public string SoonText { get { lock (_lock) return _soonText; } }

        /// <summary>Starting soon on stream (at once, like the other breaks).</summary>
        public OnAir ShowStartingSoon(string by = "button", string? transition = null) => PutOn(() =>
        {
            if (_onAir.Layout is not ("intermission" or "replay" or "none" or "slate" or "soon")) _beforeSoon = _onAir;
            return new OnAir { Layout = "soon", Slots = new List<string?>(), By = by, Since = _clock(), Transition = transition };
        });

        /// <summary>Off Starting soon (the Stream Deck's "end break"): what was on before, else the top lobby.</summary>
        private OnAir EndStartingSoon(string by, string? transition)
        {
            OnAir? back;
            lock (_lock) { back = _beforeSoon; _beforeSoon = null; }
            return Restore(back, by, transition);
        }

        /// <summary>The countdown's start time (null clears it).</summary>
        public void SetSoonAt(DateTime? at)
        {
            lock (_lock) _soonAt = at;
            Touch();
        }

        /// <summary>The line on the screen: one line, at most <see cref="SoonTextMax"/> characters ("" clears it).</summary>
        public string SetSoonText(string? text)
        {
            string t = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (t.Length > SoonTextMax) t = t.Substring(0, SoonTextMax).TrimEnd();
            lock (_lock) _soonText = t;
            Touch();
            return t;
        }

        /// <summary>Seconds to the start (0 once it's passed: "Starting now"), or null with no start time.</summary>
        public int? SoonLeft()
        {
            var at = SoonAt;
            return at is { } a ? Math.Max(0, (int)Math.Ceiling((a - _clock()).TotalSeconds)) : null;
        }

        /// <summary>For the desk page: on stream or not, the start time, seconds left and the line.</summary>
        public object SoonState()
        {
            var at = SoonAt;
            return new { On = OnAir.Layout == "soon", At = at?.ToString("o"), Left = SoonLeft(), Text = SoonText };
        }

        /// <summary>
        /// A start time as the caster types it: minutes from now ("10", "in 10", "10 min") or a time of day in
        /// <paramref name="zone"/> ("19:30", "7:30pm"; one that's passed today is tomorrow's). Null when it
        /// isn't one of those.
        /// </summary>
        public static DateTime? ParseStart(string? value, DateTime utcNow, TimeZoneInfo zone)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            var mins = Regex.Match(v, @"^(?:in\s*)?(\d{1,4})\s*(?:m|min|mins|minute|minutes)?$");
            if (mins.Success)
            {
                int n = int.Parse(mins.Groups[1].Value, CultureInfo.InvariantCulture);
                return n is >= 0 and <= 24 * 60 ? utcNow.AddMinutes(n) : null;
            }
            var time = Regex.Match(v, @"^(\d{1,2})(?:[:.](\d{2}))?\s*(am|pm)?$");
            if (!time.Success || time.Groups[2].Length == 0 && time.Groups[3].Length == 0) return null;
            int h = int.Parse(time.Groups[1].Value, CultureInfo.InvariantCulture);
            int m = time.Groups[2].Length > 0 ? int.Parse(time.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (time.Groups[3].Length > 0)
            {
                if (h is < 1 or > 12) return null;
                h = h % 12 + (time.Groups[3].Value == "pm" ? 12 : 0);
            }
            if (h > 23 || m > 59) return null;
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
            var start = local.Date.AddHours(h).AddMinutes(m);
            if (start <= local) start = start.AddDays(1);
            // A time the clocks skip that night (going forward an hour) isn't one.
            try { return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), zone); }
            catch (ArgumentException) { return null; }
        }

        /// <summary>
        /// The Stream Deck's (and the desk's) Starting soon settings from a link's query: in=MINUTES or
        /// at=TIME sets the countdown (in= or at= empty clears it), text= the line. What it did, or why not.
        /// </summary>
        public (bool Ok, string Message) SetSoon(string? inMinutes, string? at, string? text, TimeZoneInfo? zone = null)
        {
            var said = new List<string>();
            string? when = inMinutes ?? at;
            if (when != null)
            {
                if (when.Trim().Length == 0) { SetSoonAt(null); said.Add("no countdown"); }
                else if (ParseStart(when, _clock(), zone ?? TimeZoneInfo.Local) is { } start)
                {
                    SetSoonAt(start);
                    var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(start, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local);
                    said.Add($"starts at {local:HH:mm}");
                }
                else return (false, "Type minutes (10) or a time (19:30).");
            }
            if (text != null) { string t = SetSoonText(text); said.Add(t.Length == 0 ? "no text" : $"text \"{t}\""); }
            return (true, said.Count == 0 ? "Nothing to change." : "Starting soon: " + string.Join(", ", said) + ".");
        }
    }
}
