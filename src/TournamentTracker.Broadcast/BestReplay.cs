using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>A replay staged in Preview: which clip, and how the desk and the Stream Deck name it.</summary>
    public sealed class PreviewReplay
    {
        public string ClipId { get; init; } = "";
        public string Lobby { get; init; } = "";
        /// <summary>The card's tier ("must", "veryHigh", "high", "medium").</summary>
        public string Tier { get; init; } = "";
        /// <summary>For the desk: "Replay: LJ · Jake killed Maria in Electrical (MUST SHOW)" (it names the impostor).</summary>
        public string Note { get; init; } = "";
        /// <summary>For the Stream Deck without impostors: "Replay: LJ: kill (MUST SHOW)".</summary>
        public string Label { get; init; } = "";
        /// <summary>A still of the moment is there (the desk's Preview shows it).</summary>
        public bool Thumb { get; init; }
    }

    /// <summary>
    /// Best replay (the desk's N and Action button, the Stream Deck's Best replay key): one choice made
    /// here, so the desk and the Stream Deck never disagree. The saved clips are rated by their card: its
    /// tier first (MUST SHOW, very high, high, medium, from caster-priority.json), then a play viewers
    /// missed (it happened while its lobby was off stream) before one they saw live, then its points,
    /// then the newest. Only clips ready to play, not yet played as a replay, of a play worth a card
    /// (medium and up), and from the last <see cref="Window"/>. "Replay that" clips and montages are
    /// never picked: the caster asked for those by hand.
    /// </summary>
    public static class BestReplay
    {
        /// <summary>
        /// How recent a clip must be. Three minutes: in Among Us a round between meetings usually lasts one
        /// to three minutes, so an older play has usually been overtaken by a meeting or an ejection and no
        /// longer explains what's on screen.
        /// </summary>
        public static TimeSpan Window { get; set; } = TimeSpan.FromMinutes(3);

        public const string NothingYet = "No replay worth showing yet.";

        /// <summary>A clip as Best replay rates it.</summary>
        public sealed record Rated(Clip Clip, string Tier, double Points, bool Missed);

        private static int TierRank(string tier) => tier switch { "must" => 4, "veryHigh" => 3, "high" => 2, "medium" => 1, _ => 0 };

        /// <summary>A tier as the desk writes it.</summary>
        public static string TierName(string tier) => tier switch { "must" => "MUST SHOW", "veryHigh" => "VERY HIGH", "high" => "HIGH", "medium" => "MEDIUM", _ => "LOW" };

        /// <summary>A clip's rating, or null when it isn't one Best replay picks (whatever its state).</summary>
        private static Rated? Rate(Clip c, Func<string, Card?> cardOf, PriorityConfig config, DateTime now)
        {
            if (c.Rule is "instant" or "montage" || c.Rule.Length == 0 || c.PlayedAt != null) return null;
            if (now - c.EventAt > Window) return null;
            var card = c.CardId == null ? null : cardOf(c.CardId);
            var rule = config.Rule(c.Rule);
            if (card == null && !rule.On) return null;
            double points = card?.Value ?? rule.Points;
            if (points < config.Tiers.Medium) return null;
            string tier = card?.Tier is { Length: > 0 } t ? t : config.TierOf(points);
            return new Rated(c, tier, points, card != null && card.ShownAt == null);
        }

        /// <summary>The clips Best replay can pick now, best first.</summary>
        public static List<Rated> Rank(IEnumerable<Clip> clips, Func<string, Card?> cardOf, PriorityConfig config, DateTime now) =>
            clips.Where(c => c.State == "ready" && c.File != null)
                .Select(c => Rate(c, cardOf, config, now)).Where(r => r != null).Select(r => r!)
                .OrderByDescending(r => TierRank(r.Tier)).ThenByDescending(r => r.Missed).ThenByDescending(r => r.Points).ThenByDescending(r => r.Clip.EventAt)
                .ToList();

        /// <summary>
        /// The best one, or null. Pressed again while <paramref name="inPreview"/> is staged, the next one
        /// down (round to the best again after the last), so each press offers another.
        /// </summary>
        public static Rated? Pick(IEnumerable<Clip> clips, Func<string, Card?> cardOf, PriorityConfig config, DateTime now, string? inPreview = null)
        {
            var ranked = Rank(clips, cardOf, config, now);
            if (ranked.Count == 0) return null;
            int at = inPreview == null ? -1 : ranked.FindIndex(r => r.Clip.Id == inPreview);
            return ranked[at < 0 ? 0 : (at + 1) % ranked.Count];
        }

        /// <summary>A worthwhile play whose clip is still being saved (so "nothing yet" can say one is coming).</summary>
        public static bool Saving(IEnumerable<Clip> clips, Func<string, Card?> cardOf, PriorityConfig config, DateTime now) =>
            clips.Any(c => c.State == "saving" && Rate(c, cardOf, config, now) != null);

        /// <summary>What goes in Preview for a pick.</summary>
        public static PreviewReplay ForPreview(Rated r) => new PreviewReplay
        {
            ClipId = r.Clip.Id,
            Lobby = r.Clip.Lobby,
            Tier = r.Tier,
            Note = $"Replay: {r.Clip.Lobby} · {NameTag.Plain(r.Clip.Title)} ({TierName(r.Tier)})",
            Label = $"Replay: {BroadcastServer.ReplayLabel(r.Clip)} ({TierName(r.Tier)})",
            Thumb = r.Clip.Thumbnail != null,
        };
    }

    public sealed partial class CasterDesk
    {
        /// <summary>The lobby ranking's weights and tiers as they are now (caster-priority.json).</summary>
        public PriorityConfig Priority => _config();

        /// <summary>Preview is this replay alone: Take plays it.</summary>
        public PreviewState StageReplay(PreviewReplay replay)
        {
            lock (_lock) _preview = new PreviewState { Replay = replay };
            Touch();
            return Preview;
        }

        /// <summary>Preview is this lobby's player camera (who it follows is set apart): Take puts it on stream.</summary>
        public PreviewState PreviewCam(string lobby)
        {
            lock (_lock) _preview = new PreviewState { Slots = new[] { lobby }, Chosen = "cams" };
            Touch();
            return Preview;
        }

        /// <summary>A staged replay went on (or is gone): Preview is emptied, unless something else was staged since.</summary>
        public void ReplayTaken(PreviewState staged)
        {
            lock (_lock) { if (ReferenceEquals(_preview, staged)) _preview = new PreviewState(); }
            Touch();
        }
    }

    public sealed partial class BroadcastServer
    {
        /// <summary>The best replay now, for the Stream Deck's key, or null.</summary>
        private BestReplay.Rated? BestNow()
        {
            var desk = _desk;
            var replays = _replays;
            if (desk == null || replays == null) return null;
            return BestReplay.Pick(replays.Clips, desk.Find, desk.Priority, DateTime.UtcNow);
        }

        /// <summary>
        /// Best replay (/deck/replay/best, and its old links /deck/lobby/action and /deck/action): the best
        /// recent clip into Preview, named; Take plays it. Nothing worth it: said, and nothing changes.
        /// Needs no OBS (only playing it does) and isn't held: it switches nothing.
        /// </summary>
        private (int Status, string Message) BestReplayToPreview()
        {
            var desk = _desk;
            var replays = _replays;
            if (desk == null || replays == null) return (423, "Red Alert is locked.");
            var now = DateTime.UtcNow;
            var clips = replays.Clips;
            var pick = BestReplay.Pick(clips, desk.Find, desk.Priority, now, desk.Preview.Replay?.ClipId);
            if (pick == null)
                return (409, BestReplay.Saving(clips, desk.Find, desk.Priority, now) ? "No replay worth showing yet: one is still being saved." : BestReplay.NothingYet);
            var staged = BestReplay.ForPreview(pick);
            desk.StageReplay(staged);
            return (200, $"{staged.Note}. In Preview: Take plays it.");
        }

        /// <summary>Take with a replay in Preview: it plays, as Play last does, and leaves Preview.</summary>
        private async Task<(int Status, string Message)> TakeReplayAsync(CasterDesk desk, PreviewState staged)
        {
            var replays = _replays;
            if (replays == null || _obs == null) return (423, "Red Alert is locked.");
            var clip = replays.Find(staged.Replay!.ClipId);
            if (clip == null || clip.State != "ready")
            {
                desk.ReplayTaken(staged);
                return (409, "That replay is gone: Preview is cleared.");
            }
            if (!_obs.Connected) return (503, "OBS isn't connected: replays play in OBS.");
            string said = await replays.PlayAsync(clip).ConfigureAwait(false);
            if (replays.Now?.Id != clip.Id) return (409, said);
            desk.ReplayTaken(staged);
            return (200, said);
        }
    }
}
