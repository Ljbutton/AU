using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// One answer worked out for everyone asking at the same moment: the Live desk polls several times
    /// a second (and again after each click), and two polls that overlap now share one piece of work
    /// instead of building everything twice. A fresh answer is reused for a moment, but never across an
    /// action: anything posted to Red Alert makes the next answer new, so a click is seen at once.
    /// </summary>
    internal sealed class SharedAnswer
    {
        private readonly object _lock = new object();
        private Task<byte[]>? _running;
        private long _runningVersion = -1;
        private (byte[] Body, DateTime At, long Version)? _last;

        public Task<byte[]> GetAsync(Func<byte[]> make, TimeSpan fresh, long version)
        {
            lock (_lock)
            {
                if (_last is { } l && l.Version == version && DateTime.UtcNow - l.At < fresh) return Task.FromResult(l.Body);
                if (_running != null && _runningVersion == version) return _running;
                _runningVersion = version;
                var task = _running = Task.Run(() =>
                {
                    try
                    {
                        var body = make();
                        lock (_lock) if (_last is not { } had || had.Version <= version) _last = (body, DateTime.UtcNow, version);
                        return body;
                    }
                    finally { lock (_lock) if (_runningVersion == version) _running = null; }
                });
                return task;
            }
        }
    }

    public sealed partial class BroadcastServer
    {
        // Anything posted to /app/ (an action) makes the shared desk answers new.
        private long _postVersion;
        private readonly SharedAnswer _deskAnswer = new SharedAnswer(), _liveAnswer = new SharedAnswer();
        /// <summary>How long a desk answer is reused for polls that come close together.</summary>
        internal static TimeSpan DeskFresh { get; set; } = TimeSpan.FromMilliseconds(150);

        private static (int, string, byte[]) Json200(byte[] body) => (200, "application/json", body);

        /// <summary>Everything the desk page shows (every page's data): the full answer.</summary>
        private object DeskPayload()
        {
            var desk = _desk!;
            var ranking = desk.Board.Ranking();
            var links = _caster!.DataLinks();
            return new
            {
                desk = desk.State(), obs = _obs?.Status(), replay = _replays?.State(), roster = desk.RosterState(), story = desk.StoryState(),
                montages = _montages?.State(), moments = _replays?.Moments(), sponsors = SponsorState(),
                voice = new { status = desk.VoiceState(), obs = _obs?.VoiceStatus() },
                twitch = _twitch?.State(),
                broadcast = _broadcast == null ? null : new { url = _caster?.Url == null ? null : _caster.Url + "broadcast", elements = _broadcast.Settings.Refresh().Elements, names = BroadcastSettings.ElementNames, problem = _broadcast.Settings.Problem, alerts = desk.Alerts.Settings, alertsWaiting = desk.Alerts.Waiting, queue = desk.Graphics.State(), afterGame = desk.PromptState(), afterGameAuto = _broadcast.Settings.Current.AfterGameAuto, hold = _broadcast.Settings.Current.GraphicSeconds },
                names = links.ToDictionary(d => d.Lobby, d => desk.NamesFor(d.Lobby)), receivers = links.Select(d => new { lobby = d.Lobby, url = d.Url }).ToList(),
                previews = Previews(), gameReplays = desk.Replays.List(), twitchHandles = ranking.ToDictionary(r => r.Lobby, r => desk.TwitchOf(r.Lobby)),
                deck = DeckPort(),
            };
        }

        /// <summary>
        /// The Live desk's own answer, small and quick for polling several times a second: the desk,
        /// OBS (without the setup checks), the replay on now and the ready clips, the voice, the graphics
        /// queue and the after-game prompts. The rest (roster, montages, moments, sponsors, Twitch…)
        /// stays on /app/admin/desk for the pages that show it.
        /// </summary>
        private object LivePayload()
        {
            var desk = _desk!;
            return new
            {
                desk = desk.State(),
                obs = _obs?.LiveStatus(),
                replay = _replays == null ? null : new
                {
                    now = _replays.NowState(),
                    ready = _replays.ReadyClips().Take(10).Select(c => new { id = c.Id, title = c.Title, lobby = c.Lobby }).ToList(),
                    problem = _replays.Problem ?? _obs?.ReplayProblem,
                },
                voice = new { obs = _obs?.VoiceStatus() },
                broadcast = _broadcast == null ? null : new
                {
                    queue = desk.Graphics.State(), afterGame = desk.PromptState(),
                    standings = _broadcast.Settings.Current.Elements.GetValueOrDefault("standings"),
                    alertsPaused = desk.Alerts.Settings.Paused, alertsWaiting = desk.Alerts.Waiting,
                },
                reads = _desk!.Sponsors.ReadsDue(PlayedGames(), desk.Tables.CurrentRound),
                deck = DeckPort(),
            };
        }

        private Task<byte[]> DeskAnswerAsync() => _deskAnswer.GetAsync(() => JsonSerializer.SerializeToUtf8Bytes(DeskPayload(), Json), DeskFresh, Interlocked.Read(ref _postVersion));
        private Task<byte[]> LiveAnswerAsync() => _liveAnswer.GetAsync(() => JsonSerializer.SerializeToUtf8Bytes(LivePayload(), Json), DeskFresh, Interlocked.Read(ref _postVersion));
    }
}
