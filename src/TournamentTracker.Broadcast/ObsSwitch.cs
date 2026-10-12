using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// The switching engine. Switches go to OBS one at a time and the newest one wins: a second Stream
    /// Deck press while the first is still swooshing replaces it, so OBS can never end on an older
    /// pick than the desk shows. A switch is one round trip (an obs-websocket batch: the moves, shows,
    /// hides, mutes and the scene change together), runs the source upkeep only when a lobby going on
    /// has no source yet, and only mutes what changed. The upkeep reads each scene's items once and
    /// keeps them until OBS says that scene changed.
    /// </summary>
    public sealed partial class ObsDirector
    {
        // ---- One switch at a time, the newest wins --------------------------------------------------

        private readonly object _wantLock = new object();
        private (OnAir Air, string? Transition, long Seq)? _want;
        private long _wantSeq;
        private bool _draining;
        private readonly List<(long Seq, TaskCompletionSource Done)> _waiting = new List<(long, TaskCompletionSource)>();

        /// <summary>
        /// Puts what's on stream in OBS: shows the on-air lobbies in their slots in the layout's scene,
        /// hides the rest, switches to it and sets the sound. <paramref name="transition"/>: "cut" (no
        /// swoosh), "swoosh", "fade" (a scene change fades), or null (a swoosh for lobbies, a cut for
        /// cameras). Done when this switch, or a newer one that replaced it, is in OBS.
        /// </summary>
        public Task ApplyAsync(OnAir air, string? transition = null)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool start = false;
            lock (_wantLock)
            {
                long seq = ++_wantSeq;
                _want = (air, transition ?? air.Transition, seq);
                _waiting.Add((seq, done));
                if (!_draining) { _draining = true; start = true; }
            }
            if (start) _ = Task.Run(DrainAsync);
            return done.Task;
        }

        /// <summary>A newer switch is waiting: the one under way can stop.</summary>
        private bool Superseded() { lock (_wantLock) return _want != null; }

        /// <summary>No switch is waiting or under way (for tests).</summary>
        public bool Switching { get { lock (_wantLock) return _draining; } }

        private async Task DrainAsync()
        {
            while (true)
            {
                (OnAir Air, string? Transition, long Seq) next;
                lock (_wantLock)
                {
                    if (_want is not { } w) { _draining = false; return; }
                    next = w;
                    _want = null;
                }
                try { await ApplyNowAsync(next.Air, next.Transition).ConfigureAwait(false); }
                catch (Exception e) { Problem = "OBS: " + e.Message; }
                List<TaskCompletionSource> finished;
                lock (_wantLock)
                {
                    finished = _waiting.Where(x => x.Seq <= next.Seq).Select(x => x.Done).ToList();
                    _waiting.RemoveAll(x => x.Seq <= next.Seq);
                }
                foreach (var f in finished) f.TrySetResult();
            }
        }

        /// <summary>A lobby going on has no source in OBS yet (or its source is gone, or due another try): the upkeep runs first.</summary>
        private bool NeedsSources(OnAir air)
        {
            var feeds = _feeds();
            bool Sent(string l) => feeds.Any(f => string.Equals(f.Lobby, l, StringComparison.OrdinalIgnoreCase));
            foreach (var l in air.Slots)
            {
                if (l == null || !Sent(l)) continue;
                if (!Settings.Sources.TryGetValue(l, out var name) || _dead.Contains(name)) return true;
                if (_sourceProblems.TryGetValue(l, out var p) && DateTime.UtcNow >= p.RetryAt) return true;
            }
            if (air.IsCam && CamFeeds?.Invoke() is { } cams)
                foreach (var l in air.Slots)
                    if (l != null && !Settings.CamSources.ContainsKey(l) && cams.Any(f => string.Equals(f.Lobby, l, StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }

        private async Task ApplyNowAsync(OnAir air, string? transition)
        {
            var obs = _obs;
            if (obs == null || !Settings.Scenes.TryGetValue(air.Layout == "cams" ? "cam" : air.Layout, out var scene)) return;
            // From here until OBS confirms, its item events are ours (arranging the scene), not a switch by hand.
            var ex = new Expect { Scene = scene, Air = air, Asked = DateTime.UtcNow };
            _expect = ex;
            // Neither the upkeep nor the swoosh may lose the switch: OBS being slow there is only noted.
            if (NeedsSources(air))
            {
                try { await AddSourcesAsync().ConfigureAwait(false); }
                catch (Exception e) { Problem = "OBS: " + e.Message; }
            }
            // A swoosh when what's on stream changes (a new scene, or pictures moving in this one). No
            // swoosh to, from or within the player camera (game to player to player is a straight cut),
            // unless asked for; none at all for a cut.
            string key = AirKey(air);
            bool camCut = air.IsCam || Scene == Settings.Scenes.GetValueOrDefault("cam");
            bool sceneChange = Scene != scene;
            bool swoosh = transition == "swoosh" || transition == null && !camCut;
            if (swoosh && (key != _airKey || sceneChange))
            {
                try { await SwooshAsync(sceneChange).ConfigureAwait(false); }
                catch (Exception) { }
            }
            // A newer switch came in meanwhile (a second press): it goes on instead, at this swoosh's cut.
            if (Superseded()) { if (_expect == ex) _expect = null; return; }
            _airKey = key;
            bool applied = false;
            await _busy.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Superseded()) { if (_expect == ex) _expect = null; return; }
                var boxes = Slots(air.Layout, Width, Height, air.Layout == "full" ? 0 : Settings.Gap, air.Slots.Count);
                // The live scene as it is now (read fresh: someone may have moved things by hand).
                var items = await ItemsAsync(obs, scene).ConfigureAwait(false);
                var batch = new List<(string Type, object? Data)>();
                var camStarts = new List<string>();
                object Place(Item item, Box b) => new
                {
                    sceneName = scene,
                    sceneItemId = item.Id,
                    sceneItemTransform = new
                    {
                        positionX = b.X, positionY = b.Y, alignment = 5, rotation = 0.0,
                        boundsType = "OBS_BOUNDS_SCALE_INNER", boundsAlignment = 0, boundsWidth = b.W, boundsHeight = b.H,
                        cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0,
                    },
                };
                // Already where it should be (within half a pixel, as just read from OBS): not moved again.
                static bool There(Item item, Box b) => Math.Abs(item.X - b.X) < 0.5 && Math.Abs(item.Y - b.Y) < 0.5 && Math.Abs(item.W - b.W) < 0.5 && Math.Abs(item.H - b.H) < 0.5;
                var camBoxes = CamBoxes(air);
                foreach (var item in items)
                {
                    // The player camera scene: the on-air lobby's camera full screen, under everything; or
                    // several lobbies' cameras side by side (two) or in a quad (three or four).
                    if (CamLobbyOf(item.Source) is string camLobby)
                    {
                        int camSlot = air.IsCam ? air.Slots.FindIndex(s => string.Equals(s, camLobby, StringComparison.OrdinalIgnoreCase)) : -1;
                        bool showCam = camSlot >= 0 && camSlot < camBoxes.Count;
                        if (showCam && !There(item, camBoxes[camSlot])) batch.Add(("SetSceneItemTransform", Place(item, camBoxes[camSlot])));
                        if (item.Enabled != showCam)
                        {
                            batch.Add(("SetSceneItemEnabled", new { sceneName = scene, sceneItemId = item.Id, sceneItemEnabled = showCam }));
                            // Hidden, the camera's Source Record buffer got no pictures (and never wrote a
                            // file): started as it goes on stream, so a replay has the camera angle.
                            if (showCam) camStarts.Add(camLobby);
                        }
                        continue;
                    }
                    string? lobby = LobbyOf(item.Source);
                    if (lobby == null) continue;                     // not ours: leave it alone
                    int slot = air.Slots.FindIndex(s => string.Equals(s, lobby, StringComparison.OrdinalIgnoreCase));
                    bool show = slot >= 0 && slot < boxes.Count;
                    if (show && !There(item, boxes[slot])) batch.Add(("SetSceneItemTransform", Place(item, boxes[slot])));
                    if (item.Enabled != show) batch.Add(("SetSceneItemEnabled", new { sceneName = scene, sceneItemId = item.Id, sceneItemEnabled = show }));
                }
                // Only one lobby's game sound at a time: the full-screen one, or slot 1. Only what changes
                // is sent (OBS says when a mute is changed by hand, so what's known stays true).
                string? loud = Settings.Audio == "slot1" ? air.Slots.FirstOrDefault(s => s != null) : null;
                var mutes = new List<(string Source, bool Muted)>();
                foreach (var (lobby, source) in Settings.Sources.ToList())
                {
                    if (_dead.Contains(source)) continue;
                    bool muted = !string.Equals(lobby, loud, StringComparison.OrdinalIgnoreCase);
                    if (KnownMute(source) == muted) continue;
                    mutes.Add((source, muted));
                    batch.Add(("SetInputMute", new { inputName = source, inputMuted = muted }));
                }
                // A cut or a fade on a scene change: OBS's own transition for it, put back afterwards.
                (string? Use, string? Restore) swap = (null, null);
                if (sceneChange && transition is "cut" or "fade") swap = await TransitionForAsync(obs, transition).ConfigureAwait(false);
                var (use, restore) = swap;
                if (use != null) batch.Add(("SetCurrentSceneTransition", new { transitionName = use }));
                int sceneAt = batch.Count;
                batch.Add(("SetCurrentProgramScene", new { sceneName = scene }));
                ex.Asked = DateTime.UtcNow;
                var results = await obs.BatchAsync(batch).ConfigureAwait(false);
                if (results.Count <= sceneAt || !results[sceneAt].Ok)
                    throw results.Count > sceneAt ? results[sceneAt].Error() : new InvalidOperationException("OBS didn't do the switch.");
                // Mutes: noted as done (a source OBS refused was removed there: not asked again until it's back).
                foreach (var (source, muted) in mutes) KnowMute(source, muted);
                var failed = results.Take(sceneAt).Where(r => !r.Ok && r.Type != "SetInputMute").ToList();
                Problem = failed.Count == 0 ? null : "OBS: " + failed[0].Error().Message;
                if (use != null && restore != null) RestoreTransitionLater(obs, restore, transition == "fade" ? 1500 : 400);
                foreach (var l in camStarts) await StartCamBufferAsync(obs, l).ConfigureAwait(false);
                // Already on that scene (the pictures moved within it): OBS sends no scene change, so check.
                var cur = await obs.RequestAsync("GetCurrentProgramScene").ConfigureAwait(false);
                if (ProgramName(cur) == scene) Confirm(ex);
                else _ = Task.Delay(SwitchTimeout).ContinueWith(_ => TimedOut(ex));
                applied = true;
            }
            catch (Exception e)
            {
                Problem = "OBS: " + e.Message;
                if (_expect == ex) { _expect = null; SwitchProblem = $"OBS didn't switch to {scene}: {e.Message}"; }
                _ = Task.Run(async () => { try { await ReadBackAsync().ConfigureAwait(false); } catch (Exception) { } });
            }
            finally { _busy.Release(); }
            // The voice follows the picture (also when OBS refused: it says what the desk asked for).
            if (applied || !Superseded()) await SetVoicesAsync(air).ConfigureAwait(false);
        }

        // ---- OBS's own scene transition for a cut or a fade ------------------------------------------

        /// <summary>The transition to use for a "cut" or "fade" scene change, and the one to put back after; nulls when it's already that one.</summary>
        private async Task<(string? Use, string? Restore)> TransitionForAsync(ObsClient obs, string kind)
        {
            try
            {
                var list = await obs.RequestAsync("GetSceneTransitionList").ConfigureAwait(false);
                string current = list.TryGetProperty("currentSceneTransitionName", out var c) ? c.GetString() ?? "" : "";
                string want = kind == "fade" ? "fade_transition" : "cut_transition";
                string? use = null, currentKind = null;
                foreach (var t in list.GetProperty("transitions").EnumerateArray())
                {
                    string name = t.GetProperty("transitionName").GetString() ?? "", k = t.TryGetProperty("transitionKind", out var tk) ? tk.GetString() ?? "" : "";
                    if (k == want && use == null) use = name;
                    if (name == current) currentKind = k;
                }
                if (use == null || currentKind == want) return (null, null);
                return (use, current.Length > 0 ? current : null);
            }
            catch (ObsException) { return (null, null); }
        }

        /// <summary>Puts the usual transition back once the cut or fade has played.</summary>
        private void RestoreTransitionLater(ObsClient obs, string name, int ms) => _ = Task.Run(async () =>
        {
            await Task.Delay(ms).ConfigureAwait(false);
            try { await obs.RequestAsync("SetCurrentSceneTransition", new { transitionName = name }).ConfigureAwait(false); }
            catch (Exception) { }
        });

        // ---- What's known in OBS: lobby sources' mutes, and each scene's items ------------------------

        private readonly Dictionary<string, bool> _gameMuted = new Dictionary<string, bool>(StringComparer.Ordinal);

        private bool? KnownMute(string source) { lock (_gameMuted) return _gameMuted.TryGetValue(source, out var m) ? m : null; }
        private void KnowMute(string source, bool muted) { lock (_gameMuted) _gameMuted[source] = muted; }

        /// <summary>A mute changed in OBS (by us or by hand): noted, so the next switch sends only what's different.</summary>
        private void OnMuteKnown(JsonElement data)
        {
            string name = data.TryGetProperty("inputName", out var n) ? n.GetString() ?? "" : "";
            if (name.Length == 0 || !data.TryGetProperty("inputMuted", out var m) || m.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
            lock (_gameMuted) if (_gameMuted.ContainsKey(name)) _gameMuted[name] = m.GetBoolean();
        }

        private readonly object _itemsLock = new object();
        private readonly Dictionary<string, List<Item>> _items = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        private long _itemsGen;

        /// <summary>
        /// A scene's items for the upkeep: read once and kept until OBS says something in it changed
        /// (an item added, removed or moved in the order, or a source renamed or removed). Which sources
        /// are in a scene is all the upkeep needs; a switch always reads its scene fresh.
        /// </summary>
        private async Task<List<Item>> SceneItemsAsync(ObsClient obs, string scene)
        {
            long gen;
            lock (_itemsLock)
            {
                if (_items.TryGetValue(scene, out var have)) return have;
                gen = _itemsGen;
            }
            var list = await ItemsAsync(obs, scene).ConfigureAwait(false);
            // Kept only if nothing changed while it was asked for.
            lock (_itemsLock) if (_itemsGen == gen) _items[scene] = list;
            return list;
        }

        /// <summary>Forgets what's known of a scene's items (or every scene's).</summary>
        private void ForgetItems(string? scene = null)
        {
            lock (_itemsLock)
            {
                _itemsGen++;
                if (scene == null) _items.Clear(); else _items.Remove(scene);
            }
        }

        /// <summary>OBS's events that change which items a scene has.</summary>
        private void OnItemsEvent(string type, JsonElement data)
        {
            switch (type)
            {
                case "SceneItemCreated":
                case "SceneItemRemoved":
                case "SceneItemListReindexed":
                    ForgetItems(data.ValueKind == JsonValueKind.Object && data.TryGetProperty("sceneName", out var sn) ? sn.GetString() : null);
                    break;
                case "InputCreated":
                case "InputRemoved":
                case "InputNameChanged":
                case "SceneCreated":
                case "SceneRemoved":
                case "SceneNameChanged":
                    ForgetItems();
                    break;
            }
        }

        // ---- The upkeep's own checks: a full look every 30 s, a light one in between ----------------

        /// <summary>How often the upkeep checks every source's link again (in between, only what's new).</summary>
        public static TimeSpan FullCheckEvery { get; set; } = TimeSpan.FromSeconds(30);
        private DateTime _lastFullCheck;
        private readonly Dictionary<string, string> _verified = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Whether this upkeep is a full one (links checked again, camera sources muted again).</summary>
        private bool FullCheckDue(bool now)
        {
            var t = DateTime.UtcNow;
            if (!now && t - _lastFullCheck < FullCheckEvery) return false;
            _lastFullCheck = t;
            return true;
        }

        /// <summary>The link check for an input, unless it was checked with this same link since the last full look.</summary>
        private Func<string, Task>? CheckLink(ObsClient obs, string url, bool full) => async n =>
        {
            lock (_verified) if (!full && _verified.TryGetValue(n, out var was) && was == url) return;
            var cur = await obs.RequestAsync("GetInputSettings", new { inputName = n }).ConfigureAwait(false);
            string? has = cur.TryGetProperty("inputSettings", out var st) && st.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (has != url) await obs.RequestAsync("SetInputSettings", new { inputName = n, inputSettings = new { url } }).ConfigureAwait(false);
            lock (_verified) _verified[n] = url;
        };

        /// <summary>Everything known about OBS's state goes (a new connection, or OBS restarted).</summary>
        private void ForgetKnown()
        {
            ForgetItems();
            lock (_gameMuted) _gameMuted.Clear();
            lock (_verified) _verified.Clear();
            _lastFullCheck = default;
        }
    }
}
