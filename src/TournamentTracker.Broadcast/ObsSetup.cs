using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>One line of the OBS tab's setup check.</summary>
    public sealed class SetupCheck
    {
        public string Id { get; set; } = "";
        public bool Ok { get; set; }
        public string Title { get; set; } = "";
        /// <summary>What's wrong and how to put it right by hand (when Red Alert can't).</summary>
        public string Detail { get; set; } = "";
        /// <summary>A button that fixes it from here, or null.</summary>
        public string? Fix { get; set; }
    }

    /// <summary>
    /// The setup check, run when OBS connects: names, a leftover empty scene, Desktop Audio reaching
    /// the stream, what replays need, the broadcast delay against impostor tags, and the headphones
    /// Hear Preview plays in.
    /// </summary>
    public sealed partial class ObsDirector
    {
        private static readonly string[] DesktopKinds = { "wasapi_output_capture", "pulse_output_capture", "coreaudio_output_capture", "alsa_output_capture" };

        /// <summary>Whether impostor tags are on stream (set by the app: they're in the graphics settings).</summary>
        public Func<bool>? ImpostorTagsOn { get; set; }
        public List<SetupCheck> Checks { get; private set; } = new List<SetupCheck>();
        public DateTime? CheckedAt { get; private set; }
        /// <summary>Desktop Audio inputs that aren't muted (Discord pings and Windows sounds can reach the stream).</summary>
        public List<string> DesktopAudioOn { get; private set; } = new List<string>();
        private readonly HashSet<string> _desktopInputs = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Whether OBS has the Source Record plugin (what replays are saved with); null: not known yet.</summary>
        public bool? SourceRecord { get; private set; }

        public async Task<List<SetupCheck>> CheckSetupAsync()
        {
            var obs = _obs;
            var list = new List<SetupCheck>();
            if (obs == null) { Checks = list; return list; }
            async Task<JsonElement?> Try(string req, object? data = null)
            {
                try { return await obs.RequestAsync(req, data).ConfigureAwait(false); } catch (Exception) { return null; }
            }

            // Names: Red Alert can't rename them over the websocket.
            if (await Try("GetSceneCollectionList") is { } sc)
            {
                string name = sc.TryGetProperty("currentSceneCollectionName", out var n) ? n.GetString() ?? "" : "";
                list.Add(new SetupCheck { Id = "collection", Ok = name != "Untitled", Title = $"Scene collection: {name}",
                    Detail = name == "Untitled" ? "Give it a name you'll recognise: in OBS's menu, Scene Collection → Rename (for example \"Tournament\"). Red Alert can't rename it for you." : "" });
            }
            if (await Try("GetProfileList") is { } pl)
            {
                string name = pl.TryGetProperty("currentProfileName", out var n) ? n.GetString() ?? "" : "";
                list.Add(new SetupCheck { Id = "profile", Ok = name != "Untitled", Title = $"Profile: {name}",
                    Detail = name == "Untitled" ? "In OBS's menu, Profile → Rename (for example \"Tournament stream\"). Red Alert can't rename it for you." : "" });
            }

            // A leftover empty "Scene" from a new OBS install.
            if (await Try("GetSceneList") is { } scenes && scenes.GetProperty("scenes").EnumerateArray().Any(x => x.GetProperty("sceneName").GetString() == "Scene"))
            {
                bool empty = await Try("GetSceneItemList", new { sceneName = "Scene" }) is { } items && items.GetProperty("sceneItems").GetArrayLength() == 0;
                if (empty) list.Add(new SetupCheck { Id = "emptyScene", Ok = false, Title = "A leftover empty scene called \"Scene\"", Detail = "It has nothing in it. Delete it so it can't be switched to by mistake.", Fix = "Delete it" });
            }

            // Desktop Audio: anything playing on this PC (Discord pings, Windows sounds, a lobby you Listen to) goes to the stream.
            await ReadDesktopAudioAsync(obs).ConfigureAwait(false);
            list.Add(new SetupCheck
            {
                Id = "desktopAudio", Ok = DesktopAudioOn.Count == 0,
                Title = DesktopAudioOn.Count == 0 ? (_desktopInputs.Count == 0 ? "No Desktop Audio capture" : "Desktop Audio is muted") : $"{string.Join(", ", DesktopAudioOn)} is capturing",
                Detail = DesktopAudioOn.Count == 0 ? "" : "Discord pings, Windows sounds and anything you Listen to on this PC can reach the stream. Lobby voice has its own sources, so Desktop Audio isn't needed.",
                Fix = DesktopAudioOn.Count == 0 ? null : "Mute Desktop Audio",
            });

            // Replays: saved by the Source Record plugin's buffer on each lobby's source (not OBS's own Replay Buffer).
            if (await Try("GetSourceFilterKindList") is { } kinds && kinds.TryGetProperty("sourceFilterKinds", out var k))
                SourceRecord = k.EnumerateArray().Any(x => x.GetString() == SourceRecordKind);
            else if (ReplayProblem?.Contains("Source Record") == true) SourceRecord = false;
            list.Add(new SetupCheck
            {
                Id = "replays", Ok = SourceRecord != false && Settings.Replay.On,
                Title = !Settings.Replay.On ? "Replays are off" : SourceRecord == false ? "Replays need the Source Record plugin" : "Replays: Source Record plugin found",
                Detail = !Settings.Replay.On ? "Replays are turned off in obs.json." : SourceRecord == false
                    ? $"Red Alert keeps the last {Settings.Replay.BufferSeconds}s of each lobby with the Source Record plugin for OBS (by Exeldro). Install it from obsproject.com (Plugins), restart OBS, then press Rebuild TT scenes. OBS's own Replay Buffer isn't used and can stay off."
                    : $"Each lobby's source keeps its last {Settings.Replay.BufferSeconds}s (OBS's own Replay Buffer isn't used and can stay off).",
            });

            if (DelayAllowed)
            {
                // Impostor tags against the delay: Red Alert's broadcast delay (OBS's own stream delay counts too, if it's on).
                bool tags = ImpostorTagsOn?.Invoke() == true;
                bool delayOn = await Try("GetProfileParameter", new { parameterCategory = "Output", parameterName = "DelayEnable" }) is { } de && Param(de) is "true" or "1";
                int delay = await Try("GetProfileParameter", new { parameterCategory = "Output", parameterName = "DelaySec" }) is { } ds && int.TryParse(Param(ds), out var sec) ? sec : 0;
                int obsDelay = delayOn ? delay : 0;
                double ours = DelaySeconds;
                bool held = ours > 0 || obsDelay > 0;
                string also = obsDelay > 0 ? $" · OBS stream delay {obsDelay} s" : "";
                list.Add(new SetupCheck
                {
                    Id = "delay", Ok = !tags || held,
                    Title = $"Broadcast delay {BroadcastDelay.Say(ours)}{also}{(tags ? " · impostor tags are on" : "")}",
                    Detail = tags && !held ? "Players watching the stream see the impostors as it happens. Red Alert can hold the lobbies back a few seconds (OBS card → Broadcast delay), or turn Impostor tags off in Graphics."
                        : ours > 0 ? $"Every lobby's picture, voice and camera, and the graphics, reach the stream {BroadcastDelay.Say(ours)} late; the Live desk stays live. Your mic isn't held back: to talk over the stream's picture, give it a Sync Offset of {(int)(ours * 1000)} ms in OBS (Edit → Advanced Audio Properties)."
                        : "",
                    Fix = tags && !held ? $"Turn on a {BroadcastDelay.Say(BroadcastDelay.Default)} delay" : null,
                });
            }

            // Hear Preview plays in OBS's monitoring device: it should be the caster's headphones.
            string? monId = await Try("GetProfileParameter", new { parameterCategory = "Audio", parameterName = "MonitoringDeviceId" }) is { } mi ? ParamOrDefault(mi) : null;
            string? monName = await Try("GetProfileParameter", new { parameterCategory = "Audio", parameterName = "MonitoringDeviceName" }) is { } mn ? ParamOrDefault(mn) : null;
            bool monOff = monId is "disabled" || string.Equals(monName, "Disabled", StringComparison.OrdinalIgnoreCase);
            bool monDefault = !monOff && (string.IsNullOrEmpty(monId) || monId == "default");
            bool cue = Settings.Voice.HearPreview;
            MonitoringDevice = monOff ? "Disabled" : monDefault ? "Default" : (string.IsNullOrEmpty(monName) ? monId : monName);
            list.Add(new SetupCheck
            {
                // Default is fine for most (the PC plays to the headphones): said, not counted as a problem.
                Id = "monitor", Ok = !cue || !monOff,
                Title = $"Headphones (OBS monitoring device): {MonitoringDevice}{(cue ? "" : " · Hear Preview is off")}",
                Detail = !cue ? "" : monOff
                    ? "Hear Preview plays in OBS's monitoring device, and it's off. In OBS: Settings → Audio → Advanced → Monitoring Device, pick your headphones."
                    : monDefault
                    ? "Hear Preview plays in OBS's monitoring device, now Default (whatever this PC plays to). To be sure it's your headphones: OBS → Settings → Audio → Advanced → Monitoring Device, pick them. Keep Desktop Audio muted."
                    : "",
            });

            Checks = list;
            CheckedAt = DateTime.UtcNow;
            return list;
        }

        private static string Param(JsonElement e) => e.Str("parameterValue") ?? "";
        /// <summary>A profile setting as OBS uses it: set, else its default.</summary>
        private static string? ParamOrDefault(JsonElement e) => e.Str("parameterValue") ?? e.Str("defaultParameterValue");

        /// <summary>OBS's monitoring device as the last setup check found it ("Default", "Disabled" or its name), or null.</summary>
        public string? MonitoringDevice { get; private set; }

        private async Task ReadDesktopAudioAsync(ObsClient obs)
        {
            var inputs = (await obs.RequestAsync("GetInputList").ConfigureAwait(false)).GetProperty("inputs").EnumerateArray()
                .Where(i => i.TryGetProperty("inputKind", out var k) && DesktopKinds.Contains(k.GetString())).Select(i => i.GetProperty("inputName").GetString() ?? "").ToList();
            var on = new List<string>();
            foreach (var name in inputs)
                try { if (!(await obs.RequestAsync("GetInputMute", new { inputName = name }).ConfigureAwait(false)).GetProperty("inputMuted").GetBoolean()) on.Add(name); } catch (Exception) { }
            lock (_desktopInputs) { _desktopInputs.Clear(); _desktopInputs.UnionWith(inputs); }
            DesktopAudioOn = on;
            _desktopRead = true;
        }

        /// <summary>Desktop Audio muted or unmuted in OBS: the Live desk warns while it's on.</summary>
        private void OnMuteChanged(JsonElement data)
        {
            string name = data.TryGetProperty("inputName", out var n) ? n.GetString() ?? "" : "";
            bool known; lock (_desktopInputs) known = _desktopInputs.Contains(name);
            if (!known) return;
            bool muted = data.Flag("inputMuted");
            var on = DesktopAudioOn.Where(x => x != name).ToList();
            if (!muted) on.Add(name);
            DesktopAudioOn = on;
            QueueCue(force: true);       // Hear Preview waits while Desktop Audio is on
            var c = Checks.FirstOrDefault(x => x.Id == "desktopAudio");
            if (c != null) { c.Ok = on.Count == 0; c.Fix = on.Count == 0 ? null : "Mute Desktop Audio"; c.Title = on.Count == 0 ? "Desktop Audio is muted" : $"{string.Join(", ", on)} is capturing"; }
        }

        /// <summary>A setup check's Fix button. What it did.</summary>
        public async Task<string> FixAsync(string id)
        {
            var obs = Client;
            string said;
            switch (id)
            {
                case "emptyScene":
                {
                    var items = await obs.RequestAsync("GetSceneItemList", new { sceneName = "Scene" }).ConfigureAwait(false);
                    if (items.GetProperty("sceneItems").GetArrayLength() > 0) { said = "\"Scene\" isn't empty any more: left alone."; break; }
                    var cur = await obs.RequestAsync("GetCurrentProgramScene").ConfigureAwait(false);
                    if (ProgramName(cur) == "Scene") { said = "\"Scene\" is live right now: switch away from it first."; break; }
                    await obs.RequestAsync("RemoveScene", new { sceneName = "Scene" }).ConfigureAwait(false);
                    said = "Deleted the empty scene \"Scene\".";
                    break;
                }
                case "desktopAudio":
                    foreach (var name in DesktopAudioOn.ToList())
                        await obs.RequestAsync("SetInputMute", new { inputName = name, inputMuted = true }).ConfigureAwait(false);
                    said = "Desktop Audio muted.";
                    break;
                case "delay":
                    // Red Alert's own broadcast delay (it checks again itself).
                    return await SetDelayAsync(BroadcastDelay.Default).ConfigureAwait(false);
                default: return "Nothing to fix there.";
            }
            await CheckSetupAsync().ConfigureAwait(false);
            return said;
        }
    }
}
