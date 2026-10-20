using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>
/// A pretend OBS speaking obs-websocket 5: password challenge, scenes, inputs, scene items with
/// transforms, mute, program scene, and the events for switching scenes.
/// </summary>
public sealed class FakeObs : IAsyncDisposable
{
    public sealed class Item { public int Id; public string Source = ""; public bool Enabled; public double X, Y, W, H; }

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private WebSocket? _ws;
    private readonly SemaphoreSlim _send = new(1, 1);
    private int _nextId = 1;
    private const string Salt = "c2FsdA==", Challenge = "Y2hhbGxlbmdl";

    public int Port { get; }
    public string Password = "secret";
    public readonly Dictionary<string, List<Item>> Scenes = new() { ["Starting soon"] = new() };
    public readonly Dictionary<string, Dictionary<string, object?>> Inputs = new();
    public readonly Dictionary<string, bool> Muted = new();
    public string Program = "Starting soon";
    public readonly List<string> Requests = new();
    // Source Record and media playback.
    public bool SourceRecordInstalled = true;
    public readonly Dictionary<string, Dictionary<string, JsonElement>> Filters = new();   // "source|filter" → settings
    public readonly List<string> Saved = new();
    public string MediaState = "OBS_MEDIA_STATE_NONE";
    public double MediaCursorMs, MediaDurationMs = 30000;
    public string? MediaFile;
    public int SwooshPlays;
    public readonly Dictionary<string, double> Volume = new();
    public readonly Dictionary<string, int> SyncOffset = new();
    public List<(string Name, string Kind)> Transitions = new() { ("Fade", "fade_transition"), ("Cut", "cut_transition") };
    public string CurrentTransition = "Fade";
    /// <summary>Like a real OBS with a transition: the scene changes (and says so) this long after it's asked.</summary>
    public int SwitchDelayMs;
    /// <summary>OBS doesn't switch at all (e.g. a studio-mode mix-up).</summary>
    public bool IgnoreSwitch;
    public string Collection = "Untitled", Profile = "Untitled";
    public readonly Dictionary<string, string> Kinds = new();
    public bool DelayEnable; public int DelaySec;
    public readonly HashSet<string> FilterKinds = new() { "source_record_filter", "compressor_filter" };
    public JsonElement TransitionSettings;
    /// <summary>
    /// Removed inputs OBS keeps alive in the background: still in GetInputList, but OBS won't put
    /// them in a scene ("Tried to add a removed source to a scene") or make a new one by that name.
    /// </summary>
    public readonly HashSet<string> Zombies = new();
    /// <summary>RemoveInput leaves the input a zombie (as a real OBS can, while something still holds it).</summary>
    public bool KeepRemoved;
    public int RefusedSceneItems;
    /// <summary>Requests that fail (OBS answering with an error).</summary>
    public readonly HashSet<string> Fail = new();
    /// <summary>Requests (sent on their own, not in a batch) that OBS never answers: a stuck OBS.</summary>
    public readonly HashSet<string> Hang = new();
    /// <summary>Source Record's buffer isn't running: a save answers success = false until it's started.</summary>
    public bool BufferStuck;
    public int BufferStarts;
    private bool _saved;
    // What OBS is sending out (GetStreamStatus, GetRecordStatus, GetStats), and its screenshots.
    public bool Streaming, Reconnecting, RecordingOn;
    public long StreamBytes, StreamSkipped, StreamFrames, RenderSkipped, RenderFrames;
    public double DiskMb = 500_000;
    public int Screenshots;
    /// <summary>Every SetInputMute, by source name, in the order they came.</summary>
    public readonly List<string> MuteLog = new();
    /// <summary>Audio monitoring per input (SetInputAudioMonitorType), and OBS's monitoring device (null: OBS's default).</summary>
    public readonly Dictionary<string, string> MonitorType = new();
    public string? MonitorDeviceId, MonitorDeviceName;
    /// <summary>Mutes and monitoring changes in the order they came: "mute NAME true", "monitor NAME TYPE".</summary>
    public readonly List<string> AudioLog = new();

    /// <summary>The stream starting or stopping in OBS, with its event.</summary>
    public Task SetStreaming(bool on, string? state = null) { lock (this) Streaming = on; return Event("StreamStateChanged", new { outputActive = on, outputState = state ?? (on ? "OBS_WEBSOCKET_OUTPUT_STARTED" : "OBS_WEBSOCKET_OUTPUT_STOPPED") }); }

