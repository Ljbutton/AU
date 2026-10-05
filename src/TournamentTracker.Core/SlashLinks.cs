using System;
using System.Collections.Generic;
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
                try { await LoadCrewEmojisAsync(token, app).ConfigureAwait(false); }
                catch (Exception e) { _log.Warn("Couldn't set up the crewmate emojis: " + e.Message); }
            });
        }

        /// <summary>How long a lobby that can't answer waits before saying so, giving the right lobby time to answer first.</summary>
        private static readonly TimeSpan FallbackDelay = TimeSpan.FromSeconds(1.6);

        /// <summary>
        /// The crewmate heads as the bot's own emojis (uploaded the first time), for the live
        /// status and its colour menu.
        /// </summary>
        private async Task LoadCrewEmojisAsync(string token, string app)
        {
            var have = await _rest.AppEmojisAsync(token, app).ConfigureAwait(false);
            if (have == null) return;
            for (int color = 0; color < 18; color++)
            {
                string name = StatusFormatter.EmojiName(color);
                if (have.TryGetValue(name, out var id)) { _crewEmojis[color] = id; continue; }
                var png = TournamentTracker.Overlay.OverlayServer.CrewHead(color);
                if (png == null) continue;
                var made = await _rest.CreateAppEmojiAsync(token, app, name, png).ConfigureAwait(false);
                if (made != null) _crewEmojis[color] = made;
            }
        }

        private void OnInteraction(Interaction i)
        {
            if (i.Command == "menu")
            {
                // Only this lobby's own live status message.
                if (i.CustomId != _linkMenuId) return;
                _mainThread.Enqueue(() =>
                {
                    try
                    {
                        string answer = HandleLinkMenu(i);
                        Task.Run(() => _rest.RespondToInteractionAsync(i.Id, i.Token, answer));
                    }
                    catch (Exception e) { _log.Error("Colour menu failed: " + e.Message); }
                });
                return;
            }
            if (i.Command != "link" && i.Command != "unlink" && i.Command != "new") return;
            _mainThread.Enqueue(() =>
            {
                try
                {
                    string? answer = i.Command == "new" ? HandleNewCommand(i) : HandleSlashCommand(i);
                    // Discord wants an answer within 3 seconds, so this doesn't wait behind other posts.
                    if (answer != null)
                    {
                        Task.Run(() => _rest.RespondToInteractionAsync(i.Id, i.Token, answer));
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

        /// <summary>
        /// Someone picked a colour in this lobby's live status. Public for tests;
        /// normally fed by the gateway.
        /// </summary>
        public string HandleLinkMenu(Interaction i)
        {
            string value = i.Values.FirstOrDefault() ?? "";
            if (!int.TryParse(value, out int color)) return "That didn't work. Pick your colour again.";
            var player = WithoutReferee(Players).FirstOrDefault(p => p.ColorId == color && !p.Disconnected);
            if (player == null) return $"Nobody is {Colors.Name(color)} in the lobby now. Check your colour in Among Us and pick again.";
            var taken = Links.Find(player.Key);
            if (taken != null && taken.DiscordUserId == i.UserId) return $"You're already linked to {player}.";
            // The newest pick wins: whoever had this colour before is unlinked.
            string was = taken != null ? $" (replacing @{taken.DiscordName})" : "";
            Links.Link(player.Key, player.Name, i.UserId, i.UserName);
            RefreshStatus(force: true);
            Reply($"Linked {player} to @{i.UserName}{was}.", true, _settings.AnnounceLinks);
            return $"Linked you to {player}{was}. Automute will follow you from now on.";
        }

        /// <summary>A problem with the live message the host should see in the app (e.g. the bot can't post where /new asked).</summary>
        public string? StatusProblem { get; private set; }

        /// <summary>
        /// /new: the host moves their lobby's live message (with the colour menu) to the channel
        /// they typed it in. This lobby answers when the host's Discord account is linked to the
        /// person typing, or when they give this lobby's code (which also links them as the host).
        /// Null when it's another lobby's. Public for tests; normally fed by the gateway.
        /// </summary>
        public string? HandleNewCommand(Interaction i)
        {
            if (!_settings.AutoMute.IsConfigured || Players.Count == 0) return null;
            var host = Players.FirstOrDefault(p => p.IsHost);
            if (host == null) return null;
            var hostLink = Links.Find(host.Key);
            string? code = i.Option("code")?.Trim();
            bool byCode = code != null && _lobbyCode.Length > 0 && string.Equals(code, _lobbyCode, StringComparison.OrdinalIgnoreCase);
            bool isHost = hostLink != null && hostLink.DiscordUserId == i.UserId;
            if (!isHost && !byCode) return null;
            if (!isHost && hostLink != null && !i.IsStaff)
                return $"Only this lobby's host (@{hostLink.DiscordName}) or a referee can move its message.";
            if (i.ChannelId.Length == 0) return "Use /new in the text channel you want the lobby's message in.";

            string linked = "";
            if (hostLink == null)
            {
                Links.Link(host.Key, host.Name, i.UserId, i.UserName);
                linked = $" You're linked to {host} now, so next time /new on its own is enough.";
            }
            _statusChosen = i.ChannelId;
            StatusProblem = null;
            SaveState();
            lock (_postLock) _statusSentJson = "";
            if (_statusDesired != null) ScheduleStatusFlush();
            RefreshStatus(force: true);
            string lobby = LobbyLabel();
            return $"{(lobby.Length > 0 ? lobby + "'s lobby" : "Your lobby")} message is in this channel now (the old one is removed). Players pick their colour under it to link.{linked}";
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
                case "new":
                    return "No open lobby is yours. Open your lobby in Among Us first. If you haven't linked yourself yet, use /new code: with your lobby code (it's on the lobby screen).";
                default:
                    return "You aren't linked in any open lobby. Links are made per lobby, so /unlink while you're in the Among Us lobby.";
            }
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
            // The newest link wins: whoever had this player before is unlinked.
            var taken = Links.Find(player.Key);
            string was = taken != null && taken.DiscordUserId != userId ? $" (replacing @{taken.DiscordName})" : "";
            Links.Link(player.Key, player.Name, userId, userName);
            RefreshStatus(force: true);
            Reply($"Linked {player} to @{userName}{was}.", true, _settings.AnnounceLinks);
            return $"Linked {(other != null ? "@" + userName : "you")} to {player} in {lobbyName}{was}. Automute will follow {(other != null ? "them" : "you")} from now on.";
        }
    
        /// <summary>
        /// link &lt;player id&gt; &lt;Discord name&gt;: the host links a player from The Button. The name is
        /// looked up in the Discord server (username or server nickname), and the answer waits
        /// for the lookup so the app shows it.
        /// </summary>
        private void LinkCommand(string[] args)
        {
            if (args.Length < 2 || !int.TryParse(args[0], out int id))
            {
                Reply("Type the player's Discord name after the @.", false);
                return;
            }
            var player = Players.FirstOrDefault(p => p.PlayerId == id);
            if (player == null)
            {
                Reply("That player isn't in the lobby any more.", false);
                return;
            }
            string query = string.Join(" ", args.Skip(1)).Trim().TrimStart('@').Trim();
            string? token = _settings.AutoMute.BotTokens.FirstOrDefault();
            string guild = _settings.AutoMute.GuildId;
            if (token == null || guild.Length == 0)
            {
                Reply("Linking needs a bot and a Discord server (from your setup code).", false);
                return;
            }
            if (query.Length == 0)
            {
                Reply("Type the player's Discord name after the @.", false);
                return;
            }
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingForApp = finished.Task;
            Task.Run(async () =>
            {
                IReadOnlyList<GuildMember> found;
                try { found = await _rest.SearchMembersAsync(token, guild, query).ConfigureAwait(false); }
                catch (Exception) { found = Array.Empty<GuildMember>(); }
                _mainThread.Enqueue(() =>
                {
                    _answeringApp = true;
                    try { FinishLink(player, query, found); }
                    finally { _answeringApp = false; finished.TrySetResult(true); }
                });
            });
        }

        private void FinishLink(PlayerSnapshot player, string query, IReadOnlyList<GuildMember> found)
        {
            // An exact username or nickname wins; otherwise it has to be the only match.
            var exact = found.Where(m => string.Equals(m.Username, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Nick, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.GlobalName, query, StringComparison.OrdinalIgnoreCase)).ToList();
            var pick = exact.Count == 1 ? exact[0] : exact.Count == 0 && found.Count == 1 ? found[0] : null;
            if (pick == null)
            {
                Reply(found.Count == 0
                    ? $"Nobody called @{query} is in the Discord server."
                    : $"More than one person matches @{query}: {string.Join(", ", found.Take(5).Select(m => "@" + m.Username))}. Type more of the name.", false);
                return;
            }
            var taken = Links.Find(player.Key);
            string was = taken != null && taken.DiscordUserId != pick.Id ? $" (replacing @{taken.DiscordName})" : "";
            Links.Link(player.Key, player.Name, pick.Id, pick.Username);
            RefreshStatus(force: true);
            Reply($"Linked {player} to @{pick.Username}{was}.", true, _settings.AnnounceLinks);
        }

        /// <summary>unlink &lt;player id&gt;, from The Button.</summary>
        private void UnlinkCommand(string[] args)
        {
            var player = args.Length > 0 && int.TryParse(args[0], out int id) ? Players.FirstOrDefault(p => p.PlayerId == id) : null;
            var link = player == null ? null : Links.Find(player.Key);
            if (player == null || link == null)
            {
                Reply("That player isn't linked.", false);
                return;
            }
            Links.Unlink(player.Key);
            BlockAutoLink(player.Key);
            RefreshStatus(force: true);
            Reply($"Unlinked {player} from @{link.DiscordName}.", false);
        }
    }
}
