using TournamentTracker;
using TournamentTracker.Discord;
using TournamentTracker.Stats;

// Settings come from the GitHub repository's secrets and variables (see README):
//   DISCORD_BOT_TOKEN          secret: the organiser's bot
//   PRELIM_CHANNEL_IDS         variable: the preliminary channels, comma separated
//   PRELIM_LEADERBOARD_CHANNEL variable (optional): post every leaderboard here instead
string token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN") ?? "";
string channels = Environment.GetEnvironmentVariable("PRELIM_CHANNEL_IDS") ?? "";
string? postTo = Environment.GetEnvironmentVariable("PRELIM_LEADERBOARD_CHANNEL");
if (token.Length == 0 || channels.Trim().Length == 0)
{
    Console.WriteLine("DISCORD_BOT_TOKEN or PRELIM_CHANNEL_IDS isn't set; nothing to do.");
    return 0;
}

var log = new DelegateLog(Console.WriteLine, m => Console.WriteLine("warning: " + m), m => Console.Error.WriteLine("error: " + m));
var rest = new DiscordRest(DiscordRest.CreateHttpClient(), log);
int changed = await new PrelimLeaderboards(rest, token, log).UpdateAsync(channels.Split(',', StringSplitOptions.RemoveEmptyEntries), postTo);
Console.WriteLine($"{changed} leaderboard(s) posted or updated.");
return 0;
