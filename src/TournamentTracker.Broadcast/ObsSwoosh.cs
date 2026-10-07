using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>The swoosh on every switch: a stinger video (with its sound, which goes to the stream).</summary>
    public sealed class SwooshSettings
    {
        public bool On { get; set; } = true;
        /// <summary>The stinger video (a WebM with transparency is best). Empty: The Button makes a placeholder with ffmpeg.</summary>
        public string Path { get; set; } = "";
        /// <summary>When the video covers the whole screen: the switch happens then.</summary>
        public int TransitionPointMs { get; set; } = 450;
        /// <summary>Switches closer together than this get only one swoosh.</summary>
        public double DedupeSeconds { get; set; } = 1.5;
    }

    /// <summary>
    /// Part 17: a swoosh on every switch. A scene change uses OBS's Stinger transition when there is one
    /// called "TT Swoosh" (OBS can't make transitions through obs-websocket; add it once under Scene
    /// Transitions → + → Stinger and The Button sets its video and timing). Every other switch (a
    /// layout that only moves pictures, the grid changing, or no stinger transition) plays the same
    /// video as the top layer of the TT scenes and switches at its transition point. Switches within
    /// a second and a half share one swoosh; scrubbing replays never swooshes.
    /// </summary>
    public sealed partial class ObsDirector
    {
        public const string SwooshSource = "TT Swoosh";
        public const string StingerTransition = "TT Swoosh";
        public const string PlaceholderName = "tt-swoosh.webm";
        /// <summary>What the made swoosh looks like (the tournament's colours and logo): a new look makes a new file.</summary>
        public Func<string>? SwooshKey { get; set; }
        private DateTime _lastSwoosh;
        private bool _stinger;
        private string? _airKey;

        /// <summary>Makes the placeholder stinger (with ffmpeg) at the given path; true when it did.</summary>
        public Func<string, Task<bool>>? MakeSwoosh { get; set; }

        /// <summary>The stinger video in use.</summary>
        public string SwooshFile => Settings.Swoosh.Path.Length > 0 ? Settings.Swoosh.Path
            : System.IO.Path.Combine(_path != null ? System.IO.Path.GetDirectoryName(_path)! : ClipFolder, SwooshKey?.Invoke() is { Length: > 0 } k ? $"tt-swoosh-{k}.webm" : PlaceholderName);

        public string? SwooshProblem { get; private set; }
        /// <summary>How many swooshes played (for the tab and tests).</summary>
        public int Swooshes { get; private set; }

        /// <summary>The stinger video, the TT Swoosh layer on top of every TT scene, and the Stinger transition if there is one.</summary>
        public async Task EnsureSwooshAsync()
        {
            var obs = _obs;
            if (obs == null) return;
            var s = Settings.Swoosh;
            string file = SwooshFile;
            try
            {
                if (!File.Exists(file) && s.Path.Length == 0 && MakeSwoosh != null) await MakeSwoosh(file).ConfigureAwait(false);
                SwooshProblem = File.Exists(file) ? null : s.Path.Length > 0 ? $"The swoosh video isn't there: {file}" : "No swoosh video yet: get ffmpeg (Montages card) or set one in obs.json (swoosh.path).";
                await _busy.WaitAsync().ConfigureAwait(false);
                try
                {
                    var inputs = (await obs.RequestAsync("GetInputList").ConfigureAwait(false)).GetProperty("inputs").EnumerateArray().Select(i => i.GetProperty("inputName").GetString()).ToHashSet();
                    var settings = new { local_file = file, is_local_file = true, looping = false, restart_on_activate = false, close_when_inactive = false, clear_on_media_end = true, hw_decode = true };
                    var scenes = Settings.Scenes.Values.Concat(new[] { Settings.Replay.Scene }).ToList();
                    var have = (await obs.RequestAsync("GetSceneList").ConfigureAwait(false)).GetProperty("scenes").EnumerateArray().Select(x => x.GetProperty("sceneName").GetString()).ToHashSet();
                    if (inputs.Contains(SwooshSource)) await obs.RequestAsync("SetInputSettings", new { inputName = SwooshSource, inputSettings = settings }).ConfigureAwait(false);
                    foreach (var scene in scenes.Where(have.Contains))
                    {
                        if (!inputs.Contains(SwooshSource))
                        {
                            await obs.RequestAsync("CreateInput", new { sceneName = scene, inputName = SwooshSource, inputKind = "ffmpeg_source", inputSettings = settings, sceneItemEnabled = true }).ConfigureAwait(false);
                            inputs.Add(SwooshSource);
                        }
                        var items = await ItemsAsync(obs, scene).ConfigureAwait(false);
                        var mine = items.FirstOrDefault(i => i.Source == SwooshSource);
                        int id = mine?.Id ?? (await obs.RequestAsync("CreateSceneItem", new { sceneName = scene, sourceName = SwooshSource, sceneItemEnabled = true }).ConfigureAwait(false)).GetProperty("sceneItemId").GetInt32();
                        int count = mine == null ? items.Count + 1 : items.Count;
                        await obs.RequestAsync("SetSceneItemIndex", new { sceneName = scene, sceneItemId = id, sceneItemIndex = count - 1 }).ConfigureAwait(false);
                    }
                    // The Stinger transition, when the caster made one.
                    _stinger = false;
                    try
                    {
                        var list = await obs.RequestAsync("GetSceneTransitionList").ConfigureAwait(false);
                        _stinger = list.GetProperty("transitions").EnumerateArray().Any(t => t.GetProperty("transitionName").GetString() == StingerTransition
                            && t.GetProperty("transitionKind").GetString() == "obs_stinger_transition");
                        if (_stinger)
                        {
                            await obs.RequestAsync("SetCurrentSceneTransition", new { transitionName = StingerTransition }).ConfigureAwait(false);
                            await obs.RequestAsync("SetCurrentSceneTransitionSettings", new { transitionSettings = new { path = file, transition_point_type = 0, transition_point = s.TransitionPointMs }, overlay = true }).ConfigureAwait(false);
                        }
                    }
                    catch (ObsException) { _stinger = false; }
                }
                finally { _busy.Release(); }
            }
            catch (Exception e) { SwooshProblem = "Swoosh: " + e.Message; }
        }

        /// <summary>Whether OBS has the TT Swoosh Stinger transition (scene changes use it).</summary>
        public bool HasStinger => _stinger;

        /// <summary>
        /// A swoosh before a switch, unless one just played. A scene change with the Stinger transition
        /// needs nothing here (OBS plays it); otherwise the TT Swoosh layer plays and the switch waits
        /// for its transition point.
        /// </summary>
        public async Task<bool> SwooshAsync(bool sceneChange)
        {
            var obs = _obs;
            var s = Settings.Swoosh;
            if (obs == null || !s.On) return false;
            var now = DateTime.UtcNow;
            if ((now - _lastSwoosh).TotalSeconds < s.DedupeSeconds) return false;
            _lastSwoosh = now;
            Swooshes++;
            if (sceneChange && _stinger) return true;
            try
            {
                await obs.RequestAsync("TriggerMediaInputAction", new { inputName = SwooshSource, mediaAction = "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_RESTART" }).ConfigureAwait(false);
                await Task.Delay(Math.Max(0, s.TransitionPointMs)).ConfigureAwait(false);
            }
            catch (ObsException) { }
            return true;
        }

        /// <summary>A key for what's on stream, so applying the same thing twice doesn't swoosh.</summary>
        private static string AirKey(OnAir air) => air.Layout + "|" + string.Join(",", air.Slots.Select(x => x ?? "-"));
    }
}
