using System.Text.Json;
using TournamentTracker.App.Broadcast;
using Xunit;

namespace TournamentTracker.Tests;

/// <summary>The roster: real names for everyone, matched by Discord link, friend code or in-game name.</summary>
public class RosterTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    public void Dispose() => _dir.Dispose();

    private string Path => System.IO.Path.Combine(_dir.Path, Roster.FileName);

    private Roster Write(string csv)
    {
        File.WriteAllText(Path, csv);
        return new Roster(Path);
    }

    [Fact]
    public void A_new_roster_file_is_written_with_an_example()
    {
        var r = new Roster(Path);
        Assert.StartsWith(Roster.Header, File.ReadAllText(Path));
        Assert.Empty(r.Entries);                                   // the example line is a note
    }

    [Fact]
    public void Reads_the_csv_with_quotes_several_names_and_skips_notes()
    {
        var r = Write(Roster.Header + "\n# a note\nJake Rivera,123456789012345678,JakeR;jakey,coolfox#1234,JAKE riv-AIR-uh\n\"Lopez, Maria\",,Mari,,\n,555,,,\n");
        var e = r.Entries;
        Assert.Equal(2, e.Count);
        Assert.Equal("123456789012345678", e[0].DiscordId);
        Assert.Equal(new[] { "JakeR", "jakey" }, e[0].InGameNames);
        Assert.Equal("coolfox#1234", e[0].FriendCodes.Single());
        Assert.Equal("JAKE riv-AIR-uh", e[0].Pronunciation);
        Assert.Equal("Lopez, Maria", e[1].Name);
        Assert.Contains("line 5", r.Problem);                     // the row with no name
    }

    [Fact]
    public void Matches_by_hand_then_Discord_then_friend_code_then_in_game_name()
    {
        var r = Write(Roster.Header + "\nJake,111,Jakey,fox#1,\nMaria,222,Mari,owl#2,\nSam,,Sammy,,\nSam Two,,Sammy,,\n");
        Assert.Equal(("Jake", MatchHow.Discord), Name(r.Match("someone#9", "111", "whatever")));
        Assert.Equal(("Maria", MatchHow.FriendCode), Name(r.Match("owl#2", null, "x")));
        Assert.Equal(("Jake", MatchHow.InGameName), Name(r.Match("name:jakey", null, "JAKEY")));
        Assert.Equal((null, MatchHow.None), Name(r.Match("name:sammy", null, "Sammy")));       // two Sammys: can't tell
        r.Override("name:sammy", "Sam Two");
        Assert.Equal(("Sam Two", MatchHow.Manual), Name(r.Match("name:sammy", null, "Sammy")));
        r.Override("owl#2", "");
        Assert.Equal((null, MatchHow.Manual), Name(r.Match("owl#2", "222", "x")));             // picked "not on the roster"
        r.Override("owl#2", null);
        Assert.Equal(("Maria", MatchHow.Discord), Name(r.Match("owl#2", "222", "x")));
        // Overrides are kept.
        Assert.Equal(("Sam Two", MatchHow.Manual), Name(new Roster(Path).Match("name:sammy", null, "Sammy")));
    }

    private static (string?, MatchHow) Name((RosterEntry? Entry, MatchHow How) m) => (m.Entry?.Name, m.How);

    [Fact]
    public void Plays_use_real_names_with_the_colour_and_names_go_back_to_the_referee()
    {
        File.WriteAllText(Path, Roster.Header + "\nJake Rivera,111,,,\nMaria Lopez,,,owl#2,\n");
        var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig(), Path);
        try
        {
            object P(int id, string name, int color, string key, string? discord) => new { id, name, color, key, discord, imp = (bool?)null, dead = (bool?)null };
            desk.Apply(JsonSerializer.Serialize(new { type = "snap", lobby = "LJ", round = 1, phase = "ingame", crewAlive = 7, impAlive = 2, taskPct = 30, t = 0,
                players = new[] { P(1, "jk", 8, "fox#1", "111"), P(2, "mari", 11, "owl#2", null), P(3, "anon", 0, "who#3", null) } }));
            desk.Apply(JsonSerializer.Serialize(new { type = "event", kind = "kill", lobby = "LJ", round = 1, t = 1, room = "Electrical", winning = false,
                killer = new { id = 1, name = "jk", color = 8, colorName = "Purple" }, victim = new { id = 2, name = "mari", color = 11, colorName = "Lime" } }));
            var card = JsonSerializer.SerializeToElement(desk.State(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }).GetProperty("cards")[0];
            Assert.Equal("[[8|Jake Rivera]] killed [[11|Maria Lopez]] in Electrical", card.GetProperty("text").GetString());
            Assert.Equal("Jake Rivera killed Maria Lopez in Electrical", NameTag.Plain(card.GetProperty("text").GetString()!));

            var names = desk.NamesFor("LJ");
            Assert.Equal("Jake Rivera", names["fox#1"]);
            Assert.Equal("Maria Lopez", names["owl#2"]);
            Assert.False(names.ContainsKey("who#3"));                // not on the roster: keeps their own name

            var state = JsonSerializer.SerializeToElement(desk.RosterState(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.Equal(1, state.GetProperty("unmatched").GetInt32());
            var anon = state.GetProperty("lobbies")[0].GetProperty("players").EnumerateArray().Single(p => p.GetProperty("id").GetInt32() == 3);
            Assert.Equal("None", anon.GetProperty("how").GetString());
        }
        finally { desk.Dispose(); }
    }

    [Fact]
    public void Unmatched_players_keep_their_in_game_name_never_just_a_colour()
    {
        var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        try
        {
            desk.Apply(JsonSerializer.Serialize(new { type = "event", kind = "vent", lobby = "LJ", round = 1, t = 1, action = "enter", room = "MedBay",
                player = new { id = 4, name = "Soggy", color = 0, colorName = "Red" } }));
            var card = JsonSerializer.SerializeToElement(desk.State(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }).GetProperty("cards")[0];
            Assert.Equal("[[0|Soggy]] vented in MedBay", card.GetProperty("text").GetString());
        }
        finally { desk.Dispose(); }
    }

    [Fact]
    public void Simulated_players_match_the_simulated_roster()
    {
        var desk = new CasterDesk(null, () => _clock.Now, new PriorityConfig());
        desk.Simulate(true);
        try
        {
            for (int i = 0; i < 40; i++) { _clock.Advance(1); desk.SimTick(); }
            var state = JsonSerializer.SerializeToElement(desk.RosterState(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var players = state.GetProperty("lobbies").EnumerateArray().SelectMany(l => l.GetProperty("players").EnumerateArray()).ToList();
            Assert.NotEmpty(players);
            Assert.Contains(players, p => p.GetProperty("how").GetString() == "Discord");
            Assert.Contains(players, p => p.GetProperty("how").GetString() == "FriendCode");
            Assert.Contains(players, p => p.GetProperty("how").GetString() == "None");
        }
        finally { desk.Dispose(); }
    }
}
