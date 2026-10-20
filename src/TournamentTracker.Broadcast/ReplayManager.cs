using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Replays: saves a clip for every kill, ejection, meeting and game end (and for any card on
    /// demand; in simulation mode without OBS, a stand-in clip made with ffmpeg), plays it in OBS's TT Replay
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
        private string? _sponsorKey;
        // Slow motion: the clip's speed (percent of normal).
        private int _speed = 100;
        private readonly List<Clip> _montages = new List<Clip>();

        public string? Problem { get; private set; }
        /// <summary>The REPLAY tag's text, for the tag page.</summary>
        public Action<string>? TagChanged { get; set; }
        /// <summary>ffmpeg (stand-in clips in simulation mode, thumbnails).</summary>
        public MontageBuilder? Builder { get; set; }
        /// <summary>Sponsors: "Kill Cam presented by" on kill replays.</summary>
        public SponsorBook? Sponsors { get; set; }
        /// <summary>A clip is saved (ready or failed).</summary>
        public event Action<Clip>? Saved;
        /// <summary>A replay or montage played to its end.</summary>
        public event Action<Clip>? Ended;
        /// <summary>Something went on or off in the replay scene (true: a replay or montage is up).</summary>
        public event Action<bool>? OnChanged;

        private ReplaySettings S => _obs.Settings.Replay;

        public ReplayManager(CasterDesk desk, ObsDirector obs, Func<DateTime>? clock = null, bool loop = true)
        {
            _desk = desk;
            _obs = obs;
            _clock = clock ?? (() => DateTime.UtcNow);
            desk.CardMade += card =>
            {
                if (S.On && S.OnEveryKill && S.AutoSave.Contains(card.Rule) && CanSave)
                    _ = Task.Run(() => SaveAsync(card));
            };
            if (loop)
            {
                _timer = new Timer(_ => _ = TickAsync(), null, 100, 33);
                // Old clips are cleared away now and then (never the ones a montage or the archive uses).
                _cleanTimer = new Timer(_ => { try { CleanUp(); } catch (Exception) { } }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
            }
        }

        private Timer? _cleanTimer;

        public Clip? Find(string id) { lock (_lock) return _clips.FirstOrDefault(c => c.Id == id) ?? _montages.FirstOrDefault(c => c.Id == id); }

        /// <summary>Every saved clip, oldest first.</summary>
        public List<Clip> Clips { get { lock (_lock) return _clips.ToList(); } }

        /// <summary>Clips can be saved: OBS is connected, or (simulation) ffmpeg makes stand-ins.</summary>
        public bool CanSave => _obs.Connected || _desk.Simulating;

        /// <summary>Where stand-in clips and thumbnails go.</summary>
        public string Folder => _obs.ClipFolder;

        /// <summary>A clip the caster no longer wants (the Moments library's delete).</summary>
        public void Forget(string id)
        {
            lock (_lock) _clips.RemoveAll(c => c.Id == id);
        }
        /// <summary>
        /// Simulation stopped: its clips go, files too (stand-ins and anything OBS saved of a fake
        /// lobby), and a replay or montage of them that's up is stopped (OBS stays on its scene).
        /// The ids of the clips that went.
        /// </summary>
        public async Task<HashSet<string>> ForgetAsync(Func<string, bool> which, ISet<string>? alsoPlaying = null)
        {
            List<Clip> gone;
            bool stop;
            lock (_lock)
            {
                gone = _clips.Where(c => which(c.Lobby)).ToList();
                _clips.RemoveAll(gone.Contains);
                stop = _now != null && (which(_now.Lobby) || gone.Any(c => c.Id == _now.Id) || alsoPlaying?.Contains(_now.Id) == true);
                if (stop) { _now = null; _playing = false; _returnTo = null; }
            }
            if (stop) await OffAsync().ConfigureAwait(false);
            foreach (var c in gone) { Files.Delete(c.File); Files.Delete(c.Thumbnail); }
            return gone.Select(c => c.Id).ToHashSet();
        }

        /// <summary>Stops the replay or montage up now if it's one of these (no scene change).</summary>
        public Task StopIfAsync(ISet<string> ids) => ForgetAsync(_ => false, ids);

        public Clip? ForCard(string cardId) { lock (_lock) return _clips.LastOrDefault(c => c.CardId == cardId); }

        // ---- Saving -------------------------------------------------------------------------------

        /// <summary>The clip for a card: who to keep in frame, and when the play was.</summary>
        public Clip Describe(Card card)
        {
            var clip = new Clip { Id = "r" + Interlocked.Increment(ref _seq), Lobby = card.Lobby, Title = card.Text, Rule = card.Rule, CardId = card.Id, Pre = S.PreSeconds, Post = S.PostSeconds, EventAt = card.At };
            var live = _desk.Board.Lobby(card.Lobby);
            clip.Round = live?.Round ?? 0;
            clip.Game = live?.Game;
            if (card.Source is { } src)
            {
                if (src.Str("room") is { } room) clip.Room = room;
                if (src.Int("round") is { } round) clip.Round = round;
                if (src.Str("game") is { } game) clip.Game = game;
                // The key player: who the play is about (their player card comes up with the replay).
                string? keyProp = card.Rule switch { "kill" or "winningKill" => "killer", "witnessedKill" => "impostor", "meeting" => "caller", "eject" => "ejected", "witnessedVent" or "vent" => "player", _ => null };
                if (card.Rule == "witnessedVent") keyProp = "impostor";
                if (keyProp != null && src.TryGetProperty(keyProp, out var kp) && kp.ValueKind == JsonValueKind.Object && kp.TryGetProperty("id", out var kid) && kid.ValueKind == JsonValueKind.Number)
                {
                    clip.KeyPlayerId = kid.GetInt32();
                    clip.KeyPlayer = live != null && live.People.TryGetValue(kid.GetInt32(), out var lp) && lp.Key.Length > 0 ? lp.Key
                        : kp.Str("key");
                }
                if (src.Long("t") is { } t) clip.EventAt = _desk.Tracks.ToCaster(card.Lobby, t);
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
            Keep(clip);
            _desk.SetClip(card.Id, clip.Id, "saving");
            await SaveClipAsync(clip).ConfigureAwait(false);
            _desk.SetClip(card.Id, clip.Id, clip.State);
            Saved?.Invoke(clip);
            return clip;
        }

        internal void Keep(Clip clip)
        {
            lock (_lock)
            {
                _clips.Add(clip);
                if (_clips.Count > 400) _clips.RemoveRange(0, _clips.Count - 400);
            }
        }

        /// <summary>
        /// "Replay that" (vMix's instant replay without marking anything first): the last few seconds of
        /// a lobby, saved now with no card, played as soon as it's ready when <paramref name="play"/>.
        /// The wide shot (no crop to follow), both angles when the camera is on. Only played over what
        /// it was asked over: a panic, a hold, a break or another lobby since leaves it ready for "last".
        /// </summary>
        public Clip SaveNow(string lobby, bool play = true)
        {
            var now = _clock();
            var air = _desk.OnAir;
            bool held = _desk.Held;
            var live = _desk.Board.Lobby(lobby);
            var clip = new Clip
            {
                Id = "r" + Interlocked.Increment(ref _seq), Lobby = lobby, Rule = "instant",
                Title = $"{lobby}: the last {S.PreSeconds:0} seconds", Pre = S.PreSeconds, Post = 0, EventAt = now,
                Round = live?.Round ?? 0, Game = live?.Game,
            };
            Keep(clip);
            _ = Task.Run(async () =>
            {
                await SaveClipAsync(clip).ConfigureAwait(false);
                Saved?.Invoke(clip);
                if (play && clip.State == "ready" && StillOver(air, held, lobby)) await PlayAsync(clip).ConfigureAwait(false);
            });
            return clip;
        }

        /// <summary>
        /// What a "replay that" was asked over is still on stream: the same switch, or that lobby live
        /// in another layout (not in a break or a replay), and no hold since.
        /// </summary>
        private bool StillOver(OnAir air, bool heldThen, string lobby)
        {
            if (_desk.Held && !heldThen) return false;
            var now = _desk.OnAir;
            return ReferenceEquals(now, air) || now.Layout is not ("break" or "replay") && now.Has(lobby);
        }

        /// <summary>The newest clip ready to play (a montage isn't one), or null.</summary>
        public Clip? Newest()
        {
            lock (_lock) return _clips.LastOrDefault(c => c.State == "ready");
        }

        /// <summary>Has OBS write a clip's buffer once the seconds after its moment have passed; its state says how it went.</summary>
        private async Task SaveClipAsync(Clip clip)
        {
            try
            {
                // A simulated lobby's picture is a stand-in page: its clip is a stand-in too (OBS or not).
                if (_desk.Simulating && (CasterDesk.IsSimLobby(clip.Lobby) || !_obs.Connected))
                {
                    if (Builder?.Ffmpeg == null) throw new InvalidOperationException("ffmpeg isn't installed: simulation makes its clips with it (Montages → Get ffmpeg).");
                    await StandInAsync(clip).ConfigureAwait(false);
                    return;
                }
                if (!_obs.Connected) throw new InvalidOperationException("Connect OBS first: replays come from its replay buffer.");
                var now = _clock();
                // With the broadcast delay, OBS's picture (and its buffer) is that far behind the live desk:
                // the save waits for the moment to reach OBS, and the clip ends that much earlier than when it's saved.
                double lag = _obs.DelaySeconds;
                if ((now - clip.EventAt).TotalSeconds > S.BufferSeconds - clip.Pre + lag)
                    throw new InvalidOperationException($"Too long ago: OBS keeps the last {S.BufferSeconds}s of each lobby.");
                var wait = clip.EventAt.AddSeconds(clip.Post + 0.3 + lag) - now;
                if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
                await _saveOne.WaitAsync().ConfigureAwait(false);
                try
                {
                    clip.SavedAt = _clock().AddSeconds(-lag);
                    clip.File = await _obs.SaveClipAsync(clip.Lobby).ConfigureAwait(false);
                    // The player camera's view of the same moment, when it's on: a second angle.
                    if (_obs.HasCamReplay(clip.Lobby))
                    {
                        try
                        {
                            clip.CamSavedAt = _clock().AddSeconds(-lag);
                            clip.CamFile = await _obs.SaveCamClipAsync(clip.Lobby).ConfigureAwait(false);
                            clip.CamPlayerId = CamFollowing(clip.Lobby);
                        }
                        catch (Exception) { clip.CamFile = null; clip.CamSavedAt = null; }
                    }
                }
                finally { _saveOne.Release(); }
                // The positions for the last seconds arrive about a second late.
                await Task.Delay(1300).ConfigureAwait(false);
                clip.Samples = _desk.Tracks.Between(clip.Lobby, clip.EventAt.AddSeconds(-clip.Pre - 2), clip.SavedAt.Value.AddSeconds(1));
                await ThumbnailAsync(clip).ConfigureAwait(false);
                // The camera was on someone in the play (the killer, the victim…): its close-up plays first.
                if (clip.CamFile != null && clip.CamPlayerId is int cam && clip.Focus.Contains(cam)) clip.SwapAngle();
                clip.State = "ready";
            }
            catch (Exception e)
            {
                clip.State = "failed";
                clip.Problem = e.Message;
            }
        }

        /// <summary>Who the lobby's player camera is following now, as its host last said.</summary>
        private int? CamFollowing(string lobby) => CasterDesk.CamOf(_desk.Board.Lobby(lobby)?.Spec).Following;

        /// <summary>Simulation without OBS: a stand-in video (the lobby and the play over moving colours) made with ffmpeg.</summary>
        private async Task StandInAsync(Clip clip)
        {
            var wait = clip.EventAt.AddSeconds(clip.Post + 0.3) - _clock();
            if (wait > TimeSpan.Zero && wait < TimeSpan.FromSeconds(30)) await Task.Delay(wait).ConfigureAwait(false);
            string dir = Path.Combine(Folder, "Simulated");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{clip.Id}.mp4");
            double length = Math.Round(clip.Pre + clip.Post + 0.3, 1);
            await _saveOne.WaitAsync().ConfigureAwait(false);
            try
            {
                var (ok, output) = await Builder!.PlaceholderAsync(file, $"{clip.Lobby} · {clip.Title}", length).ConfigureAwait(false);
                if (!ok) throw new InvalidOperationException("ffmpeg couldn't make a stand-in clip: " + output.Split('\n').LastOrDefault(l => l.Trim().Length > 0));
            }
            finally { _saveOne.Release(); }
            clip.File = file;
            clip.SavedAt = clip.EventAt.AddSeconds(clip.Post + 0.3);
            clip.Duration = length;
            clip.Samples = _desk.Tracks.Between(clip.Lobby, clip.EventAt.AddSeconds(-clip.Pre - 2), clip.SavedAt.Value.AddSeconds(1));
            await ThumbnailAsync(clip).ConfigureAwait(false);
            clip.State = "ready";
        }

        /// <summary>A still at the moment of the play, for the Moments library.</summary>
        private async Task ThumbnailAsync(Clip clip)
        {
            if (Builder?.Ffmpeg == null || clip.File == null || !File.Exists(clip.File)) return;
            try
            {
                clip.Duration ??= await Builder.ProbeAsync(clip.File).ConfigureAwait(false);
                if (clip.Duration == null) return;
                string dir = Path.Combine(Path.GetDirectoryName(clip.File)!, "Thumbs");
                Directory.CreateDirectory(dir);
                string jpg = Path.Combine(dir, Path.GetFileNameWithoutExtension(clip.File) + ".jpg");
                double at = clip.Duration.Value - ((clip.SavedAt ?? clip.EventAt) - clip.EventAt).TotalSeconds - S.VideoDelayMs / 1000.0;
                var (ok, _) = await Builder.ThumbnailAsync(clip.File, Math.Max(0, at), jpg).ConfigureAwait(false);
                if (ok) clip.Thumbnail = jpg;
            }
            catch (Exception) { }
        }

        // ---- Playing ------------------------------------------------------------------------------

        public Task<string> PlayAsync(string clipId)
        {
            var clip = Find(clipId);
            return clip == null ? Task.FromResult("That replay is gone.") : PlayAsync(clip);
        }

        /// <summary>A montage (or any finished video) in the replay scene, full frame.</summary>
        private List<MontageMoment> _moments = new List<MontageMoment>();
        private string? _momentCard;

        public Task<string> PlayVideoAsync(string id, string file, string title, string? sponsor = null, List<MontageMoment>? moments = null)
        {
            _moments = moments ?? new List<MontageMoment>();
            _momentCard = null;
            var clip = new Clip { Id = id, Lobby = "", Title = title, Rule = "montage", File = file, State = "ready", EventAt = _clock() };
            lock (_lock)
            {
                _montages.RemoveAll(c => c.Id == id);
                _montages.Add(clip);
            }
            _montageSponsor = sponsor;
            return PlayAsync(clip);
        }

        private string? _montageSponsor;

        public async Task<string> PlayAsync(Clip clip)
        {
            if (clip.State != "ready" || clip.File == null) return clip.State == "saving" ? "The replay is still being saved." : "No replay: " + (clip.Problem ?? "it wasn't saved.");
            try
            {
                // Each replay starts at normal speed.
                await _obs.NormalSpeedAsync().ConfigureAwait(false);
                lock (_lock) _speed = 100;
                double duration = await _obs.LoadClipAsync(clip.File).ConfigureAwait(false);
                clip.Duration = duration;
                double start = clip.StartAt(S.VideoDelayMs);
                await _obs.SeekAsync(start).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_now == null || _desk.OnAir.Layout != "replay") _returnTo = _desk.OnAir;
                    _now = clip;
                    clip.PlayedAt ??= _clock();
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
                // Kill Cam presented by a sponsor on kill replays; any replay presented by a "replay" sponsor.
                EndSponsor();
                bool montage = clip.Rule == "montage";
                bool kill = clip.Rule is "kill" or "winningKill" or "witnessedKill";
                Sponsor? killcam = null;
                string placement = "replay";
                if (!montage && Sponsors != null)
                {
                    if (kill && Sponsors.Next("killcam") is { } kc) { killcam = kc; placement = "killcam"; }
                    else if (Sponsors.Next("replay") is { } rs) killcam = rs;
                }
                if (killcam != null)
                {
                    _sponsorKey = placement + ":" + clip.Id + ":" + _clock().Ticks;
                    Sponsors!.Begin(_sponsorKey, killcam, placement, clip.Lobby, clip.Title);
                }
                TagChanged?.Invoke(JsonSerializer.Serialize(new
                {
                    on = true, lobby = clip.Lobby, title = clip.Title,
                    kind = montage ? "montage" : kill ? "killcam" : "replay",
                    sponsor = killcam?.Name ?? (montage ? _montageSponsor : null),
                    sponsorLogo = killcam != null ? SponsorBook.LogoUrl(killcam) : null,
                }));
                OnChanged?.Invoke(true);
                // The key player's card (killer, reporter, caller, ejected) comes up with the replay.
                if (!montage && clip.KeyPlayer != null) _desk.ShowPlayerCard(clip.KeyPlayer, clip.Lobby, auto: true);
                Problem = null;
                return montage ? $"Montage: {NameTag.Plain(clip.Title)}." : $"Replay: {clip.Lobby} · {NameTag.Plain(clip.Title)}.";
            }
            catch (Exception e)
            {
                Problem = e.Message;
                return (clip.Rule == "montage" ? "Montage failed: " : "Replay failed: ") + e.Message;
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
                    case "angle": return await AngleAsync(clip).ConfigureAwait(false);
                    case "slow": return await SlowAsync().ConfigureAwait(false);
                    default: return "Unknown control: " + action;
                }
            }
            catch (Exception e) { Problem = e.Message; return e.Message; }
        }

        /// <summary>
        /// The other angle of the replay on screen (player camera ↔ whole map), at the same moment: the
        /// same time before the clip's end, since both were saved together. Playing or paused as before.
        /// </summary>
        private async Task<string?> AngleAsync(Clip clip)
        {
            if (clip.CamFile == null) return "This replay has no player camera angle (the camera was off).";
            double fromEnd = (clip.Duration ?? 0) - Cursor();
            bool playing = _playing;
            clip.SwapAngle();
            double duration = await _obs.LoadClipAsync(clip.File!).ConfigureAwait(false);
            clip.Duration = duration;
            double at = Math.Max(0, Math.Min(duration - 0.05, duration - fromEnd));
            await _obs.SeekAsync(at).ConfigureAwait(false);
            await _obs.MediaAsync(playing ? "play" : "pause").ConfigureAwait(false);
            lock (_lock) { _cursor = at; _cursorAt = _clock(); _playing = playing; _zoomBy = 1; _panX = _panY = 0; }
            await TickAsync(true).ConfigureAwait(false);
            return clip.Angle == "cam" ? "Player camera angle." : "Whole map angle.";
        }

        /// <summary>Slow motion in turn: 100% → 50% → 25% → 100%, from the same moment, playing or paused as before.</summary>
        public int Speed { get { lock (_lock) return _speed; } }

        private async Task<string?> SlowAsync()
        {
            int next;
            double at;
            bool playing;
            lock (_lock) { next = _speed == 100 ? 50 : _speed == 50 ? 25 : 100; at = _playing ? _cursor + (_clock() - _cursorAt).TotalSeconds * _speed / 100.0 : _cursor; playing = _playing; }
            // OBS starts the clip again when its speed changes: back to the same moment.
            await _obs.SetReplaySpeedAsync(next).ConfigureAwait(false);
            await _obs.SeekAsync(at).ConfigureAwait(false);
            await _obs.MediaAsync(playing ? "play" : "pause").ConfigureAwait(false);
            lock (_lock) { _speed = next; _cursor = at; _cursorAt = _clock(); _playing = playing; }
            await TickAsync(true).ConfigureAwait(false);
            return null;
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
            await OffAsync().ConfigureAwait(false);
            if (back != null && back.Layout != "none" && back.Layout != "replay")
            {
                _desk.Show("", back.Layout, null, back.Slots.Select(x => x ?? "").ToList());
                return "Back to live.";
            }
            var top = _desk.Board.Ranking().FirstOrDefault(r => r.Online);
            if (top != null) _desk.Show(top.Lobby);
            return "Back to live.";
        }

        /// <summary>The replay scene let go: OBS's player paused, the sponsor's appearance over, the REPLAY tag off.</summary>
        private async Task OffAsync()
        {
            try { await _obs.MediaAsync("pause").ConfigureAwait(false); } catch (Exception) { }
            EndSponsor();
            TagChanged?.Invoke(JsonSerializer.Serialize(new { on = false }));
            OnChanged?.Invoke(false);
        }

        private void EndSponsor()
        {
            if (_sponsorKey != null) Sponsors?.End(_sponsorKey);
            _sponsorKey = null;
        }

        /// <summary>Whether a replay or montage is up, and which.</summary>
        public Clip? Now => _now;

        /// <summary>A replay is up and playing (not paused or ended).</summary>
        public bool Playing { get { lock (_lock) return _now != null && _playing; } }

        /// <summary>How many saved clips are ready to play.</summary>
        public int ReadyCount { get { lock (_lock) return _clips.Count(c => c.State == "ready"); } }

        /// <summary>Where the clip is now (media time: slow motion moves it more slowly).</summary>
        private double Cursor()
        {
            lock (_lock) return _playing ? _cursor + (_clock() - _cursorAt).TotalSeconds * _speed / 100.0 : _cursor;
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
                if (_playing) _played += dt * Speed / 100.0;
                Clip? ended = null;
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
                            {
                                if (!_ended) ended = clip;
                                _playing = false; _ended = true;
                            }
                        }
                    }
                    catch (Exception) { }
                }
                if (ended != null)
                {
                    EndSponsor();
                    Ended?.Invoke(ended);
                    // A montage played in intermission goes back to it by itself (not while switching is held).
                    if (ended.Rule == "montage" && _returnTo?.Layout == "intermission" && !_desk.Held) { await LiveAsync().ConfigureAwait(false); return; }
                }
                // During a montage, each moment's key player gets their card.
                if (clip.Rule == "montage" && _moments.Count > 0)
                {
                    double at = Cursor();
                    var m = _moments.LastOrDefault(x => at >= x.At + 0.3 && at < x.At + x.Length);
                    string? id = m == null ? null : m.At + "|" + m.Key;
                    if (m != null && id != _momentCard) { _momentCard = id; _desk.ShowPlayerCard(m.Key, m.Lobby, auto: true); }
                }
                View view;
                // The player camera is already close up: shown whole (zoom and pan still work by hand).
                lock (_lock) view = clip.Angle == "cam" ? new View(Math.Max(0, Math.Min(1, 0.5 + _panX)), Math.Max(0, Math.Min(1, 0.5 + _panY)), _zoomBy) : Framing.At(clip, S, Cursor(), _played, _zoomBy, _panX, _panY, _follow);
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

        /// <summary>Saved clips ready to play, newest first (for Twitch replay requests).</summary>
        public List<(string Id, string Title, string Lobby)> ReadyClips()
        {
            lock (_lock) return _clips.Where(c => c.State == "ready").AsEnumerable().Reverse().Take(30).Select(c => (c.Id, NameTag.Plain(c.Title), c.Lobby)).ToList();
        }

        /// <summary>The Moments library: every saved clip, newest first.</summary>
        public object Moments()
        {
            var now = _clock();
            lock (_lock)
                return _clips.Where(c => c.State == "ready").AsEnumerable().Reverse().Take(200).Select(c => new
                {
                    c.Id, c.Lobby, c.Title, c.Rule, c.Round, c.Game, c.Used, c.KeyPlayer,
                    At = c.EventAt.ToString("o"),
                    Ago = Math.Max(0, (int)(now - c.EventAt).TotalSeconds),
                    Thumb = c.Thumbnail != null,
                }).ToList();
        }

        /// <summary>Just the replay on stream now (for the Live desk's quick answer and the Stream Deck), or null.</summary>
        public object? NowState()
        {
            lock (_lock)
            {
                var c = _now;
                if (c == null) return null;
                return new
                {
                    ClipId = c.Id, c.Lobby, c.Title, Montage = c.Rule == "montage",
                    Playing = _playing, Ended = _ended, Speed = _speed,
                    Cursor = Math.Round(Cursor(), 3),
                    Duration = c.Duration,
                    Start = Math.Round(c.StartAt(S.VideoDelayMs), 3),
                    Event = c.Duration == null || c.SavedAt == null ? (double?)null : Math.Round(c.Duration.Value - (c.SavedAt.Value - c.EventAt).TotalSeconds, 3),
                    View = new { _lastView.X, _lastView.Y, _lastView.Zoom },
                    ZoomBy = Math.Round(_zoomBy, 2),
                    Follow = _follow && _panX == 0 && _panY == 0,
                    c.Angle,
                    HasCam = c.CamFile != null,
                };
            }
        }

        public object State()
        {
            var now = _clock();
            lock (_lock)
            {
                return new
                {
                    On = S.On,
                    Problem = Problem ?? _obs.ReplayProblem,
                    Folder = _obs.ClipFolder,
                    DiskFreeGb = DiskFreeGb(),
                    CleanUp = CleanUpNote,
                    S.PreSeconds, S.PostSeconds, S.MaxZoom, S.BufferSeconds,
                    Hotkeys = S.Hotkeys,
                    Clips = _clips.AsEnumerable().Reverse().Take(30).Select(x => new
                    {
                        x.Id, x.Lobby, x.Title, x.State, x.Problem, x.CardId, x.Rule,
                        Cam = x.CamFile != null,
                        At = x.EventAt.ToString("o"),
                        Ago = Math.Max(0, (int)(now - x.EventAt).TotalSeconds),
                    }).ToList(),
                    Now = NowState(),
                };
            }
        }

        // ---- Clips on disk: the newest kept, old ones cleared away ------------------------------------

        /// <summary>Files a montage, the montage archive or anything else still needs (never cleared away).</summary>
        public Func<ISet<string>>? Protected { get; set; }
        /// <summary>A clip younger than this is never cleared away (it may be about to be played).</summary>
        public static TimeSpan KeepAtLeast { get; set; } = TimeSpan.FromHours(2);
        /// <summary>What the last clean-up did (shown with the replays), or null.</summary>
        public string? CleanUpNote { get; private set; }

        /// <summary>Free space on the clip folder's disk, in GB (null when it can't be told).</summary>
        public double? DiskFreeGb()
        {
            try
            {
                string folder = Folder;
                string? root = Path.GetPathRoot(Path.GetFullPath(folder));
                return root == null ? null : Math.Round(new DriveInfo(root).AvailableFreeSpace / 1073741824.0, 1);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Clears old clips away so a day of auto-saved replays can't fill the disk: only Red Alert's own
        /// clip files (named "TT …" in the clip folder, and simulation's stand-ins), oldest first, until
        /// they fit in <see cref="ReplaySettings.KeepGb"/> and none is older than
        /// <see cref="ReplaySettings.KeepDays"/>, and more while the disk is under
        /// <see cref="ReplaySettings.MinFreeGb"/> free. Never one used by a montage or the archive, one
        /// marked used, the one playing, or one younger than <see cref="KeepAtLeast"/>. Returns how many went.
        /// </summary>
        public int CleanUp(DateTime? now = null)
        {
            var s = S;
            if (s.KeepGb <= 0 && s.KeepDays <= 0 && s.MinFreeGb <= 0) return 0;
            string folder = Folder;
            if (!Directory.Exists(folder)) return 0;
            var t = now ?? DateTime.UtcNow;
            var keep = new HashSet<string>(Protected?.Invoke() ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                foreach (var c in _clips.Where(c => c.Used || c.State == "saving"))
                    foreach (var f in new[] { c.File, c.CamFile }) if (f != null) keep.Add(Path.GetFullPath(f));
                if (_now != null) foreach (var f in new[] { _now.File, _now.CamFile }) if (f != null) keep.Add(Path.GetFullPath(f));
            }
            var files = new DirectoryInfo(folder).EnumerateFiles("TT *").Concat(Directory.Exists(Path.Combine(folder, "Simulated")) ? new DirectoryInfo(Path.Combine(folder, "Simulated")).EnumerateFiles() : Enumerable.Empty<FileInfo>())
                .Where(f => f.Extension.ToLowerInvariant() is ".mp4" or ".mkv" or ".mov" or ".flv" or ".ts" or ".webm")
                .OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            double? free = DiskFreeGb();
            int gone = 0;
            long freed = 0;
            foreach (var f in files)
            {
                bool tooMuch = s.KeepGb > 0 && total > s.KeepGb * 1073741824.0;
                bool tooOld = s.KeepDays > 0 && t - f.LastWriteTimeUtc > TimeSpan.FromDays(s.KeepDays);
                bool diskLow = s.MinFreeGb > 0 && free is { } gb && gb + freed / 1073741824.0 < s.MinFreeGb;
                if (!tooMuch && !tooOld && !diskLow) continue;
                if (t - f.LastWriteTimeUtc < KeepAtLeast || keep.Contains(f.FullName)) continue;
                try
                {
                    long size = f.Length;
                    f.Delete();
                    string thumb = Path.Combine(f.DirectoryName ?? folder, "Thumbs", Path.GetFileNameWithoutExtension(f.Name) + ".jpg");
                    if (File.Exists(thumb)) File.Delete(thumb);
                    total -= size;
                    freed += size;
                    gone++;
                    lock (_lock)
                    {
                        _clips.RemoveAll(c => c.File != null && string.Equals(Path.GetFullPath(c.File), f.FullName, StringComparison.OrdinalIgnoreCase));
                        foreach (var c in _clips.Where(c => c.CamFile != null && string.Equals(Path.GetFullPath(c.CamFile), f.FullName, StringComparison.OrdinalIgnoreCase))) { c.CamFile = null; c.CamSavedAt = null; }
                    }
                }
                catch (Exception) { }
            }
            if (gone > 0) CleanUpNote = $"Cleared away {gone} old replay clip{(gone == 1 ? "" : "s")} ({freed / 1048576.0:0} MB) at {t.ToLocalTime():HH:mm}.";
            return gone;
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
            _cleanTimer?.Dispose();
            _cleanTimer = null;
        }
    }
}
