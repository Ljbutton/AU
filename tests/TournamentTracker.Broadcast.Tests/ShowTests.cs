using System.Text.Json;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>Off-screen alerts (Part 18), intermission (19A), the win counter (20) and player cards (21).</summary>
public class ShowTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly CasterDesk _desk;
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ShowTests() => _desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), null, _dir.Path);

    public void Dispose() { _desk.Dispose(); _dir.Dispose(); }

    private void Snap(string lobby, string phase = "ingame") =>
        _desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby, phase, crewAlive = 6, impAlive = 2, taskPct = 20, t = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds() }));

    private static object P(int id, string name, int color, bool imp = false) => new { id, name, color, colorName = "X", imp, key = name.ToLower() + "#1" };

    private void E(string lobby, string kind, object data, int round = 1, string? game = null)
    {
        var msg = JsonSerializer.SerializeToElement(data).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        msg["type"] = "event"; msg["kind"] = kind; msg["lobby"] = lobby; msg["round"] = round; msg["game"] = game ?? lobby + "-1"; msg["clock"] = 30;
        msg["t"] = new DateTimeOffset(_clock.Now).ToUnixTimeMilliseconds();
        _desk.Apply(JsonSerializer.Serialize(msg));
    }

    private void Kill(string lobby, string killer = "Jake", string victim = "Maria") =>
        E(lobby, "kill", new { killer = P(0, killer, 4, true), victim = P(1, victim, 1), room = "Electrical", winning = false });

    private JsonElement Broadcast() => JsonSerializer.SerializeToElement(new BroadcastApp(_desk, () => null, null).State(), Camel);

    // ---- Part 18 ---------------------------------------------------------------------------------

    [Fact]
    public void Plays_in_lobbies_off_screen_get_a_banner_merged_per_lobby_and_queued_one_at_a_time()
    {
        foreach (var l in new[] { "LJ", "MAL", "ZED", "KAI", "BO" }) Snap(l);
        _desk.Show("LJ");
        Kill("LJ");                                              // on screen: no banner
        Assert.Empty(_desk.Alerts.Active());
        Kill("MAL");
        _clock.Advance(1);
        Kill("MAL", "Sam", "Priya");                             // the same lobby joins its banner
        var a = _desk.Alerts.Active().Single();
        Assert.Equal(("MAL", "kill", 2), (a.Lobby, a.Kind, a.Count));
        Assert.Equal("Sam killed Priya in Electrical", NameTag.Plain(a.Text));

        Kill("ZED"); Kill("KAI"); Kill("BO");
        Assert.Equal(new[] { "MAL" }, _desk.Alerts.Active().Select(x => x.Lobby));     // never more than one at once
        Assert.Equal(3, _desk.Alerts.Waiting);
        _clock.Advance(4.5);
        Assert.Equal(new[] { "ZED" }, _desk.Alerts.Active().Select(x => x.Lobby));
        _clock.Advance(4.5); _desk.Alerts.Active();
        _clock.Advance(4.5);
        Assert.Equal(new[] { "BO" }, _desk.Alerts.Active().Select(x => x.Lobby));
        var shown = Broadcast().GetProperty("alerts")[0];
        Assert.Equal("BO", shown.GetProperty("lobby").GetString());
        Assert.Equal(5, shown.GetProperty("number").GetInt32());   // BO connected fifth: its number stays 5

        // Off: a kind, or everything.
        _clock.Advance(5);
        _desk.Alerts.SetType("kill", false);
        Kill("ZED", "Priya", "Sam");
        Assert.Empty(_desk.Alerts.Active());
        _desk.Alerts.SetType("kill", true);
        _desk.Alerts.SetPaused(true);
        E("KAI", "gameEnd", new { winner = "Crewmates", reason = "HumansByVote", how = "vote", abandoned = false });
        Assert.Empty(_desk.Alerts.Active());
        _desk.Alerts.SetPaused(false);
        E("BO", "gameEnd", new { winner = "Impostors", reason = "ImpostorsByKill", how = "kills", abandoned = false });
        Assert.Equal("win", _desk.Alerts.Active().Single().Kind);
        // Remembered.
        Assert.True(new AlertQueue(() => _clock.Now, Path.Combine(_dir.Path, "alerts.json")).Settings.Types["kill"]);
    }

    [Fact]
    public void Body_reports_and_emergency_buttons_are_told_apart()
    {
        Assert.Equal("report", AlertQueue.KindOf("meeting", "[[3|Priya]] reported [[1|Maria]]'s body"));
        Assert.Equal("button", AlertQueue.KindOf("meeting", "[[3|Priya]] called an emergency meeting"));
        Assert.Null(AlertQueue.KindOf("meeting", "Meeting"));
        Assert.Equal("kill", AlertQueue.KindOf("winningKill", "x"));
        Assert.Null(AlertQueue.KindOf("gameEnd", "Game abandoned"));
        Assert.Null(AlertQueue.KindOf("vent", "x"));
    }

    // ---- Part 19A --------------------------------------------------------------------------------

    [Fact]
    public void Between_rounds_intermission_is_offered_or_comes_up_by_itself_and_a_lobby_going_live_ends_it()
    {
        Snap("LJ", "lobby"); Snap("MAL", "lobby");
        _desk.Show("LJ");
        _desk.Tick();
        _clock.Advance(50);
        Snap("LJ", "lobby"); Snap("MAL", "lobby");
        _desk.Tick();
        Assert.True(_desk.IntermissionOffer);
        Assert.Equal("full", _desk.OnAir.Layout);

        _desk.AutoIntermission = true;
        _desk.Tick();
        Assert.Equal("intermission", _desk.OnAir.Layout);
        Assert.Null(_desk.OnAir.Label("LJ"));
        _desk.NextRoundAt = _clock.Now.AddMinutes(10);
        _desk.QueuedMontage = ("m1", "Round 1");
        var it = Broadcast().GetProperty("intermission");
        Assert.Equal("Round 1", it.GetProperty("montage").GetString());
        Assert.Equal(JsonValueKind.Object, Broadcast().GetProperty("wins").ValueKind);   // the win counter is part of it
        Assert.Empty(Broadcast().GetProperty("slots").EnumerateArray());

        // A lobby starts: back by itself (auto) to it.
        Snap("MAL", "ingame");
        _desk.Tick();
        Assert.Equal("full", _desk.OnAir.Layout);
        Assert.Equal("MAL", _desk.OnAir.Slots[0]);

        // By hand: it stays, and says which lobby went live.
        _desk.AutoIntermission = false;
        _desk.ShowIntermission();
        _desk.Tick();
        Assert.Equal("intermission", _desk.OnAir.Layout);
        Assert.Equal("MAL", _desk.LiveDuringIntermission);
        _desk.EndIntermission();
        Assert.Equal("MAL", _desk.OnAir.Slots[0]);
    }

    // ---- Parts 20 and 21 -------------------------------------------------------------------------

    private void Game(string lobby, int n, string winner)
    {
        var players = new[] { P(0, "Jake", 4, true), P(1, "Maria", 1), P(2, "Sam", 2) };
        E(lobby, "gameStart", new { map = "Polus", players, roster = players }, game: $"{lobby}-{n}");
        _clock.Advance(20);
        if (winner == "Impostors") E(lobby, "kill", new { killer = players[0], victim = players[1], room = "Admin", winning = true }, game: $"{lobby}-{n}");
        E(lobby, "gameEnd", new { winner, reason = "x", how = winner == "Impostors" ? "kills" : "vote", abandoned = false }, game: $"{lobby}-{n}");
        _clock.Advance(60);
    }

    [Fact]
    public void The_win_counter_counts_today_or_this_round()
    {
        Game("LJ", 1, "Impostors");
        Game("LJ", 2, "Crewmates");
        Game("MAL", 1, "Impostors");
        Assert.Equal((2, 1), _desk.Wins());
        var w = Broadcast().GetProperty("wins");
        Assert.Equal(2, w.GetProperty("impostors").GetInt32());
        Assert.Equal("today", w.GetProperty("scope").GetString());
        _desk.WinScope = "round";
        Assert.Equal((2, 1), _desk.Wins());
    }

    [Fact]
    public void A_player_card_shows_their_records_on_their_tile_and_goes_after_a_few_seconds()
    {
        Game("LJ", 1, "Impostors");
        Game("LJ", 2, "Crewmates");
        Snap("LJ"); Snap("MAL");
        _desk.Show("LJ");
        Assert.Equal("Player card on stream.", _desk.ShowPlayerCard("jake#1", "LJ"));
        var card = Broadcast().GetProperty("playerCard");
        Assert.True(card.GetProperty("full").GetBoolean());
        var d = card.GetProperty("data");
        Assert.Equal("Jake", d.GetProperty("name").GetString());
        Assert.Equal(4, d.GetProperty("color").GetInt32());
        Assert.Equal(1, d.GetProperty("imp").GetProperty("w").GetInt32());
        Assert.Equal(1, d.GetProperty("imp").GetProperty("l").GetInt32());
        Assert.Equal(1, d.GetProperty("kills").GetInt32());
        _clock.Advance(8.5);                                     // the graphics queue's hold time
        Assert.Equal(JsonValueKind.Null, Broadcast().GetProperty("playerCard").ValueKind);

        // In 2-up it sits on the lobby's half; in a 3×3 grid the tile is too small.
        _desk.Show("LJ", "2up", 2);
        _desk.ShowPlayerCard("jake#1", "LJ");
        card = Broadcast().GetProperty("playerCard");
        Assert.False(card.GetProperty("full").GetBoolean());
        Assert.True(card.GetProperty("x").GetDouble() > 900);
        foreach (var l in new[] { "LJ", "MAL", "A1", "A2", "A3", "A4", "A5", "A6", "A7" }) Snap(l, "lobby");
        _desk.ShowGrid();
        _clock.Advance(8.5); Broadcast();
        Assert.StartsWith("Player card queued: it goes on when LJ is on screen", _desk.ShowPlayerCard("maria#1", "LJ"));
        Assert.Equal(JsonValueKind.Null, Broadcast().GetProperty("playerCard").ValueKind);
        // Back to LJ full screen: its turn.
        _desk.Show("LJ");
        Assert.Equal("maria#1", Broadcast().GetProperty("playerCard").GetProperty("id").GetString()!.Split('|')[0]);
    }

    [Fact]
    public void One_big_graphic_at_a_time_each_on_its_own_lobby()
    {
        Snap("LJ"); Snap("MAL"); Snap("KAI");
        _desk.Show("KAI");
        var app = new BroadcastApp(_desk, () => null, null);
        JsonElement St() => JsonSerializer.SerializeToElement(app.State(), Camel);
        // Two tables and a note at once: one at a time, the note (about nobody) first, MAL's waits for MAL.
        _desk.Graphics.Add("afterGame", "MAL", "MAL after the game", new { lobby = "MAL", round = 1, rows = new object[0] });
        _desk.Graphics.Add("afterGame", "KAI", "KAI after the game", new { lobby = "KAI", round = 1, rows = new object[0] });
        _desk.Graphics.Add("storyline", null, "A note", "A note");
        var s = St();
        Assert.Equal("KAI", s.GetProperty("big").GetProperty("lobby").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("storyline").ValueKind);
        _clock.Advance(8.5);
        s = St();
        Assert.Equal("storyline", s.GetProperty("big").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("standingsChange").ValueKind);
        _clock.Advance(8.5);
        Assert.Equal(JsonValueKind.Null, St().GetProperty("big").ValueKind);        // MAL's table waits: MAL isn't on
        // Quad with MAL in slot 4: its table goes inside MAL's quarter.
        _desk.Show("", "4up", null, new List<string> { "KAI", "LJ", "", "MAL" });
        s = St();
        var at = s.GetProperty("standingsChange").GetProperty("at");
        Assert.False(at.GetProperty("full").GetBoolean());
        Assert.True(at.GetProperty("x").GetDouble() > 900 && at.GetProperty("y").GetDouble() > 500);
        _desk.Graphics.Skip();
        Assert.Equal(JsonValueKind.Null, St().GetProperty("big").ValueKind);
    }

    [Fact]
    public void After_a_game_the_desk_asks_and_saved_tables_play_in_intermission()
    {
        Snap("LJ");
        _desk.Show("LJ");
        var app = new BroadcastApp(_desk, () => null, null);
        var games = new List<TournamentTracker.Stats.GameRecord>();
        _desk.ExternalGames = () => games;
        TournamentTracker.Stats.GameRecord G(int n, string winner)
        {
            var g = new TournamentTracker.Stats.GameRecord { GameNumber = n, Host = "LJ", Round = 1, StartedUtc = _clock.Now, Winner = winner, EndReason = winner == "Impostors" ? "ImpostorsByKill" : "HumansByVote" };
            g.Players.Add(new TournamentTracker.Stats.GamePlayer { Key = "a", Name = "A", IsImpostor = true, Kills = 2 });
            g.Players.Add(new TournamentTracker.Stats.GamePlayer { Key = "b", Name = "B" });
            g.Players.Add(new TournamentTracker.Stats.GamePlayer { Key = "c", Name = "C" });
            TournamentTracker.Stats.Scoring.ScoreGame(g, new TournamentTracker.ScoringRules());
            return g;
        }
        void Next() { _clock.Advance(6); _desk.Tick(); }
        games.Add(G(1, "Crewmates")); Next();
        games.Add(G(2, "Impostors")); Next();
        var prompts = JsonSerializer.SerializeToElement(_desk.PromptState(), Camel).GetProperty("prompts");
        Assert.Equal("LJ", prompts[0].GetProperty("lobby").GetString());
        Assert.Null(_desk.Graphics.Current());                    // nothing on stream until asked
        Assert.Contains("next intermission", _desk.AnswerPrompt(prompts[0].GetProperty("id").GetString()!, "save"));
        _desk.ShowIntermission();
        _desk.Tick();
        Assert.Equal("afterGame", _desk.Graphics.Current()!.Kind);
        _desk.Graphics.Clear();
        // Unanswered prompts go by themselves after a minute.
        _desk.Show("LJ");
        games.Add(G(3, "Crewmates")); Next();
        Assert.Single(JsonSerializer.SerializeToElement(_desk.PromptState(), Camel).GetProperty("prompts").EnumerateArray());
        _clock.Advance(61); _desk.Tick();
        Assert.Empty(JsonSerializer.SerializeToElement(_desk.PromptState(), Camel).GetProperty("prompts").EnumerateArray());
        // The old way: straight into the queue.
        app.Settings.SetQueue(null, true);
        games.Add(G(4, "Impostors")); Next();
        Assert.Equal("afterGame", _desk.Graphics.Current()!.Kind);
    }
}
