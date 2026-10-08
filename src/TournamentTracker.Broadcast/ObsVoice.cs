using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>The lobbies' voice on stream (Part 11), in obs.json.</summary>
    public sealed class VoiceSettings
    {
        public bool On { get; set; } = true;
        /// <summary>Every lobby voice silenced (the caster's mute-all key).</summary>
        public bool MuteAll { get; set; }
        /// <summary>A lobby whose voice stays up whatever is on screen (the caster's override), or empty: the voice follows the picture.</summary>
        public string Pin { get; set; } = "";
        /// <summary>Per lobby: volume (dB, 0 = as sent) and delay (ms, to line the voice up with the picture).</summary>
        public Dictionary<string, double> Volume { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Offset { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The caster's microphone in OBS: lobby voice ducks under it (a compressor with that sidechain). Empty: no ducking.</summary>
        public string DuckUnder { get; set; } = "";
        /// <summary>Set once Red Alert has picked the mic for <see cref="DuckUnder"/> (or the caster chose): it's not picked again.</summary>
        public bool DuckPicked { get; set; }
        /// <summary>How hard the voice ducks (compressor ratio) and from what level of the caster's mic (dB).</summary>
        public double DuckRatio { get; set; } = 8;
        public double DuckThreshold { get; set; } = -30;
    }

    /// <summary>
    /// Part 11 on the caster's side: each lobby's voice (its referee's Discord and game sound, sent as
    /// its own VDO.Ninja stream) is a "TT Voice" source in every TT scene. The voice follows the
    /// picture: the full-screen lobby, or slot 1, is heard; the rest are muted. During replays,
    /// montages, intermission and when nothing is on, every voice is muted. The caster can pin one
    /// lobby's voice, mute them all, set each lobby's volume and delay, and duck them under their mic.
    /// It only goes to the stream: OBS's monitoring stays off, and nothing goes back to the players.
    /// </summary>
    public sealed partial class ObsDirector
    {
        public const string VoicePrefix = "TT Voice ";
        public const string DuckFilter = "TT Duck";

        /// <summary>Each lobby's voice stream (VDO.Ninja, no picture), as OBS should load it.</summary>
        public Func<IReadOnlyList<(string Lobby, string Url)>>? VoiceFeeds { get; set; }
        public string? VoiceProblem { get; private set; }
        public List<string> InputNames { get; private set; } = new List<string>();
        private readonly Dictionary<string, (bool Muted, double Db, int Offset)> _voiceSet = new Dictionary<string, (bool, double, int)>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _voiceInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Voice source → lobby (a source can have moved to a fresh name, "TT Voice LJ 2").</summary>
        private readonly Dictionary<string, string> _voiceLobby = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string VoiceLobby(string name) => _voiceLobby.TryGetValue(name, out var l) ? l : name.StartsWith(VoicePrefix, StringComparison.Ordinal) ? name.Substring(VoicePrefix.Length) : name;
        private string _duckWas = "\u0000";
        private OnAir? _voiceAir;

        /// <summary>Voice sources for every lobby that sends one, in every TT scene, and the ducking filter.</summary>
        public async Task EnsureVoiceAsync()
        {
            var obs = _obs;
            var feeds = VoiceFeeds?.Invoke();
            if (obs == null || feeds == null || !Settings.Voice.On) return;
            await _busy.WaitAsync().ConfigureAwait(false);
            try
            {
                var all = (await obs.RequestAsync("GetInputList").ConfigureAwait(false)).GetProperty("inputs").EnumerateArray()
                    .Select(i => (Name: i.GetProperty("inputName").GetString() ?? "", Kind: i.TryGetProperty("inputKind", out var k) ? k.GetString() ?? "" : "")).ToList();
                var inputs = all.Select(i => i.Name).ToHashSet();
                // For the ducking picker: OBS's own inputs (the caster's mic among them), not ours.
                InputNames = inputs.Where(n => !n.StartsWith("TT ", StringComparison.Ordinal)).OrderBy(n => n).ToList();
                // The first time: duck under the obvious mic (changeable; "No ducking" is remembered too).
                if (!Settings.Voice.DuckPicked && Settings.Voice.DuckUnder.Length == 0 && PickMic(all) is { } mic)
                {
                    Settings.Voice.DuckUnder = mic;
                    Settings.Voice.DuckPicked = true;
                    Save();
                }
                var scenes = Settings.Scenes.Values.Concat(new[] { Settings.Replay.Scene }).ToList();
                var have = (await obs.RequestAsync("GetSceneList").ConfigureAwait(false)).GetProperty("scenes").EnumerateArray().Select(x => x.GetProperty("sceneName").GetString()).ToHashSet();
                string duck = Settings.Voice.DuckUnder.Trim();
                bool duckChanged = duck != _duckWas;
                var inScenes = scenes.Where(have.Contains).ToList();
                foreach (var (lobby, url) in feeds)
                {
                    if (!_desk.Simulating && CasterDesk.IsSimLobby(lobby)) continue;
                    string root = VoicePrefix + lobby, current = Live(root);
                    bool fresh = !Usable(current, inputs);
                    bool known = _voiceInputs.Contains(current);
                    // Tiny and see-through: only its sound matters. Stays connected when muted, so switching is instant.
                    // In every scene, always active: a hidden source would go silent; muting decides who's heard.
                    var (name, ids, _) = await EnsureInputAsync(obs, root, current, inScenes, inputs, "browser_source",
                        new { url, width = 64, height = 64, reroute_audio = true, shutdown = false, restart_when_active = false },
                        enabled: true,
                        existing: known ? null : async n =>
                        {
                            var cur = await obs.RequestAsync("GetInputSettings", new { inputName = n }).ConfigureAwait(false);
                            string? was = cur.TryGetProperty("inputSettings", out var st) && st.TryGetProperty("url", out var u) ? u.GetString() : null;
                            if (was != url) await obs.RequestAsync("SetInputSettings", new { inputName = n, inputSettings = new { url } }).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    if (name != current)
                    {
                        Renamed(root, name);
                        _voiceInputs.Remove(current);
                        _voiceSet.Remove(current);
                        fresh = true;
                    }
                    if (fresh) await obs.RequestAsync("SetInputMute", new { inputName = name, inputMuted = true }).ConfigureAwait(false);
                    // At the bottom, out of the way of the pictures and graphics.
                    foreach (var scene in inScenes)
                    {
                        var items = await ItemsAsync(obs, scene).ConfigureAwait(false);
                        if (items.FindIndex(i => i.Id == ids[scene]) != 0) await obs.RequestAsync("SetSceneItemIndex", new { sceneName = scene, sceneItemId = ids[scene], sceneItemIndex = 0 }).ConfigureAwait(false);
                    }
                    if (fresh || duckChanged || !_voiceInputs.Contains(name)) await DuckAsync(obs, name, duck).ConfigureAwait(false);
                    _voiceInputs.Add(name);
                    _voiceLobby[name] = lobby;
                }
                _duckWas = duck;
                VoiceProblem = null;
            }
            catch (Exception e) { VoiceProblem = "Lobby voice: " + e.Message; }
            finally { _busy.Release(); }
            if (_voiceAir != null) await SetVoicesAsync(_voiceAir).ConfigureAwait(false);
        }

        /// <summary>The caster's mic among OBS's inputs: an audio input capture, preferring one called mic.</summary>
        public static string? PickMic(IEnumerable<(string Name, string Kind)> inputs)
        {
            var caps = inputs.Where(i => !i.Name.StartsWith("TT ", StringComparison.Ordinal) && i.Kind is "wasapi_input_capture" or "coreaudio_input_capture" or "pulse_input_capture" or "alsa_input_capture").ToList();
            return caps.FirstOrDefault(i => i.Name.Contains("mic", StringComparison.OrdinalIgnoreCase)).Name ?? caps.FirstOrDefault().Name;
        }

        // ---- Level meters (OBS's InputVolumeMeters event) ------------------------------------------------
        private readonly Dictionary<string, (double Db, DateTime At)> _levels = new Dictionary<string, (double, DateTime)>(StringComparer.OrdinalIgnoreCase);

        private void OnMeters(JsonElement data)
        {
            if (!data.TryGetProperty("inputs", out var list) || list.ValueKind != JsonValueKind.Array) return;
            var now = DateTime.UtcNow;
            lock (_levels)
                foreach (var i in list.EnumerateArray())
                {
                    string name = i.TryGetProperty("inputName", out var n) ? n.GetString() ?? "" : "";
                    double peak = 0;
                    if (i.TryGetProperty("inputLevelsMul", out var ch) && ch.ValueKind == JsonValueKind.Array)
                        foreach (var c in ch.EnumerateArray())
                            if (c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 1 && c[1].ValueKind == JsonValueKind.Number) peak = Math.Max(peak, c[1].GetDouble());
                    _levels[name] = (peak <= 0.000001 ? -100 : Math.Round(20 * Math.Log10(peak), 1), now);
                }
        }

        /// <summary>An input's level now (dB, peak), or null when OBS hasn't sent one lately.</summary>
        public double? Level(string input)
        {
            lock (_levels) return _levels.TryGetValue(input, out var l) && (DateTime.UtcNow - l.At).TotalSeconds < 2 ? l.Db : null;
        }

        /// <summary>The ducking compressor on a voice source, keyed to the caster's mic; removed when there's none.</summary>
        private async Task DuckAsync(ObsClient obs, string source, string duck)
        {
            var list = await obs.RequestAsync("GetSourceFilterList", new { sourceName = source }).ConfigureAwait(false);
            bool has = list.GetProperty("filters").EnumerateArray().Any(f => f.GetProperty("filterName").GetString() == DuckFilter);
            if (duck.Length == 0)
            {
                if (has) await obs.RequestAsync("RemoveSourceFilter", new { sourceName = source, filterName = DuckFilter }).ConfigureAwait(false);
                return;
            }
            var settings = new { ratio = Settings.Voice.DuckRatio, threshold = Settings.Voice.DuckThreshold, attack_time = 6, release_time = 450, output_gain = 0.0, sidechain_source = duck };
            if (has) await obs.RequestAsync("SetSourceFilterSettings", new { sourceName = source, filterName = DuckFilter, filterSettings = settings, overlay = true }).ConfigureAwait(false);
            else await obs.RequestAsync("CreateSourceFilter", new { sourceName = source, filterName = DuckFilter, filterKind = "compressor_filter", filterSettings = settings }).ConfigureAwait(false);
        }

        /// <summary>Whose voice is heard for what's on stream (null: nothing, e.g. a replay).</summary>
        public string? LoudVoice(OnAir? air)
        {
            var v = Settings.Voice;
            if (!v.On || v.MuteAll) return null;
            if (v.Pin.Length > 0) return v.Pin;
            if (air == null || air.Layout is "replay" or "intermission" or "slate" or "none") return null;
            return air.Slots.FirstOrDefault(s => s != null);
        }

        /// <summary>Mutes every lobby voice but the one heard, and sets each one's volume and delay (only what changed).</summary>
        public async Task SetVoicesAsync(OnAir? air)
        {
            _voiceAir = air;
            var obs = _obs;
            if (obs == null) return;
            string? loud = LoudVoice(air);
            foreach (var name in _voiceInputs.ToList())
            {
                string lobby = VoiceLobby(name);
                var want = (Muted: !string.Equals(lobby, loud, StringComparison.OrdinalIgnoreCase),
                            Db: Settings.Voice.Volume.TryGetValue(lobby, out var db) ? db : 0,
                            Offset: Settings.Voice.Offset.TryGetValue(lobby, out var ms) ? ms : 0);
                _voiceSet.TryGetValue(name, out var was);
                bool known = _voiceSet.ContainsKey(name);
                try
                {
                    if (!known || was.Muted != want.Muted) await obs.RequestAsync("SetInputMute", new { inputName = name, inputMuted = want.Muted }).ConfigureAwait(false);
                    if (!known || was.Db != want.Db) await obs.RequestAsync("SetInputVolume", new { inputName = name, inputVolumeDb = Math.Max(-100, Math.Min(26, want.Db)) }).ConfigureAwait(false);
                    if (!known || was.Offset != want.Offset) await obs.RequestAsync("SetInputAudioSyncOffset", new { inputName = name, inputAudioSyncOffset = Math.Max(-950, Math.Min(20000, want.Offset)) }).ConfigureAwait(false);
                    _voiceSet[name] = want;
                }
                catch (ObsException) { _voiceInputs.Remove(name); _voiceSet.Remove(name); }    // removed in OBS: built again next time
            }
        }

        /// <summary>A change from the caster tab (pin, mute all, volume, delay, ducking): saved and applied now.</summary>
        public async Task VoiceChangedAsync(bool duck = false)
        {
            Save();
            if (duck) _duckWas = "\u0000";
            await EnsureVoiceAsync().ConfigureAwait(false);
            await SetVoicesAsync(_voiceAir ?? _desk.OnAir).ConfigureAwait(false);
        }

        /// <summary>The Mute all key that works from any window (set by the app).</summary>
        public string? MuteHotkey { get; set; }

        public object VoiceStatus() => new
        {
            Settings.Voice.On, Settings.Voice.MuteAll, Settings.Voice.Pin, Settings.Voice.DuckUnder,
            Volume = Settings.Voice.Volume, Offset = Settings.Voice.Offset,
            Heard = LoudVoice(_voiceAir ?? _desk.OnAir),
            // What each lobby's voice source in OBS is putting out (muted ones still show their level).
            Levels = _voiceInputs.GroupBy(VoiceLobby, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => Level(g.First()), StringComparer.OrdinalIgnoreCase),
            Hotkey = MuteHotkey,
            Sources = _voiceInputs.Select(VoiceLobby).OrderBy(x => x).ToList(),
            Problem = VoiceProblem,
            Inputs = InputNames,
        };
    }
}
