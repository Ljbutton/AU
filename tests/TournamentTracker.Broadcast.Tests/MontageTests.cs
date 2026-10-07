using System.Text.Json;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Montages (Part 15), the Moments library (Part 19B) and sponsors (Part 16).</summary>
public class MontageTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Dispose() => _dir.Dispose();

    /// <summary>ffmpeg for the rendering tests: TT_FFMPEG, or one on the PATH. Without it those tests check nothing.</summary>
    private static string? Ffmpeg()
    {
        var env = Environment.GetEnvironmentVariable("TT_FFMPEG");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        return new MontageBuilder(() => new ReplaySettings(), () => "", null).Ffmpeg;
    }

    private MontageBuilder Builder(ReplaySettings? s = null)
    {
        s ??= new ReplaySettings();
        s.Ffmpeg = Ffmpeg() ?? "";
        return new MontageBuilder(() => s, () => _dir.Path, null);
    }

    private static string Kill(string killer, int kc, string victim, int vc, string room) => $"{NameTag.Make(kc, killer)} killed {NameTag.Make(vc, victim)} in {room}";

    [Fact]
    public void A_kills_lower_third_is_killer_arrow_victim_and_the_room()
    {
        var c = new Clip { Rule = "kill", Title = Kill("Jake", 4, "Maria", 1, "Electrical"), Room = "Electrical" };
        Assert.Equal("[[4|Jake]] → [[1|Maria]], Electrical", MontageBuilder.LowerThird(c));
        var e = new Clip { Rule = "eject", Title = "[[2|Sam]] ejected: Impostor" };
        Assert.Equal(e.Title, MontageBuilder.LowerThird(e));
    }

    [Fact]
    public void Too_many_clips_keeps_the_best_plays_in_time_order()
    {
        var t = DateTime.UtcNow;
        var clips = Enumerable.Range(0, 12).Select(i => new Clip { Id = "c" + i, Rule = i == 7 ? "winningKill" : i == 3 ? "eject" : "kill", EventAt = t.AddSeconds(i) }).ToList();
        var picked = MontageBuilder.Pick(clips, 20, 5.5);
        Assert.Equal(4, picked.Count);                    // (20 + 0.4) / (5.5 - 0.4)
        Assert.Contains(picked, c => c.Id == "c7");
        Assert.Contains(picked, c => c.Id == "c3");
        Assert.Equal(picked.OrderBy(c => c.EventAt).Select(c => c.Id), picked.Select(c => c.Id));
        Assert.Equal(5, MontageBuilder.Pick(clips.Take(5), 60, 5.5).Count);
    }

    [Fact]
    public void The_ffmpeg_command_crops_each_clip_draws_the_lower_third_and_wipes_between_them()
    {
        var b = Builder();
        var segs = new List<Segment>
        {
            new() { CardTop = "PRESENTED BY", Card = "Acme", CardSmall = "Snacks for crewmates", Length = 3 },
            new() { File = "a.mp4", Start = 4, Length = 5.5, View = new View(0.6, 0.4, 2), Lower = "[[4|Jake]] → [[1|Maria]], Electrical" },
            new() { File = "b.mp4", Start = 1, Length = 5.5, View = new View(0.5, 0.5, 1), Lower = "[[2|Sam]] ejected: Impostor" },
        };
        var cmd = b.Command(segs, "out.mp4");
        string graph = cmd[cmd.IndexOf("-filter_complex") + 1];
        Assert.Contains("crop=w=iw/2:h=ih/2:x=iw*0.6-iw/2/2:y=ih*0.4-ih/2/2", graph);
        Assert.Contains("text='Jake → ", graph);                  // one line, with room for each head before its name
        Assert.Contains("  Maria\\, Electrical'", graph);
        Assert.Contains(cmd, a => a.EndsWith(Path.Combine("tt-crew", "4.png")));   // Jake's orange crewmate head
        Assert.Contains(cmd, a => a.EndsWith(Path.Combine("tt-crew", "1.png")));
        Assert.True(File.Exists(MontageBuilder.HeadFile(4)));
        Assert.Matches(@"overlay=x=11\d", graph);                // the first head, in the room left for it
        Assert.Contains("text='PRESENTED BY'", graph);
        Assert.Equal(2, graph.Split("xfade=transition=wiperight").Length - 1);
        Assert.Equal(13.2, MontageBuilder.Length(segs), 3);
        Assert.Contains("-an", cmd);
    }

    [Fact]
    public async Task A_montage_renders_with_ffmpeg()
    {
        if (Ffmpeg() == null) return;
        var b = Builder();
        string a = Path.Combine(_dir.Path, "a.mp4"), c = Path.Combine(_dir.Path, "c.mp4");
        Assert.True((await b.PlaceholderAsync(a, "LJ · [[4|Jake]] killed [[1|Maria]]", 6)).Ok);
        Assert.True((await b.PlaceholderAsync(c, "LJ · [[2|Sam]] ejected", 6)).Ok);
        var now = DateTime.UtcNow;
        var clipA = new Clip { Id = "a", File = a, Rule = "kill", Title = Kill("Jake", 4, "Maria", 1, "Electrical"), Room = "Electrical", EventAt = now, SavedAt = now.AddSeconds(2) };
        var clipC = new Clip { Id = "c", File = c, Rule = "eject", Title = "[[2|Sam]] ejected: Impostor", EventAt = now, SavedAt = now.AddSeconds(2) };
        var segs = new List<Segment> { new() { CardTop = "PRESENTED BY", Card = "Acme", Length = 2 }, b.Cut(clipA, 6), b.Cut(clipC, 6) };
        var m = await b.BuildAsync(new Montage { Id = "m1", Kind = "game", Title = "LJ · game 1" }, segs);
        Assert.Equal("ready", m.State);
        Assert.True(File.Exists(m.File));
        double? length = await b.ProbeAsync(m.File!);
        Assert.InRange(length!.Value, MontageBuilder.Length(segs) - 0.3, MontageBuilder.Length(segs) + 0.3);
        var jpg = Path.Combine(_dir.Path, "a.jpg");
        Assert.True((await b.ThumbnailAsync(a, 3, jpg)).Ok);
        Assert.True(new FileInfo(jpg).Length > 1000);
    }

    [Fact]
    public async Task The_placeholder_swoosh_is_a_short_see_through_video_with_sound()
    {
        if (Ffmpeg() == null) return;
        var b = Builder();
        string file = Path.Combine(_dir.Path, "tools", ObsDirector.PlaceholderName);
        Assert.True(await b.SwooshAsync(file));
        var (_, info) = await b.RunAsync(new[] { "-hide_banner", "-i", file });
        Assert.Contains("vp9", info);
        Assert.Contains("opus", info);
        Assert.InRange((await b.ProbeAsync(file))!.Value, 0.8, 1.1);

        // In the tournament's colours, with its logo: it covers the whole screen at the switch point (0.5 s), and only then.
        string logo = MontageBuilder.HeadFile(4);
        b.SwooshTheme = () => ("#7a2cf0", "#00e5ff", logo);
        string branded = Path.Combine(_dir.Path, "tools", "branded.webm");
        Assert.True(await b.SwooshAsync(branded));
        async Task<int> MinAlpha(double at)
        {
            var (_, o) = await b.RunAsync(new[] { "-hide_banner", "-c:v", "libvpx-vp9", "-ss", at.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", branded, "-frames:v", "1", "-vf", "alphaextract,signalstats,metadata=print", "-f", "null", "-" });
            return int.Parse(System.Text.RegularExpressions.Regex.Match(o, @"signalstats\.YMIN=(\d+)").Groups[1].Value);
        }
        Assert.True(await MinAlpha(0.5) >= 250);
        Assert.Equal(0, await MinAlpha(0.1));
        Assert.Contains("0x7a2cf0", string.Join(" ", MontageBuilder.SwooshCommand("x.webm", "#7a2cf0", "#00e5ff", null)));
        Assert.Contains("0x1fa143", string.Join(" ", MontageBuilder.SwooshCommand("x.webm", "rgba(1,2,3,.5)", "#00e5ff", null)));   // not a colour it can use: the default
    }

    // ---- In simulation: stand-in clips, the end-of-game montage, a custom one that goes once played ----

    [Fact]
    public async Task In_simulation_plays_get_stand_in_clips_and_a_game_gets_its_montage()
    {
        if (Ffmpeg() == null) return;
        await using var obs = new FakeObs();
        var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path);
        await using var director = new ObsDirector(Path.Combine(_dir.Path, ObsSettings.FileName), desk, () => new List<(string, string)> { ("ZZ", "https://vdo.ninja/?view=a") });
        director.Settings.Replay.Folder = _dir.Path;
        director.Settings.Replay.PostSeconds = 0.2;
        director.Settings.Replay.PreSeconds = 3;
        director.Settings.Replay.Ffmpeg = Ffmpeg()!;
        var builder = new MontageBuilder(() => director.Settings.Replay, () => director.ClipFolder, null);
        var replays = new ReplayManager(desk, director, () => _clock.Now, loop: false) { Builder = builder, Sponsors = desk.Sponsors };
        var montages = new MontageManager(desk, replays, builder, desk.Sponsors, () => director.Settings.Replay, () => _clock.Now);
        File.WriteAllText(Path.Combine(_dir.Path, SponsorBook.FileName), "{\"sponsors\":[{\"name\":\"Acme\",\"tagline\":\"Snacks\",\"placements\":[\"montage\",\"killcam\"]}]}");
        desk.Sponsors.Refresh(force: true);
        try
        {
            desk.Simulate(true);
            Assert.True(replays.CanSave);
            void E(string kind, object data)
            {
                var msg = JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
                msg["type"] = "event"; msg["kind"] = kind; msg["lobby"] = "ZZ"; msg["round"] = 1; msg["game"] = "ZZ-1"; msg["clock"] = 30; msg["t"] = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds();
                desk.Apply(JsonSerializer.Serialize(msg));
            }
            object P(int id, string name, int color, bool imp = false) => new { id, name, color, colorName = "X", imp };
            E("gameStart", new { map = "Polus", players = new[] { P(0, "Jake", 4, true), P(1, "Maria", 1), P(2, "Sam", 2) } });
            _clock.Advance(20);
            E("kill", new { killer = P(0, "Jake", 4, true), victim = P(1, "Maria", 1), room = "Electrical", winning = false });
            for (int i = 0; i < 400 && !replays.Clips.Any(c => c.Lobby == "ZZ" && c.State != "saving"); i++) await Task.Delay(50);
            var clip = replays.Clips.Single(c => c.Lobby == "ZZ" && c.Rule == "kill");
            Assert.Equal("ready", clip.State);
            Assert.True(File.Exists(clip.File));
            Assert.Equal("Electrical", clip.Room);
            Assert.Equal(1, clip.Round);
            Assert.Equal("ZZ-1", clip.Game);
            Assert.Equal(0, clip.KeyPlayerId);                 // the killer
            Assert.NotNull(clip.Thumbnail);

            // The game ends: its montage builds by itself, opened by the sponsor's card.
            _clock.Advance(30);
            E("gameEnd", new { winner = "Impostors", reason = "ImpostorsByKill", how = "kills", abandoned = false });
            for (int i = 0; i < 600 && !montages.All.Any(m => m.Kind == "game" && m.Lobby == "ZZ" && m.State != "building"); i++) await Task.Delay(50);
            var game = montages.All.Single(m => m.Kind == "game" && m.Lobby == "ZZ");
            Assert.Equal("ready", game.State);
            Assert.Contains(clip.Id, game.ClipIds);
            Assert.Equal("Acme", game.Sponsor);
            Assert.True(File.Exists(game.File));

            // A custom montage from the Moments library: once it has played to the end it's archived and the clip is USED.
            var custom = await montages.CustomAsync(new[] { clip.Id }, "Best of ZZ");
            Assert.Equal("ready", custom.State);
            await director.ConnectAsync("127.0.0.1", obs.Port, "secret");
            string said = await montages.PlayAsync(custom.Id);
            Assert.StartsWith("Montage: Best of ZZ", said);
            Assert.Equal(custom.File, obs.MediaFile);
            Assert.True(clip.Used);
            int before = montages.CustomPlayed;
            obs.MediaState = "OBS_MEDIA_STATE_ENDED";
            _clock.Advance(1);
            await replays.TickAsync();
            Assert.Null(montages.Find(custom.Id));
            Assert.Equal(before + 1, montages.CustomPlayed);
            // Played: it's in the archive with its clips, and its video stays until deleted.
            var archived = montages.Archive.Single(a => a.Title == "Best of ZZ");
            Assert.True(File.Exists(archived.File));
            Assert.Equal(clip.Id, archived.Clips.Single().Id);
            Assert.StartsWith("Montage: Best of ZZ", await montages.ReplayArchivedAsync(archived.Id));
            await replays.LiveAsync();
            var rebuilt = await montages.RebuildAsync(archived.Id);
            Assert.Equal("ready", rebuilt.State);
            Assert.True(File.Exists(rebuilt.File));
            Assert.Equal(new[] { clip.Id }, rebuilt.ClipIds);
            Assert.Contains("for good", montages.DeleteArchived(archived.Id));
            Assert.False(File.Exists(archived.File));
            Assert.Empty(montages.Archive.Where(a => a.Id == archived.Id));
            // The game montage stays after playing.
            await montages.PlayAsync(game.Id);
            // Simulated games are never proof of delivery: nothing goes in the sponsor log.
            Assert.Empty(desk.Sponsors.Appearances());
            var moments = JsonSerializer.SerializeToElement(replays.Moments(), Camel);
            Assert.True(moments.EnumerateArray().Single(m => m.GetProperty("id").GetString() == clip.Id).GetProperty("used").GetBoolean());
        }
        finally
        {
            replays.Dispose();
            desk.Dispose();
        }
    }

    // ---- Sponsors ------------------------------------------------------------------------------

    private SponsorBook Book(string json)
    {
        File.WriteAllText(Path.Combine(_dir.Path, SponsorBook.FileName), json);
        return new SponsorBook(_dir.Path, () => _clock.Now);
    }

    [Fact]
    public void Sponsor_reads_come_due_by_time_or_games_snooze_and_are_logged_when_done()
    {
        var book = Book("{\"sponsors\":[{\"name\":\"Acme\",\"readScript\":\"Acme snacks!\",\"readEveryMinutes\":20},{\"name\":\"Bolt\",\"readScript\":\"Bolt.\",\"readEveryGames\":2},{\"name\":\"Quiet\"}]}");
        Assert.Empty(book.ReadsDue(0, 0));                 // nothing the moment it starts
        _clock.Advance(60 * 21);
        Assert.Equal(new[] { "Acme" }, book.ReadsDue(1, 0).Select(r => r.Sponsor));
        Assert.Equal("Acme snacks!", book.ReadsDue(1, 0)[0].Script);
        Assert.Contains(book.ReadsDue(2, 0), r => r.Sponsor == "Bolt");
        book.ReadSnooze("Bolt");
        Assert.DoesNotContain(book.ReadsDue(2, 0), r => r.Sponsor == "Bolt");
        _clock.Advance(6 * 60);
        Assert.Contains(book.ReadsDue(2, 0), r => r.Sponsor == "Bolt");
        book.ReadDone("Acme", 2, 0);
        Assert.DoesNotContain(book.ReadsDue(2, 0), r => r.Sponsor == "Acme");
        var log = book.Appearances().Single();
        Assert.Equal(("Acme", "read"), (log.Sponsor, log.Placement));
        Assert.Contains("Verbal read (said by the caster): 1 times", book.Export().Summary);
    }

    [Fact]
    public void Sponsors_take_turns_in_a_placement_and_every_appearance_is_logged()
    {
        var book = Book("{\"sponsors\":[{\"name\":\"Acme\",\"placements\":[\"killcam\",\"grid\"]},{\"name\":\"Bolt, Inc\",\"placements\":[\"KillCam\"]},{\"name\":\"Quiet\",\"placements\":[]}]}");
        Assert.Equal("Acme", book.Next("killcam")!.Name);
        Assert.Equal("Bolt, Inc", book.Next("killcam")!.Name);
        Assert.Equal("Acme", book.Next("killcam")!.Name);
        Assert.Null(book.Next("break"));

        book.Begin("k1", book.Sponsors[1], "killcam", "LJ", "[[4|Jake]] killed [[1|Maria]]");
        _clock.Advance(7);
        book.End("k1");
        book.Begin("g1", book.Sponsors[0], "grid");
        _clock.Advance(60);
        book.EndAll("g");
        var log = book.Appearances();
        Assert.Equal(2, log.Count);
        Assert.Equal(7, log[0].Seconds);
        var (csv, summary) = book.Export();
        Assert.Contains("\"Bolt, Inc\",killcam,", csv);
        Assert.Contains("Jake killed Maria", csv);
        Assert.Contains("Acme: 1 appearances, 1 minutes on screen", summary);
        Assert.Contains("Kill Cam (kill replays): 1 times, 7 s", summary);
    }

    [Fact]
    public void A_new_sponsors_file_has_an_example_with_no_placements()
    {
        var book = new SponsorBook(_dir.Path);
        Assert.True(File.Exists(book.Path));
        Assert.Equal("Example Sponsor", book.Sponsors.Single().Name);
        Assert.Null(book.Next("killcam"));
    }

    [Fact]
    public void A_sponsor_break_splits_the_screen_ends_by_itself_and_a_must_show_play_cuts_back()
    {
        var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path);
        File.WriteAllText(Path.Combine(_dir.Path, SponsorBook.FileName), "{\"sponsors\":[{\"name\":\"Acme\",\"placements\":[\"break\",\"grid\",\"standings\"],\"breakSeconds\":20}]}");
        desk.Sponsors.Refresh(force: true);
        try
        {
            void Snap(string lobby) => desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby, phase = "ingame", crewAlive = 6, impAlive = 2, taskPct = 20, t = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds() }));
            Snap("LJ"); Snap("MAL"); Snap("ZED");
            desk.Show("LJ");
            Assert.StartsWith("Sponsor break: Acme for 20s", desk.StartBreak());
            Assert.Equal("break", desk.OnAir.Layout);
            Assert.Equal("LIVE (sponsor break)", desk.OnAir.Label("LJ"));
            _clock.Advance(21);
            Snap("LJ"); Snap("MAL"); Snap("ZED");
            desk.Tick();
            Assert.Equal("full", desk.OnAir.Layout);
            Assert.Equal(21, desk.Sponsors.Appearances().Single().Seconds);

            // Again, and a must-show play in another lobby: straight to it, full screen.
            desk.StartBreak();
            _clock.Advance(3);
            desk.Apply(JsonSerializer.Serialize(new { type = "event", kind = "eject", lobby = "MAL", ejected = new { id = 3, name = "Sam", color = 2 }, wasImpostor = true, t = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds() }));
            Assert.Equal("full", desk.OnAir.Layout);
            Assert.Equal("MAL", desk.OnAir.Slots[0]);
            Assert.Null(desk.Break);

            // The grid's empty tile goes to a grid sponsor (3 lobbies in a 2×2).
            Snap("LJ"); Snap("MAL"); Snap("ZED");
            desk.ShowGrid();
            Assert.True(desk.GridSponsors.ContainsKey(4), string.Join(",", desk.OnAir.Slots.Select(x => x ?? "-")) + " " + desk.OnAir.Layout);
            Assert.Equal("Acme", desk.GridSponsors[4].Name);
            var app = new BroadcastApp(desk, () => null, null);
            var state = JsonSerializer.SerializeToElement(app.State(), Camel);
            Assert.Equal("Acme", state.GetProperty("extras").GetProperty("sponsorTiles").GetProperty("4").GetProperty("name").GetString());
            desk.Show("LJ");
            Assert.Contains(desk.Sponsors.Appearances(), a => a.Placement == "grid");
        }
        finally { desk.Dispose(); }
    }
}
