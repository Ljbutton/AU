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
        await _send.WaitAsync();
        try { await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(msg), WebSocketMessageType.Text, true, CancellationToken.None); }
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
            await ws.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed.", CancellationToken.None);
            return;
        }
        await Send(ws, new { op = 2, d = new { negotiatedRpcVersion = 1 } });
        while (true)
        {
            var msg = await Receive(ws);
            if (msg == null) return;
            var d = msg.Value.GetProperty("d");
            string type = d.GetProperty("requestType").GetString()!, id = d.GetProperty("requestId").GetString()!;
            var data = d.TryGetProperty("requestData", out var rd) ? rd : default;
            object? response; bool ok = true; string? comment = null;
            lock (this)
            {
                Requests.Add(type);
                try { response = Handle(type, data); }
                catch (Exception e) { ok = false; comment = e.Message; response = null; }
            }
            await Send(ws, new { op = 7, d = new { requestType = type, requestId = id, requestStatus = new { result = ok, code = ok ? 100 : 600, comment }, responseData = response } });
            if (ok && type == "SetCurrentProgramScene") await Event("CurrentProgramSceneChanged", new { sceneName = Program });
        }
    }

    private static string S(JsonElement d, string p) => d.GetProperty(p).GetString()!;

    private object? Handle(string type, JsonElement d)
    {
        switch (type)
        {
            case "GetVideoSettings": return new { baseWidth = 1920, baseHeight = 1080, outputWidth = 1920, outputHeight = 1080 };
            case "GetSceneList": return new { currentProgramSceneName = Program, scenes = Scenes.Keys.Select(k => new { sceneName = k }).ToList() };
            case "CreateScene":
                if (Scenes.ContainsKey(S(d, "sceneName"))) throw new Exception("exists");
                Scenes[S(d, "sceneName")] = new(); return null;
            case "GetInputList": return new { inputs = Inputs.Keys.Select(k => new { inputName = k, inputKind = "browser_source" }).ToList() };
            case "CreateInput":
            {
                string name = S(d, "inputName");
                Inputs[name] = JsonSerializer.Deserialize<Dictionary<string, object?>>(d.GetProperty("inputSettings").GetRawText())!;
                var item = new Item { Id = _nextId++, Source = name, Enabled = d.GetProperty("sceneItemEnabled").GetBoolean(), W = 1920, H = 1080 };
                Scenes[S(d, "sceneName")].Add(item);
                return new { sceneItemId = item.Id };
            }
            case "GetInputSettings": return new { inputSettings = Inputs[S(d, "inputName")], inputKind = "browser_source" };
            case "SetInputSettings":
                foreach (var p in d.GetProperty("inputSettings").EnumerateObject()) Inputs[S(d, "inputName")][p.Name] = p.Value.ToString();
                return null;
            case "CreateSceneItem":
            {
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
            case "SetInputMute": Muted[S(d, "inputName")] = d.GetProperty("inputMuted").GetBoolean(); return null;
            case "SetCurrentProgramScene": Program = S(d, "sceneName"); return null;
            case "GetCurrentProgramScene": return new { currentProgramSceneName = Program, sceneName = Program };
            default: throw new Exception("unknown request " + type);
        }
    }

    public Task Event(string type, object data) =>
        _ws == null ? Task.CompletedTask : Send(_ws, new { op = 5, d = new { eventType = type, eventIntent = 4, eventData = data } });

    /// <summary>Someone switches scene in OBS by hand.</summary>
    public Task SwitchTo(string scene) { lock (this) Program = scene; return Event("CurrentProgramSceneChanged", new { sceneName = scene }); }

    public List<Item> Shown(string scene) { lock (this) return Scenes[scene].Where(i => i.Enabled).ToList(); }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_ws?.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        _listener.Stop();
        _listener.Close();
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

    private static async Task Until(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++) await Task.Delay(20);
        Assert.True(done());
    }

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
            Assert.Equal(new[] { "TT Lobby LJ", "TT Lobby MAL", "TT Lobby Soggy" }, _obs.Scenes[scene].Select(i => i.Source).OrderBy(x => x));
        Assert.Empty(_obs.Scenes["Starting soon"]);
        Assert.Equal("https://vdo.ninja/?view=a&password=x&cleanoutput", _obs.Inputs["TT Lobby LJ"]["url"]!.ToString());
        Assert.Equal("True", _obs.Inputs["TT Lobby LJ"]["reroute_audio"]!.ToString());
        Assert.All(_obs.Scenes["TT Full"], i => Assert.False(i.Enabled));     // hidden until put on

        // The mapping and the connection are saved for next time (the password stays on this PC).
        var saved = ObsSettings.Load(SettingsPath);
        Assert.Equal("TT Lobby MAL", saved.Sources["MAL"]);
        Assert.True(saved.AutoConnect);
        Assert.Equal(_obs.Port, saved.Port);

        // Building again adds nothing twice; a new lobby gets its source.
        _feeds.Add(("Kai", "https://vdo.ninja/?view=d&password=x&cleanoutput"));
        await _director.BuildAsync();
        Assert.Equal(4, _obs.Scenes["TT Quad"].Count);
        Assert.Equal(4, _obs.Inputs.Count);
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