    public FakeObs()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            _ws = ws;
            _ = Task.Run(() => Talk(ws));
        }
    }

    private async Task Send(WebSocket ws, object msg)
    {
        using var limit = new CancellationTokenSource(5000);    // never a test that waits forever on a stuck socket
        await _send.WaitAsync(limit.Token);
        try { await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(msg), WebSocketMessageType.Text, true, limit.Token); }
        finally { _send.Release(); }
    }

    private static async Task<JsonElement?> Receive(WebSocket ws)
    {
        var buf = new byte[65536];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, CancellationToken.None); } catch { return null; }
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private async Task Talk(WebSocket ws)
    {
        await Send(ws, new { op = 0, d = new { obsWebSocketVersion = "5.5.0", obsStudioVersion = "31.0.0", rpcVersion = 1, authentication = new { challenge = Challenge, salt = Salt } } });
        var identify = await Receive(ws);
        if (identify == null) return;
        string? auth = identify.Value.GetProperty("d").TryGetProperty("authentication", out var a) ? a.GetString() : null;
        if (auth != ObsClient.Auth(Password, Salt, Challenge))
        {
            await ws.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed.", new CancellationTokenSource(1000).Token);
            return;
        }
        await Send(ws, new { op = 2, d = new { negotiatedRpcVersion = 1 } });
        while (true)
        {
            var msg = await Receive(ws);
            if (msg == null) return;
            int op = msg.Value.GetProperty("op").GetInt32();
            var d = msg.Value.GetProperty("d");
            if (op == 8)
            {
                // A RequestBatch: each request in turn (its events as it's done), then one answer with every result.
                lock (this) Batches++;
                bool halt = d.TryGetProperty("haltOnFailure", out var h) && h.ValueKind == JsonValueKind.True;
                var results = new List<object>();
                foreach (var r in d.GetProperty("requests").EnumerateArray())
                {
                    string rt = r.GetProperty("requestType").GetString()!;
                    var rdata = r.TryGetProperty("requestData", out var x) ? x : default;
                    var (rok, rcomment, rresponse) = await Process(rt, rdata);
                    results.Add(new { requestType = rt, requestId = r.TryGetProperty("requestId", out var ri) ? ri.GetString() : null, requestStatus = new { result = rok, code = rok ? 100 : 600, comment = rcomment }, responseData = rresponse });
                    if (rok) await After(rt, rdata);
                    if (!rok && halt) break;
                }
                await Send(ws, new { op = 9, d = new { requestId = d.GetProperty("requestId").GetString(), results } });
                continue;
            }
            string type = d.GetProperty("requestType").GetString()!, id = d.GetProperty("requestId").GetString()!;
            var data = d.TryGetProperty("requestData", out var rd) ? rd : default;
            bool hang;
            lock (this) hang = Hang.Contains(type);
            if (hang) continue;
            var (ok, comment, response) = await Process(type, data);
            await Send(ws, new { op = 7, d = new { requestType = type, requestId = id, requestStatus = new { result = ok, code = ok ? 100 : 600, comment }, responseData = response } });
            if (ok) await After(type, data);
        }
    }

    /// <summary>Batches (RequestBatch messages) asked so far.</summary>
    public int Batches;

    private async Task<(bool Ok, string? Comment, object? Response)> Process(string type, JsonElement data)
    {
        if (type == "Sleep") { await Task.Delay(data.TryGetProperty("sleepMillis", out var ms) ? ms.GetInt32() : 0); return (true, null, null); }
        if (RequestDelayMs > 0) await Task.Delay(RequestDelayMs);
        lock (this)
        {
            Requests.Add(type);
            try { return (true, null, Handle(type, data)); }
            catch (Exception e) { return (false, e.Message, null); }
        }
    }

    /// <summary>Every request takes this long (a busy OBS).</summary>
    public int RequestDelayMs;

    /// <summary>The events a real OBS sends for a request that worked.</summary>
    private async Task After(string type, JsonElement data)
    {
        {
            bool ok = true;
            if (type is "CreateInput" or "CreateSceneItem")
                await Event("SceneItemCreated", new { sceneName = data.GetProperty("sceneName").GetString(), sourceName = type == "CreateInput" ? data.GetProperty("inputName").GetString() : data.GetProperty("sourceName").GetString() });
            if (type == "RemoveInput") await Event("InputRemoved", new { inputName = data.GetProperty("inputName").GetString() });
            if (type == "SetSceneItemIndex") await Event("SceneItemListReindexed", new { sceneName = data.GetProperty("sceneName").GetString() });
            if (type is "CreateScene" or "RemoveScene") await Event(type == "CreateScene" ? "SceneCreated" : "SceneRemoved", new { sceneName = data.GetProperty("sceneName").GetString() });
            if (ok && type == "SetCurrentProgramScene" && !IgnoreSwitch)
            {
                string want = data.GetProperty("sceneName").GetString()!;
                if (SwitchDelayMs > 0) _ = Task.Run(async () => { await Task.Delay(SwitchDelayMs); lock (this) Program = want; await Event("CurrentProgramSceneChanged", new { sceneName = want }); });
                else if (_switchedFrom != want) await Event("CurrentProgramSceneChanged", new { sceneName = want });
            }
            if (ok && type == "SetSceneItemEnabled")
                await Event("SceneItemEnableStateChanged", new { sceneName = data.GetProperty("sceneName").GetString(), sceneItemId = data.GetProperty("sceneItemId").GetInt32(), sceneItemEnabled = data.GetProperty("sceneItemEnabled").GetBoolean() });
            if (ok && type == "SetInputMute")
                await Event("InputMuteStateChanged", new { inputName = data.GetProperty("inputName").GetString(), inputMuted = data.GetProperty("inputMuted").GetBoolean() });
            if (ok && type == "CallVendorRequest" && _saved)
            {
                string source = data.GetProperty("requestData").GetProperty("source").GetString()!;
                string path = $"/clips/{source.Replace(' ', '_')}-{Saved.Count + 1}.mp4";
                lock (this) Saved.Add(path);
                await Task.Delay(50);
                await Event("VendorEvent", new { vendorName = "source-record", eventType = "replay_buffer_saved", eventData = new { path, filter = "TT Replay", source } });
            }
        }
    }

    private string _switchedFrom = "";
    private static string S(JsonElement d, string p) => d.GetProperty(p).GetString()!;

    private object? Handle(string type, JsonElement d)
    {
        if (Fail.Contains(type)) throw new Exception("OBS is busy.");
        switch (type)
        {
            case "GetVideoSettings": return new { baseWidth = 1920, baseHeight = 1080, outputWidth = 1920, outputHeight = 1080 };
            case "GetSceneList": return new { currentProgramSceneName = Program, scenes = Scenes.Keys.Select(k => new { sceneName = k }).ToList() };
            case "CreateScene":
                if (Scenes.ContainsKey(S(d, "sceneName"))) throw new Exception("exists");
                Scenes[S(d, "sceneName")] = new(); return null;
            case "GetInputList": return new { inputs = Inputs.Keys.Concat(Zombies).Select(k => new { inputName = k, inputKind = Kinds.GetValueOrDefault(k, "browser_source") }).ToList() };
            case "CreateInput":
            {
                string name = S(d, "inputName");
                if (Zombies.Contains(name)) throw new Exception("A source already exists by that input name.");
                Inputs[name] = JsonSerializer.Deserialize<Dictionary<string, object?>>(d.GetProperty("inputSettings").GetRawText())!;
                var item = new Item { Id = _nextId++, Source = name, Enabled = d.GetProperty("sceneItemEnabled").GetBoolean(), W = 1920, H = 1080 };
                Scenes[S(d, "sceneName")].Add(item);
                return new { sceneItemId = item.Id };
            }
            case "GetInputSettings": return new { inputSettings = Inputs[S(d, "inputName")], inputKind = "browser_source" };
            case "SetInputSettings":
                foreach (var p in d.GetProperty("inputSettings").EnumerateObject()) Inputs[S(d, "inputName")][p.Name] = p.Value.ToString();
                if (d.GetProperty("inputSettings").TryGetProperty("local_file", out var lf)) { MediaFile = lf.GetString(); MediaCursorMs = 0; MediaState = "OBS_MEDIA_STATE_PAUSED"; }
                return null;
            case "CreateSceneItem":
            {
                if (Zombies.Contains(S(d, "sourceName"))) { RefusedSceneItems++; throw new Exception("Tried to add a removed source to a scene."); }
                var item = new Item { Id = _nextId++, Source = S(d, "sourceName"), Enabled = d.GetProperty("sceneItemEnabled").GetBoolean(), W = 1920, H = 1080 };
                Scenes[S(d, "sceneName")].Add(item);
                return new { sceneItemId = item.Id };
            }
            case "GetSceneItemList":
                return new
                {
                    sceneItems = Scenes[S(d, "sceneName")].Select(i => new
                    {
                        sceneItemId = i.Id, sourceName = i.Source, sceneItemEnabled = i.Enabled,
                        sceneItemTransform = new { positionX = i.X, positionY = i.Y, boundsWidth = i.W, boundsHeight = i.H, width = i.W, height = i.H, alignment = 5 },
                    }).ToList(),
                };
            case "SetSceneItemTransform":
            {
                var item = Scenes[S(d, "sceneName")].Single(i => i.Id == d.GetProperty("sceneItemId").GetInt32());
                var t = d.GetProperty("sceneItemTransform");
                item.X = t.GetProperty("positionX").GetDouble(); item.Y = t.GetProperty("positionY").GetDouble();
                item.W = t.GetProperty("boundsWidth").GetDouble(); item.H = t.GetProperty("boundsHeight").GetDouble();
                return null;
            }
            case "SetSceneItemEnabled":
                Scenes[S(d, "sceneName")].Single(i => i.Id == d.GetProperty("sceneItemId").GetInt32()).Enabled = d.GetProperty("sceneItemEnabled").GetBoolean();
                return null;
            case "SetInputMute": Muted[S(d, "inputName")] = d.GetProperty("inputMuted").GetBoolean(); MuteLog.Add(S(d, "inputName")); AudioLog.Add($"mute {S(d, "inputName")} {Muted[S(d, "inputName")].ToString().ToLowerInvariant()}"); return null;
            case "SetInputAudioMonitorType": MonitorType[S(d, "inputName")] = S(d, "monitorType"); AudioLog.Add($"monitor {S(d, "inputName")} {S(d, "monitorType")}"); return null;
            case "GetSourceFilterList":
                return new { filters = Filters.Keys.Where(k => k.StartsWith(S(d, "sourceName") + "|")).Select(k => new { filterName = k.Split('|')[1], filterKind = "source_record_filter" }).ToList() };
            case "CreateSourceFilter":
                if (S(d, "filterKind") != "compressor_filter" && (!SourceRecordInstalled || S(d, "filterKind") != "source_record_filter")) throw new Exception("Your specified filter kind is not supported by OBS.");
                Filters[S(d, "sourceName") + "|" + S(d, "filterName")] = d.GetProperty("filterSettings").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                return null;
            case "RemoveSourceFilter": Filters.Remove(S(d, "sourceName") + "|" + S(d, "filterName")); return null;
            case "SetInputVolume": Volume[S(d, "inputName")] = d.GetProperty("inputVolumeDb").GetDouble(); return null;
            case "SetInputAudioSyncOffset": SyncOffset[S(d, "inputName")] = d.GetProperty("inputAudioSyncOffset").GetInt32(); return null;
            case "SetSourceFilterSettings":
                foreach (var p in d.GetProperty("filterSettings").EnumerateObject()) Filters[S(d, "sourceName") + "|" + S(d, "filterName")][p.Name] = p.Value.Clone();
                return null;
            case "CallVendorRequest":
                if (!SourceRecordInstalled) throw new Exception("No vendor was found by that name.");
                if (S(d, "vendorName") != "source-record") throw new Exception("bad vendor request");
                _saved = false;
                if (S(d, "requestType") == "replay_buffer_start") { BufferStarts++; BufferStuck = false; return new { vendorName = "source-record", requestType = "replay_buffer_start", responseData = new { success = true } }; }
                if (S(d, "requestType") != "replay_buffer_save") throw new Exception("bad vendor request");
                _saved = !BufferStuck;
                if (BufferStuck) return new { vendorName = "source-record", requestType = "replay_buffer_save", responseData = new { success = false } };
                return new { vendorName = "source-record", requestType = "replay_buffer_save", responseData = new { success = true } };
            case "GetMediaInputStatus":
                return new { mediaState = MediaState, mediaDuration = MediaFile == null ? (double?)null : MediaDurationMs, mediaCursor = MediaFile == null ? (double?)null : MediaCursorMs };
            case "TriggerMediaInputAction":
            {
                if (S(d, "inputName") == "TT Swoosh") { SwooshPlays++; return null; }
                string a = S(d, "mediaAction");
                MediaState = a.EndsWith("PLAY") ? "OBS_MEDIA_STATE_PLAYING" : a.EndsWith("PAUSE") ? "OBS_MEDIA_STATE_PAUSED" : MediaState;
                return null;
            }
            case "GetSceneTransitionList":
                return new { currentSceneTransitionName = CurrentTransition, transitions = Transitions.Select(t => new { transitionName = t.Name, transitionKind = t.Kind }).ToArray() };
            case "SetCurrentSceneTransition": CurrentTransition = S(d, "transitionName"); return null;
            case "SetCurrentSceneTransitionSettings": TransitionSettings = d.GetProperty("transitionSettings").Clone(); return null;
            case "SetMediaInputCursor": MediaCursorMs = d.GetProperty("mediaCursor").GetDouble(); return null;
            case "SetCurrentProgramScene":
                _switchedFrom = Program;
                if (!IgnoreSwitch && SwitchDelayMs == 0) Program = S(d, "sceneName");
                return null;
            case "GetSceneCollectionList": return new { currentSceneCollectionName = Collection, sceneCollections = new[] { Collection } };
            case "GetProfileList": return new { currentProfileName = Profile, profiles = new[] { Profile } };
            case "GetSourceFilterKindList": return new { sourceFilterKinds = SourceRecordInstalled ? FilterKinds.ToList() : FilterKinds.Where(k => k != "source_record_filter").ToList() };
            case "GetProfileParameter":
                return S(d, "parameterName") switch
                {
                    "DelayEnable" => new { parameterValue = (string?)(DelayEnable ? "true" : "false") }, "DelaySec" => new { parameterValue = (string?)DelaySec.ToString() },
                    "MonitoringDeviceId" => new { parameterValue = MonitorDeviceId }, "MonitoringDeviceName" => new { parameterValue = MonitorDeviceName },
                    _ => new { parameterValue = (string?)null },
                };
            case "SetProfileParameter":
                if (S(d, "parameterName") == "DelayEnable") DelayEnable = S(d, "parameterValue") == "true";
                if (S(d, "parameterName") == "DelaySec") DelaySec = int.Parse(S(d, "parameterValue"));
                return null;
            case "GetInputMute": return new { inputMuted = Muted.GetValueOrDefault(S(d, "inputName")) };
            case "RemoveScene": Scenes.Remove(S(d, "sceneName")); return null;
            case "RemoveInput":
            {
                string name = S(d, "inputName");
                if (!Inputs.Remove(name)) throw new Exception("No such input.");
                foreach (var sc in Scenes.Values) sc.RemoveAll(i => i.Source == name);
                if (KeepRemoved) Zombies.Add(name);
                return null;
            }
            case "SetSceneItemIndex":
            {
                var list = Scenes[S(d, "sceneName")];
                var item = list.Single(i => i.Id == d.GetProperty("sceneItemId").GetInt32());
                list.Remove(item);
                list.Insert(Math.Min(list.Count, d.GetProperty("sceneItemIndex").GetInt32()), item);
                return null;
            }
            case "GetCurrentProgramScene": return new { currentProgramSceneName = Program, sceneName = Program };
            case "GetStreamStatus":
                return new { outputActive = Streaming, outputReconnecting = Reconnecting, outputTimecode = "00:01:02.345", outputDuration = 62345, outputCongestion = 0.0, outputBytes = StreamBytes, outputSkippedFrames = StreamSkipped, outputTotalFrames = StreamFrames };
            case "GetRecordStatus": return new { outputActive = RecordingOn, outputPaused = false, outputTimecode = "00:01:00.000", outputDuration = 60000, outputBytes = 1000 };
            case "GetStats":
                return new { cpuUsage = 12.5, memoryUsage = 900.0, availableDiskSpace = DiskMb, activeFps = 60.0, averageFrameRenderTime = 2.0, renderSkippedFrames = RenderSkipped, renderTotalFrames = RenderFrames, outputSkippedFrames = 0, outputTotalFrames = StreamFrames };
            case "GetSourceScreenshot":
                Screenshots++;
                return new { imageData = "data:image/jpg;base64," + Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, (byte)(Screenshots & 0xFF), 0xFF, 0xD9 }) };
            default: throw new Exception("unknown request " + type);
        }
    }

    public Task Event(string type, object data) =>
        _ws == null ? Task.CompletedTask : Send(_ws, new { op = 5, d = new { eventType = type, eventIntent = 4, eventData = data } });

    /// <summary>Someone switches scene in OBS by hand.</summary>
    public Task SwitchTo(string scene) { lock (this) Program = scene; return Event("CurrentProgramSceneChanged", new { sceneName = scene }); }

    public List<Item> Shown(string scene) { lock (this) return Scenes[scene].Where(i => i.Enabled && i.Source != "TT Swoosh").ToList(); }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        var ws = _ws;
        // On Windows, HttpListener's WebSocket can block inside CloseAsync (and the listener's Close)
        // on a lock a cancelled receive still holds, before any time limit applies: shut down on
        // another thread and stop waiting after a moment. The fake is done with either way.
        var shut = Task.Run(async () =>
        {
            try { if (ws?.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", new CancellationTokenSource(1000).Token); } catch { }
            try { _listener.Stop(); } catch { }
            // On Linux, Close after Stop looks the port up again and can find another test's listener on it.
            try { _listener.Close(); } catch { }
        });
        await Task.WhenAny(shut, Task.Delay(3000));
    }
}

