using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// The replay half of OBS: each lobby source keeps a rolling buffer (Source Record's replay
    /// buffer, as a filter on the source), a clip is saved on demand, and the TT Replay scene plays
    /// it in a Media Source that can be paused and scrubbed, cropped and zoomed by moving and
    /// scaling the picture, with a REPLAY tag on top.
    /// </summary>
    public sealed partial class ObsDirector
    {
        public const string SourceRecordKind = "source_record_filter";
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _saving = new ConcurrentDictionary<string, TaskCompletionSource<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _filtered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int? _clipItem;

        /// <summary>Why replays can't work right now (e.g. Source Record isn't installed), or null.</summary>
        public string? ReplayProblem { get; private set; }
        /// <summary>The page shown on top of replays (the REPLAY tag).</summary>
        public string? TagUrl { get; set; }
        /// <summary>The on-stream graphics app, on top of every TT scene.</summary>
        public string? BroadcastUrl { get; set; }
        public const string BroadcastSource = "TT Broadcast";

        /// <summary>The graphics app in every TT scene (and the replay scene), always the top layer.</summary>
        public async Task EnsureBroadcastAsync()
        {
            var obs = _obs;
            if (obs == null || BroadcastUrl == null) return;
            await _busy.WaitAsync().ConfigureAwait(false);
            try
            {
                var inputs = (await obs.RequestAsync("GetInputList").ConfigureAwait(false)).GetProperty("inputs").EnumerateArray().Select(i => i.GetProperty("inputName").GetString()).ToHashSet();
                var scenes = Settings.Scenes.Values.Concat(new[] { Settings.Replay.Scene }).ToList();
                var have = (await obs.RequestAsync("GetSceneList").ConfigureAwait(false)).GetProperty("scenes").EnumerateArray().Select(x => x.GetProperty("sceneName").GetString()).ToHashSet();
                foreach (var scene in scenes.Where(have.Contains))
                {
                    if (!inputs.Contains(BroadcastSource))
                    {
                        await obs.RequestAsync("CreateInput", new
                        {
                            sceneName = scene, inputName = BroadcastSource, inputKind = "browser_source",
                            inputSettings = new { url = BroadcastUrl, width = (int)Width, height = (int)Height, shutdown = false, restart_when_active = false, reroute_audio = true },
                            sceneItemEnabled = true,
                        }).ConfigureAwait(false);
                        inputs.Add(BroadcastSource);
                    }
                    var items = await ItemsAsync(obs, scene).ConfigureAwait(false);
                    var mine = items.FirstOrDefault(i => i.Source == BroadcastSource);
                    int id = mine?.Id ?? (await obs.RequestAsync("CreateSceneItem", new { sceneName = scene, sourceName = BroadcastSource, sceneItemEnabled = true }).ConfigureAwait(false)).GetProperty("sceneItemId").GetInt32();
                    int count = mine == null ? items.Count + 1 : items.Count;
                    // OBS lists the bottom layer first: the top is the last index.
                    await obs.RequestAsync("SetSceneItemIndex", new { sceneName = scene, sceneItemId = id, sceneItemIndex = count - 1 }).ConfigureAwait(false);
                }
            }
            catch (Exception e) { Problem = "OBS graphics layer: " + e.Message; }
            finally { _busy.Release(); }
        }

        public string ClipFolder => Settings.Replay.Folder.Length > 0 ? Settings.Replay.Folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) is { Length: > 0 } v ? v : Path.GetTempPath(), "TT Replays");

        /// <summary>The Source Record filter that keeps this lobby's last few seconds, on its source.</summary>
        private async Task EnsureReplayFilterAsync(ObsClient obs, string lobby, string source)
        {
            if (!Settings.Replay.On || _filtered.Contains(source)) return;
            try
            {
                var list = await obs.RequestAsync("GetSourceFilterList", new { sourceName = source }).ConfigureAwait(false);
                bool has = list.GetProperty("filters").EnumerateArray().Any(f => f.GetProperty("filterName").GetString() == Settings.Replay.FilterName);
                var settings = new
                {
                    record_mode = 0,                           // no full recording, just the buffer
                    replay_buffer = true,
                    replay_duration = Settings.Replay.BufferSeconds,
                    path = ClipFolder,
                    rec_format = Settings.Replay.Format,
                    replay_filename_formatting = $"TT {SafeName(lobby)} %CCYY-%MM-%DD %hh-%mm-%ss",
                };
                if (!has)
                    await obs.RequestAsync("CreateSourceFilter", new { sourceName = source, filterName = Settings.Replay.FilterName, filterKind = SourceRecordKind, filterSettings = settings }).ConfigureAwait(false);
                else
                    await obs.RequestAsync("SetSourceFilterSettings", new { sourceName = source, filterName = Settings.Replay.FilterName, filterSettings = settings, overlay = true }).ConfigureAwait(false);
                _filtered.Add(source);
                if (ReplayProblem?.StartsWith("Install", StringComparison.Ordinal) != true) ReplayProblem = null;
            }
            catch (ObsException e)
            {
                ReplayProblem = e.Message.IndexOf("kind", StringComparison.OrdinalIgnoreCase) >= 0 || e.Code == 605
                    ? "Install the Source Record plugin for OBS (by Exeldro) for replays, then press Rebuild TT scenes."
                    : "Replays: " + e.Message;
            }
        }

        private static string SafeName(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());

        /// <summary>The TT Replay scene: the clip (a Media Source) and the REPLAY tag on top.</summary>
        public async Task EnsureReplaySceneAsync()
        {
            var obs = _obs;
            if (obs == null || !Settings.Replay.On) return;
            var r = Settings.Replay;
            await _busy.WaitAsync().ConfigureAwait(false);
            try
            {
                var scenes = (await obs.RequestAsync("GetSceneList").ConfigureAwait(false)).GetProperty("scenes").EnumerateArray().Select(s => s.GetProperty("sceneName").GetString()).ToHashSet();
                if (!scenes.Contains(r.Scene)) await obs.RequestAsync("CreateScene", new { sceneName = r.Scene }).ConfigureAwait(false);
                var inputs = (await obs.RequestAsync("GetInputList").ConfigureAwait(false)).GetProperty("inputs").EnumerateArray().Select(i => i.GetProperty("inputName").GetString()).ToHashSet();
                if (!inputs.Contains(r.ClipSource))
                    await obs.RequestAsync("CreateInput", new
                    {
                        sceneName = r.Scene, inputName = r.ClipSource, inputKind = "ffmpeg_source",
                        inputSettings = new { is_local_file = true, local_file = "", looping = false, restart_on_activate = false, close_when_inactive = false, clear_on_media_end = false, hw_decode = true },
                        sceneItemEnabled = true,
                    }).ConfigureAwait(false);
                if (TagUrl != null && !inputs.Contains(r.TagSource))
                    await obs.RequestAsync("CreateInput", new
                    {
                        sceneName = r.Scene, inputName = r.TagSource, inputKind = "browser_source",
                        inputSettings = new { url = TagUrl, width = (int)Width, height = (int)Height, shutdown = false, restart_when_active = false },
                        sceneItemEnabled = true,
                    }).ConfigureAwait(false);
                var items = await ItemsAsync(obs, r.Scene).ConfigureAwait(false);
                _clipItem = items.FirstOrDefault(i => i.Source == r.ClipSource)?.Id;
                if (_clipItem == null) _clipItem = (await obs.RequestAsync("CreateSceneItem", new { sceneName = r.Scene, sourceName = r.ClipSource, sceneItemEnabled = true }).ConfigureAwait(false)).GetProperty("sceneItemId").GetInt32();
                if (TagUrl != null && !items.Any(i => i.Source == r.TagSource))
                    await obs.RequestAsync("CreateSceneItem", new { sceneName = r.Scene, sourceName = r.TagSource, sceneItemEnabled = true }).ConfigureAwait(false);
            }
            catch (Exception e) { ReplayProblem = "Replays: " + e.Message; }
            finally { _busy.Release(); }
        }

        /// <summary>Saves the lobby's buffer to a file now; answers with the file once Source Record says it's written.</summary>
        public async Task<string> SaveClipAsync(string lobby)
        {
            var obs = _obs ?? throw new InvalidOperationException("Not connected to OBS.");
            if (!Settings.Sources.TryGetValue(lobby, out var source)) throw new InvalidOperationException($"{lobby} has no source in OBS yet.");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _saving[source] = tcs;
            try
            {
                await obs.RequestAsync("CallVendorRequest", new { vendorName = "source-record", requestType = "replay_buffer_save", requestData = new { source, filter = Settings.Replay.FilterName } }).ConfigureAwait(false);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                if (done != tcs.Task) throw new TimeoutException("OBS didn't save the clip. Is the lobby's replay buffer running (Source Record filter on its source)?");
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (ObsException e) when (e.Code == 206 || e.Message.IndexOf("vendor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ReplayProblem = "Install the Source Record plugin for OBS (by Exeldro) for replays.";
                throw new InvalidOperationException(ReplayProblem);
            }
            finally { _saving.TryRemove(source, out _); }
        }

        private void OnVendorEvent(JsonElement data)
        {
            if (!data.TryGetProperty("vendorName", out var v) || v.GetString() != "source-record") return;
            if (!data.TryGetProperty("eventType", out var t) || t.GetString() != "replay_buffer_saved") return;
            if (!data.TryGetProperty("eventData", out var d)) return;
            string source = d.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
            string path = d.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            if (_saving.TryGetValue(source, out var tcs)) tcs.TrySetResult(path);
        }

        // ---- Playing a clip ---------------------------------------------------------------------

        /// <summary>Loads a clip into the replay Media Source (paused at its start); answers with its length in seconds.</summary>
        public async Task<double> LoadClipAsync(string file)
        {
            var obs = _obs ?? throw new InvalidOperationException("Not connected to OBS.");
            if (_clipItem == null) await EnsureReplaySceneAsync().ConfigureAwait(false);
            await obs.RequestAsync("SetInputSettings", new { inputName = Settings.Replay.ClipSource, inputSettings = new { local_file = file, is_local_file = true } }).ConfigureAwait(false);
            for (int i = 0; i < 40; i++)
            {
                var st = await MediaStatusAsync().ConfigureAwait(false);
                if (st.Duration > 0) return st.Duration;
                await Task.Delay(100).ConfigureAwait(false);
            }
            throw new InvalidOperationException("OBS couldn't open the clip: " + file);
        }

        /// <summary>The clip's state ("OBS_MEDIA_STATE_PLAYING", …), length and position, in seconds.</summary>
        public async Task<(string State, double Duration, double Cursor)> MediaStatusAsync()
        {
            var obs = _obs ?? throw new InvalidOperationException("Not connected to OBS.");
            var st = await obs.RequestAsync("GetMediaInputStatus", new { inputName = Settings.Replay.ClipSource }).ConfigureAwait(false);
            double Ms(string p) => st.TryGetProperty(p, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() / 1000 : 0;
            return (st.TryGetProperty("mediaState", out var s) ? s.GetString() ?? "" : "", Ms("mediaDuration"), Ms("mediaCursor"));
        }

        public Task MediaAsync(string action) =>
            (_obs ?? throw new InvalidOperationException("Not connected to OBS.")).RequestAsync("TriggerMediaInputAction",
                new { inputName = Settings.Replay.ClipSource, mediaAction = "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_" + action.ToUpperInvariant() });

        public Task SeekAsync(double seconds) =>
            (_obs ?? throw new InvalidOperationException("Not connected to OBS.")).RequestAsync("SetMediaInputCursor",
                new { inputName = Settings.Replay.ClipSource, mediaCursor = Math.Max(0, Math.Round(seconds * 1000)) });

        /// <summary>Puts the replay scene on stream (our own switch, not mistaken for a manual one).</summary>
        public async Task ShowReplaySceneAsync()
        {
            var obs = _obs ?? throw new InvalidOperationException("Not connected to OBS.");
            _ignoreSceneUntil = DateTime.UtcNow.AddSeconds(1.5);
            _lastAppliedScene = Settings.Replay.Scene;
            await obs.RequestAsync("SetCurrentProgramScene", new { sceneName = Settings.Replay.Scene }).ConfigureAwait(false);
            Scene = Settings.Replay.Scene;
        }

        /// <summary>Crops and zooms the replay: moves and scales the clip so the view's centre is in the middle of the canvas.</summary>
        public async Task FrameAsync(View view)
        {
            var obs = _obs;
            if (obs == null || _clipItem == null) return;
            var (x, y, w, h) = Framing.Place(view, Width, Height);
            await obs.RequestAsync("SetSceneItemTransform", new
            {
                sceneName = Settings.Replay.Scene,
                sceneItemId = _clipItem.Value,
                sceneItemTransform = new
                {
                    positionX = x, positionY = y, alignment = 5, rotation = 0.0,
                    boundsType = "OBS_BOUNDS_SCALE_INNER", boundsAlignment = 0, boundsWidth = w, boundsHeight = h,
                    cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0,
                },
            }).ConfigureAwait(false);
        }
    }
}
