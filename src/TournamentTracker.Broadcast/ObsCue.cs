using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Hear Preview (a headphone cue): the lobby in Preview (its first one) plays in the caster's
    /// headphones, never on stream. One "TT Cue" source in every TT scene plays that lobby's voice
    /// link, with OBS's audio monitoring set to Monitor Only: OBS plays it in its monitoring device
    /// (Settings → Audio → Advanced → Monitoring Device) and leaves it out of the stream and the
    /// recording. It's the live link, without the broadcast delay, so it matches the Live desk's
    /// pictures. Muted (and its link dropped) when Preview is empty, on Mute all and on panic; held back
    /// while Desktop Audio is capturing, which could carry the headphones to the stream. The lobby
    /// voice sources on stream are left as they are.
    /// </summary>
    public sealed partial class ObsDirector
    {
        public const string CueSource = "TT Cue";
        public const string MonitorOnly = "OBS_MONITORING_TYPE_MONITOR_ONLY";

        private readonly SemaphoreSlim _cueGate = new SemaphoreSlim(1, 1);
        private string? _cueName;           // the source in OBS, once made on this connection
        private string? _cueUrl;            // the link it plays now; null: silent
        private string? _cueWanted = "\u0000";
        private volatile bool _desktopRead;
        private bool _cueChecked;
        private int _cueQueued;

        /// <summary>The lobby in the caster's headphones now, or null.</summary>
        public string? CueHeard { get; private set; }
        /// <summary>Why Hear Preview isn't playing the lobby in Preview, or null.</summary>
        public string? CueProblem { get; private set; }

        /// <summary>
        /// The lobby that should be in the headphones: the first in Preview, while Hear Preview is on
        /// and the lobby voice isn't all muted. Not auto grid's own suggestion (not the caster's pick).
        /// </summary>
        public string? CueWant()
        {
            var v = Settings.Voice;
            if (!v.HearPreview || v.MuteAll) return null;
            var pv = _desk.Preview;
            return pv.Auto != null ? null : pv.Slots.FirstOrDefault();
        }

        /// <summary>The lobby's own voice link (live: no broadcast delay), or null when it sends none.</summary>
        private string? CueLink(string lobby)
        {
            if (!_desk.Simulating && CasterDesk.IsSimLobby(lobby)) return null;
            var feeds = VoiceFeeds?.Invoke();
            if (feeds == null) return null;
            foreach (var (l, url) in feeds) if (string.Equals(l, lobby, StringComparison.OrdinalIgnoreCase)) return url;
            return null;
        }

        private void ForgetCue()
        {
            _cueName = null; _cueUrl = null; CueHeard = null;
            _desktopRead = false;
            _cueChecked = false;
            _cueWanted = "\u0000";
        }

        /// <summary>
        /// A TT Cue already in OBS when Red Alert connects (from before a restart) is muted, so it can't go on
        /// playing an old lobby in the headphones. Looked at once per connection, by the voice upkeep.
        /// </summary>
        private async Task SilenceOldCueAsync(ObsClient obs, ICollection<string> inputs)
        {
            if (_cueChecked || !_cueGate.Wait(0)) return;
            try
            {
                _cueChecked = true;
                string name = Live(CueSource);
                if (_cueName == null && inputs.Contains(name))
                    await obs.RequestAsync("SetInputMute", new { inputName = name, inputMuted = true }).ConfigureAwait(false);
            }
            catch (ObsException) { }
            finally { _cueGate.Release(); }
        }

        /// <summary>The cue source was removed or renamed in OBS: made again at the next look.</summary>
        private void CueGone(JsonElement data)
        {
            var name = _cueName;
            if (name == null || data.ValueKind != JsonValueKind.Object) return;
            foreach (var p in new[] { "inputName", "oldInputName" })
                if (data.TryGetProperty(p, out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == name) { _cueName = null; _cueUrl = null; CueHeard = null; QueueCue(force: true); return; }
        }

        /// <summary>The desk changed (Preview among it): the headphones follow, when what they should play changed.</summary>
        private void QueueCue() => QueueCue(false);

        private void QueueCue(bool force)
        {
            if (_obs == null) return;
            if (!force && string.Equals(CueWant(), _cueWanted, StringComparison.OrdinalIgnoreCase)) return;
            if (Interlocked.Exchange(ref _cueQueued, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                Volatile.Write(ref _cueQueued, 0);
                try { await CueAsync().ConfigureAwait(false); } catch (Exception) { }
            });
        }

        /// <summary>
        /// Puts the right lobby (or nothing) in the headphones. Only what changed is sent. Going on: Monitor
        /// Only first, then the link, then unmuted. Going off: muted first, then the link dropped.
        /// </summary>
        public async Task CueAsync()
        {
            var obs = _obs;
            if (obs == null) return;
            await _cueGate.WaitAsync().ConfigureAwait(false);
            try
            {
                string? want = CueWant();
                _cueWanted = want;
                string? url = want == null ? null : CueLink(want);
                string? problem = want != null && url == null ? $"{want} sends no lobby voice: there's nothing to hear." : null;
                if (url != null)
                {
                    if (!_desktopRead) await ReadDesktopAudioAsync(obs).ConfigureAwait(false);
                    var on = DesktopAudioOn;
                    if (on.Count > 0)
                    {
                        problem = $"Hear Preview waits while {string.Join(", ", on)} is capturing: it could carry your headphones to the stream. Mute Desktop Audio.";
                        url = null;
                    }
                }
                CueProblem = problem;
                if (url == _cueUrl && (url == null || _cueName != null)) return;
                if (url == null)
                {
                    var name = _cueName;
                    if (name != null)
                    {
                        await obs.RequestAsync("SetInputMute", new { inputName = name, inputMuted = true }).ConfigureAwait(false);
                        await obs.RequestAsync("SetInputSettings", new { inputName = name, inputSettings = new { url = "about:blank" } }).ConfigureAwait(false);
                    }
                    _cueUrl = null;
                    CueHeard = null;
                    return;
                }
                string cue = _cueName ?? await MakeCueAsync(obs).ConfigureAwait(false);
                _cueName = cue;
                // Monitor Only, said again each time it goes on: never on stream, whatever was set by hand in OBS.
                await obs.RequestAsync("SetInputAudioMonitorType", new { inputName = cue, monitorType = MonitorOnly }).ConfigureAwait(false);
                await obs.RequestAsync("SetInputSettings", new { inputName = cue, inputSettings = new { url } }).ConfigureAwait(false);
                await obs.RequestAsync("SetInputMute", new { inputName = cue, inputMuted = false }).ConfigureAwait(false);
                _cueUrl = url;
                CueHeard = want;
            }
            catch (Exception e)
            {
                // OBS said no (or the source is gone): looked at again, and made again if need be, next time.
                CueProblem = "Hear Preview: " + e.Message;
                _cueName = null; _cueUrl = null; CueHeard = null;
            }
            finally { _cueGate.Release(); }
        }

        /// <summary>
        /// The TT Cue source in every TT scene (and the replay scene), always shown (a hidden source goes
        /// silent), at the bottom: muted and Monitor Only before it's given a lobby.
        /// </summary>
        private async Task<string> MakeCueAsync(ObsClient obs)
        {
            await _busy.WaitAsync().ConfigureAwait(false);
            try
            {
                var inputs = await InputNamesAsync(obs).ConfigureAwait(false);
                var have = await SceneNamesAsync(obs).ConfigureAwait(false);
                var scenes = Settings.Scenes.Values.Concat(new[] { Settings.Replay.Scene }).Where(have.Contains).ToList();
                string current = Live(CueSource);
                var (name, ids, added) = await EnsureInputAsync(obs, CueSource, current, scenes, inputs, "browser_source",
                    new { url = "about:blank", width = 64, height = 64, reroute_audio = true, shutdown = false, restart_when_active = false },
                    enabled: true).ConfigureAwait(false);
                if (name != current) Renamed(CueSource, name);
                await obs.RequestAsync("SetInputMute", new { inputName = name, inputMuted = true }).ConfigureAwait(false);
                await obs.RequestAsync("SetInputAudioMonitorType", new { inputName = name, monitorType = MonitorOnly }).ConfigureAwait(false);
                if (added)
                    foreach (var (scene, id) in ids)
                    {
                        await obs.RequestAsync("SetSceneItemIndex", new { sceneName = scene, sceneItemId = id, sceneItemIndex = 0 }).ConfigureAwait(false);
                        ForgetItems(scene);
                    }
                return name;
            }
            finally { _busy.Release(); }
        }

        /// <summary>Hear Preview for the desk and the Stream Deck.</summary>
        public object CueStatus() => new { On = Settings.Voice.HearPreview, Lobby = CueHeard, Problem = Settings.Voice.HearPreview ? CueProblem : null };
    }
}
