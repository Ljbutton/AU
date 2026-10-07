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

    public sealed class DiscordVoiceApi : IVoiceApi
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
    }

    /// <summary>
    /// Applies voice states to Discord in the background. Callers only say what each member
    /// should be; the dispatcher works out what still has to change. A state that changes
    /// again before it was sent is simply replaced, so a quick meeting→tasks flip never
    /// queues stale requests. One worker runs per bot token so extra bots add throughput.
    /// </summary>
    public sealed class MuteDispatcher : IDisposable
    {
        private readonly IVoiceApi _api;
        private readonly ILog _log;
        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly Dictionary<string, VoiceState> _desired = new Dictionary<string, VoiceState>();
        private readonly Dictionary<string, VoiceState> _applied = new Dictionary<string, VoiceState>();
        private readonly Dictionary<string, (DateTime RetryAt, int Failures)> _blocked = new Dictionary<string, (DateTime, int)>();
        private readonly HashSet<string> _inFlight = new HashSet<string>();
        private readonly List<string> _queue = new List<string>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<Task> _workers = new List<Task>();

        public MuteDispatcher(IVoiceApi api, IReadOnlyList<string> botTokens, ILog log, Func<DateTime>? clock = null)
        {
            if (botTokens.Count == 0) throw new ArgumentException("At least one bot token is required", nameof(botTokens));
            _api = api;
            _log = log;
            _clock = clock ?? (() => DateTime.UtcNow);
            foreach (var token in botTokens)
                _workers.Add(Task.Run(() => WorkerAsync(token, _cts.Token)));
        }

        /// <summary>First delay after a member turned out not to be in voice; it doubles on each miss.</summary>
        public TimeSpan NotInVoiceRetry { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan MaxRetry { get; set; } = TimeSpan.FromMinutes(2);

        public void SetDesired(string userId, VoiceState state)
        {
            lock (_lock)
            {
                bool changed = !_desired.TryGetValue(userId, out var old) || old != state;
                _desired[userId] = state;
                if (changed) _blocked.Remove(userId);
                if (IsApplied(userId, state) || _blocked.ContainsKey(userId)) return;
                if (!_queue.Contains(userId)) _queue.Add(userId);
            }
            _signal.Release();
        }

        /// <summary>
        /// Sends everyone's state again, now: Discord is reachable again after The Button was
        /// closed (which unmutes everyone it had muted), so what was applied before can't be trusted.
        /// </summary>
        public void ResendAll()
        {
            int n;
            lock (_lock)
            {
                _applied.Clear();
                _blocked.Clear();
                foreach (var id in _desired.Keys)
                    if (!_queue.Contains(id)) _queue.Add(id);
                n = _queue.Count;
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

        private bool TryTake(out string userId, out VoiceState state)
        {
            lock (_lock)
            {
                var now = _clock();
                foreach (var blocked in _blocked.Where(b => b.Value.RetryAt <= now).Select(b => b.Key).ToList())
                {
                    if (!_queue.Contains(blocked)) _queue.Add(blocked);
                }

                for (int i = 0; i < _queue.Count; i++)
                {
                    string id = _queue[i];
                    if (_inFlight.Contains(id)) continue;
                    _queue.RemoveAt(i--);
                    var desired = _desired[id];
                    if (IsApplied(id, desired)) continue;
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

        private void Complete(string userId, VoiceState sent, DiscordResult result)
        {
            bool requeue;
            lock (_lock)
            {
                _inFlight.Remove(userId);
                if (result.Ok)
                {
                    _applied[userId] = sent;
                    _blocked.Remove(userId);
                }
                else
                {
                    // Not in voice yet (or at all). The state is still owed: a server mute set
                    // earlier sticks when they rejoin, so keep retrying with a growing delay.
                    _applied.Remove(userId);
                    int failures = _blocked.TryGetValue(userId, out var b) ? b.Failures + 1 : 1;
                    var baseDelay = result.IsNotInVoice ? NotInVoiceRetry : TimeSpan.FromSeconds(30);
                    var delay = TimeSpan.FromTicks(Math.Min(MaxRetry.Ticks, baseDelay.Ticks * (1L << Math.Min(failures - 1, 10))));
                    _blocked[userId] = (_clock() + delay, failures);
                    if (!result.IsNotInVoice)
                        _log.Warn($"Could not set voice state for Discord user {userId}: {result}");
                }

                var desired = _desired[userId];
                requeue = desired != sent && !_queue.Contains(userId);
                if (requeue)
                {
                    _blocked.Remove(userId);
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
                        try
                        {
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
                        Complete(userId, state, result);
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
