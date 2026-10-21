using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Settings → Save support bundle: when something goes wrong at an event, one click puts everything
    /// needed to work out why in one zip on the Desktop (RedAlert-support-….zip), to send as it is. What
    /// goes in, and what's taken out, is <see cref="RedAlertSupport"/>; this adds what only the running
    /// app knows: the versions, what the desk, OBS, the voice and the lobbies' health were doing at that
    /// moment, the warnings the Live desk was showing, and the Stream Deck's port.
    /// </summary>
    public sealed partial class BroadcastServer
    {
        /// <summary>How many of the log's latest warnings and errors the summary repeats.</summary>
        private const int SummaryLogLines = 40;

        /// <summary>Where the zip goes: the folder set for tests, else the Desktop, else next to the settings. True when it's the Desktop.</summary>
        private (string? Folder, bool Desktop) SupportFolder()
        {
            if (!string.IsNullOrEmpty(_env.SupportFolder)) return (_env.SupportFolder, false);
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) return (desktop, true);
            }
            catch (Exception) { }
            return (SideFolder(), false);
        }

        private string? ObsFolder()
        {
            if (!string.IsNullOrEmpty(_env.ObsFolder)) return _env.ObsFolder;
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrEmpty(roaming) ? null : Path.Combine(roaming, "obs-studio");
        }

        /// <summary>
        /// Saves the support bundle. <paramref name="pageWarnings"/> are the warnings the Live desk showed
        /// (worked out on the page), <paramref name="window"/> the window's browser version (WebView2).
        /// </summary>
        internal async Task<object> SaveSupportBundleAsync(IReadOnlyList<string> pageWarnings, string? window)
        {
            var (folder, desktop) = SupportFolder();
            if (folder == null) return new { ok = false, message = "Nowhere to save the support bundle." };
            try
            {
                var bundle = RedAlertSupport.FromFolders(SideFolder(), ObsFolder());
                // The hosts' VDO.Ninja links carry their passwords: those go wherever they turn up.
                var caster = _caster;
                if (caster != null)
                    foreach (var link in caster.ObsLinks().Concat(caster.VoiceLinks()).Concat(caster.CamLinks()).Concat(caster.DataLinks()))
                        bundle.AddSecretsFromLink(link.Url);
                // The Watch page's code, as it is now (saved or not).
                bundle.AddSecret(_watch?.Code);
                bundle.Add("versions.txt", await VersionsTextAsync(window).ConfigureAwait(false), "Versions: Red Alert, Windows, the window, OBS and obs-websocket, the Source Record plugin, ffmpeg.");
                bundle.Add("summary.txt", SupportSummary(pageWarnings), "What was going on when this was saved: warnings, problems, the desk, OBS, lobby voice, lobby health, and the log's latest warnings.");
                bundle.Add("state.json", SupportState(), "The same as summary.txt, as Red Alert holds it (for a closer look).");
                bundle.Add("stream-deck.txt", DeckText(), "The Stream Deck: the port Red Alert is on, whether a key is set (the key itself is removed), and the lobby numbers.");
                var now = DateTime.Now;
                Directory.CreateDirectory(folder);
                string path = RedAlertSupport.FreePath(folder, now);
                await Task.Run(() => bundle.Save(path, _env.Version, now)).ConfigureAwait(false);
                string name = Path.GetFileName(path);
                Log.Info("Saved a support bundle: " + name);
                try { _env.Open(folder); } catch (Exception) { }
                return new
                {
                    ok = true,
                    file = name,
                    message = $"Saved {name} {(desktop ? "on the Desktop" : "in " + folder)}. Codes, passwords and keys are taken out: send it as it is, with a line on what went wrong and when.",
                };
            }
            catch (Exception e)
            {
                Log.Warn("The support bundle wasn't saved: " + e.Message);
                return new { ok = false, message = "Couldn't save the support bundle: " + e.Message };
            }
        }

        // ---- versions.txt -------------------------------------------------------------------------

        private async Task<string> VersionsTextAsync(string? window)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Red Alert {(_env.Version.Length > 0 ? _env.Version : "(version unknown)")}");
            sb.AppendLine($"Saved {DateTime.Now:yyyy-MM-dd HH:mm:ss} (this PC's time, UTC{DateTimeOffset.Now:zzz})");
            sb.AppendLine($"System: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), Red Alert running as {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
            if (!string.IsNullOrWhiteSpace(window)) sb.AppendLine("Window (WebView2): " + OneLine(window, 300));
            var obs = _obs;
            if (obs == null) sb.AppendLine("OBS: Red Alert is locked (no administration code), so it isn't connecting to OBS.");
            else if (!obs.Connected) sb.AppendLine("OBS: not connected" + (obs.Problem != null ? " (" + OneLine(obs.Problem) + ")" : "."));
            else
            {
                var (o, ws, platform) = await obs.VersionsAsync().ConfigureAwait(false);
                sb.AppendLine($"OBS: {o ?? "version unknown"}");
                sb.AppendLine($"obs-websocket: {ws ?? "version unknown"}");
                if (platform != null) sb.AppendLine("OBS runs on: " + platform);
            }
            if (obs != null)
                sb.AppendLine("Source Record plugin (replays): " + (obs.SourceRecord switch
                {
                    true => "found",
                    false => "NOT installed: replays can't be saved",
                    _ => "not known yet (OBS isn't connected, or its setup check hasn't run)",
                }));
            if (_montages != null) sb.AppendLine("ffmpeg (montages, simulation clips): " + (_montages.Builder.Ffmpeg != null ? "found" : "not found"));
            var tw = _twitch;
            if (tw != null)
                sb.AppendLine("Twitch: " + (tw.Settings.Off ? "off" : tw.Settings.TestMode ? "test mode (nothing sent)" : tw.Auth.Token == null ? "not signed in" : tw.Connected ? "signed in and connected" : "signed in, not connected"));
            sb.AppendLine($"Red Alert's port: {Port}{(Port != DefaultPort ? $" (the usual {DefaultPort} was taken)" : "")}");
            sb.AppendLine(TeamWatchLine());
            return sb.ToString();
        }

        // ---- summary.txt and state.json -------------------------------------------------------------

        private static JsonElement? Element(Func<object?> make)
        {
            try { var o = make(); return o == null ? null : JsonSerializer.SerializeToElement(o, Json); }
            catch (Exception) { return null; }
        }

        private static string? S(JsonElement? e, string name) =>
            e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "yes",
                JsonValueKind.False => "no",
                _ => null,
            } : null;

        private static JsonElement? P(JsonElement? e, string name) =>
            e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

        private static bool B(JsonElement? e, string name) => P(e, name) is { ValueKind: JsonValueKind.True };

        private static IEnumerable<JsonElement> Items(JsonElement? e) => e is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

        private static string Join(JsonElement? e) => string.Join(", ", Items(e).Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ValueKind == JsonValueKind.Null ? "empty" : x.GetRawText()));

        private static string OneLine(string text, int max = 400)
        {
            string t = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length > max ? t.Substring(0, max) + "…" : t;
        }

        /// <summary>What the desk, OBS, the voice and the lobbies' health hold now, as JSON.</summary>
        private (JsonElement? Desk, JsonElement? Obs, JsonElement? Voice, JsonElement? VoiceObs) SupportPieces()
        {
            var desk = _desk;
            var obs = _obs;
            return (Element(() => desk?.State()), Element(() => obs?.Status()), Element(() => desk?.VoiceState()), Element(() => obs?.VoiceStatus()));
        }

        /// <summary>
        /// The desk for the bundle, without what gives the games away (the cards and earlier plays, who
        /// is in each lobby and who the impostors are): a bundle may be sent in the middle of an event,
        /// to someone who plays in it.
        /// </summary>
        private static JsonNode? DeskForBundle(JsonElement? desk)
        {
            if (desk is not { ValueKind: JsonValueKind.Object } d || JsonNode.Parse(d.GetRawText()) is not JsonObject node) return null;
            node.Remove("cards");
            node.Remove("history");
            // The team's suggestions are in their own words, which could name an impostor.
            node.Remove("suggestions");
            if (node["lobbies"] is JsonArray lobbies)
                foreach (var l in lobbies.OfType<JsonObject>())
                    foreach (var spoiler in new[] { "players", "people", "line" }) l.Remove(spoiler);
            return node;
        }

        private string SupportState()
        {
            var (desk, obs, voice, voiceObs) = SupportPieces();
            var tw = _twitch;
            var state = new
            {
                app = _env.Version,
                unlockedFor = _organizer?.Tournament,
                discordProblem = _organizer?.Problem,
                casterProblem = _caster?.Problem,
                desk = DeskForBundle(desk), obs,
                voice = new { lobbies = voice, obs = voiceObs },
                deck = DeckPort(),
                twitch = tw == null ? null : new { tw.Settings.Off, tw.Settings.TestMode, SignedIn = tw.Auth.Token != null, tw.Connected, tw.Problem },
            };
            return JsonSerializer.Serialize(state, new JsonSerializerOptions(Json) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        private string SupportSummary(IReadOnlyList<string> pageWarnings)
        {
            var sb = new StringBuilder();
            void Line(string text) => sb.AppendLine(text);
            void Section(string title, Action fill)
            {
                sb.AppendLine().AppendLine("== " + title + " ==");
                try { fill(); } catch (Exception e) { Line("(couldn't be read: " + e.Message + ")"); }
            }

            var (desk, obs, voice, voiceObs) = SupportPieces();
            Line($"Red Alert {_env.Version}, saved {DateTime.Now:yyyy-MM-dd HH:mm:ss} (this PC's time) / {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC.");
            Line(_organizer == null ? "LOCKED: no administration code, so the desk, OBS and the lobbies aren't running." : $"Unlocked for {_organizer.Tournament}.");

            Section("Warnings the Live desk was showing", () =>
            {
                if (pageWarnings.Count == 0) Line("None (or the window didn't send them).");
                foreach (var w in pageWarnings) Line("- " + OneLine(w));
            });

            Section("Problems Red Alert knows about", () =>
            {
                var list = new List<string>();
                void Add(string what, string? problem) { if (!string.IsNullOrWhiteSpace(problem)) list.Add($"{what}: {OneLine(problem!)}"); }
                Add("Discord (results channel)", _organizer?.Problem);
                Add("Caster pages", _caster?.Problem);
                Add("Desk settings", S(desk, "problem"));
                Add("Mute-all key", HotkeyProblem);
                Add("Port", PortWarning);
                Add("Graphics settings", _broadcast?.Settings.Problem);
                Add("Sponsors", _desk?.Sponsors.Problem);
                Add("Roster", _desk?.Roster.Problem);
                Add("Replays", _replays?.Problem);
                Add("Twitch", _twitch?.Problem);
                var o = _obs;
                if (o != null)
                {
                    Add("OBS", o.Problem);
                    Add("OBS switch", o.SwitchProblem);
                    Add("OBS replays", o.ReplayProblem);
                    Add("OBS player camera", o.CamProblem);
                    Add("OBS swoosh", o.SwooshProblem);
                    Add("OBS lobby voice", o.VoiceProblem);
                    Add("OBS lobby voice (note)", o.VoiceNote);
                    foreach (var kv in o.SourceProblems) Add($"OBS source for {kv.Key}", kv.Value);
                    if (o.Connected)
                    {
                        foreach (var (level, text) in o.OutputWarnings()) Add(level == "bad" ? "Stream" : "OBS output", text);
                        if (o.DesktopAudioOn.Count > 0) Add("Desktop Audio", string.Join(", ", o.DesktopAudioOn) + " goes to the stream");
                        foreach (var c in o.Checks.Where(c => !c.Ok)) Add("OBS setup check", c.Title + (c.Detail.Length > 0 ? ": " + c.Detail : ""));
                    }
                }
                if (list.Count == 0) Line("None.");
                foreach (var x in list) Line("- " + x);
            });

            Section("Desk", () =>
            {
                if (desk == null) { Line("Not running (locked)."); return; }
                Line("Simulation: " + (B(desk, "simulating") ? "ON (SIM-1 to SIM-4 are fake lobbies)" : "off"));
                var air = P(desk, "onAir");
                Line($"On stream: {S(air, "layout") ?? "none"} · {(Items(P(air, "slots")).Any() ? Join(P(air, "slots")) : "no lobby")} (by {S(air, "by") ?? "?"}{(S(air, "scene") is { } sc ? ", OBS scene " + sc : "")}{(S(air, "since") is { } since ? ", since " + since : "")})");
                var pvw = P(desk, "preview");
                Line($"Preview: {(Items(P(pvw, "slots")).Any() ? $"{S(pvw, "layout")} · {Join(P(pvw, "slots"))}" : "empty")}");
                var hold = P(desk, "hold");
                Line("Hold: " + (B(hold, "on") ? $"ON{(S(hold, "why") is { } why ? " (" + why + ")" : "")}{(P(hold, "heldBack") is { } hb ? ", held back: " + S(hb, "text") : "")}" : "off"));
                Line("Panic: " + (B(desk, "panic") ? "ON" : "off"));
                if (P(desk, "autoSwitched") is { } asw) Line($"Auto switch: {S(asw, "text")} ({S(asw, "at")})");
                var inter = P(desk, "intermission");
                Line($"Intermission: {(B(inter, "on") ? "on stream" : "off")}, auto {(B(inter, "auto") ? "on" : "off")}; auto grid {(B(desk, "autoGrid") ? "on" : "off")}");
                if (P(desk, "break") is { } brk) Line($"Sponsor break: {S(brk, "sponsor")} ({S(brk, "left")} s left)");
                Line("Lobbies (number · name · sending · phase · round/game · health · score):");
                if (!Items(P(desk, "lobbies")).Any()) Line("  none yet");
                foreach (var l in Items(P(desk, "lobbies")))
                    Line($"  {S(l, "no")} · {S(l, "lobby")} · {(B(l, "online") ? "online" : "OFFLINE")} · {S(l, "phase")} · round {S(l, "round")}{(S(l, "game") is { } g ? " game " + g : "")} · {S(l, "health")} · {S(l, "score")}{(S(l, "onAir") is { } oa ? " · " + oa : "")}");
            });

            Section("OBS", () =>
            {
                if (obs == null) { Line("Not running (locked)."); return; }
                Line(B(obs, "connected") ? $"Connected to OBS {S(obs, "version")} at {S(obs, "host")}:{S(obs, "port")}, scene {S(obs, "scene") ?? "?"}, canvas {S(obs, "canvas")}" : "NOT connected" + (S(obs, "problem") is { } p ? ": " + p : "."));
                if (S(obs, "switchingTo") is { } to) Line("Waiting for OBS to switch to " + to);
                var output = P(obs, "output");
                if (B(output, "known"))
                    Line($"Streaming: {(B(output, "streaming") ? "yes" : "no")}{(B(output, "reconnecting") ? " (RECONNECTING)" : "")} · recording: {(B(output, "recording") ? "yes" : "no")} · {S(output, "kbps") ?? "?"} kbps · dropped {S(output, "droppedPct") ?? "?"}% · CPU {S(output, "cpu") ?? "?"}% · {S(output, "fps") ?? "?"} fps · disk {S(output, "diskGb") ?? "?"} GB free");
                var swoosh = P(obs, "swoosh");
                Line("Swoosh: " + (B(swoosh, "on") ? "on" : "off") + (S(swoosh, "problem") is { } sp ? " (" + sp + ")" : ""));
                Line("Source Record (replays): " + (S(obs, "sourceRecord") switch { "yes" => "found", "no" => "NOT installed", _ => "not known yet" }));
                var sources = P(obs, "sources") is { ValueKind: JsonValueKind.Object } src ? src.EnumerateObject().Select(x => $"{x.Name} → {x.Value.GetString()}").ToList() : new List<string>();
                Line("Lobby sources: " + (sources.Count > 0 ? string.Join(", ", sources) : "none yet"));
                if (P(obs, "missing") is { ValueKind: JsonValueKind.Object } miss)
                    foreach (var m in miss.EnumerateObject()) Line($"MISSING on stream: {m.Name}: {m.Value.GetString()}");
                foreach (var c in Items(P(obs, "checks"))) Line($"Setup check: {(B(c, "ok") ? "ok" : "LOOK")} · {S(c, "title")}");
            });

            Section("Lobby voice", () =>
            {
                if (voiceObs == null) { Line("Not running (locked)."); return; }
                Line($"On stream: {(B(voiceObs, "on") ? "yes" : "NO")} · mute all: {(B(voiceObs, "muteAll") ? "MUTED" : "no")} · listening to: {(S(voiceObs, "pin") is { Length: > 0 } pin ? pin : "nobody (follows the picture)")} · ducks under: {(S(voiceObs, "duckUnder") is { Length: > 0 } duck ? duck : "nothing")}");
                Line($"Heard on stream now: {S(voiceObs, "heard") ?? "nobody"} · mute-all key: {S(voiceObs, "hotkey") ?? "?"}");
                var levels = P(voiceObs, "levels") is { ValueKind: JsonValueKind.Object } lv ? lv.EnumerateObject().Select(x => $"{x.Name} {x.Value.GetRawText()}").ToList() : new List<string>();
                Line("Levels in OBS (dB): " + (levels.Count > 0 ? string.Join(", ", levels) : "no voice sources in OBS"));
                Line("Each referee's Button (sending · Discord · Among Us · levels dB: Discord / Among Us / Sent):");
                if (!Items(voice).Any()) Line("  none yet");
                foreach (var v in Items(voice))
                    Line(!B(v, "reported") ? $"  {S(v, "lobby")} · hasn't reported its voice"
                        : $"  {S(v, "lobby")} · {(B(v, "sending") ? "sending" : "NOT sending")}{(B(v, "online") ? "" : $" (last heard {S(v, "ago")} s ago)")} · Discord {S(v, "discord") ?? "?"} · Among Us {S(v, "game") ?? "?"} · {S(v, "voiceDb")} / {S(v, "gameDb")} / {S(v, "outDb") ?? "n/a"}{(S(v, "problem") is { } vp ? " · " + vp : "")}");
            });

            Section("Lobby health", () =>
            {
                var health = P(desk, "health");
                if (health == null) { Line("Not running (locked)."); return; }
                if (!Items(P(health, "lobbies")).Any()) Line("No lobby has sent anything yet.");
                foreach (var h in Items(P(health, "lobbies")))
                {
                    string problems = Join(P(h, "problems"));
                    Line($"{S(h, "lobby")}: {S(h, "level")?.ToUpperInvariant()}{(problems.Length > 0 ? " (" + problems + ")" : "")} · data {S(h, "dataAge") ?? "?"} s old · video {S(h, "video")} · audio {S(h, "audio")} · lag {S(h, "lagMs") ?? "?"} ms{(S(h, "down") is { } d ? $" · down {d} s" : "")}{(S(h, "update") is { } u ? " · " + u : "")}");
                }
                foreach (var i in Items(P(health, "interrupted")))
                    Line($"Interrupted game: {S(i, "lobby")} {S(i, "game")} (round {S(i, "round")}): {S(i, "why")} · {S(i, "decision") ?? "not decided"} · {S(i, "at")}");
            });

            Section($"Latest warnings and errors in {RedAlertSupport.LogName} (newest last)", () =>
            {
                var lines = LogWarnings(SummaryLogLines);
                if (lines.Count == 0) Line("None.");
                foreach (var l in lines) Line(l);
            });
            return sb.ToString();
        }

        /// <summary>The latest WARN and ERROR lines of red-alert.log (and the one before it), oldest first.</summary>
        private List<string> LogWarnings(int count)
        {
            var found = new List<string>();
            string? path = Log.FilePath;
            if (path == null) return found;
            foreach (var file in new[] { path + ".1", path })
            {
                if (!File.Exists(file)) continue;
                try
                {
                    foreach (var line in RedAlertSupport.ReadTail(file).Split('\n'))
                        if (line.Contains(" WARN ", StringComparison.Ordinal) || line.Contains(" ERROR ", StringComparison.Ordinal)) found.Add(line.TrimEnd('\r'));
                }
                catch (Exception) { }
            }
            return found.Skip(Math.Max(0, found.Count - count)).ToList();
        }

        // ---- stream-deck.txt ------------------------------------------------------------------------

        private string DeckText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Red Alert listens on 127.0.0.1:{Port} (the usual port is {DefaultPort}; the Stream Deck plugin also looks on {DefaultPort + 1}-{DefaultPort + 3}).");
            if (PortWarning is { } w) sb.AppendLine(w);
            sb.AppendLine(string.IsNullOrEmpty(_settings.DeckKey)
                ? "Stream Deck key: none made yet (no Stream Deck link or key has been copied from Settings)."
                : "Stream Deck key: set (left out of this bundle). New key in Settings → Stream Deck makes a new one.");
            var desk = _desk;
            if (desk == null) sb.AppendLine("Lobby numbers: Red Alert is locked.");
            else
            {
                var numbers = desk.Numbers();
                sb.AppendLine(numbers.Count == 0 ? "Lobby numbers: none yet." : "Lobby numbers (what the Stream Deck's lobby keys mean):");
                foreach (var (lobby, no, online) in numbers.OrderBy(n => n.No)) sb.AppendLine($"  {no} · {lobby}{(online ? "" : " (not sending)")}");
            }
            return sb.ToString();
        }
    }
}
