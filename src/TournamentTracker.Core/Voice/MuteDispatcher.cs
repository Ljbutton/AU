using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TournamentTracker.Discord;

namespace TournamentTracker.Voice
{
    public interface IVoiceApi
    {
        Task<DiscordResult> SetVoiceStateAsync(string botToken, string userId, VoiceState state, CancellationToken ct);
    }

    /// <summary>
    /// A voice API that takes the state at the moment it sends, after any rate-limit wait, so a
    /// change that waited seconds for Discord never goes out stale.
    /// </summary>
    public interface ILateVoiceApi : IVoiceApi
    {
        Task<DiscordResult> SetVoiceStateAsync(string botToken, string userId, Func<VoiceState> state, CancellationToken ct);
    }

    public sealed class DiscordVoiceApi : ILateVoiceApi
    {
        private readonly DiscordRest _rest;
        private readonly string _guildId;

        public DiscordVoiceApi(DiscordRest rest, string guildId)
        {
            _rest = rest;
            _guildId = guildId;
        }

        public Task<DiscordResult> SetVoiceStateAsync(string botToken, string userId, VoiceState state, CancellationToken ct) =>
            _rest.SetVoiceStateAsync(botToken, _guildId, userId, state.Mute, state.Deaf, ct);

        public Task<DiscordResult> SetVoiceStateAsync(string botToken, string userId, Func<VoiceState> state, CancellationToken ct) =>
            _rest.SetVoiceStateAsync(botToken, _guildId, userId, () => { var s = state(); return (s.Mute, s.Deaf); }, "Among Us automute", ct);
    }

    /// <summary>
    /// Applies voice states to Discord in the background. Callers only say what each member
    /// should be; the dispatcher works out what still has to change. A state that changes
    /// again before it was sent is simply replaced, so a quick meeting→tasks flip never
    /// queues stale requests. Each bot sends a few changes at once (Discord answers each in a
    /// fraction of a second, so one at a time left a 10-player lobby waiting seconds for the last
    /// mute); its rate limit is still honoured, and extra bots add more.
    /// </summary>
    public sealed class MuteDispatcher : IDisposable
    {
        /// <summary>A change that failed, waiting to be tried again.</summary>
        private sealed class Retry
        {
            public DateTime At;
            public int Failures;
            /// <summary>Discord said the member isn't in voice (a server mute can only change while they are).</summary>
            public bool NotInVoice;
            /// <summary>Since then, someone in voice was reported without them: they really had left.</summary>
            public bool SeenOut;
        }

        private readonly IVoiceApi _api;
        private readonly ILog _log;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly Dictionary<string, VoiceState> _desired = new Dictionary<string, VoiceState>();
        private readonly Dictionary<string, VoiceState> _applied = new Dictionary<string, VoiceState>();
        private readonly Dictionary<string, Retry> _blocked = new Dictionary<string, Retry>();
        /// <summary>How many of <see cref="_blocked"/> wait for their member to be in voice (read without the lock).</summary>
        private int _notInVoice;
        private readonly HashSet<string> _inFlight = new HashSet<string>();
        private readonly List<string> _queue = new List<string>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<Task> _workers = new List<Task>();

        /// <summary>Changes each bot has on the wire at once.</summary>
        public const int LanesPerBot = 4;

        public MuteDispatcher(IVoiceApi api, IReadOnlyList<string> botTokens, ILog log, Func<DateTime>? clock = null, int lanesPerBot = LanesPerBot)
        {
            if (botTokens.Count == 0) throw new ArgumentException("At least one bot token is required", nameof(botTokens));
            _api = api;
            _log = log;
            _clock = clock ?? (() => DateTime.UtcNow);
            foreach (var token in botTokens)
                for (int lane = 0; lane < Math.Max(1, lanesPerBot); lane++)
                    _workers.Add(Task.Run(() => WorkerAsync(token, _cts.Token)));
        }

