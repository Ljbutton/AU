using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TournamentTracker.Discord;
using TournamentTracker.Stats;

namespace TournamentTracker
{
    /// <summary>
    /// The organiser's commands, typed in the private results channel from any device:
    /// <list type="bullet">
    /// <item>!lobbies 3: the lobbies for round 3 from round 2's results, snake-seeded.</item>
    /// <item>!move, !swap, !drop, !add, !host: adjust them (someone can't continue, a host changes).</item>
    /// <item>!start 3 [in 10]: every lobby's mod switches to round 3, and the players are pinged with their lobby.</item>
    /// </list>
    /// Every tournament host's mod hears them over the gateway; the lead lobby (from its setup
    /// code, or whoever typed !lead in-game last) answers, so nothing is posted twice.
    /// </summary>
    public sealed partial class TournamentSession
    {
        public const string LeadNotePrefix = "Lead · ";
        private static readonly string[] PlanCommands = { "move", "swap", "drop", "add", "host" };

        private bool? _isLead;
        public bool IsLead => _isLead ?? _settings.Lead;

        /// <summary>The lobbies being put together for the next round (lead only).</summary>
        public LobbyPlan? Plan { get; private set; }

        /// <summary>A round set from the channel while a game was running: applied when it ends.</summary>
        private int? _pendingRound;

        private void OnChannelMessage(ChannelMessage m)
        {
            if (Shared == null || m.ChannelId != Shared.ChannelId) return;
            _mainThread.Enqueue(() =>
            {
                try { HandleChannelMessage(m); }
                catch (Exception e) { _log.Error("Channel command failed: " + e.Message); }
            });
        }

        /// <summary>Handles one results-channel message. Public for tests; normally fed by the gateway.</summary>
        public void HandleChannelMessage(ChannelMessage m)
        {
            string text = m.Content.Trim();
            if (m.AuthorIsBot)
            {
                // Another lobby took over answering.
                if (text.StartsWith(LeadNotePrefix, StringComparison.Ordinal))
                    _isLead = text.StartsWith(LeadNotePrefix + LobbyLabel() + " ", StringComparison.OrdinalIgnoreCase);
                return;
            }
            if (!text.StartsWith("!", StringComparison.Ordinal)) return;
            var parts = text.Substring(1).Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            string command = parts[0].ToLowerInvariant();
            var args = parts.Skip(1).ToList();

            if (command == "start")
            {
                if (args.Count == 0 || !int.TryParse(args[0].TrimStart('r', 'R'), out int round) || round <= 0)
                {
                    if (IsLead) Answer(m, "Usage: !start <round> [in <minutes>]", ok: false);
                    return;
                }
                int? minutes = args.Count >= 3 && args[1].Equals("in", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[2], out var mins) ? mins : (int?)null;
                StartRoundFromChannel(round);
                if (IsLead) Chain(() => AnnounceRoundAsync(m, round, minutes));
                return;
            }
            if (!IsLead) return;

            if (command == "lobbies")
            {
                if (args.Count == 0 || !int.TryParse(args[0].TrimStart('r', 'R'), out int round) || round < 2)
                {
                    Answer(m, "Usage: !lobbies <round> (2 or later: it's built from the round before)", ok: false);
                    return;
                }
                Chain(async () =>
                {
                    await RefreshCombinedAsync().ConfigureAwait(false);
                    if (Combined == null) return;
                    var plan = LobbyPlan.Build(Combined.GameRecords, round, _settings.AdvanceCount, _settings.GamesPerRound);
                    if (plan.Lobbies.Sum(l => l.Players.Count) == 0)
                    {
                        await AnswerAsync(m, $"Nobody has moved on from round {round - 1} yet.", false).ConfigureAwait(false);
                        return;
                    }
                    _mainThread.Enqueue(() => { Plan = plan; SaveState(); });
                    await PostPlanAsync(plan, $"Round {round} lobbies, seeded from round {round - 1}.").ConfigureAwait(false);
                    await Shared!.ReactAsync(m.Id, "✅").ConfigureAwait(false);
                });
                return;
            }
            if (PlanCommands.Contains(command))
            {
                if (Plan == null)
                {
                    Answer(m, "No lobbies to change yet: type !lobbies <round> first.", ok: false);
                    return;
                }
                string result = Plan.Apply(command, args);
                SaveState();
                var plan = Plan;
                Chain(async () =>
                {
                    await PostPlanAsync(plan, result).ConfigureAwait(false);
                    await Shared!.ReactAsync(m.Id, "✅").ConfigureAwait(false);
                });
            }
        }

