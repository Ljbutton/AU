using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TournamentTracker.Discord;
using TournamentTracker.Voice;

namespace TournamentTracker
{
    /// <summary>
    /// For the organiser's view and the caster overlay: in a tournament with a private results
    /// channel, each host's game keeps one "Live data" message there (posted by its bot) with
    /// the lobby's state: round, phase, players, deaths, roles, tasks, the round standings and
    /// the latest events. It's edited a few seconds after something changes, re-sent every
    /// minute so the organiser can tell it's alive, and moved below each game's results.
    /// </summary>
    public sealed partial class TournamentSession
    {
        public const string LiveTitlePrefix = "Live data · ";
        private static readonly TimeSpan LiveMinInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan LiveHeartbeat = TimeSpan.FromSeconds(60);

        private readonly object _liveLock = new object();
        private string? _liveMessageId;
        private string _liveDesired = "", _liveSent = "";
        private DateTime _liveNextBuild, _liveNextSend, _liveLastSent;
        private bool _liveBusy, _liveRepost;

        private bool LivePublishOn =>
            _settings.PublishLive && _settings.Mode != TrackerMode.Standard && Shared != null && _settings.AutoMute.BotTokens.Count > 0;

        /// <summary>The live data as the organiser's view reads it. Public for tests.</summary>
        public string LiveJson(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, string lobbyCode, string map) =>
            JsonSerializer.Serialize(LiveData(phase, players, lobbyCode, map));

        private Dictionary<string, object?> LiveData(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, string lobbyCode, string map)
        {
            var game = Tracker.Current;
            bool playing = phase == VoicePhase.Tasks || phase == VoicePhase.Meeting;
            var standings = OverlayStandings();
            var data = new Dictionary<string, object?>
            {
                ["v"] = 1,
                ["lobby"] = LobbyLabel(players),
                ["tour"] = _settings.TournamentName,
                ["round"] = Round,
                ["played"] = GamesThisRound,
                ["per"] = _settings.GamesPerRound,
                ["adv"] = _settings.AdvanceCount,
                ["phase"] = phase.ToString(),
                ["map"] = map,
                ["code"] = lobbyCode,
                ["game"] = game?.Name,
                // The host's game video (VDO.Ninja "id:password"), while they're sending it.
                ["vdo"] = FeedForLive,
                ["st"] = standings.Select(s => JsonSerializer.SerializeToElement(s, OverlayJson)).Select(e => new[] { e.GetProperty("name").GetString(), e.GetProperty("points").GetString() }).ToList(),
                // name, colour, dead as players know it, really dead, impostor, tasks done, tasks total
                ["p"] = WithoutReferee(players).Select(p => new object[]
                {
                    p.Name, p.ColorId,
                    p.Disconnected || _knownDead.Contains(p.Key) ? 1 : 0,
                    p.Disconnected || p.IsDead ? 1 : 0,
                    playing && p.IsImpostor ? 1 : 0,
                    playing && !p.IsImpostor ? p.TasksCompleted : 0,
                    playing && !p.IsImpostor ? p.TasksTotal : 0,
                }).ToList(),
                ["f"] = game == null ? new List<object>() : game.Timeline.TakeLast(8)
                    .Select(e => (object)new[] { TimeSpan.FromSeconds(e.AtSeconds).ToString(@"m\:ss"), e.Kind, e.Text.Length > 140 ? e.Text.Substring(0, 140) : e.Text }).ToList(),
            };
            return data;
        }

        private void PublishLive(VoicePhase phase, IReadOnlyList<PlayerSnapshot> players, string lobbyCode, string map)
        {
            if (!LivePublishOn) return;
            var now = _clock();
            if (now < _liveNextBuild) return;
            _liveNextBuild = now.AddSeconds(1);
            var data = LiveData(phase, players, lobbyCode, map);
            Work.Post(() =>
            {
                string json = JsonSerializer.Serialize(data);
                lock (_liveLock)
                {
                    bool heartbeat = now - _liveLastSent > LiveHeartbeat;
                    if (json == _liveDesired && !heartbeat) return;
                    _liveDesired = json;
                    if (heartbeat) _liveSent = "";
                }
                SendLive();
            });
        }

        /// <summary>After a game: move the live message below the game's results so it's easy to find.</summary>
        private void BumpLive()
        {
            if (!LivePublishOn) return;
            lock (_liveLock) { _liveRepost = true; _liveSent = ""; }
            SendLive();
        }

        private void SendLive()
        {
            lock (_liveLock)
            {
                if (_liveBusy) return;
                _liveBusy = true;
            }
            Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        string json, token = _settings.AutoMute.BotTokens[0], channel = Shared!.ChannelId;
                        string? id;
                        bool repost;
                        lock (_liveLock)
                        {
                            if (_liveDesired == _liveSent || _liveDesired.Length == 0) return;
                            json = _liveDesired;
                            id = _liveMessageId;
                            repost = _liveRepost;
                            _liveRepost = false;
                        }
                        var wait = _liveNextSend - DateTime.UtcNow;
                        if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);

                        string stamped = json.Insert(json.Length - 1, $",\"t\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
                        var message = new WebhookMessage
                        {
                            Embeds = new List<Embed>
                            {
                                new Embed
                                {
                                    Title = LiveTitlePrefix + LobbyLabel(),
                                    Description = "```json\n" + stamped + "\n```",
                                    Color = 0x2B2D31,
                                    Footer = new EmbedFooter { Text = "Kept up to date for The Button's organiser view and caster overlay. Please don't delete it." },
                                },
                            },
                        };
                        if (repost && id != null)
                        {
                            await _rest.DeleteMessageAsync(token, channel, id).ConfigureAwait(false);
                            id = null;
                        }
                        if (id != null)
                        {
                            var edit = await _rest.EditEmbedsAsync(token, channel, id, message).ConfigureAwait(false);
                            if (edit.Status == 404) id = null;
                            else if (!edit.Ok) _log.Warn("Could not update the live data: " + edit);
                        }
                        if (id == null)
                        {
                            var made = await _rest.PostEmbedsAsync(token, channel, message).ConfigureAwait(false);
                            if (made.Ok) id = DiscordRest.MessageIdOf(made);
                            else _log.Warn("Could not post the live data: " + made);
                        }
                        lock (_liveLock)
                        {
                            _liveMessageId = id;
                            _liveSent = json;
                            _liveLastSent = _clock();
                            _liveNextSend = DateTime.UtcNow + LiveMinInterval;
                        }
                    }
                }
                catch (Exception e) { _log.Warn("Live data failed: " + e.Message); }
                finally
                {
                    bool again;
                    lock (_liveLock)
                    {
                        _liveBusy = false;
                        again = _liveDesired != _liveSent && _liveDesired.Length > 0;
                    }
                    if (again) SendLive();      // something changed while this was finishing
                }
            });
        }
    }
}
