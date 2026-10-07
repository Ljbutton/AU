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
    /// the stream, what replays need, and stream delay against impostor tags.
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

            // Stream delay against impostor tags: without a delay, players can find the impostors by watching.
            bool tags = ImpostorTagsOn?.Invoke() == true;
            bool delayOn = await Try("GetProfileParameter", new { parameterCategory = "Output", parameterName = "DelayEnable" }) is { } de && Param(de) is "true" or "1";
            int delay = await Try("GetProfileParameter", new { parameterCategory = "Output", parameterName = "DelaySec" }) is { } ds && int.TryParse(Param(ds), out var sec) ? sec : 0;
            int effective = delayOn ? delay : 0;
            list.Add(new SetupCheck
            {
                Id = "delay", Ok = !tags || effective >= 30,
                Title = tags ? $"Impostor tags are on · stream delay {(effective > 0 ? effective + " s" : "off")}" : $"Stream delay {(effective > 0 ? effective + " s" : "off")} (impostor tags are off)",
                Detail = tags && effective < 30 ? "Players watching the stream could find the impostors. In OBS: Settings → Advanced → Stream Delay, tick Enable and set at least 60 s (it takes effect the next time you start streaming), or turn Impostor tags off in Graphics." : "",
                Fix = tags && effective < 30 ? "Set a 90 s delay" : null,
            });

            Checks = list;
            CheckedAt = DateTime.UtcNow;
            return list;
        }

        private static string Param(JsonElement e) => e.TryGetProperty("parameterValue", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private async Task ReadDesktopAudioAsync(ObsClient obs)
        {
            var inputs = (await obs.RequestAsync("GetInputList").ConfigureAwait(false)).GetProperty("inputs").EnumerateArray()
                .Where(i => i.TryGetProperty("inputKind", out var k) && DesktopKinds.Contains(k.GetString())).Select(i => i.GetProperty("inputName").GetString() ?? "").ToList();
            var on = new List<string>();
            foreach (var name in inputs)
                try { if (!(await obs.RequestAsync("GetInputMute", new { inputName = name }).ConfigureAwait(false)).GetProperty("inputMuted").GetBoolean()) on.Add(name); } catch (Exception) { }
            lock (_desktopInputs) { _desktopInputs.Clear(); _desktopInputs.UnionWith(inputs); }
            DesktopAudioOn = on;
        }

        /// <summary>Desktop Audio muted or unmuted in OBS: the Live desk warns while it's on.</summary>
        private void OnMuteChanged(JsonElement data)
        {
            string name = data.TryGetProperty("inputName", out var n) ? n.GetString() ?? "" : "";
            bool known; lock (_desktopInputs) known = _desktopInputs.Contains(name);
            if (!known) return;
            bool muted = data.TryGetProperty("inputMuted", out var m) && m.ValueKind == JsonValueKind.True;
            var on = DesktopAudioOn.Where(x => x != name).ToList();
            if (!muted) on.Add(name);
            DesktopAudioOn = on;
            var c = Checks.FirstOrDefault(x => x.Id == "desktopAudio");
            if (c != null) { c.Ok = on.Count == 0; c.Fix = on.Count == 0 ? null : "Mute Desktop Audio"; c.Title = on.Count == 0 ? "Desktop Audio is muted" : $"{string.Join(", ", on)} is capturing"; }
        }

        /// <summary>A setup check's Fix button. What it did.</summary>
        public async Task<string> FixAsync(string id)
        {
            var obs = _obs ?? throw new InvalidOperationException("Not connected to OBS.");
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
                    await obs.RequestAsync("SetProfileParameter", new { parameterCategory = "Output", parameterName = "DelayEnable", parameterValue = "true" }).ConfigureAwait(false);
                    await obs.RequestAsync("SetProfileParameter", new { parameterCategory = "Output", parameterName = "DelaySec", parameterValue = "90" }).ConfigureAwait(false);
                    said = "Stream delay set to 90 s in OBS (it starts the next time you start streaming).";
                    break;
                default: return "Nothing to fix there.";
            }
            await CheckSetupAsync().ConfigureAwait(false);
            return said;
        }
    }
}
