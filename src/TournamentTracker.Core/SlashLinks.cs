using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TournamentTracker.Discord;
using TournamentTracker.Stats;

namespace TournamentTracker
{
    /// <summary>
    /// Linking is done in Discord: players type /link with their in-game name or colour, and
    /// referees can add a user to link someone else. Every host's mod hears the command over
    /// the gateway; the lobby it's about answers (the one whose voice channel the person is
    /// in, or whose lobby has a player of that name), and the others stay quiet.
    /// </summary>
    public sealed partial class TournamentSession
    {
        private void OnGatewayReady()
        {
            var state = _gateway?.State;
            string? token = _settings.AutoMute.BotTokens.FirstOrDefault();
            if (state?.ApplicationId == null || token == null) return;
            string app = state.ApplicationId, guild = _settings.AutoMute.GuildId;
            Task.Run(async () =>
            {
                try
                {
                    var result = await _rest.PutGuildCommandsAsync(token, app, guild, SlashCommands.Definitions()).ConfigureAwait(false);
                    if (!result.Ok) _log.Warn("Couldn't add /link to the Discord server: " + $"{result.Status} {result.Body}");
                }
                catch (Exception e) { _log.Warn("Couldn't add /link to the Discord server: " + e.Message); }
            });
        }

        /// <summary>How long a lobby that can't answer waits before saying so, giving the right lobby time to answer first.</summary>
        private static readonly TimeSpan FallbackDelay = TimeSpan.FromSeconds(1.6);

        private void OnInteraction(Interaction i)
        {
            if (i.Command != "link" && i.Command != "unlink" && i.Command != "stats") return;
            _mainThread.Enqueue(() =>
            {
                try
                {
                    string? answer = i.Command == "stats" ? HandleStatsSlash(i) : HandleSlashCommand(i);
                    // Discord wants an answer within 3 seconds, so this doesn't wait behind other posts.
                    if (answer != null)
                    {
                        Task.Run(() => _rest.RespondToInteractionAsync(i.Id, i.Token, answer, onlyThem: i.Command != "stats"));
                        return;
                    }
                    // Not this lobby's to answer. If no lobby has answered in a moment, say why, so the
                    // player doesn't just see "the application did not respond". Once another lobby
                    // has answered, Discord turns this one down, which is fine.
                    string fallback = SlashFallback(i);
                    Task.Run(async () =>
                    {
                        await Task.Delay(FallbackDelay).ConfigureAwait(false);
                        await _rest.RespondToInteractionAsync(i.Id, i.Token, fallback).ConfigureAwait(false);
                    });
                }
                catch (Exception e) { _log.Error("/" + i.Command + " failed: " + e.Message); }
            });
        }

        /// <summary>The answer when no open lobby could handle the command.</summary>
        public static string SlashFallback(Interaction i)
        {
            string? player = i.Option("player");
            switch (i.Command)
            {
                case "link":
                    bool colour = player != null && Colors.Parse(player).HasValue;
                    return $"Couldn't find \"{player}\" in any open lobby. /link works once you're in the Among Us lobby: " +
                           (colour
                               ? "a colour only works while you're in that lobby's voice channel, so join it or use your in-game name instead."
                               : "join the lobby first, then use your in-game name exactly as it shows (or your colour, from the lobby's voice channel).");
                case "unlink":
                    return "You aren't linked in any open lobby. Links are made per lobby, so /unlink while you're in the Among Us lobby.";
                default:
                    return player != null
                        ? $"No tournament stats for \"{player}\". Check the in-game name (the start of it is enough), or try /stats user:@them."
                        : "No tournament stats for you yet. Play a counted game, and /link in the lobby so /stats knows which player is you.";
            }
        }