        /// <summary>!lead in-game: this lobby answers the channel commands from now on.</summary>
        private void LeadCommand()
        {
            if (Shared == null || _settings.Mode != TrackerMode.Tournament)
            {
                Reply("Channel commands need a tournament host code with a results channel.", false);
                return;
            }
            _isLead = true;
            string label = LobbyLabel();
            Chain(() => Shared.PostNoteAsync($"{LeadNotePrefix}{label} answers channel commands (!lobbies, !start…) now."));
            Reply("This lobby now answers the results-channel commands (!lobbies, !move, !start…).", false);
        }

        private void StartRoundFromChannel(int round)
        {
            if (round == Round) return;
            PostWhereToGo(round);
            if (Tracker.InGame)
            {
                _pendingRound = round;
                Reply($"Round {round} was started from Discord: it begins after this game.", false);
                return;
            }
            ApplyRound(round);
        }

        /// <summary>
        /// With a round start (!start), each lobby says where its players go: its voice channel
        /// and lobby code. The lead lobby's message has already pinged everyone with their lobby.
        /// </summary>
        private void PostWhereToGo(int round)
        {
            if (string.IsNullOrWhiteSpace(_settings.StatsWebhookUrl)) return;
            string? voice = GameVoiceChannel(Players);
            string label = LobbyLabel();
            if (voice == null && _lobbyCode.Length == 0) return;          // nothing useful to say yet
            var parts = new List<string>();
            if (voice != null) parts.Add($"voice <#{voice}>");
            if (_lobbyCode.Length > 0) parts.Add($"lobby code `{_lobbyCode}`");
            if (_map.Length > 0) parts.Add(_map);
            Post(_settings.StatsWebhookUrl, new WebhookMessage
            {
                Username = ReportFormatter.BotName,
                Content = $"**Round {round} · {(label.Length > 0 ? label + "'s lobby" : "Lobby")}:** join " + string.Join(" · ", parts),
            });
        }

        private void ApplyRound(int round)
        {
            int previous = Round;
            SetRound(round);
            Reply($"Round {Round} was started from Discord: points start from zero for this round (the running total keeps counting).", false);
            if (Players.Count > 0) Reply($"Round {Round} starts now!", true);
            if (previous > 0 && Round > previous) OnRoundAdvanced();
        }

        /// <summary>Called after a game ends: a round started from Discord during it takes effect now.</summary>
        private void ApplyPendingRound()
        {
            if (_pendingRound is int round)
            {
                _pendingRound = null;
                ApplyRound(round);
            }
        }

