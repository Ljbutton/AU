using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Replays: saves a clip for every kill (and for any card on demand), plays it in OBS's TT Replay
    /// scene cropped and zoomed so the players in it stay in frame (following them as the camera
    /// moves), with a slow push-in from the wide shot, and the controls: play/pause, ±1 s, frame
    /// step, scrub, restart, zoom, pan, and back to live.
    /// </summary>
    public sealed class ReplayManager : IDisposable
    {
        private const double FrameSeconds = 1 / 30.0;
        private readonly CasterDesk _desk;
        private readonly ObsDirector _obs;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly List<Clip> _clips = new List<Clip>();
        private readonly SemaphoreSlim _saveOne = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _step = new SemaphoreSlim(1, 1);
        private Timer? _timer;
        private long _seq;

        // What's playing.
        private Clip? _now;
        private bool _playing, _ended;
        private double _cursor, _played;
        private DateTime _cursorAt, _lastTick, _nextSync;
        private double _zoomBy = 1, _panX, _panY;
        private bool _follow = true;
        private View _lastView = new View(0.5, 0.5, 1);
        private OnAir? _returnTo;

        public string? Problem { get; private set; }
        /// <summary>The REPLAY tag's text, for the tag page.</summary>
        public Action<string>? TagChanged { get; set; }

        private ReplaySettings S => _obs.Settings.Replay;

        public ReplayManager(CasterDesk desk, ObsDirector obs, Func<DateTime>? clock = null, bool loop = true)
        {
            _desk = desk;
            _obs = obs;
            _clock = clock ?? (() => DateTime.UtcNow);
            desk.CardMade += card =>
            {
                if (S.On && S.OnEveryKill && (card.Rule == "kill" || card.Rule == "winningKill") && _obs.Connected)
                    _ = Task.Run(() => SaveAsync(card));
            };
            if (loop) _timer = new Timer(_ => _ = TickAsync(), null, 100, 33);
        }

        public Clip? Find(string id) { lock (_lock) return _clips.FirstOrDefault(c => c.Id == id); }
        public Clip? ForCard(string cardId) { lock (_lock) return _clips.LastOrDefault(c => c.CardId == cardId); }

        // ---- Saving -------------------------------------------------------------------------------

        /// <summary>The clip for a card: who to keep in frame, and when the play was.</summary>
        public Clip Describe(Card card)
        {
            var clip = new Clip { Id = "r" + Interlocked.Increment(ref _seq), Lobby = card.Lobby, Title = card.Text, CardId = card.Id, Pre = S.PreSeconds, Post = S.PostSeconds, EventAt = card.At };
            if (card.Source is { } src)
            {
                if (src.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number) clip.EventAt = _desk.Tracks.ToCaster(card.Lobby, t.GetInt64());
                foreach (var who in new[] { "killer", "victim", "impostor", "crewmate", "player", "witness", "caller", "body", "ejected" })
                    if (src.TryGetProperty(who, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("id", out var id) && !clip.Focus.Contains(id.GetInt32()))
                        clip.Focus.Add(id.GetInt32());
                // The body stays where the kill was.
                if (src.TryGetProperty("screen", out var sc) && sc.ValueKind == JsonValueKind.Object && sc.TryGetProperty("onScreen", out var on) && on.GetBoolean())
                    clip.Marks.Add((clip.EventAt, sc.GetProperty("x").GetDouble(), sc.GetProperty("y").GetDouble()));
                // Meetings and ejections happen in the meeting screen: no crop.
                if (card.Rule is "eject" or "ejectSkipped" or "meeting" or "gameEnd" or "gameStart") clip.Focus.Clear();
            }
            return clip;
        }

        /// <summary>Saves a replay clip for a card: waits for the seconds after the play, then has OBS write the lobby's buffer.</summary>
        public async Task<Clip> SaveAsync(Card card)
        {
            var clip = Describe(card);
            lock (_lock)
            {
                _clips.Add(clip);
                if (_clips.Count > 120) _clips.RemoveRange(0, _clips.Count - 120);
            }
            _desk.SetClip(card.Id, clip.Id, "saving");
            try
            {
                if (!_obs.Connected) throw new InvalidOperationException("Connect OBS first: replays come from its replay buffer.");
                var now = _clock();
                if ((now - clip.EventAt).TotalSeconds > S.BufferSeconds - clip.Pre)
                    throw new InvalidOperationException($"Too long ago: OBS keeps the last {S.BufferSeconds}s of each lobby.");
                var wait = clip.EventAt.AddSeconds(clip.Post + 0.3) - now;
                if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
                await _saveOne.WaitAsync().ConfigureAwait(false);
                try
                {
                    clip.SavedAt = _clock();
                    clip.File = await _obs.SaveClipAsync(clip.Lobby).ConfigureAwait(false);
                }
                finally { _saveOne.Release(); }
                // The positions for the last seconds arrive about a second late.
                await Task.Delay(1300).ConfigureAwait(false);
                clip.Samples = _desk.Tracks.Between(clip.Lobby, clip.EventAt.AddSeconds(-clip.Pre - 2), clip.SavedAt.Value.AddSeconds(1));
                clip.State = "ready";
            }
            catch (Exception e)
            {
                clip.State = "failed";
                clip.Problem = e.Message;
            }
            _desk.SetClip(card.Id, clip.Id, clip.State);
            return clip;
        }

        // ---- Playing ------------------------------------------------------------------------------

        public async Task<string> PlayAsync(string clipId)
        {
            var clip = Find(clipId);
            if (clip == null) return "That replay is gone.";
            if (clip.State != "ready" || clip.File == null) return clip.State == "saving" ? "The replay is still being saved." : "No replay: " + (clip.Problem ?? "it wasn't saved.");
            try
            {
                double duration = await _obs.LoadClipAsync(clip.File).ConfigureAwait(false);
                clip.Duration = duration;
                double start = clip.StartAt(S.VideoDelayMs);
                await _obs.SeekAsync(start).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_now == null || _desk.OnAir.Layout != "replay") _returnTo = _desk.OnAir;
                    _now = clip;
                    _cursor = start;
                    _cursorAt = _clock();
                    _played = 0;
                    _zoomBy = 1; _panX = _panY = 0; _follow = true;
                    _playing = true;
                    _ended = false;
                    _nextSync = _clock().AddSeconds(0.25);
                }
                await _obs.FrameAsync(new View(0.5, 0.5, 1)).ConfigureAwait(false);
                await _obs.ShowReplaySceneAsync().ConfigureAwait(false);
                await _obs.MediaAsync("play").ConfigureAwait(false);
                _desk.ReplayOn(clip.Lobby);
                TagChanged?.Invoke(JsonSerializer.Serialize(new { on = true, lobby = clip.Lobby, title = clip.Title }));
                Problem = null;
                return $"Replay: {clip.Lobby} · {NameTag.Plain(clip.Title)}.";
            }
            catch (Exception e)
            {
                Problem = e.Message;
                return "Replay failed: " + e.Message;
            }
        }

        /// <summary>One replay control: toggle, play, pause, back, forward, frameBack, frameForward, seek (seconds), restart, zoomIn, zoomOut, pan (dx, dy), follow, live.</summary>
        public async Task<string?> ControlAsync(string action, double value = 0, double value2 = 0)
        {
            var clip = _now;
            if (clip == null) return action == "live" ? null : "No replay is playing.";
            try
            {
                switch (action)
                {
                    case "toggle":
                        return await ControlAsync(_playing ? "pause" : "play").ConfigureAwait(false);
                    case "play":
                        if (_ended || _cursor >= (clip.Duration ?? 0) - 0.05) return await ControlAsync("restart").ConfigureAwait(false);
                        await _obs.MediaAsync("play").ConfigureAwait(false);
                        lock (_lock) { _playing = true; _cursorAt = _clock(); }
                        return null;
                    case "pause":
                        await _obs.MediaAsync("pause").ConfigureAwait(false);
                        lock (_lock) { _cursor = Cursor(); _playing = false; _cursorAt = _clock(); }
                        // Where OBS actually stopped, so stepping from here is exact.
                        try { var st = await _obs.MediaStatusAsync().ConfigureAwait(false); lock (_lock) { _cursor = st.Cursor; _cursorAt = _clock(); } }
                        catch (Exception) { }
                        return null;
                    case "back": await SeekToAsync(Cursor() - (value == 0 ? 1 : value)).ConfigureAwait(false); return null;
                    case "forward": await SeekToAsync(Cursor() + (value == 0 ? 1 : value)).ConfigureAwait(false); return null;
                    case "frameBack":
                    case "frameForward":
                        if (_playing) await ControlAsync("pause").ConfigureAwait(false);
                        await SeekToAsync(Cursor() + (action == "frameBack" ? -FrameSeconds : FrameSeconds)).ConfigureAwait(false);
                        return null;
                    case "seek": await SeekToAsync(value).ConfigureAwait(false); return null;
                    case "restart":
                        await SeekToAsync(clip.StartAt(S.VideoDelayMs)).ConfigureAwait(false);
                        lock (_lock) { _played = 0; }
                        await _obs.MediaAsync("play").ConfigureAwait(false);
                        lock (_lock) { _playing = true; _ended = false; _cursorAt = _clock(); }
                        return null;
                    case "zoomIn": lock (_lock) _zoomBy = Math.Min(3, _zoomBy * 1.25); await TickAsync(true).ConfigureAwait(false); return null;
                    case "zoomOut": lock (_lock) _zoomBy = Math.Max(0.4, _zoomBy / 1.25); await TickAsync(true).ConfigureAwait(false); return null;
                    case "pan":
                        lock (_lock)
                        {
                            double z = Math.Max(1, _lastView.Zoom);
                            _panX += value * 0.08 / z;
                            _panY += value2 * 0.08 / z;
                        }
                        await TickAsync(true).ConfigureAwait(false);
                        return null;
                    case "follow": lock (_lock) { _zoomBy = 1; _panX = _panY = 0; _follow = true; } await TickAsync(true).ConfigureAwait(false); return null;
                    case "wide": lock (_lock) { _follow = !_follow; } await TickAsync(true).ConfigureAwait(false); return null;
                    case "live": return await LiveAsync().ConfigureAwait(false);
                    default: return "Unknown control: " + action;
                }
            }
            catch (Exception e) { Problem = e.Message; return e.Message; }
        }

        private async Task SeekToAsync(double seconds)
        {
            var clip = _now;
            if (clip == null) return;
            seconds = Math.Max(0, Math.Min((clip.Duration ?? seconds) - 0.02, seconds));
            await _obs.SeekAsync(seconds).ConfigureAwait(false);
            lock (_lock) { _cursor = seconds; _cursorAt = _clock(); _ended = false; }
            await TickAsync(true).ConfigureAwait(false);
        }

        /// <summary>Back to the live lobbies: whatever was on before the replay.</summary>
        public async Task<string> LiveAsync()
        {
            OnAir? back;
            lock (_lock) { back = _returnTo; _now = null; _playing = false; _returnTo = null; }
            try { await _obs.MediaAsync("pause").ConfigureAwait(false); } catch (Exception) { }
            TagChanged?.Invoke(JsonSerializer.Serialize(new { on = false }));
            if (back != null && back.Layout != "none" && back.Layout != "replay")
            {
                _desk.Show("", back.Layout, null, back.Slots.Select(x => x ?? "").ToList());
                return "Back to live.";
            }
            var top = _desk.Board.Ranking().FirstOrDefault(r => r.Online);
            if (top != null) _desk.Show(top.Lobby);
            return "Back to live.";
        }

        private double Cursor()
        {
            lock (_lock) return _playing ? _cursor + (_clock() - _cursorAt).TotalSeconds : _cursor;
        }

        /// <summary>About 30 times a second while a replay is up: keeps the crop on the players.</summary>
        public async Task TickAsync(bool force = false)
        {
            var clip = _now;
            if (clip == null) return;
            if (!await _step.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                var now = _clock();
                double dt = _lastTick == default ? 0 : Math.Min(0.25, (now - _lastTick).TotalSeconds);
                _lastTick = now;
                if (_playing) _played += dt;
                if (now >= _nextSync)
                {
                    _nextSync = now.AddSeconds(0.25);
                    try
                    {
                        var st = await _obs.MediaStatusAsync().ConfigureAwait(false);
                        lock (_lock)
                        {
                            _cursor = st.Cursor;
                            _cursorAt = _clock();
                            if (st.Duration > 0) clip.Duration = st.Duration;
                            if (st.State.EndsWith("ENDED", StringComparison.Ordinal) || st.State.EndsWith("PAUSED", StringComparison.Ordinal) && _playing && st.Cursor >= st.Duration - 0.05)
                            { _playing = false; _ended = true; }
                        }
                    }
                    catch (Exception) { }
                }
                View view;
                lock (_lock) view = Framing.At(clip, S, Cursor(), _played, _zoomBy, _panX, _panY, _follow);
                bool moved = Math.Abs(view.X - _lastView.X) > 0.0005 || Math.Abs(view.Y - _lastView.Y) > 0.0005 || Math.Abs(view.Zoom - _lastView.Zoom) > 0.001;
                if (moved || force)
                {
                    _lastView = view;
                    await _obs.FrameAsync(view).ConfigureAwait(false);
                }
            }
            catch (Exception e) { Problem = e.Message; }
            finally { _step.Release(); }
        }

        public object State()
        {
            var now = _clock();
            lock (_lock)
            {
                var c = _now;
                return new
                {
                    On = S.On,
                    Problem = Problem ?? _obs.ReplayProblem,
                    Folder = _obs.ClipFolder,
                    S.PreSeconds, S.PostSeconds, S.MaxZoom, S.BufferSeconds,
                    Hotkeys = S.Hotkeys,
                    Clips = _clips.AsEnumerable().Reverse().Take(30).Select(x => new
                    {
                        x.Id, x.Lobby, x.Title, x.State, x.Problem, x.CardId,
                        At = x.EventAt.ToString("o"),
                        Ago = Math.Max(0, (int)(now - x.EventAt).TotalSeconds),
                    }).ToList(),
                    Now = c == null ? null : new
                    {
                        ClipId = c.Id, c.Lobby, c.Title,
                        Playing = _playing, Ended = _ended,
                        Cursor = Math.Round(Cursor(), 3),
                        Duration = c.Duration,
                        Start = Math.Round(c.StartAt(S.VideoDelayMs), 3),
                        Event = c.Duration == null || c.SavedAt == null ? (double?)null : Math.Round(c.Duration.Value - (c.SavedAt.Value - c.EventAt).TotalSeconds, 3),
                        View = new { _lastView.X, _lastView.Y, _lastView.Zoom },
                        ZoomBy = Math.Round(_zoomBy, 2),
                        Follow = _follow && _panX == 0 && _panY == 0,
                    },
                };
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