public class ObsTests : IAsyncLifetime
{
    private readonly FakeClock _clock = new();
    private readonly TempDir _dir = new();
    private FakeObs _obs = null!;
    private CasterDesk _desk = null!;
    private ObsDirector _director = null!;
    private readonly List<(string Lobby, string Url)> _feeds = new() { ("LJ", "https://vdo.ninja/?view=a&password=x&cleanoutput"), ("MAL", "https://vdo.ninja/?view=b&password=x&cleanoutput"), ("Soggy", "https://vdo.ninja/?view=c&password=x&cleanoutput") };
    private string SettingsPath => Path.Combine(_dir.Path, ObsSettings.FileName);

    public Task InitializeAsync()
    {
        _obs = new FakeObs();
        _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        _director = new ObsDirector(SettingsPath, _desk, () => _feeds.ToList());
        _desk.Switch = air => _director.ApplyAsync(air).Wait();
        foreach (var l in new[] { "LJ", "MAL", "Soggy" })
            _desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = l, round = 1, phase = "ingame", crewAlive = 7, impAlive = 2, taskPct = 30, t = 0 }));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _director.DisposeAsync();
        await _obs.DisposeAsync();
        _desk.Dispose();
        _dir.Dispose();
    }

    private Task<string> Connect(string password = "secret") => _director.ConnectAsync("127.0.0.1", _obs.Port, password);

    private static Task Until(Func<bool> done) => Wait.For(done, 4000);

    [Fact]
    public void The_password_answer_matches_obs_websocket_5()
    {
        // Worked by hand from the protocol: base64(sha256(base64(sha256(password + salt)) + challenge)).
        using var sha = System.Security.Cryptography.SHA256.Create();
        string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes("pw" + "salt")));
        string expected = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + "chal")));
        Assert.Equal(expected, ObsClient.Auth("pw", "salt", "chal"));
    }

    [Fact]
    public async Task A_wrong_password_or_no_OBS_says_what_to_do()
    {
        Assert.Contains("password", await Connect("nope"));
        Assert.False(_director.Connected);
        var nobody = new ObsDirector(null, _desk, () => _feeds);
        string message = await nobody.ConnectAsync("127.0.0.1", 1, "x");
        Assert.Contains("WebSocket", message);
    }

    [Fact]
    public async Task Connecting_builds_the_TT_scenes_with_a_source_per_lobby_and_leaves_other_scenes_alone()
    {
        Assert.StartsWith("Connected to OBS 31.0.0", await Connect());
        foreach (var scene in new[] { "TT Full", "TT 2-up", "TT Quad" })
            Assert.Equal(new[] { "TT Lobby LJ", "TT Lobby MAL", "TT Lobby Soggy" }, _obs.Scenes[scene].Select(i => i.Source).Where(x => x.StartsWith("TT Lobby ")).OrderBy(x => x));
        Assert.Equal("TT Swoosh", _obs.Scenes["TT Full"].Last().Source);        // the swoosh plays on top
        Assert.Empty(_obs.Scenes["Starting soon"]);
        Assert.Equal("https://vdo.ninja/?view=a&password=x&cleanoutput&videobitrate=8000&buffer=3000", _obs.Inputs["TT Lobby LJ"]["url"]!.ToString());
        Assert.Equal("True", _obs.Inputs["TT Lobby LJ"]["reroute_audio"]!.ToString());
        Assert.All(_obs.Scenes["TT Full"].Where(i => i.Source.StartsWith("TT Lobby ")), i => Assert.False(i.Enabled));     // hidden until put on

        // The mapping and the connection are saved for next time (the password stays on this PC).
        var saved = ObsSettings.Load(SettingsPath);
        Assert.Equal("TT Lobby MAL", saved.Sources["MAL"]);
        Assert.True(saved.AutoConnect);
        Assert.Equal(_obs.Port, saved.Port);

        // Building again adds nothing twice; a new lobby gets its source.
        _feeds.Add(("Kai", "https://vdo.ninja/?view=d&password=x&cleanoutput"));
        await _director.BuildAsync();
        Assert.Equal(4, _obs.Scenes["TT Quad"].Count(i => i.Source.StartsWith("TT Lobby ")));
        Assert.Equal(4, _obs.Inputs.Keys.Count(k => k.StartsWith("TT Lobby ")));
    }

    [Fact]
    public async Task Full_screen_shows_one_lobby_with_its_sound_and_switches_scene()
    {
        await Connect();
        _desk.Show("MAL");
        Assert.Equal("TT Full", _obs.Program);
        var shown = _obs.Shown("TT Full").Single();
        Assert.Equal("TT Lobby MAL", shown.Source);
        Assert.Equal((0.0, 0.0, 1920.0, 1080.0), (shown.X, shown.Y, shown.W, shown.H));
        Assert.False(_obs.Muted["TT Lobby MAL"]);
        Assert.True(_obs.Muted["TT Lobby LJ"]);
        Assert.True(_obs.Muted["TT Lobby Soggy"]);
    }

    [Fact]
    public async Task Every_switch_swooshes_once_and_a_Stinger_transition_takes_scene_changes()
    {
        await Connect();
        _director.Settings.Swoosh.DedupeSeconds = 0.3;
        _director.Settings.Swoosh.TransitionPointMs = 0;
        OnAir Air(string layout, params string?[] slots) => new() { Layout = layout, Slots = slots.ToList() };
        int plays = _obs.SwooshPlays;
        await _director.ApplyAsync(Air("full", "LJ"));
        Assert.Equal(plays + 1, _obs.SwooshPlays);
        await Task.Delay(350);
        await _director.ApplyAsync(Air("full", "LJ"));                 // nothing changed: no swoosh
        Assert.Equal(plays + 1, _obs.SwooshPlays);
        await _director.ApplyAsync(Air("full", "MAL"));                // same scene, another picture: swoosh
        Assert.Equal(plays + 2, _obs.SwooshPlays);
        await _director.ApplyAsync(Air("4up", "LJ", "MAL", "Soggy", null));   // straight after: shares that swoosh
        Assert.Equal(plays + 2, _obs.SwooshPlays);
        Assert.Equal("TT Quad", _obs.Program);

        // With a Stinger transition called TT Swoosh, scene changes use it (and its video is set).
        _obs.Transitions.Add(("TT Swoosh", "obs_stinger_transition"));
        await _director.EnsureSwooshAsync();
        Assert.Equal("TT Swoosh", _obs.CurrentTransition);
        Assert.Equal(_director.SwooshFile, _obs.TransitionSettings.GetProperty("path").GetString());
        await Task.Delay(350);
        int count = _director.Swooshes;
        await _director.ApplyAsync(Air("2up", "LJ", "MAL"));
        Assert.Equal(plays + 2, _obs.SwooshPlays);                       // OBS played the stinger itself
        Assert.Equal(count + 1, _director.Swooshes);
        await Task.Delay(350);
        _director.Settings.Swoosh.On = false;
        await _director.ApplyAsync(Air("2up", "MAL", "LJ"));
        Assert.Equal(count + 1, _director.Swooshes);
    }

    [Fact]
    public async Task Quad_and_2up_place_each_lobby_in_its_slot()
    {
        await Connect();
        _desk.Show("", "4up", null, new List<string> { "Soggy", "LJ", "", "MAL" });
        Assert.Equal("TT Quad", _obs.Program);
        var boxes = ObsDirector.Slots("4up", 1920, 1080, 8);
        var shown = _obs.Shown("TT Quad").ToDictionary(i => i.Source);
        Assert.Equal(3, shown.Count);
        Assert.Equal((boxes[0].X, boxes[0].Y), (shown["TT Lobby Soggy"].X, shown["TT Lobby Soggy"].Y));
        Assert.Equal((boxes[1].X, boxes[1].Y), (shown["TT Lobby LJ"].X, shown["TT Lobby LJ"].Y));
        Assert.Equal((boxes[3].X, boxes[3].Y), (shown["TT Lobby MAL"].X, shown["TT Lobby MAL"].Y));
        Assert.Equal(948, shown["TT Lobby MAL"].W);
        Assert.False(_obs.Muted["TT Lobby Soggy"]);       // slot 1's sound
        Assert.True(_obs.Muted["TT Lobby MAL"]);

        _desk.Show("LJ", "2up", 2);
        Assert.Equal("TT 2-up", _obs.Program);
        var two = _obs.Shown("TT 2-up").ToDictionary(i => i.Source);
        Assert.Equal(2, two.Count);
        var b2 = ObsDirector.Slots("2up", 1920, 1080, 8);
        Assert.Equal(b2[1].X, two["TT Lobby LJ"].X);
        Assert.Equal(540 - b2[1].H / 2, two["TT Lobby LJ"].Y, 3);       // centred, 16:9
        Assert.Equal("LIVE (2-up, slot 2)", _desk.OnAir.Label("LJ"));
    }

    [Fact]
    public async Task Switching_by_hand_in_OBS_keeps_the_on_air_labels_right()
    {
        await Connect();
        _desk.Show("", "4up", null, new List<string> { "LJ", "MAL", "Soggy", "" });
        Assert.Equal("button", _desk.OnAir.By);

        // Caster flips to TT Full in OBS, which still shows whatever was last put there (nothing yet).
        await Task.Delay(1600);                              // past the echo of our own switch
        await _obs.SwitchTo("TT Quad");
        await _obs.SwitchTo("Starting soon");
        await Until(() => _desk.OnAir.Scene == "Starting soon");
        Assert.Equal("none", _desk.OnAir.Layout);
        Assert.Equal("obs", _desk.OnAir.By);
        Assert.Null(_desk.OnAir.Label("LJ"));

        await _obs.SwitchTo("TT Quad");
        await Until(() => _desk.OnAir.Layout == "4up");
        Assert.Equal("LIVE (quad, slot 2)", _desk.OnAir.Label("MAL"));
        Assert.Equal("LIVE (quad, slot 3)", _desk.OnAir.Label("Soggy"));
        Assert.Equal("obs", _desk.OnAir.By);
    }

    [Fact]
    public async Task Our_own_switch_isnt_mistaken_for_a_manual_one()
    {
        await Connect();
        _desk.Show("LJ");
        await Task.Delay(300);
        Assert.Equal("button", _desk.OnAir.By);
    }

    [Fact]
    public async Task The_desk_follows_OBS_when_a_switch_lands_late_and_item_events_come_first()
    {
        await Connect();
        _desk.Show("LJ");
        await Until(() => _desk.OnAir.Scene == "TT Full");
        // A real OBS: hiding and showing pictures sends events at once; the scene change comes after the transition.
        _obs.SwitchDelayMs = 400;
        _desk.Show("", "4up", null, new List<string> { "LJ", "MAL", "Soggy", "" });
        Assert.Equal("TT Quad", _director.SwitchingTo);
        await Until(() => _desk.OnAir.Scene == "TT Quad");
        await Task.Delay(500);                               // any late read-backs have run
        Assert.Equal("4up", _desk.OnAir.Layout);
        Assert.Equal("button", _desk.OnAir.By);
        Assert.Equal(new string?[] { "LJ", "MAL", "Soggy", null }, _desk.OnAir.Slots);
        Assert.Null(_director.SwitchProblem);

        // The Multiview card's Send: two lobbies side by side.
        _desk.ShowPicked(new[] { "Soggy", "LJ" });
        await Until(() => _desk.OnAir.Scene == "TT 2-up");
        Assert.Equal(new string?[] { "Soggy", "LJ" }, _desk.OnAir.Slots);
    }

    [Fact]
    public async Task A_switch_OBS_never_makes_is_reported_and_the_desk_shows_what_OBS_has()
    {
        var was = ObsDirector.SwitchTimeout;
        ObsDirector.SwitchTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            await Connect();
            _desk.Show("LJ");
            await Until(() => _desk.OnAir.Scene == "TT Full");
            _obs.IgnoreSwitch = true;
            _desk.Show("", "4up", null, new List<string> { "LJ", "MAL", "", "" });
            await Until(() => _director.SwitchProblem != null);
            Assert.Contains("TT Quad", _director.SwitchProblem);
            await Until(() => _desk.OnAir.Layout == "full");
            Assert.Equal("TT Full", _desk.OnAir.Scene);
        }
        finally { ObsDirector.SwitchTimeout = was; }
    }

    [Fact]
    public async Task The_setup_check_finds_names_an_empty_scene_desktop_audio_replays_and_no_delay()
    {
        lock (_obs) { _obs.Scenes["Scene"] = new(); _obs.Inputs["Desktop Audio"] = new(); _obs.Kinds["Desktop Audio"] = "wasapi_output_capture"; _obs.Muted["Desktop Audio"] = false; _obs.SourceRecordInstalled = false; }
        _director.ImpostorTagsOn = () => true;
        _director.Settings.BroadcastDelay = 0;
        await Connect();
        var checks = _director.Checks.ToDictionary(c => c.Id);
        Assert.False(checks["collection"].Ok);
        Assert.Contains("Rename", checks["profile"].Detail);
        Assert.Equal("Delete it", checks["emptyScene"].Fix);
        Assert.False(checks["desktopAudio"].Ok);
        Assert.Equal(new[] { "Desktop Audio" }, _director.DesktopAudioOn);
        Assert.Contains("Source Record", checks["replays"].Title);
        Assert.False(checks["delay"].Ok);

        await _director.FixAsync("emptyScene");
        Assert.False(_obs.Scenes.ContainsKey("Scene"));
        await _director.FixAsync("desktopAudio");
        Assert.True(_obs.Muted["Desktop Audio"]);
        Assert.Empty(_director.DesktopAudioOn);
        // The fix is Red Alert's own broadcast delay, not OBS's stream delay.
        Assert.Equal("Turn on a 3 s delay", checks["delay"].Fix);
        Assert.Contains("Broadcast delay 3 s", await _director.FixAsync("delay"));
        Assert.Equal(3, _director.DelaySeconds);
        Assert.False(_obs.DelayEnable);
        var delay = _director.Checks.Single(c => c.Id == "delay");
        Assert.True(delay.Ok);
        Assert.Equal("Broadcast delay 3 s · impostor tags are on", delay.Title);
        Assert.Contains("Sync Offset of 3000 ms", delay.Detail);
        Assert.Contains("buffer=3000", _obs.Inputs["TT Lobby LJ"]["url"]!.ToString());
        // Unmuted by hand in OBS: the desk hears about it.
        await _obs.Event("InputMuteStateChanged", new { inputName = "Desktop Audio", inputMuted = false });
        await Until(() => _director.DesktopAudioOn.Count == 1);
    }

    [Fact]
    public async Task Stopping_simulation_removes_its_sources_from_OBS_and_leaves_the_scene_alone()
    {
        _feeds.Add(("SIM-1", "http://127.0.0.1:8767/sim?lobby=SIM-1"));
        _desk.Simulate(true);
        await Connect();
        _desk.Show("LJ");
        await Until(() => _desk.OnAir.Scene == "TT Full");
        lock (_obs) { _obs.Inputs["TT Lobby OLD"] = new() { ["url"] = "http://localhost:8767/sim?lobby=OLD" }; }   // an older version's stand-in
        Assert.Contains("TT Lobby SIM-1", _obs.Inputs.Keys);
        _feeds.RemoveAll(f => f.Lobby == "SIM-1");
        _desk.Simulate(false);
        int gone = await _director.ForgetSimAsync(CasterDesk.IsSimLobby);
        Assert.Equal(2, gone);
        Assert.DoesNotContain(_obs.Inputs.Keys, k => k.Contains("SIM-") || k.Contains("OLD"));
        Assert.Contains("TT Lobby LJ", _obs.Inputs.Keys);
        Assert.Equal("TT Full", _obs.Program);
        Assert.False(_director.Settings.Sources.ContainsKey("SIM-1"));
    }

    [Fact]
    public void Slots_fill_the_canvas_with_gaps()
    {
        var full = ObsDirector.Slots("full", 1920, 1080, 0).Single();
        Assert.Equal((0.0, 0.0, 1920.0, 1080.0), (full.X, full.Y, full.W, full.H));
        var quad = ObsDirector.Slots("4up", 1920, 1080, 8);
        Assert.Equal(1920, quad[1].X + quad[1].W + 8);
        Assert.Equal(1080, quad[3].Y + quad[3].H + 8);
        var two = ObsDirector.Slots("2up", 1920, 1080, 8);
        Assert.Equal(two[0].W * 9 / 16, two[0].H, 3);
    }
}
