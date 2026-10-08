using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Sources OBS removed but still keeps alive in the background (something still holds them):
    /// GetInputList lists them, so they look present, but OBS won't put them in a scene ("Tried to
    /// add a removed source to a scene") and won't make a new one under the same name. Red Alert
    /// then makes a fresh source under a new name ("TT Lobby LJ 2"), uses that from then on, and
    /// carries on. Restarting OBS isn't needed.
    /// </summary>
    public sealed partial class ObsDirector
    {
        /// <summary>Inputs known to be removed in OBS (by Red Alert, or found refusing to go in a scene) even if OBS still lists them.</summary>
        private readonly HashSet<string> _dead = new HashSet<string>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (string Why, DateTime RetryAt)> _sourceProblems = new ConcurrentDictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);
        /// <summary>How long a lobby whose source couldn't be made waits before the next try (Rebuild tries at once).</summary>
        public static TimeSpan SourceRetry { get; set; } = TimeSpan.FromSeconds(30);
        private const int MaxRenames = 3;

        /// <summary>Lobbies whose picture couldn't be put in OBS, and why.</summary>
        public IReadOnlyDictionary<string, string> SourceProblems =>
            _sourceProblems.ToDictionary(kv => kv.Key, kv => kv.Value.Why, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Why a lobby isn't in OBS (for the Live desk when it's on stream), or null when its picture is there.
        /// Only asked while connected.
        /// </summary>
        public string? SourceIssue(string lobby)
        {
            if (!Connected) return null;
            if (_sourceProblems.TryGetValue(lobby, out var p)) return p.Why;
            if (!Settings.Sources.TryGetValue(lobby, out var name) || _dead.Contains(name))
                return "it has no picture source (is its host sending video?)";
            return null;
        }

        /// <summary>The name in use for one of Red Alert's own sources ("TT Broadcast", "TT Voice LJ"…): its own, or the fresh one it moved to.</summary>
        public string Live(string root) => Settings.Renamed.TryGetValue(root, out var n) && n.Length > 0 ? n : root;

        private void Renamed(string root, string name)
        {
            if (name == root) Settings.Renamed.Remove(root); else Settings.Renamed[root] = name;
            Save();
        }

        /// <summary>The next free name after <paramref name="root"/>: "root 2", "root 3"…</summary>
        private string FreshName(string root, ICollection<string> inputs)
        {
            for (int n = 2; ; n++)
            {
                string name = $"{root} {n}";
                if (!inputs.Contains(name) && !_dead.Contains(name)) return name;
            }
        }

        /// <summary>The input is there to use: OBS lists it and it isn't one known to be removed.</summary>
        private bool Usable(string name, ICollection<string> inputs) => inputs.Contains(name) && !_dead.Contains(name);

        /// <summary>The name to make a missing input under: its own, unless OBS still holds a removed one by that name.</summary>
        private string Creatable(string name, string root, ICollection<string> inputs) =>
            inputs.Contains(name) || _dead.Contains(name) ? FreshName(root, inputs) : name;

        private static async Task<HashSet<string>> InputNamesAsync(ObsClient obs) =>
            (await obs.RequestAsync("GetInputList").ConfigureAwait(false)).GetProperty("inputs").EnumerateArray()
                .Select(i => i.GetProperty("inputName").GetString() ?? "").ToHashSet();

        /// <summary>Makes an input in a scene; answers with its item there.</summary>
        private static async Task<int> CreateInAsync(ObsClient obs, string scene, string name, string kind, object settings, bool enabled)
        {
            var made = await obs.RequestAsync("CreateInput", new { sceneName = scene, inputName = name, inputKind = kind, inputSettings = settings, sceneItemEnabled = enabled }).ConfigureAwait(false);
            if (made.ValueKind == JsonValueKind.Object && made.TryGetProperty("sceneItemId", out var id) && id.ValueKind == JsonValueKind.Number) return id.GetInt32();
            return (await ItemsAsync(obs, scene).ConfigureAwait(false)).First(i => i.Source == name).Id;
        }

        /// <summary>
        /// Puts an input in a scene. If OBS refuses for an input it still lists, that input is a
        /// removed one it keeps in the background: <paramref name="create"/> makes a fresh one under a
        /// new name in this scene, and that name is returned for the caller to use from now on.
        /// </summary>
        private async Task<(int Id, string Name)> PlaceAsync(ObsClient obs, string scene, string name, string root, bool enabled,
            HashSet<string> inputs, Func<string, string, Task<int>> create)
        {
            try
            {
                var r = await obs.RequestAsync("CreateSceneItem", new { sceneName = scene, sourceName = name, sceneItemEnabled = enabled }).ConfigureAwait(false);
                return (r.GetProperty("sceneItemId").GetInt32(), name);
            }
            catch (ObsException) when (inputs.Contains(name))
            {
                _dead.Add(name);
                string fresh = FreshName(root, inputs);
                int id = await create(fresh, scene).ConfigureAwait(false);
                inputs.Add(fresh);
                return (id, fresh);
            }
        }

        /// <summary>
        /// One of Red Alert's own inputs in each of <paramref name="scenes"/> (made where missing),
        /// moving to a fresh name if OBS holds a removed one under the current name. Answers with
        /// the name in use and its item in each scene.
        /// </summary>
        private async Task<(string Name, Dictionary<string, int> Items, bool Added)> EnsureInputAsync(ObsClient obs, string root, string current, IEnumerable<string> scenes,
            HashSet<string> inputs, string kind, object settings, bool enabled, Func<string, Task>? existing = null)
        {
            Task<int> Create(string n, string scene) => CreateInAsync(obs, scene, n, kind, settings, enabled);
            string name = current;
            var list = scenes.ToList();
            var ids = new Dictionary<string, int>();
            bool added = false;
            if (Usable(name, inputs) && existing != null)
            {
                // A listed input that won't even answer is a removed one too.
                try { await existing(name).ConfigureAwait(false); }
                catch (ObsException) { _dead.Add(name); }
            }
            if (!Usable(name, inputs))
            {
                if (list.Count == 0) return (name, ids, false);
                added = true;
                name = Creatable(name, root, inputs);
                ids[list[0]] = await Create(name, list[0]).ConfigureAwait(false);
                inputs.Add(name);
            }
            for (int renames = 0; ; renames++)
            {
                bool moved = false;
                foreach (var scene in list)
                {
                    var mine = (await ItemsAsync(obs, scene).ConfigureAwait(false)).FirstOrDefault(i => i.Source == name);
                    if (mine != null) { ids[scene] = mine.Id; continue; }
                    var (id, placed) = await PlaceAsync(obs, scene, name, root, enabled, inputs, Create).ConfigureAwait(false);
                    ids[scene] = id;
                    added = true;
                    if (placed != name) { name = placed; moved = true; break; }
                }
                if (!moved) break;
                if (renames + 1 >= MaxRenames) throw new InvalidOperationException($"OBS keeps refusing {root}; restart OBS.");
                ids.Clear();        // the earlier scenes need the fresh one too
            }
            return (name, ids, added);
        }
    }
}