        /// <summary>
        /// /stats: a player's tournament totals and where they stand this round. Null when this
        /// lobby doesn't know the player. Public for tests; normally fed by the gateway.
        /// </summary>
        public string? HandleStatsSlash(Interaction i)
        {
            var store = Standings;
            PlayerTotals? t = null;
            string? name = i.Option("player");
            if (name != null)
            {
                var inLobby = FindPlayer(name);
                t = inLobby != null ? store.Find(inLobby.Key) : null;
                if (t == null)
                {
                    var all = store.Players.Values.Where(p => p.Games > 0).ToList();
                    var exact = all.Where(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                    var prefix = all.Where(p => p.Name.StartsWith(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                    t = exact.Count == 1 ? exact[0] : prefix.Count == 1 ? prefix[0] : null;
                }
            }
            else
            {
                string id = i.Option("user") ?? i.UserId;
                string? key = Links.FindByDiscordId(id)?.PlayerKey
                    ?? Combined?.GameRecords.OrderByDescending(g => g.StartedUtc).SelectMany(g => g.Players).FirstOrDefault(p => p.DiscordId == id)?.Key;
                t = key != null ? store.Find(key) : null;
            }
            if (t == null || t.Games == 0) return null;

            var board = store.Leaderboard().ToList();
            int rank = board.FindIndex(x => x.Key == t.Key) + 1;
            var lines = new List<string>
            {
                $"**{t.Name}** · {_settings.TournamentName}",
                $"{(rank > 0 ? $"#{rank} of {board.Count} · " : "")}**{ReportFormatter.Pts(t.Points)} pts** · {t.Wins}W-{t.Losses}L in {t.Games} game{(t.Games == 1 ? "" : "s")}",
                $"Impostor: {t.ImpostorWins}/{t.ImpostorGames} wins, {t.Kills} kills · Crew: {t.CrewWins}/{t.CrewGames} wins, votes {t.CorrectVotes}✓ {t.IncorrectVotes}✗, tasks {Math.Round(100 * t.TaskCompletion)}%",
            };
            if (Combined != null && Round > 0)
            {
                string? host = Combined.GameRecords.Where(g => g.Round == Round && g.Counted && g.Players.Any(p => p.Key == t.Key))
                    .Select(g => g.Host).FirstOrDefault();
                if (host != null)
                {
                    var rows = Stats.Standings.Lobby(Combined.GameRecords, host, Round, _settings.AdvanceCount, _settings.GamesPerRound);
                    int at = rows.FindIndex(r => r.Stats.Key == t.Key);
                    if (at >= 0)
                        lines.Add($"Round {Round}, {host}'s lobby: #{at + 1} with {ReportFormatter.Pts(rows[at].Stats.Points)} pts{(rows[at].Advancing ? ", above the cut line" : "")}");
                }
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Handles /link or /unlink. Returns the answer for the person who used it, or null when
        /// the command is about another lobby. Public for tests; normally fed by the gateway.
        /// </summary>
        public string? HandleSlashCommand(Interaction i)
        {
            if (Players.Count == 0) return null;
            string? other = i.Option("user");
            if (other == i.UserId) other = null;
            string userId = other ?? i.UserId;
            string userName = other != null && i.ResolvedNames.TryGetValue(other, out var n) ? n : other ?? i.UserName;
            string lobby = LobbyLabel();
            string lobbyName = lobby.Length > 0 ? lobby + "'s lobby" : "this lobby";

            var channel = GameVoiceChannel(Players);
            bool inMyVoice = channel != null && Presence!.Members.Any(m => m.ChannelId == channel && (m.UserId == userId || m.UserId == i.UserId));
            bool hostAsking = Players.Any(p => p.IsHost && Links.Find(p.Key)?.DiscordUserId == i.UserId);

            if (i.Command == "unlink")
            {
                var link = Links.FindByDiscordId(userId);
                bool here = link != null && Players.Any(p => string.Equals(p.Key, link.PlayerKey, StringComparison.OrdinalIgnoreCase));
                if (!here && !inMyVoice) return null;
                if (other != null && !i.IsStaff && !hostAsking) return "Only referees can unlink someone else.";
                if (link == null) return other != null ? $"@{userName} isn't linked." : "You aren't linked.";
                Links.Unlink(link.PlayerKey);
                BlockAutoLink(link.PlayerKey);
                RefreshStatus(force: true);
                return other != null ? $"Unlinked @{userName} from {link.PlayerName}." : $"Unlinked you from {link.PlayerName}.";
            }

            string text = (i.Option("player") ?? "").Trim();
            bool byColour = Colors.Parse(text).HasValue;
            var player = FindPlayer(text);
            // A colour only means something in the lobby the person is in; a name is enough on its own.
            if (!inMyVoice && (player == null || byColour)) return null;
            if (other != null && !i.IsStaff && !hostAsking) return "Only referees can link someone else. Use /link with your own in-game name.";
            if (player == null)
            {
                string who = string.Join(", ", Players.Select(p => p.ToString()));
                return $"No one in {lobbyName} matches \"{text}\". Use your in-game name or colour: {who}.";
            }
            var taken = Links.Find(player.Key);
            if (taken != null && taken.DiscordUserId != userId && other == null && !i.IsStaff)
                return $"{player} is already linked to @{taken.DiscordName}. If that's wrong, ask a referee.";

            Links.Link(player.Key, player.Name, userId, userName);
            RefreshStatus(force: true);
            Reply($"Linked {player} to @{userName}.", true, _settings.AnnounceLinks);
            return $"Linked {(other != null ? "@" + userName : "you")} to {player} in {lobbyName}. Automute will follow {(other != null ? "them" : "you")} from now on.";
        }
    }
}
