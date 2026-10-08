using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>Simulation's leftovers in OBS: the fake lobbies' picture and voice sources.</summary>
    public sealed partial class ObsDirector
    {
        /// <summary>
        /// Removes the simulator's lobby and voice sources from OBS (and from every TT scene with
        /// them): the ones named for fake lobbies, and any still pointing at a stand-in page (left by
        /// an older version, or a crash mid-simulation). Never changes which scene is live.
        /// How many went.
        /// </summary>
        public async Task<int> ForgetSimAsync(Func<string, bool> which)
        {
            foreach (var k in Settings.Sources.Keys.Where(which).ToList()) Settings.Sources.Remove(k);
            foreach (var k in Settings.Voice.Volume.Keys.Where(which).ToList()) Settings.Voice.Volume.Remove(k);
            foreach (var k in Settings.Voice.Offset.Keys.Where(which).ToList()) Settings.Voice.Offset.Remove(k);
            if (which(Settings.Voice.Pin)) Settings.Voice.Pin = "";
            if (_voiceAir?.Slots.Any(x => x != null && which(x)) == true) _voiceAir = null;
            Save();
            var obs = _obs;
            if (obs == null) return 0;
            int gone = 0;
            await _busy.WaitAsync().ConfigureAwait(false);
            try
            {
                var inputs = (await obs.RequestAsync("GetInputList").ConfigureAwait(false)).GetProperty("inputs").EnumerateArray()
                    .Select(i => i.GetProperty("inputName").GetString() ?? "").ToList();
                foreach (var name in inputs)
                {
                    string? lobby = name.StartsWith(Settings.SourcePrefix, StringComparison.Ordinal) ? name.Substring(Settings.SourcePrefix.Length)
                        : name.StartsWith(VoicePrefix, StringComparison.Ordinal) ? name.Substring(VoicePrefix.Length) : null;
                    if (lobby == null || _dead.Contains(name)) continue;
                    bool fake = which(lobby);
                    if (!fake)
                    {
                        string url = "";
                        try
                        {
                            var set = await obs.RequestAsync("GetInputSettings", new { inputName = name }).ConfigureAwait(false);
                            url = set.TryGetProperty("inputSettings", out var s) && s.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
                        }
                        catch (ObsException) { continue; }      // listed but gone (OBS keeps removed sources around): nothing to remove
                        fake = IsStandIn(url);
                    }
                    if (!fake) continue;
                    try { await obs.RequestAsync("RemoveInput", new { inputName = name }).ConfigureAwait(false); gone++; }
                    catch (ObsException) { }
                    // OBS can keep a removed source in the background and go on listing it: never use that name again this session.
                    _dead.Add(name);
                    _voiceInputs.Remove(name);
                    _voiceSet.Remove(name);
                    _voiceLobby.Remove(name);
                    _filtered.Remove(name);
                    // A stand-in under a real lobby's name (older versions): that lobby gets a fresh source.
                    foreach (var k in Settings.Sources.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList()) Settings.Sources.Remove(k);
                    foreach (var k in Settings.Renamed.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList()) Settings.Renamed.Remove(k);
                }
            }
            finally { _busy.Release(); }
            Save();
            return gone;
        }

        /// <summary>A simulation stand-in page on the caster port (/sim or /simvoice), not a real lobby's video.</summary>
        public static bool IsStandIn(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost") && u.AbsolutePath is "/sim" or "/simvoice";
    }
}