        /// <summary>Pings each lobby's players (linked ones by mention) in the tournament channel.</summary>
        private async Task AnnounceRoundAsync(ChannelMessage m, int round, int? minutes)
        {
            var plan = Plan != null && Plan.Round == round ? Plan : null;
            string when = minutes.HasValue ? $"starts in {minutes} minute{(minutes == 1 ? "" : "s")}" : "is starting now";
            var sb = new StringBuilder($"**Round {round} {when}!**");
            var mentions = new List<string>();
            if (plan != null)
            {
                foreach (var lobby in plan.Lobbies)
                {
                    sb.Append($"\n\n**{lobby.Name}**{(lobby.Host.Length > 0 ? $" (host {lobby.Host})" : "")}: ");
                    sb.Append(string.Join(" ", lobby.Players.Select(p => p.DiscordId != null ? $"<@{p.DiscordId}>" : p.Name)));
                    mentions.AddRange(lobby.Players.Where(p => p.DiscordId != null).Select(p => p.DiscordId!));
                }
            }
            var message = new WebhookMessage
            {
                Username = ReportFormatter.BotName,
                Content = ReportFormatter.Clip(sb.ToString(), 2000),
                AllowedMentions = new AllowedMentions { Users = mentions.Distinct().Take(100).ToList() },
            };
            if (!string.IsNullOrWhiteSpace(_settings.StatsWebhookUrl))
            {
                var result = await _rest.ExecuteWebhookAsync(_settings.StatsWebhookUrl, message).ConfigureAwait(false);
                if (!result.Ok) _log.Error("Could not post the round start: " + result);
            }
            await Shared!.ReactAsync(m.Id, "✅").ConfigureAwait(false);

            if (minutes > 0) _ = ReminderAsync(round, minutes.Value, mentions.Distinct().ToList());
        }

        /// <summary>Waits for the start time off the post queue, then pings everyone again.</summary>
        private async Task ReminderAsync(int round, int minutes, List<string> mentions)
        {
            try { await Delay(TimeSpan.FromMinutes(minutes)).ConfigureAwait(false); }
            catch (Exception) { return; }
            Chain(async () =>
            {
                var now = new WebhookMessage
                {
                    Username = ReportFormatter.BotName,
                    Content = ReportFormatter.Clip($"**Round {round} is starting now!** Head to your lobby." + (mentions.Count > 0 ? " " + string.Join(" ", mentions.Select(id => $"<@{id}>")) : ""), 2000),
                    AllowedMentions = new AllowedMentions { Users = mentions.Take(100).ToList() },
                };
                if (!string.IsNullOrWhiteSpace(_settings.StatsWebhookUrl))
                    await _rest.ExecuteWebhookAsync(_settings.StatsWebhookUrl, now).ConfigureAwait(false);
            });
        }

        /// <summary>How the start reminder waits; tests replace it.</summary>
        public Func<TimeSpan, Task> Delay { get; set; } = t => Task.Delay(t);

        private async Task PostPlanAsync(LobbyPlan plan, string headline)
        {
            var sb = new StringBuilder(headline);
            foreach (var lobby in plan.Lobbies)
            {
                sb.Append($"\n\n**{lobby.Name}** · {(lobby.Host.Length > 0 ? "host " + lobby.Host : "no host yet")} · {lobby.Players.Count} player{(lobby.Players.Count == 1 ? "" : "s")}\n");
                sb.Append(string.Join("\n", lobby.Players.Select((p, i) =>
                    $"`{i + 1,2}.` {p.Name} — {LobbyPlan.Pts(p.Points)} ({p.From}){(p.DiscordId == null ? " · not linked" : "")}")));
            }
            if (plan.Alternates.Count > 0)
                sb.Append("\n\n**Alternates:** " + string.Join(", ", plan.Alternates.Take(5).Select(p => $"{p.Name} ({LobbyPlan.Pts(p.Points)})")));
            sb.Append($"\n\nChange with !move <player> <lobby> · !swap <a> <b> · !drop <player> · !add <player> <lobby> · !host <lobby> <host>. Then !start {plan.Round} in 10.");
            await Shared!.PostNoteAsync(ReportFormatter.Clip(sb.ToString(), 2000)).ConfigureAwait(false);
        }

        private void Answer(ChannelMessage m, string text, bool ok) => Chain(() => AnswerAsync(m, text, ok));

        private async Task AnswerAsync(ChannelMessage m, string text, bool ok)
        {
            await Shared!.PostNoteAsync(text).ConfigureAwait(false);
            await Shared.ReactAsync(m.Id, ok ? "✅" : "❓").ConfigureAwait(false);
        }
    }
}
