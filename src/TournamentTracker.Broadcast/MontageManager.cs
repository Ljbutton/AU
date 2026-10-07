using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Montages: at the end of each game (its kills, ejections and ending), at the end of each round
    /// ("every kill", then the top plays), and custom ones from the Moments library. Built with
    /// ffmpeg in the background; the caster tab shows "Montage ready" with Play, Preview and Discard.
    /// A custom montage is deleted once it has played (its clips stay, marked used).
    /// </summary>
    public sealed class MontageManager
    {
        public const double CardSeconds = 2.5, SponsorSeconds = 3.5;
        private readonly CasterDesk _desk;
        private readonly ReplayManager _replays;
        private readonly MontageBuilder _builder;
        private readonly SponsorBook? _sponsors;
        private readonly Func<ReplaySettings> _settings;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly List<Montage> _list = new List<Montage>();
        private readonly HashSet<int> _roundsDone = new HashSet<int>();
        // Made while simulating: removed when it stops (with the rounds it marked done).
        private readonly HashSet<string> _simMade = new HashSet<string>();
        private readonly HashSet<int> _simRounds = new HashSet<int>();
        private readonly SemaphoreSlim _one = new SemaphoreSlim(1, 1);
        private long _seq;

        /// <summary>Goes up each time a custom montage has played (the Moments library clears its selection).</summary>
        public int CustomPlayed { get; private set; }
        /// <summary>A montage is ready (the intermission queues it).</summary>
        public event Action<Montage>? Ready;

        public MontageBuilder Builder => _builder;

        public MontageManager(CasterDesk desk, ReplayManager replays, MontageBuilder builder, SponsorBook? sponsors, Func<ReplaySettings> settings, Func<DateTime>? clock = null)
        {
            _desk = desk;
            _replays = replays;
            _builder = builder;
            _sponsors = sponsors;
            _settings = settings;
            _clock = clock ?? (() => DateTime.UtcNow);
            desk.Archive.Finished += g =>
            {
                if (!_settings().AutoMontages || _builder.Ffmpeg == null) return;
                _ = Task.Run(() => AfterGameAsync(g));
            };
            replays.Ended += OnEnded;
        }

        public List<Montage> All { get { lock (_lock) return _list.ToList(); } }
        public Montage? Find(string id) { lock (_lock) return _list.FirstOrDefault(m => m.Id == id); }

        private Montage New(string kind, string title, string? lobby, int round)
        {
            var m = new Montage { Id = "m" + Interlocked.Increment(ref _seq), Kind = kind, Title = title, Lobby = lobby, Round = round, Created = _clock() };
            lock (_lock) { _list.Add(m); if (_desk.Simulating) _simMade.Add(m.Id); }
            return m;
        }

        // ---- When to build ----------------------------------------------------------------------

        /// <summary>A game ended: once its last clips are saved, build its montage; and the round's, when the round is over.</summary>
        private async Task AfterGameAsync(ArchivedGame g)
        {
            // The game-end clip is saved a couple of seconds after the end.
            var until = _clock().AddSeconds(25);
            await Task.Delay(TimeSpan.FromSeconds(_settings().PostSeconds + 1)).ConfigureAwait(false);
            while (_clock() < until && _replays.Clips.Any(c => c.Lobby == g.Lobby && c.State == "saving"))
                await Task.Delay(500).ConfigureAwait(false);
            await GameAsync(g).ConfigureAwait(false);
            int round = g.Round;
            if (round > 0 && RoundOver(round)) await RoundAsync(round).ConfigureAwait(false);
            // A new round started before the last one was finished off: build that one now.
            foreach (int earlier in _desk.Archive.Games.Select(x => x.Round).Where(r => r > 0 && r < round).Distinct().ToList())
                if (!Done(earlier)) await RoundAsync(earlier).ConfigureAwait(false);
        }

        /// <summary>Every lobby in the round has played its games.</summary>
        public bool RoundOver(int round)
        {
            var games = _desk.Archive.InRound(round);
            if (games.Count == 0) return false;
            int per = Math.Max(1, _desk.GamesPerRound());
            return games.GroupBy(x => x.Lobby, StringComparer.OrdinalIgnoreCase).All(l => l.Count() >= per);
        }

        private bool Done(int round) { lock (_lock) return _roundsDone.Contains(round); }

        // ---- What goes in -----------------------------------------------------------------------

        /// <summary>How good a play is for "top plays".</summary>
        public static int Score(Clip c) => c.Rule switch
        {
            "winningKill" => 6,
            "witnessedKill" => 5,
            "eject" when NameTag.Plain(c.Title).EndsWith(": Impostor", StringComparison.Ordinal) => 5,
            "gameEnd" => 3,
            "eject" => 3,
            "kill" => 2,
            "meeting" => 1,
            _ => 0,
        };

        private List<Clip> ReadyClips(Func<Clip, bool> where) => _replays.Clips.Where(c => c.State == "ready" && c.File != null && File.Exists(c.File) && where(c)).ToList();

        private async Task<List<Segment>> CutAsync(IEnumerable<Clip> clips, double? before = null, double? after = null, Func<Clip, string?>? lower = null)
        {
            var list = new List<Segment>();
            foreach (var c in clips)
            {
                double? d = c.Duration ?? await _builder.ProbeAsync(c.File!).ConfigureAwait(false);
                if (d == null || d < 0.5) continue;
                c.Duration = d;
                var seg = _builder.Cut(c, d.Value, before, after);
                if (lower != null) seg.Lower = lower(c);
                list.Add(seg);
            }
            return list;
        }

        private Segment? SponsorCard(Montage m)
        {
            var sp = _sponsors?.Next("montage");
            if (sp == null) return null;
            m.Sponsor = sp.Name;
            return new Segment
            {
                CardTop = "PRESENTED BY",
                Card = sp.Name,
                CardSmall = sp.Tagline.Length > 0 ? sp.Tagline : null,
                CardLogo = sp.Logo.Length > 0 && File.Exists(sp.Logo) ? sp.Logo : null,
                Length = SponsorSeconds,
            };
        }

        private static Segment Card(string title, string? small = null) => new Segment { Card = title, CardSmall = small, Length = CardSeconds };

        /// <summary>The end-of-game montage: the game's best moments in order, 30–60 seconds.</summary>
        public async Task<Montage> GameAsync(ArchivedGame g)
        {
            var s = _settings();
            var m = New("game", $"{g.Lobby} · {(g.Game.Length > 0 ? g.Game : "game")}", g.Lobby, g.Round);
            var end = (g.EndedAt ?? _clock()).AddSeconds(10);
            var clips = ReadyClips(c => string.Equals(c.Lobby, g.Lobby, StringComparison.OrdinalIgnoreCase) && c.EventAt >= g.StartedAt.AddSeconds(-5) && c.EventAt <= end);
            m.ClipIds = clips.Select(c => c.Id).ToList();
            var picked = MontageBuilder.Pick(clips, s.GameMontageSeconds - CardSeconds, s.MontageBefore + s.MontageAfter);
            var segs = new List<Segment>();
            if (SponsorCard(m) is { } sc) segs.Add(sc);
            string result = g.Winner == null ? "" : $"{g.Winner} win{(g.How != null ? " by " + g.How : "")}";
            segs.Add(Card(g.Lobby.ToUpperInvariant(), $"{(g.Game.Length > 0 ? g.Game + " · " : "")}{result}"));
            segs.AddRange(await CutAsync(picked).ConfigureAwait(false));
            return await BuildAsync(m, segs, minClips: 1).ConfigureAwait(false);
        }

        /// <summary>The end-of-round montage: every kill of the round, then the top plays counting down.</summary>
        public async Task<Montage?> RoundAsync(int round, bool again = false)
        {
            lock (_lock)
            {
                if (_roundsDone.Contains(round) && !again) return null;
                _roundsDone.Add(round);
                if (_desk.Simulating) _simRounds.Add(round);
            }
            var s = _settings();
            var m = New("round", $"Round {round}", null, round);
            var all = ReadyClips(c => c.Round == round && c.Rule != "montage");
            m.ClipIds = all.Select(c => c.Id).ToList();
            var segs = new List<Segment>();
            if (SponsorCard(m) is { } sc) segs.Add(sc);

            // Every kill: about 60% of the time; shorter cuts when there are lots, then the best of them.
            var kills = all.Where(c => c.Rule is "kill" or "winningKill").OrderBy(c => c.EventAt).ToList();
            double budget = s.RoundMontageSeconds * 0.6 - CardSeconds;
            double each = s.MontageBefore + s.MontageAfter;
            if (kills.Count * (each - MontageBuilder.Transition) > budget) each = Math.Max(2.5, budget / Math.Max(1, kills.Count) + MontageBuilder.Transition);
            double scale = each / (s.MontageBefore + s.MontageAfter);
            kills = MontageBuilder.Pick(kills, budget, each);
            if (kills.Count > 0)
            {
                segs.Add(Card($"ROUND {round}", "Every kill"));
                segs.AddRange(await CutAsync(kills, s.MontageBefore * scale, s.MontageAfter * scale).ConfigureAwait(false));
            }

            // Top plays: the five best, from #5 up to #1.
            var top = all.Where(c => Score(c) >= 3).OrderByDescending(Score).ThenBy(c => c.EventAt).Take(5).ToList();
            if (top.Count > 0)
            {
                segs.Add(Card("TOP PLAYS", $"Round {round}"));
                top.Reverse();
                int n = top.Count;
                var rank = top.Select((c, i) => (c.Id, n - i)).ToDictionary(x => x.Id, x => x.Item2);
                segs.AddRange(await CutAsync(top, lower: c => $"#{rank[c.Id]} · {MontageBuilder.LowerThird(c)} ({c.Lobby})").ConfigureAwait(false));
            }
            return await BuildAsync(m, segs, minClips: 1).ConfigureAwait(false);
        }

        /// <summary>A montage of the clips picked in the Moments library, in that order.</summary>
        public async Task<Montage> CustomAsync(IReadOnlyList<string> clipIds, string? title)
        {
            var m = New("custom", string.IsNullOrWhiteSpace(title) ? "Moments" : title!.Trim(), null, 0);
            var clips = clipIds.Select(id => _replays.Find(id)).Where(c => c != null && c.State == "ready" && c.File != null && File.Exists(c.File)).Cast<Clip>().ToList();
            m.ClipIds = clips.Select(c => c.Id).ToList();
            var segs = new List<Segment>();
            if (SponsorCard(m) is { } sc) segs.Add(sc);
            segs.AddRange(await CutAsync(clips).ConfigureAwait(false));
            return await BuildAsync(m, segs, minClips: 1).ConfigureAwait(false);
        }

        private async Task<Montage> BuildAsync(Montage m, List<Segment> segs, int minClips)
        {
            if (segs.Count(x => x.Card == null) < minClips)
            {
                m.State = "failed";
                m.Problem = "No saved clips to put in it.";
                return m;
            }
            await _one.WaitAsync().ConfigureAwait(false);
            try { await _builder.BuildAsync(m, segs).ConfigureAwait(false); }
            finally { _one.Release(); }
            if (m.State == "ready")
            {
                // Up next in intermission (custom montages are for right now, not the queue).
                if (m.Kind != "custom") _desk.QueuedMontage = (m.Id, NameTag.Plain(m.Title));
                Ready?.Invoke(m);
            }
            return m;
        }

        // ---- Playing ----------------------------------------------------------------------------

        public async Task<string> PlayAsync(string id)
        {
            var m = Find(id);
            if (m == null) return "That montage is gone.";
            if (m.State != "ready" && m.State != "played" || m.File == null) return m.State == "building" ? "Still building." : "Not ready: " + (m.Problem ?? "it failed.");
            string said = await _replays.PlayVideoAsync(m.Id, m.File, m.Title, m.Sponsor, m.Moments).ConfigureAwait(false);
            if (_desk.QueuedMontage?.Id == m.Id && _replays.Now?.Id == m.Id) _desk.QueuedMontage = null;
            if (m.Sponsor != null && _replays.Now?.Id == m.Id)
                _sponsors?.Log(m.Sponsor, "montage", _clock(), _clock().AddSeconds(SponsorSeconds), m.Lobby, m.Title);
            foreach (var cid in m.ClipIds) if (_replays.Find(cid) is { } c && m.Kind == "custom") c.Used = true;
            return said;
        }

        private void OnEnded(Clip clip)
        {
            var m = Find(clip.Id);
            if (m == null) return;
            m.State = "played";
            if (m.Kind != "custom") return;
            // A custom montage is a one-off: gone once it has played, and the selection starts again.
            Discard(m.Id);
            CustomPlayed++;
        }

        public string Discard(string id)
        {
            Montage? m;
            lock (_lock)
            {
                m = _list.FirstOrDefault(x => x.Id == id);
                if (m == null) return "That montage is gone.";
                if (_replays.Now?.Id == id && m.State != "played") return "It's playing: go back to live first.";
                _list.Remove(m);
            }
            if (_desk.QueuedMontage?.Id == id) _desk.QueuedMontage = null;
            try { if (m.File != null && File.Exists(m.File)) File.Delete(m.File); } catch (Exception) { }
            return "Montage discarded.";
        }

        /// <summary>Simulation stopped: montages of its lobbies or clips go, files too. Their ids.</summary>
        public HashSet<string> Forget(Func<string, bool> which, ISet<string> clips)
        {
            List<Montage> gone;
            lock (_lock)
            {
                gone = _list.Where(m => _simMade.Contains(m.Id) || which(m.Lobby ?? "") || m.ClipIds.Any(clips.Contains) || m.Moments.Any(x => which(x.Lobby))).ToList();
                _list.RemoveAll(gone.Contains);
                _roundsDone.ExceptWith(_simRounds);
                _simRounds.Clear();
                _simMade.Clear();
            }
            foreach (var m in gone)
            {
                if (_desk.QueuedMontage?.Id == m.Id) _desk.QueuedMontage = null;
                try { if (m.File != null && File.Exists(m.File)) File.Delete(m.File); } catch (Exception) { }
            }
            return gone.Select(m => m.Id).ToHashSet();
        }

        public object State()
        {
            var now = _clock();
            lock (_lock)
                return new
                {
                    Ffmpeg = _builder.Ffmpeg != null,
                    CustomPlayed,
                    List = _list.AsEnumerable().Reverse().Take(20).Select(m => new
                    {
                        m.Id, m.Kind, m.Title, m.State, m.Problem, m.Duration, m.Sponsor, m.Lobby, m.Round,
                        Clips = m.ClipIds.Count,
                        Ago = Math.Max(0, (int)(now - m.Created).TotalSeconds),
                        Playing = _replays.Now?.Id == m.Id,
                    }).ToList(),
                };
        }
    }
}
