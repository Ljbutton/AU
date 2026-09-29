using System;
using System.Linq;
using System.Threading.Tasks;
using TournamentTracker.Discord;

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

        private void OnInteraction(Interaction i)
        {
            if (i.Command != "link" && i.Command != "unlink") return;
            _mainThread.Enqueue(() =>
            {
                try
                {
                    string? answer = HandleSlashCommand(i);
                    // Discord wants an answer within 3 seconds, so this doesn't wait behind other posts.
                    if (answer != null) Task.Run(() => _rest.RespondToInteractionAsync(i.Id, i.Token, answer));
                }
                catch (Exception e) { _log.Error("/" + i.Command + " failed: " + e.Message); }
            });
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
            return $"Linked {(other != null ? "@" + userName : "you")} to {player} in {lobbyName}. Automute will follow {(other != null ? "them" : "you")} from now on.";
        }
    }
}