        /// <summary>First delay after a member turned out not to be in voice; it doubles on each miss.</summary>
        public TimeSpan NotInVoiceRetry { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan MaxRetry { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Timed tries for a member who isn't in voice. After that the change waits for them to join
        /// voice (see <see cref="InVoice"/>) or for a new state, and is only tried every
        /// <see cref="NotInVoiceParked"/>, instead of using up the bot's rate limit every two
        /// minutes all evening for someone who went home.
        /// </summary>
        public int NotInVoiceTries { get; set; } = 3;

        /// <summary>
        /// How often a change is still tried once <see cref="NotInVoiceTries"/> are used up. It never
        /// stops altogether: <see cref="InVoice"/> needs the gateway, and while that is down (REST can
        /// still work) a member who came back would otherwise keep a server mute until the next phase.
        /// </summary>
        public TimeSpan NotInVoiceParked { get; set; } = TimeSpan.FromMinutes(5);

        public void SetDesired(string userId, VoiceState state)
        {
            lock (_lock)
            {
                bool changed = !_desired.TryGetValue(userId, out var old) || old != state;
                _desired[userId] = state;
                // Someone the gateway saw leave voice keeps waiting: Discord would only turn the
                // change down again. They get the newest state as soon as they are back (InVoice).
                if (changed && !GoneFromVoice(userId)) Unblock(userId);
                if (IsApplied(userId, state) || _blocked.ContainsKey(userId)) return;
                if (!_queue.Contains(userId)) _queue.Add(userId);
            }
            _signal.Release();
        }

        /// <summary>
        /// Sends everyone's state again, now: Discord is reachable again after The Button was
        /// closed (which unmutes everyone it had muted), so what was applied before can't be
        /// trusted. Only people known to be open and meant to stay open are left alone: The
        /// Button never touched them, and there can be dozens after a long evening.
        /// </summary>
        public void ResendAll()
        {
            int n;
            lock (_lock)
            {
                foreach (var id in _blocked.Keys.ToList()) Unblock(id);
                foreach (var kv in _desired)
                {
                    if (kv.Value == VoiceState.Open && _applied.TryGetValue(kv.Key, out var a) && a == VoiceState.Open) continue;
                    _applied.Remove(kv.Key);
                    if (!_queue.Contains(kv.Key)) _queue.Add(kv.Key);
                }
                n = _queue.Count;
            }
            if (n > 0) _signal.Release(n);
        }

        /// <summary>Someone's change waits for them to be in voice: worth telling the dispatcher who is (<see cref="InVoice"/>).</summary>
        public bool WaitingForVoice => Volatile.Read(ref _notInVoice) > 0;

        /// <summary>
        /// Everyone in a voice channel of the server right now (the gateway's view). A member whose
        /// change was held back because they had left voice gets it as soon as they are back,
        /// instead of at the next retry: a server mute set earlier sticks when they rejoin, so they
        /// would otherwise come back muted for up to two minutes.
        /// </summary>
        public void InVoice(IEnumerable<string> userIds)
        {
            if (!WaitingForVoice) return;
            var here = new HashSet<string>(userIds);
            int n = 0;
            lock (_lock)
            {
                foreach (var kv in _blocked.Where(b => b.Value.NotInVoice).ToList())
                {
                    if (!here.Contains(kv.Key))
                    {
                        kv.Value.SeenOut = true;
                        continue;
                    }
                    // Only a real return (seen out, now in). If the gateway shows them in voice all
                    // along, it disagrees with Discord's answer: timed retries carry on, slowly.
                    if (!kv.Value.SeenOut)
                    {
                        var soonest = _clock() + MaxRetry;
                        if (kv.Value.At > soonest) kv.Value.At = soonest;
                        continue;
                    }
                    Unblock(kv.Key);
                    if (_inFlight.Contains(kv.Key) || _queue.Contains(kv.Key)) continue;
                    _queue.Add(kv.Key);
                    n++;
                }
            }
            if (n > 0) _signal.Release(n);
        }

        public VoiceState? Applied(string userId)
        {
            lock (_lock) return _applied.TryGetValue(userId, out var s) ? s : (VoiceState?)null;
        }

        public IReadOnlyList<string> KnownUsers
        {
            get { lock (_lock) return _desired.Keys.Concat(_applied.Keys).Distinct().ToList(); }
        }

        /// <summary>Requests that are queued or on the wire, not counting members waiting to join voice.</summary>
        public int Pending
        {
            get { lock (_lock) return _queue.Count + _inFlight.Count; }
        }

        public async Task<bool> WaitIdleAsync(TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (Pending > 0)
            {
                if (DateTime.UtcNow >= until) return false;
                await Task.Delay(25).ConfigureAwait(false);
            }
            return true;
        }

        private bool IsApplied(string userId, VoiceState state) => _applied.TryGetValue(userId, out var a) && a == state;

        /// <summary>Discord said this member isn't in voice, and the gateway has since seen them out of it.</summary>
        private bool GoneFromVoice(string userId) =>
            _blocked.TryGetValue(userId, out var r) && r.NotInVoice && r.SeenOut;

        private void Unblock(string userId)
        {
            if (_blocked.TryGetValue(userId, out var r) && r.NotInVoice) _notInVoice--;
            _blocked.Remove(userId);
        }

        private void Block(string userId, Retry retry)
        {
            Unblock(userId);
            _blocked[userId] = retry;
            if (retry.NotInVoice) _notInVoice++;
        }

        /// <summary>The change mutes or deafens someone who isn't yet.</summary>
        private bool TakesAway(string userId, VoiceState state)
        {
            _applied.TryGetValue(userId, out var a);
            return (state.Mute && !a.Mute) || (state.Deaf && !a.Deaf);
        }

        private bool TryTake(out string userId, out VoiceState state)
        {
            lock (_lock)
            {
                var now = _clock();
                // Not someone already on the wire: their old retry time has passed, but a copy queued
                // now would be sent again the moment that try fails, whatever its new wait.
                foreach (var blocked in _blocked.Where(b => b.Value.At <= now && !_inFlight.Contains(b.Key)).Select(b => b.Key).ToList())
                {
                    if (!_queue.Contains(blocked)) _queue.Add(blocked);
                }

                // Taking voice away goes first: at a meeting the dead are muted before the living are
                // let go, and when it ends the living are muted before the dead get their voice back.
                // Discord only lets each bot change so many people a second, and whoever is last waits,
                // so a dead player mustn't be the one still talking while the others are sorted out.
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < _queue.Count; i++)
                    {
                        string id = _queue[i];
                        if (_inFlight.Contains(id)) continue;
                        var desired = _desired[id];
                        if (IsApplied(id, desired)) { _queue.RemoveAt(i--); continue; }
                        if (pass == 0 && !TakesAway(id, desired)) continue;
                        _queue.RemoveAt(i);
                        _inFlight.Add(id);
                        userId = id;
                        state = desired;
                        return true;
                    }
            }
            userId = "";
            state = default;
            return false;
        }

        /// <summary>What this member should be now (read just before a request goes out).</summary>
        private VoiceState Current(string userId, VoiceState fallback)
        {
            lock (_lock) return _desired.TryGetValue(userId, out var s) ? s : fallback;
        }

        private void Complete(string userId, VoiceState sent, DiscordResult result)
        {
            bool requeue;
            lock (_lock)
            {
                _inFlight.Remove(userId);
                if (result.Ok)
                {
                    _applied[userId] = sent;
                    Unblock(userId);
                }
                else
                {
                    // Not in voice yet (or at all). The state is still owed: a server mute set
                    // earlier sticks when they rejoin, so keep retrying with a growing delay, and
                    // after a few misses mostly wait for them to be back in voice (tried again only
                    // every NotInVoiceParked).
                    _applied.Remove(userId);
                    // Asked for again while this try was on the wire (the same state, every tick): the
                    // wait below applies all the same. A state that changed meanwhile is queued again
                    // further down.
                    _queue.Remove(userId);
                    int failures = _blocked.TryGetValue(userId, out var b) ? b.Failures + 1 : 1;
                    var baseDelay = result.IsNotInVoice ? NotInVoiceRetry : TimeSpan.FromSeconds(30);
                    var delay = TimeSpan.FromTicks(Math.Min(MaxRetry.Ticks, baseDelay.Ticks * (1L << Math.Min(failures - 1, 10))));
                    var at = _clock() + (result.IsNotInVoice && failures > NotInVoiceTries ? NotInVoiceParked : delay);
                    Block(userId, new Retry { At = at, Failures = failures, NotInVoice = result.IsNotInVoice });
                    if (!result.IsNotInVoice)
                        _log.Warn($"Could not set voice state for Discord user {userId}: {result}");
                }

                var desired = _desired[userId];
                requeue = desired != sent && !_queue.Contains(userId);
                if (requeue)
                {
                    Unblock(userId);
                    _queue.Add(userId);
                }
            }
            if (requeue) _signal.Release();
        }

        private async Task WorkerAsync(string token, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    while (!ct.IsCancellationRequested && TryTake(out var userId, out var state))
                    {
                        DiscordResult result;
                        var sent = state;
                        try
                        {
                            // The state is read again right before each try: if Discord's rate limit
                            // held the request back, the game may want something else by then.
                            if (_api is ILateVoiceApi late)
                                result = await late.SetVoiceStateAsync(token, userId, () => sent = Current(userId, state), ct).ConfigureAwait(false);
                            else
                                result = await _api.SetVoiceStateAsync(token, userId, state, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception e)
                        {
                            result = new DiscordResult { Ok = false, Body = e.Message };
                        }
                        Complete(userId, sent, result);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    _log.Error("Automute worker error: " + e);
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { Task.WaitAll(_workers.ToArray(), TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _cts.Dispose();
        }
    }
}
