using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>One option in a poll, prediction or chat vote, as the stream shows it.</summary>
    public sealed class TwitchOption
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        /// <summary>The player's colour (their crewmate head), when it's a player.</summary>
        public int? Color { get; set; }
        /// <summary>"imp" or "crew" for the team outcomes (their heads).</summary>
        public string? Icon { get; set; }
        /// <summary>The player key or lobby it stands for.</summary>
        public string Value { get; set; } = "";
        public long Votes { get; set; }
        public long Points { get; set; }
    }

    public sealed class TwitchItem
    {
        /// <summary>"poll", "prediction" or "chat" (!sus).</summary>
        public string Type { get; set; } = "";
        /// <summary>game, first, moreWins (predictions); meeting, lobby, mvp (polls and chat votes).</summary>
        public string Kind { get; set; } = "";
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public List<TwitchOption> Options { get; set; } = new List<TwitchOption>();
        public string Status { get; set; } = "open";      // open, locked, ended, canceled
        public DateTime Started { get; set; }
        public DateTime EndsAt { get; set; }
        public string? Lobby { get; set; }
        public string? Game { get; set; }
        public int Round { get; set; }
        public List<string> Lobbies { get; set; } = new List<string>();
        /// <summary>More wins this round: each lobby's first ending since it opened.</summary>
        public Dictionary<string, string> Endings { get; set; } = new Dictionary<string, string>();
        /// <summary>!sus: each viewer's latest vote (viewer id → (login, option)).</summary>
        public Dictionary<string, (string Login, string Option)> Voters { get; set; } = new Dictionary<string, (string, string)>();
        public string? Ejected { get; set; }
        /// <summary>A meeting vote whose ejection has been shown.</summary>
        public bool Done { get; set; }
    }

    /// <summary>A Chat Detective: how many of their !sus votes picked an impostor.</summary>
    public sealed class Detective
    {
        public string Login { get; set; } = "";
        public string Name { get; set; } = "";
        public int Correct { get; set; }
        public int Guesses { get; set; }
        public Dictionary<int, int> RoundCorrect { get; set; } = new Dictionary<int, int>();
    }

    /// <summary>A channel point redemption waiting for the caster.</summary>
    public sealed class Redemption
    {
        public string Id { get; set; } = "";
        public string RewardId { get; set; } = "";
        public string Kind { get; set; } = "";             // replay, shoutout
        public string User { get; set; } = "";
        public string Input { get; set; } = "";
        public string? PlayerKey { get; set; }
        public string? PlayerName { get; set; }
        public string? Flag { get; set; }
        public string Status { get; set; } = "waiting";    // waiting, approved, denied
        public DateTime At { get; set; }
    }

    /// <summary>
    /// Part 23: Twitch. Predictions on the featured lobby's game (opened at its start, locked after a
    /// while, resolved at its end, refunded if it's interrupted) and on the round; "who's the
    /// impostor?" polls and a !sus chat vote in meetings on stream; Chat Detective; "which lobby
    /// next?" (the caster approves the switch) and the round MVP; channel point rewards for replays
    /// and shoutouts (approved by the caster); everything shown on stream. Nothing here comes from
    /// the caster's own view: options are shuffled and never say who the impostors are; whether chat
    /// was right is said only once the game has shown it.
    /// </summary>
    public sealed class TwitchDirector : IDisposable
    {
        private readonly CasterDesk _desk;
        private readonly string? _settingsPath, _detectivesPath;
        private readonly HttpClient _http;
        private readonly Func<DateTime> _clock;
        private readonly Random _random;
        private readonly object _lock = new object();
        private readonly List<(DateTime Due, string What, Func<Task> Act)> _due = new List<(DateTime, string, Func<Task>)>();
        private readonly List<(DateTime At, string Text)> _log = new List<(DateTime, string)>();
        private readonly Dictionary<string, Detective> _detectives = new Dictionary<string, Detective>();
        private readonly List<Redemption> _redemptions = new List<Redemption>();
        private readonly Dictionary<string, string> _rewards = new Dictionary<string, string>();     // reward id → kind
        private readonly List<(string Game, TwitchItem Item)> _toReveal = new List<(string, TwitchItem)>();
        private readonly HashSet<int> _roundsDone = new HashSet<int>();
        private readonly List<TwitchItem> _meetings = new List<TwitchItem>();
        private TwitchEventSub? _events;
        private DateTime _lastLobbyPoll = DateTime.MinValue, _lastHot = DateTime.MinValue, _nextFake;

        public TwitchSettings Settings { get; }
        public TwitchAuth Auth { get; }
        public ITwitchApi? Api { get; private set; }
        public TwitchUser? User { get; private set; }
        public string? Problem { get; private set; }
        public TwitchItem? Prediction { get; private set; }
        public TwitchItem? Poll { get; private set; }
        public TwitchItem? Vote { get; private set; }
        /// <summary>The results graphic on stream, until when.</summary>
        public (object Data, DateTime Until)? Result { get; private set; }
        /// <summary>The sign-in code waiting to be entered.</summary>
        public (string Code, string Url, DateTime Until, string Device, int Interval)? SignIn { get; private set; }
        private DateTime _nextSignInPoll;

        /// <summary>Saved replays (id, title, lobby), newest first, and playing one (Part 6).</summary>
        public Func<List<(string Id, string Title, string Lobby)>>? Clips { get; set; }
        public Func<string, Task<string>>? PlayReplay { get; set; }
        /// <summary>Listen to Twitch's events once connected (off in tests).</summary>
        public bool StartEvents { get; set; } = true;
        /// <summary>Test mode's pretend audience (votes, chat, redemptions); off in tests.</summary>
        public bool FakeAudience { get; set; } = true;

        public TwitchDirector(CasterDesk desk, string? folder, HttpClient http, Func<DateTime>? clock = null, Random? random = null)
        {
            _desk = desk;
            _http = http;
            _clock = clock ?? (() => DateTime.UtcNow);
            _random = random ?? new Random();
            _settingsPath = folder == null ? null : Path.Combine(folder, TwitchSettings.FileName);
            _detectivesPath = folder == null ? null : Path.Combine(folder, "twitch-detectives.json");
            Settings = TwitchSettings.Load(_settingsPath);
            Auth = new TwitchAuth(http, () => Settings.ClientId, folder == null ? null : Path.Combine(folder, "twitch-token.json"), _clock);
            try
            {
                if (_detectivesPath != null && File.Exists(_detectivesPath))
                    foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, Detective>>(File.ReadAllText(_detectivesPath)) ?? new Dictionary<string, Detective>()) _detectives[kv.Key] = kv.Value;
            }
            catch (Exception) { }
            desk.Fed += OnFed;
            desk.Interrupted += it => Later("interrupted " + it.Game, () => GameOverAsync(it.Lobby, it.Game, null, true, new HashSet<string>()));
        }

        public void Save() => Settings.Save(_settingsPath);

        private void Log(string text)
        {
            lock (_lock)
            {
                _log.Insert(0, (_clock(), text));
                if (_log.Count > 120) _log.RemoveAt(_log.Count - 1);
            }
        }

        /// <summary>Polls, predictions and channel points need Affiliate or Partner.</summary>
        public bool CanPoll => Api != null && User?.BroadcasterType is "affiliate" or "partner";
        public bool Connected => Api != null;

        // ---- Connecting ---------------------------------------------------------------------------

        /// <summary>Connects (the fake in test mode, else Twitch with the saved sign-in) and says what won't work.</summary>
        public async Task<string> ConnectAsync()
        {
            Disconnect();
            if (Settings.TestMode)
            {
                Api = new FakeTwitchApi(_random);
                User = await Api.MeAsync().ConfigureAwait(false);
                Problem = null;
                await EnsureRewardsAsync().ConfigureAwait(false);
                Log("Test mode: fake Twitch (nothing is sent).");
                return "Test mode: a fake Twitch channel. Votes, chat and redemptions are made up here.";
            }
            if (Settings.ClientId.Length == 0) return Fail("Put your Twitch application's Client ID in first (dev.twitch.tv → Your Console → Register Your Application, client type Public).");
            if (Auth.Token == null) return Fail("Not signed in: press Sign in.");
            try
            {
                await Auth.ValidateAsync().ConfigureAwait(false);
                var api = new HelixApi(_http, Auth, () => Settings.ClientId);
                User = await api.MeAsync().ConfigureAwait(false);
                Api = api;
                var notes = new List<string>();
                if (!CanPoll) notes.Add("This channel isn't Affiliate or Partner: Twitch only lets those run polls, predictions and channel point rewards, so those are off. The !sus chat vote and Chat Detective still work.");
                var missing = Auth.Missing();
                if (missing.Count > 0) notes.Add("Sign in again to allow: " + string.Join(", ", missing) + ".");
                Problem = notes.Count == 0 ? null : string.Join(" ", notes);
                if (StartEvents)
                {
                    _events = new TwitchEventSub(api, () => User!.Id);
                    _events.Event += (type, ev) => Handle(type, ev);
                    _events.Start();
                }
                await EnsureRewardsAsync().ConfigureAwait(false);
                Log($"Connected as {User.Name} ({(User.BroadcasterType.Length > 0 ? User.BroadcasterType : "not Affiliate/Partner")}).");
                return $"Connected to Twitch as {User.Name}." + (Problem != null ? " " + Problem : "");
            }
            catch (Exception e) { return Fail("Twitch: " + e.Message); }
        }

        private string Fail(string why) { Problem = why; Log(why); return why; }

        public void Disconnect()
        {
            _events?.Dispose();
            _events = null;
            Api = null;
            User = null;
        }

        /// <summary>Signing in: a code to enter at twitch.tv/activate; The Button waits for it.</summary>
        public async Task<string> StartSignInAsync()
        {
            if (Settings.ClientId.Length == 0) return Fail("Put your Twitch application's Client ID in first.");
            try
            {
                var (device, code, url, interval, expires) = await Auth.StartAsync().ConfigureAwait(false);
                SignIn = (code, url, _clock().AddSeconds(expires), device, interval);
                _nextSignInPoll = _clock().AddSeconds(interval);
                return $"Go to {url} signed in as your channel and enter {code}.";
            }
            catch (Exception e) { return Fail("Twitch sign-in: " + e.Message); }
        }

        private async Task SignInTickAsync()
        {
            if (SignIn is not { } s || _clock() < _nextSignInPoll) return;
            if (_clock() > s.Until) { SignIn = null; Fail("The sign-in code ran out: press Sign in again."); return; }
            _nextSignInPoll = _clock().AddSeconds(Math.Max(1, s.Interval));
            try
            {
                if (await Auth.PollAsync(s.Device).ConfigureAwait(false) == null) return;
                SignIn = null;
                Log("Signed in to Twitch as " + Auth.Token!.Login + ".");
                await ConnectAsync().ConfigureAwait(false);
            }
            catch (Exception e) { SignIn = null; Fail("Twitch sign-in: " + e.Message); }
        }

        public void SignOut() { Disconnect(); Auth.SignOut(); Log("Signed out of Twitch."); }

        /// <summary>The two rewards (made by The Button, so it may manage their redemptions).</summary>
        private async Task EnsureRewardsAsync()
        {
            if (!CanPoll || Api == null) return;
            try
            {
                var have = await Api.RewardsAsync().ConfigureAwait(false);
                async Task One(string kind, string feature, string title, int cost, string prompt, bool input)
                {
                    var r = have.FirstOrDefault(x => x.Title == title);
                    string? id = r?.Id;
                    if (id == null && Settings.Feature(feature)) id = await Api.CreateRewardAsync(title, cost, prompt, input).ConfigureAwait(false);
                    if (id != null) lock (_lock) _rewards[id] = kind;
                }
                await One("replay", "replayReward", "Request a replay", Settings.ReplayCost, "Which moment? The caster picks what plays.", false).ConfigureAwait(false);
                await One("shoutout", "shoutoutReward", "Shoutout a player", Settings.ShoutoutCost, "Which player? Their name as on the tournament list.", true).ConfigureAwait(false);
            }
            catch (Exception e) { Log("Channel point rewards: " + e.Message); }
        }

        // ---- Timing --------------------------------------------------------------------------------

        /// <summary>After the stream delay (0 by default): Twitch acts when viewers see it happen.</summary>
        private void Later(string what, Func<Task> act)
        {
            lock (_lock) _due.Add((_clock().AddSeconds(Math.Max(0, Settings.DelaySeconds)), what, act));
        }

        /// <summary>Once a second: what's due, sign-in, calm moments, round ends, and test mode's fake traffic.</summary>
        public async Task TickAsync()
        {
            await SignInTickAsync().ConfigureAwait(false);
            var now = _clock();
            List<(DateTime Due, string What, Func<Task> Act)> ready;
            lock (_lock) { ready = _due.Where(d => d.Due <= now).ToList(); _due.RemoveAll(d => d.Due <= now); }
            foreach (var d in ready)
            {
                try { await d.Act().ConfigureAwait(false); }
                catch (Exception e) { Log($"{d.What}: {e.Message}"); }
            }
            if (Settings.Off || Api == null) return;
            // Polls and chat votes run out; a prediction's window closes.
            if (Vote is { Status: "open" } v && now >= v.EndsAt) v.Status = "ended";
            if (Api is FakeTwitchApi fake && FakeAudience) await FakeTrafficAsync(fake).ConfigureAwait(false);
            if (Prediction is { Status: "open" } p && now >= p.EndsAt) p.Status = "locked";
            if (Poll is { Status: "open" } pl && now >= pl.EndsAt.AddSeconds(3)) await PollEndedAsync(pl).ConfigureAwait(false);

            if (SimHold) return;
            var ranking = _desk.Board.Ranking();
            if (ranking.Any(r => r.Online && r.Tier is "must" or "veryHigh")) _lastHot = now;
            var air = _desk.OnAir;
            // Round over (intermission): the MVP poll and Chat Detective shoutouts.
            int round = _desk.Tables.CurrentRound;
            if (air.Layout == "intermission" && round > 0 && _roundsDone.Add(round))
            {
                if (Settings.AutoOn("mvpPoll")) await StartMvpPollAsync().ConfigureAwait(false);
                if (Settings.Feature("detectives")) await DetectiveShoutoutAsync(round).ConfigureAwait(false);
            }
            // A calm moment: "which lobby next?".
            if (Settings.AutoOn("lobbyPoll") && Poll == null && Vote is not { Status: "open" } && air.Layout is "full" or "2up" or "4up" or "grid"
                && (now - _lastHot).TotalSeconds > 20 && (now - _lastLobbyPoll).TotalMinutes >= Settings.LobbyPollEveryMinutes)
                await StartLobbyPollAsync().ConfigureAwait(false);
        }

        // ---- What the games do ----------------------------------------------------------------------

        /// <summary>The lobby predictions follow: picked by the caster, else the one on stream, else the top one.</summary>
        public string? Featured()
        {
            if (Settings.Featured.Length > 0) return Settings.Featured;
            var air = _desk.OnAir;
            if (air.Layout is "full" or "2up" or "4up" or "break" && air.Slots.FirstOrDefault(x => x != null) is { } on) return on;
            return _desk.Board.Ranking().FirstOrDefault(r => r.Online)?.Lobby;
        }

        private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private string? KeyOf(string lobby, JsonElement player)
        {
            if (player.ValueKind != JsonValueKind.Object) return null;
            int id = player.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : -1;
            var l = _desk.Board.Lobby(lobby);
            if (l != null && l.People.TryGetValue(id, out var p) && p.Key.Length > 0) return p.Key;
            return Str(player, "key") ?? (id >= 0 ? "id:" + id : null);
        }

        /// <summary>Simulated games never start anything on a real channel by themselves (Twitch's own test mode is fine).</summary>
        private bool SimHold => _desk.Simulating && !Settings.TestMode;

        private void OnFed(string lobby, JsonElement m)
        {
            if (Settings.Off || SimHold && CasterDesk.IsSimLobby(lobby)) return;
            string kind = Str(m, "kind") ?? "";
            string game = Str(m, "game") ?? "";
            int round = m.TryGetProperty("round", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
            switch (kind)
            {
                case "gameStart":
                    if (Settings.AutoOn("predictions") && string.Equals(lobby, Featured(), StringComparison.OrdinalIgnoreCase))
                        Later("prediction " + game, () => StartGamePredictionAsync(lobby, game, round));
                    break;
                case "meeting":
                {
                    var air = _desk.OnAir;
                    if (!Settings.AutoOn("meetings") || !air.Has(lobby) || air.Layout == "replay") break;
                    // Who's alive now, as the meeting screen shows everyone.
                    var people = Alive(lobby);
                    Later("meeting " + game, () => StartMeetingAsync(lobby, game, round, people));
                    break;
                }
                case "eject":
                {
                    string? ejected = m.TryGetProperty("ejected", out var e) && e.ValueKind == JsonValueKind.Object ? KeyOf(lobby, e) : null;
                    bool? was = m.TryGetProperty("wasImpostor", out var w) && w.ValueKind is JsonValueKind.True or JsonValueKind.False ? w.GetBoolean() : null;
                    Later("eject " + game, () => EjectAsync(lobby, game, ejected, was));
                    break;
                }
                case "gameEnd":
                {
                    bool abandoned = m.TryGetProperty("abandoned", out var ab) && ab.ValueKind == JsonValueKind.True;
                    var imps = m.TryGetProperty("impostors", out var ip) && ip.ValueKind == JsonValueKind.Array
                        ? ip.EnumerateArray().Select(x => KeyOf(lobby, x)).Where(x => x != null).Select(x => x!).ToHashSet()
                        : new HashSet<string>();
                    if (imps.Count == 0 && _desk.Board.Lobby(lobby) is { } l) imps = l.People.Values.Where(p => p.Imp == true).Select(p => p.Key).ToHashSet();
                    string? winner = Str(m, "winner");
                    Later("game end " + game, () => GameOverAsync(lobby, game, winner, abandoned, imps));
                    break;
                }
            }
        }

        /// <summary>Living players (real names, colours) in a lobby, in a random order: nothing about who they are.</summary>
        private List<TwitchOption> Alive(string lobby)
        {
            var l = _desk.Board.Lobby(lobby);
            if (l == null) return new List<TwitchOption>();
            var list = l.People.Values.Where(p => p.Dead != true).OrderBy(p => p.Id)
                .Select(p => new TwitchOption { Title = _desk.Board.DisplayName(l, p.Id, p.Name), Color = p.Color, Value = p.Key.Length > 0 ? p.Key : "id:" + p.Id }).ToList();
            return Shuffle(list);
        }

        private List<T> Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--) { int j = _random.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
            return list;
        }

        // ---- Predictions ------------------------------------------------------------------------------

        private bool Ready(string what, string feature, bool needsPartner = true)
        {
            if (Settings.Off) { Log($"{what}: Twitch is off."); return false; }
            if (!Settings.Feature(feature)) { Log($"{what}: switched off."); return false; }
            if (Api == null) { Log($"{what}: not connected to Twitch."); return false; }
            if (needsPartner && !CanPoll) { Log($"{what}: needs Affiliate or Partner."); return false; }
            return true;
        }

        private async Task<bool> OpenPredictionAsync(TwitchItem item)
        {
            if (Prediction is { Status: "open" or "locked" } busy) { Log($"\"{item.Title}\" not started: \"{busy.Title}\" is still running (Twitch allows one at a time)."); return false; }
            var (id, outcomes) = await Api!.CreatePredictionAsync(item.Title, item.Options.Select(o => o.Title).ToList(), Settings.PredictionSeconds).ConfigureAwait(false);
            item.Id = id;
            for (int i = 0; i < item.Options.Count && i < outcomes.Count; i++) item.Options[i].Id = outcomes[i].Id;
            item.Started = _clock();
            item.EndsAt = _clock().AddSeconds(TwitchLimits.Clamp(Settings.PredictionSeconds, TwitchLimits.PredictionMinSeconds, TwitchLimits.PredictionMaxSeconds));
            Prediction = item;
            Log("Prediction: " + item.Title);
            return true;
        }

        /// <summary>"LJ: Impostors or Crewmates?" for a lobby's game (the featured lobby's, or the one given).</summary>
        public async Task<string> StartGamePredictionAsync(string? lobby = null, string? game = null, int round = 0)
        {
            lobby ??= Featured();
            if (lobby == null) return "No lobby to predict on.";
            if (!Ready("Prediction", "predictions")) return "Can't start a prediction now: see the log.";
            var l = _desk.Board.Lobby(lobby);
            // From the tab: the game it's playing now. At a game's start the lobby says so before its first snapshot.
            if (game == null && l?.Phase is not ("ingame" or "meeting")) return $"{lobby} isn't in a game.";
            game ??= l?.Game;
            if (game == null) return $"{lobby} isn't in a game.";
            var item = new TwitchItem
            {
                Type = "prediction", Kind = "game", Lobby = lobby, Game = game, Round = round > 0 ? round : l?.Round ?? 0,
                Title = TwitchLimits.Cut($"{lobby}: Impostors or Crewmates?", TwitchLimits.PredictionTitle),
                Options = Shuffle(new List<TwitchOption> { new() { Title = "Impostors", Icon = "imp", Value = "Impostors" }, new() { Title = "Crewmates", Icon = "crew", Value = "Crewmates" } }),
            };
            return await OpenPredictionAsync(item).ConfigureAwait(false) ? $"Prediction open: {item.Title}" : "A prediction is already running.";
        }

        /// <summary>Round predictions: "first" (which lobby finishes first) or "moreWins" (more impostor or crew wins).</summary>
        public async Task<string> StartRoundPredictionAsync(string kind)
        {
            if (!Ready("Round prediction", "roundPredictions")) return "Can't start a prediction now: see the log.";
            var playing = _desk.Board.Ranking().Where(r => r.Online && r.Phase is "ingame" or "meeting").Select(r => r.Lobby).ToList();
            int round = _desk.Tables.CurrentRound;
            TwitchItem item;
            if (kind == "first")
            {
                if (playing.Count < 2) return "It needs two lobbies mid-game.";
                item = new TwitchItem { Type = "prediction", Kind = "first", Round = round, Lobbies = playing.Take(TwitchLimits.MaxOutcomes).ToList(), Title = "Which lobby finishes first?",
                    Options = Shuffle(playing.Take(TwitchLimits.MaxOutcomes).Select(l => new TwitchOption { Title = l, Value = l }).ToList()) };
            }
            else
            {
                if (playing.Count < 1) return "No lobby is mid-game.";
                item = new TwitchItem { Type = "prediction", Kind = "moreWins", Round = round, Lobbies = playing, Title = "This round: more impostor or crew wins?",
                    Options = Shuffle(new List<TwitchOption> { new() { Title = "More impostor wins", Icon = "imp", Value = "Impostors" }, new() { Title = "More crew wins", Icon = "crew", Value = "Crewmates" } }) };
            }
            return await OpenPredictionAsync(item).ConfigureAwait(false) ? "Prediction open: " + item.Title : "A prediction is already running.";
        }

        /// <summary>The caster's buttons: lock, cancel (refund), or resolve with an outcome.</summary>
        public async Task<string> PredictionActionAsync(string action, string? outcome = null)
        {
            var p = Prediction;
            if (p == null || Api == null || p.Status is "ended" or "canceled") return "No prediction running.";
            switch (action)
            {
                case "lock":
                    await Api.EndPredictionAsync(p.Id, "LOCKED").ConfigureAwait(false);
                    p.Status = "locked";
                    return "Prediction locked.";
                case "cancel":
                    await CancelPredictionAsync(p, "canceled by the caster").ConfigureAwait(false);
                    return "Prediction canceled: everyone gets their points back.";
                default:
                    var o = p.Options.FirstOrDefault(x => x.Id == outcome || x.Value == outcome);
                    if (o == null) return "Pick the outcome that won.";
                    await ResolveAsync(p, o).ConfigureAwait(false);
                    return $"Prediction resolved: {o.Title}.";
            }
        }

        private async Task ResolveAsync(TwitchItem p, TwitchOption winner)
        {
            if (p.Status == "open") await Api!.EndPredictionAsync(p.Id, "LOCKED").ConfigureAwait(false);
            await Api!.EndPredictionAsync(p.Id, "RESOLVED", winner.Id).ConfigureAwait(false);
            p.Status = "ended";
            long total = p.Options.Sum(o => o.Points);
            ShowResult(new
            {
                type = "prediction", title = p.Title,
                lines = p.Options.Select(o => new { name = o.Title, color = o.Color, icon = o.Icon, pct = total > 0 ? (int)Math.Round(100.0 * o.Points / total) : 0, win = o == winner }).ToList(),
                text = $"{winner.Title}!",
            });
            Log($"Prediction resolved: {winner.Title}.");
        }

        /// <summary>Simulation stopped: anything running about its fake lobbies is cancelled (points refunded) or ended.</summary>
        public async Task ForgetAsync(Func<string, bool> which)
        {
            bool About(TwitchItem? x) => x != null && (x.Lobby != null && which(x.Lobby) || x.Lobbies.Any(which) || x.Options.Any(o => o.Value != null && which(o.Value)));
            try
            {
                if (Prediction is { Status: "open" or "locked" } p && About(p) && Api != null) await CancelPredictionAsync(p, "simulation stopped").ConfigureAwait(false);
                if (About(Prediction)) Prediction = null;
                if (Poll is { } pl && About(pl)) { if (Api != null) await Api.EndPollAsync(pl.Id).ConfigureAwait(false); Poll = null; }
                if (About(Vote)) Vote = null;
            }
            catch (Exception e) { Log("Stopping simulation: " + e.Message); }
            lock (_lock)
            {
                _meetings.RemoveAll(x => About(x));
                _toReveal.RemoveAll(x => About(x.Item));
                _due.RemoveAll(d => d.What.Split(' ').Any(which));
            }
            if (Result != null) Result = null;
        }

        private async Task CancelPredictionAsync(TwitchItem p, string why)
        {
            await Api!.EndPredictionAsync(p.Id, "CANCELED").ConfigureAwait(false);
            p.Status = "canceled";
            Log($"Prediction canceled ({why}): points refunded.");
        }

        // ---- Meetings: "who's the impostor?" and !sus ------------------------------------------------

        private async Task StartMeetingAsync(string lobby, string game, int round, List<TwitchOption> people)
        {
            if (people.Count < 2) return;
            string mode = Settings.MeetingMode;
            bool poll = (mode is "poll" or "both" || mode == "auto" && people.Count <= TwitchLimits.PollMaxChoices) && Settings.Feature("meetingPolls") && CanPoll && Poll == null;
            bool chat = (mode is "chat" or "both" || mode == "auto" && !poll) && Settings.Feature("chatVote") && Api != null;
            await StartMeetingVoteAsync(lobby, game, round, people, poll, chat).ConfigureAwait(false);
        }

        /// <summary>From the tab: a poll and/or a !sus vote for the lobby on stream (its meeting now).</summary>
        public async Task<string> StartMeetingFromTabAsync(string type)
        {
            var air = _desk.OnAir;
            string? lobby = air.Slots.FirstOrDefault(x => x != null);
            var l = lobby == null ? null : _desk.Board.Lobby(lobby);
            if (l == null || l.Phase != "meeting") return "The lobby on stream isn't in a meeting.";
            bool poll = type == "poll";
            if (poll && !Ready("Who's the impostor? poll", "meetingPolls")) return "Can't start a poll now: see the log.";
            if (!poll && !Ready("!sus vote", "chatVote", false)) return "Can't start a chat vote now: see the log.";
            await StartMeetingVoteAsync(lobby!, l.Game ?? "", l.Round, Alive(lobby!), poll, !poll).ConfigureAwait(false);
            return poll ? "Poll open: who's the impostor?" : "Chat vote open: !sus <name>.";
        }

        private async Task StartMeetingVoteAsync(string lobby, string game, int round, List<TwitchOption> people, bool poll, bool chat)
        {
            string title = TwitchLimits.Cut($"Who's the impostor? ({lobby})", TwitchLimits.PollTitle);
            int seconds = TwitchLimits.Clamp(Settings.MeetingSeconds, TwitchLimits.PollMinSeconds, TwitchLimits.PollMaxSeconds);
            if (poll && Poll == null)
            {
                // Up to five: a random five when more are alive (never picked by who they are).
                var choices = people.Take(TwitchLimits.PollMaxChoices).Select(o => new TwitchOption { Title = o.Title, Color = o.Color, Value = o.Value }).ToList();
                var (id, created) = await Api!.CreatePollAsync(title, choices.Select(c => c.Title).ToList(), seconds).ConfigureAwait(false);
                for (int i = 0; i < choices.Count && i < created.Count; i++) { choices[i].Id = created[i].Id; choices[i].Title = created[i].Title; }
                Poll = new TwitchItem { Type = "poll", Kind = "meeting", Id = id, Title = title, Options = choices, Lobby = lobby, Game = game, Round = round, Started = _clock(), EndsAt = _clock().AddSeconds(seconds) };
                lock (_lock) _meetings.Add(Poll);
                Log("Poll: " + title);
            }
            if (chat)
            {
                Vote = new TwitchItem { Type = "chat", Kind = "meeting", Id = "sus" + _clock().Ticks, Title = title, Options = people.Select(o => new TwitchOption { Id = o.Value, Title = o.Title, Color = o.Color, Value = o.Value }).ToList(),
                    Lobby = lobby, Game = game, Round = round, Started = _clock(), EndsAt = _clock().AddSeconds(seconds) };
                lock (_lock) { _meetings.Add(Vote); if (_meetings.Count > 20) _meetings.RemoveAt(0); }
                Log("Chat vote: " + title);
                try { await Api!.SendChatAsync(TwitchLimits.Cut($"Who's the impostor in {lobby}? Type !sus and a name: {string.Join(", ", people.Select(p => p.Title))}", TwitchLimits.ChatMessage)).ConfigureAwait(false); }
                catch (Exception e) { Log("Chat: " + e.Message); }
            }
        }

        /// <summary>A chat message: "!sus jake" (a name, a first name, an in-game name or a colour) counts once per viewer.</summary>
        public void Chat(string userId, string login, string text)
        {
            var v = Vote;
            if (v == null || v.Status != "open" || Settings.Off) return;
            var m = Regex.Match(text.Trim(), @"^!sus\s+(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return;
            var pick = MatchPlayer(m.Groups[1].Value, v);
            if (pick == null) return;
            lock (_lock) v.Voters[userId] = (login, pick.Value);
            foreach (var o in v.Options) o.Votes = v.Voters.Values.Count(x => x.Option == o.Value);
        }

        private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "");

        private TwitchOption? MatchPlayer(string said, TwitchItem v)
        {
            string n = Norm(said);
            if (n.Length == 0) return null;
            var l = v.Lobby == null ? null : _desk.Board.Lobby(v.Lobby);
            IEnumerable<string> Names(TwitchOption o)
            {
                yield return o.Title;
                yield return o.Title.Split(' ')[0];
                var p = l?.People.Values.FirstOrDefault(x => x.Key == o.Value || "id:" + x.Id == o.Value);
                if (p != null) { yield return p.Name; yield return Colors.Name(p.Color); }
            }
            return v.Options.FirstOrDefault(o => Names(o).Any(x => Norm(x) == n))
                ?? (n.Length >= 3 ? v.Options.Where(o => Names(o).Any(x => Norm(x).StartsWith(n, StringComparison.Ordinal))).Take(2).ToList() is { Count: 1 } one ? one[0] : null : null);
        }

        private TwitchOption? Top(TwitchItem item) => item.Options.Where(o => o.Votes > 0).OrderByDescending(o => o.Votes).FirstOrDefault();

        private async Task EjectAsync(string lobby, string game, string? ejected, bool? wasImpostor)
        {
            List<TwitchItem> open;
            lock (_lock) open = _meetings.Where(x => !x.Done && x.Lobby == lobby && x.Game == game && x.Status != "canceled").ToList();
            foreach (var item in open)
            {
                item.Done = true;
                if (item.Type == "poll" && item.Status == "open") { try { await Api!.EndPollAsync(item.Id).ConfigureAwait(false); } catch (Exception e) { Log("Ending the poll: " + e.Message); } }
                item.Status = "ended";
                item.Ejected = ejected;
                var top = Top(item);
                long total = item.Options.Sum(o => o.Votes);
                string? ejName = ejected == null ? null : item.Options.FirstOrDefault(o => o.Value == ejected)?.Title ?? Alive(lobby).FirstOrDefault(o => o.Value == ejected)?.Title;
                ShowResult(new
                {
                    type = "chatThinks", title = "CHAT THINKS", lobby,
                    lines = item.Options.OrderByDescending(o => o.Votes).Take(3).Select(o => new { name = o.Title, color = o.Color, pct = total > 0 ? (int)Math.Round(100.0 * o.Votes / total) : 0 }).ToList(),
                    text = ejName != null ? $"{ejName} was ejected" : "Nobody was ejected",
                    // Whether chat was right only when the game itself shows it (confirm ejects on).
                    right = Settings.RevealAtEject && top != null && wasImpostor != null && top.Value == ejected ? wasImpostor : null,
                });
                if (top != null) lock (_lock) _toReveal.Add((game, item));
                if (item == Poll) Poll = null;
            }
        }

        private async Task GameOverAsync(string lobby, string game, string? winner, bool interrupted, HashSet<string> impostors)
        {
            // Predictions.
            var p = Prediction;
            if (p != null && p.Status is "open" or "locked" && Api != null)
            {
                if (p.Kind == "game" && p.Lobby == lobby && p.Game == game)
                {
                    if (interrupted || winner == null) await CancelPredictionAsync(p, $"{game} was interrupted").ConfigureAwait(false);
                    else if (p.Options.FirstOrDefault(o => o.Value == winner) is { } o) await ResolveAsync(p, o).ConfigureAwait(false);
                }
                else if (p.Kind == "first" && !interrupted && p.Lobbies.Contains(lobby) && p.Options.FirstOrDefault(o => o.Value == lobby) is { } first)
                    await ResolveAsync(p, first).ConfigureAwait(false);
                else if (p.Kind == "moreWins" && p.Lobbies.Contains(lobby) && !p.Endings.ContainsKey(lobby))
                {
                    if (interrupted || winner == null) p.Lobbies.Remove(lobby); else p.Endings[lobby] = winner;
                    if (p.Lobbies.Count > 0 && p.Lobbies.All(p.Endings.ContainsKey))
                    {
                        int imp = p.Endings.Values.Count(x => x == "Impostors"), crew = p.Endings.Values.Count(x => x == "Crewmates");
                        if (imp == crew) await CancelPredictionAsync(p, "a tie").ConfigureAwait(false);
                        else await ResolveAsync(p, p.Options.First(o => o.Value == (imp > crew ? "Impostors" : "Crewmates"))).ConfigureAwait(false);
                    }
                    else if (p.Lobbies.Count == 0) await CancelPredictionAsync(p, "no game finished").ConfigureAwait(false);
                }
            }
            if (interrupted) { lock (_lock) _toReveal.RemoveAll(x => x.Game == game); return; }
            // Was chat right? Said now the game has shown the impostors; Chat Detective scores.
            List<TwitchItem> votes;
            lock (_lock) { votes = _toReveal.Where(x => x.Game == game).Select(x => x.Item).ToList(); _toReveal.RemoveAll(x => x.Game == game); }
            var lines = new List<object>();
            foreach (var v in votes)
            {
                if (Top(v) is { } top) lines.Add(new { name = top.Title, color = top.Color, right = impostors.Contains(top.Value) });
                if (v.Type != "chat" || !Settings.Feature("detectives")) continue;
                lock (_lock)
                    foreach (var (viewer, (login, pick)) in v.Voters)
                    {
                        if (!_detectives.TryGetValue(viewer, out var d)) _detectives[viewer] = d = new Detective { Login = login, Name = login };
                        d.Guesses++;
                        if (!impostors.Contains(pick)) continue;
                        d.Correct++;
                        d.RoundCorrect[v.Round] = d.RoundCorrect.GetValueOrDefault(v.Round) + 1;
                    }
            }
            if (votes.Any(v => v.Type == "chat")) SaveDetectives();
            if (lines.Count > 0) ShowResult(new { type = "reveal", title = $"{lobby}: WAS CHAT RIGHT?", lines });
        }

        private void SaveDetectives()
        {
            try
            {
                if (_detectivesPath == null) return;
                string json;
                lock (_lock) json = JsonSerializer.Serialize(_detectives);
                File.WriteAllText(_detectivesPath, json);
            }
            catch (Exception) { }
        }

        public List<Detective> Detectives(int? round = null, int take = 10)
        {
            lock (_lock)
                return _detectives.Values.Where(d => round == null ? d.Correct > 0 : d.RoundCorrect.GetValueOrDefault(round.Value) > 0)
                    .OrderByDescending(d => round == null ? d.Correct : d.RoundCorrect.GetValueOrDefault(round.Value)).ThenBy(d => d.Guesses).Take(take).ToList();
        }

        private async Task DetectiveShoutoutAsync(int round)
        {
            var top = Detectives(round, 3);
            if (top.Count == 0) return;
            ShowResult(new { type = "detectives", title = $"ROUND {round} CHAT DETECTIVES", lines = top.Select(d => new { name = d.Name, n = d.RoundCorrect.GetValueOrDefault(round) }).ToList() }, 15);
            try { await Api!.SendChatAsync($"Round {round} Chat Detectives: " + string.Join(", ", top.Select(d => $"@{d.Login} ({d.RoundCorrect.GetValueOrDefault(round)})")) + ". Nice sleuthing!").ConfigureAwait(false); }
            catch (Exception e) { Log("Chat: " + e.Message); }
        }

        // ---- Lobby and MVP polls --------------------------------------------------------------------

        public async Task<string> StartLobbyPollAsync()
        {
            _lastLobbyPoll = _clock();
            if (!Ready("Which lobby next?", "lobbyPoll")) return "Can't start a poll now: see the log.";
            if (Poll != null) return "A poll is already running (Twitch allows one at a time).";
            var air = _desk.OnAir;
            string? on = air.Layout == "full" ? air.Slots.FirstOrDefault() : null;
            var lobbies = _desk.Board.Ranking().Where(r => r.Online && r.Phase != "menu" && !_desk.IsDown(r.Lobby) && !string.Equals(r.Lobby, on, StringComparison.OrdinalIgnoreCase)).Select(r => r.Lobby).ToList();
            if (lobbies.Count < 2) return "It needs two lobbies to choose from.";
            var opts = Shuffle(lobbies.Take(TwitchLimits.PollMaxChoices).Select(l => new TwitchOption { Title = l, Value = l }).ToList());
            return await OpenPollAsync("lobby", "Which lobby should we watch next?", opts, Settings.LobbyPollSeconds).ConfigureAwait(false);
        }

        public async Task<string> StartMvpPollAsync()
        {
            if (!Ready("Round MVP", "mvpPoll")) return "Can't start a poll now: see the log.";
            if (Poll != null) return "A poll is already running (Twitch allows one at a time).";
            int round = _desk.Tables.CurrentRound;
            var rows = (round > 0 ? _desk.Tables.Round(round) : _desk.Tables.Overall()).Take(TwitchLimits.PollMaxChoices).ToList();
            if (rows.Count < 2) return "Not enough results for an MVP poll yet.";
            var opts = Shuffle(rows.Select(r => new TwitchOption { Title = r.Name, Color = r.Color, Value = r.Key }).ToList());
            return await OpenPollAsync("mvp", round > 0 ? $"Round {round} MVP?" : "Tournament MVP so far?", opts, Settings.MvpPollSeconds, round).ConfigureAwait(false);
        }

        private async Task<string> OpenPollAsync(string kind, string title, List<TwitchOption> opts, int seconds, int round = 0)
        {
            seconds = TwitchLimits.Clamp(seconds, TwitchLimits.PollMinSeconds, TwitchLimits.PollMaxSeconds);
            var (id, created) = await Api!.CreatePollAsync(title, opts.Select(o => o.Title).ToList(), seconds).ConfigureAwait(false);
            for (int i = 0; i < opts.Count && i < created.Count; i++) { opts[i].Id = created[i].Id; opts[i].Title = created[i].Title; }
            Poll = new TwitchItem { Type = "poll", Kind = kind, Id = id, Title = TwitchLimits.Cut(title, TwitchLimits.PollTitle), Options = opts, Round = round, Started = _clock(), EndsAt = _clock().AddSeconds(seconds) };
            Log("Poll: " + title);
            return "Poll open: " + title;
        }

        public async Task<string> EndPollAsync()
        {
            var p = Poll;
            if (p == null || Api == null) return "No poll running.";
            await Api.EndPollAsync(p.Id).ConfigureAwait(false);
            await PollEndedAsync(p).ConfigureAwait(false);
            return "Poll ended.";
        }

        private async Task PollEndedAsync(TwitchItem p)
        {
            if (Poll != p) return;
            Poll = null;
            p.Status = "ended";
            var top = Top(p);
            long total = p.Options.Sum(o => o.Votes);
            int Pct(TwitchOption o) => total > 0 ? (int)Math.Round(100.0 * o.Votes / total) : 0;
            switch (p.Kind)
            {
                case "lobby" when top != null:
                    // The caster decides: a card with a button to put it on.
                    _desk.AddNotice(top.Value, "twitchLobby", "high", $"Chat picked {top.Value} next ({Pct(top)}% of {total} votes).", 72, 180, "show:" + top.Value);
                    ShowResult(new { type = "poll", title = "CHAT PICKED", lines = p.Options.OrderByDescending(o => o.Votes).Select(o => new { name = o.Title, color = o.Color, pct = Pct(o), win = o == top }).ToList() });
                    Log($"Chat picked {top.Value} next: waiting for the caster.");
                    break;
                case "mvp" when top != null:
                    ShowResult(new { type = "poll", title = p.Title.ToUpperInvariant(), lines = p.Options.OrderByDescending(o => o.Votes).Select(o => new { name = o.Title, color = o.Color, pct = Pct(o), win = o == top }).ToList(), text = $"Chat's MVP: {top.Title}" }, 15);
                    try { await Api!.SendChatAsync($"Chat's {p.Title.TrimEnd('?')}: {top.Title} with {Pct(top)}% of the vote!").ConfigureAwait(false); } catch (Exception e) { Log("Chat: " + e.Message); }
                    break;
                case "meeting":
                    break;                                                      // shown at the ejection
            }
        }

        // ---- Channel points --------------------------------------------------------------------------

        private static readonly string[] Banned =
        {
            "fuck", "shit", "bitch", "cunt", "dick", "cock", "pussy", "nigg", "fag", "retard", "whore", "slut", "rape", "nazi", "kys", "porn", "penis", "vagina",
        };

        /// <summary>The shoutout filter: banned words, even spelled with numbers or symbols.</summary>
        public bool Profane(string text)
        {
            string t = text.ToLowerInvariant().Replace('0', 'o').Replace('1', 'i').Replace('3', 'e').Replace('4', 'a').Replace('5', 's').Replace('7', 't').Replace('@', 'a').Replace('$', 's').Replace('!', 'i');
            string squashed = Regex.Replace(t, @"[^a-z]", "");
            return Banned.Concat(Settings.BannedWords.Select(w => w.ToLowerInvariant())).Any(w => w.Length > 0 && (squashed.Contains(Regex.Replace(w, @"[^a-z]", ""), StringComparison.Ordinal)));
        }

        private async Task RedeemedAsync(JsonElement ev)
        {
            string rewardId = ev.TryGetProperty("reward", out var rw) ? Str(rw, "id") ?? "" : "";
            string? kind;
            lock (_lock) kind = _rewards.TryGetValue(rewardId, out var k) ? k : null;
            if (kind == null) return;                                           // someone else's reward
            var r = new Redemption { Id = Str(ev, "id") ?? "", RewardId = rewardId, Kind = kind, User = Str(ev, "user_name") ?? Str(ev, "user_login") ?? "?", Input = (Str(ev, "user_input") ?? "").Trim(), At = _clock() };
            if (kind == "shoutout")
            {
                if (!Settings.Feature("shoutoutReward")) return;
                if (Profane(r.Input))
                {
                    r.Status = "denied";
                    r.Flag = "turned away by the filter";
                    try { await Api!.UpdateRedemptionAsync(rewardId, r.Id, false).ConfigureAwait(false); } catch (Exception e) { Log("Refund: " + e.Message); }
                    Log($"Shoutout from {r.User} turned away by the filter (points refunded).");
                }
                else if (FindPlayer(r.Input) is { } p) { r.PlayerKey = p.Key; r.PlayerName = p.Name; }
                else r.Flag = "not on the tournament list";
            }
            else if (!Settings.Feature("replayReward")) return;
            lock (_lock) { _redemptions.Insert(0, r); if (_redemptions.Count > 60) _redemptions.RemoveAt(_redemptions.Count - 1); }
            if (r.Status == "waiting") Log($"{(kind == "replay" ? "Replay request" : "Shoutout")} from {r.User}{(r.Input.Length > 0 ? ": " + r.Input : "")} (waiting for you).");
        }

        /// <summary>A roster player by name (full, first name or in-game name).</summary>
        private (string Key, string Name)? FindPlayer(string said)
        {
            string n = Norm(said);
            if (n.Length < 2) return null;
            var rows = _desk.Tables.Overall();
            foreach (var e in _desk.Roster.Entries)
                if (Norm(e.Name) == n || Norm(e.Name.Split(' ')[0]) == n || e.InGameNames.Any(x => Norm(x) == n))
                {
                    var row = rows.FirstOrDefault(x => x.Name == e.Name);
                    var live = _desk.Board.Ranking().SelectMany(x => x.People).FirstOrDefault(x => _desk.Roster.Match(x.Key, x.Discord, x.Name).Entry == e);
                    return (row?.Key ?? live?.Key ?? "name:" + e.Name.ToLowerInvariant(), e.Name);
                }
            return null;
        }

        /// <summary>The caster's answer: approve (a replay plays, or the player's card goes up) or deny (points back).</summary>
        public async Task<string> DecideAsync(string id, bool approve, string? clipId = null)
        {
            Redemption? r;
            lock (_lock) r = _redemptions.FirstOrDefault(x => x.Id == id && x.Status == "waiting");
            if (r == null || Api == null) return "That request is gone.";
            string message;
            if (!approve) message = $"Denied: {r.User}'s points are refunded.";
            else if (r.Kind == "replay")
            {
                clipId ??= Clips?.Invoke().FirstOrDefault().Id;
                if (clipId == null || PlayReplay == null) return "No saved replay to play yet.";
                message = await PlayReplay(clipId).ConfigureAwait(false);
            }
            else
            {
                if (r.PlayerKey == null) return "That isn't a player on the list: deny it, or pick the player in the Players card.";
                message = _desk.ShowPlayerCard(r.PlayerKey, null);
                try { await Api.SendChatAsync($"Shoutout to {r.PlayerName}, requested by @{r.User}!").ConfigureAwait(false); } catch (Exception e) { Log("Chat: " + e.Message); }
            }
            await Api.UpdateRedemptionAsync(r.RewardId, r.Id, approve).ConfigureAwait(false);
            r.Status = approve ? "approved" : "denied";
            Log($"{(r.Kind == "replay" ? "Replay request" : "Shoutout")} from {r.User}: {(approve ? "approved" : "denied")}.");
            return message;
        }

        // ---- Twitch's events -------------------------------------------------------------------------

        /// <summary>One EventSub notification (real, or test mode's).</summary>
        public void Handle(string type, JsonElement ev)
        {
            try
            {
                switch (type)
                {
                    case "channel.chat.message":
                        Chat(Str(ev, "chatter_user_id") ?? "", Str(ev, "chatter_user_login") ?? "", ev.TryGetProperty("message", out var msg) ? Str(msg, "text") ?? "" : "");
                        break;
                    case "channel.poll.progress" or "channel.poll.end":
                    {
                        var p = Poll;
                        if (p == null || Str(ev, "id") != p.Id) break;
                        foreach (var c in ev.GetProperty("choices").EnumerateArray())
                            if (p.Options.FirstOrDefault(o => o.Id == Str(c, "id")) is { } o) o.Votes = c.TryGetProperty("votes", out var v) ? v.GetInt64() : 0;
                        if (type == "channel.poll.end") _ = PollEndedAsync(p);
                        break;
                    }
                    case "channel.prediction.progress" or "channel.prediction.lock" or "channel.prediction.end":
                    {
                        var p = Prediction;
                        if (p == null || Str(ev, "id") != p.Id) break;
                        foreach (var c in ev.GetProperty("outcomes").EnumerateArray())
                            if (p.Options.FirstOrDefault(o => o.Id == Str(c, "id")) is { } o)
                            {
                                o.Votes = c.TryGetProperty("users", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : o.Votes;
                                o.Points = c.TryGetProperty("channel_points", out var cp) && cp.ValueKind == JsonValueKind.Number ? cp.GetInt64() : o.Points;
                            }
                        if (type == "channel.prediction.lock" && p.Status == "open") p.Status = "locked";
                        if (type == "channel.prediction.end" && p.Status is "open" or "locked") p.Status = Str(ev, "status") == "canceled" ? "canceled" : "ended";
                        break;
                    }
                    case "channel.channel_points_custom_reward_redemption.add":
                        Later("redemption", () => RedeemedAsync(ev.Clone()));
                        break;
                }
            }
            catch (Exception e) { Log($"{type}: {e.Message}"); }
        }

        // ---- Test mode: a pretend audience ----------------------------------------------------------

        private static readonly string[] FakeViewers = { "sus_hunter", "ventwatcher", "crewmate4life", "emergencybtn", "taskmaster99", "redwasvented", "skeldwalker", "o2fixer", "polusfan", "miramira", "cafeteriaguy", "cardswipe", "lightsoff", "admintable", "medscanner", "asteroidpro" };

        private async Task FakeTrafficAsync(FakeTwitchApi fake)
        {
            var now = _clock();
            // Votes trickle in on the poll, points on the prediction, !sus in chat (picked at random: test mode knows nothing either).
            if (Poll is { Status: "open" } p)
            {
                foreach (var o in p.Options) o.Votes += _random.Next(0, 4);
                Handle("channel.poll.progress", JsonSerializer.SerializeToElement(new { id = p.Id, choices = p.Options.Select(o => new { id = o.Id, votes = o.Votes }) }));
            }
            if (Prediction is { Status: "open" } pr)
                foreach (var o in pr.Options) { int n = _random.Next(0, 3); o.Votes += n; o.Points += n * _random.Next(50, 2000); }
            if (Vote is { Status: "open" } v && v.Options.Count > 0)
                for (int i = _random.Next(0, 4); i > 0; i--)
                {
                    var who = FakeViewers[_random.Next(FakeViewers.Length)];
                    Chat("fake-" + who, who, "!sus " + v.Options[_random.Next(v.Options.Count)].Title.Split(' ')[0]);
                }
            // Now and then someone spends points.
            if (_nextFake == default) _nextFake = now.AddSeconds(20);
            if (now >= _nextFake && _rewards.Count > 0)
            {
                _nextFake = now.AddSeconds(_random.Next(45, 120));
                var (rid, kind) = _rewards.ElementAt(_random.Next(_rewards.Count));
                string input = kind == "replay" ? "that last kill!" : _random.Next(5) == 0 ? "sh1t player" : _desk.Roster.Entries.Count > 0 ? _desk.Roster.Entries[_random.Next(_desk.Roster.Entries.Count)].Name.Split(' ')[0] : "nobody";
                var who = FakeViewers[_random.Next(FakeViewers.Length)];
                Handle("channel.channel_points_custom_reward_redemption.add", JsonSerializer.SerializeToElement(new { id = "fake" + now.Ticks, user_login = who, user_name = who, user_input = input, reward = new { id = rid, title = kind } }));
            }
            await Task.CompletedTask;
        }

        // ---- What the tab and the stream show --------------------------------------------------------

        private void ShowResult(object data, double seconds = 10) => Result = (data, _clock().AddSeconds(seconds));

        private object View(TwitchItem i)
        {
            long total = i.Type == "prediction" ? i.Options.Sum(o => o.Points) : i.Options.Sum(o => o.Votes);
            return new
            {
                i.Type, i.Kind, i.Title, i.Status, i.Lobby, i.Game,
                Left = Math.Max(0, (int)Math.Ceiling((i.EndsAt - _clock()).TotalSeconds)),
                Total = total,
                Voters = i.Type == "prediction" ? i.Options.Sum(o => o.Votes) : i.Type == "chat" ? i.Voters.Count : total,
                Options = i.Options.Select(o => new
                {
                    o.Id, o.Title, o.Color, o.Icon, o.Value, o.Votes, o.Points,
                    Pct = total > 0 ? (int)Math.Round(100.0 * (i.Type == "prediction" ? o.Points : o.Votes) / total) : 0,
                }).ToList(),
            };
        }

        /// <summary>The graphic on stream: what's running now (a meeting vote first), and the last result for a few seconds.</summary>
        public object? Overlay()
        {
            if (Settings.Off || Api == null) return null;
            var now = _clock();
            // A meeting vote only while its lobby is on stream; between rounds the MVP poll comes first.
            var air = _desk.OnAir;
            bool between = air.Layout is "intermission" or "slate";
            var vote = Vote is { } mv && !between && air.Has(mv.Lobby ?? "") ? mv : null;
            var poll = Poll is { Kind: "meeting" } mp && (between || !air.Has(mp.Lobby ?? "")) ? null : Poll;
            var active = new[] { between ? poll : vote, between ? null : poll, Prediction }.FirstOrDefault(x => x != null && x.Status is "open" || x?.Type == "prediction" && x.Status == "locked");
            return new
            {
                active = active == null ? null : View(active),
                result = Result is { } r && now < r.Until ? r.Data : null,
                detectives = Settings.Feature("detectives") ? Detectives(null, 5).Select(d => new { name = d.Name, n = d.Correct }).ToList() : null,
            };
        }

        public object State()
        {
            List<object> log, reds;
            lock (_lock)
            {
                log = _log.Take(40).Select(l => (object)new { At = l.At.ToString("o"), l.Text }).ToList();
                reds = _redemptions.Take(20).Select(r => (object)new { r.Id, r.Kind, r.User, r.Input, r.PlayerName, r.Flag, r.Status, At = r.At.ToString("o") }).ToList();
            }
            return new
            {
                Settings.Off, Settings.TestMode, Settings.ClientId, Settings.DelaySeconds, Settings.Featured, Settings.MeetingMode, Settings.RevealAtEject,
                Settings.PredictionSeconds, Settings.MeetingSeconds,
                Features = Settings.Features, Auto = Settings.Auto, Names = TwitchSettings.FeatureNames,
                Connected, CanPoll, User = User == null ? null : new { User.Name, User.Login, Type = User.BroadcasterType },
                SignedIn = Auth.Token != null,
                SignIn = SignIn is { } s ? new { s.Code, s.Url } : null,
                Events = _events?.State, EventsProblem = _events?.Problem,
                Problem,
                FeaturedNow = Featured(),
                Prediction = Prediction is { Status: "open" or "locked" } p ? View(p) : null,
                Poll = Poll == null ? null : View(Poll),
                Vote = Vote is { Status: "open" } v ? View(v) : null,
                Redemptions = reds,
                Clips = (Clips?.Invoke() ?? new List<(string, string, string)>()).Take(15).Select(c => new { c.Id, c.Title, c.Lobby }).ToList(),
                Detectives = Detectives(null, 10).Select(d => new { d.Name, d.Correct, d.Guesses }).ToList(),
                Log = log,
            };
        }

        public void Dispose() => Disconnect();
    }

    /// <summary>Test mode: a pretend Twitch channel (an Affiliate) that takes everything and sends nothing.</summary>
    public sealed class FakeTwitchApi : ITwitchApi
    {
        private readonly Random _random;
        private int _n;
        public List<string> Calls { get; } = new List<string>();
        public List<string> ChatSent { get; } = new List<string>();
        public string BroadcasterType { get; set; } = "affiliate";
        public FakeTwitchApi(Random? random = null) => _random = random ?? new Random();
        private string Id(string kind) => $"{kind}-{++_n}";

        public Task<TwitchUser> MeAsync() => Task.FromResult(new TwitchUser { Id = "1", Login = "test_channel", Name = "Test channel", BroadcasterType = BroadcasterType });
        public Task<(string Id, List<TwitchChoice> Choices)> CreatePollAsync(string title, IList<string> choices, int seconds)
        {
            Calls.Add($"poll {title} [{string.Join("|", choices)}] {seconds}");
            return Task.FromResult((Id("poll"), TwitchLimits.Choices(choices.Take(TwitchLimits.PollMaxChoices), TwitchLimits.PollChoice).Select(c => new TwitchChoice { Id = Id("choice"), Title = c }).ToList()));
        }
        public Task EndPollAsync(string id) { Calls.Add("endpoll " + id); return Task.CompletedTask; }
        public Task<(string Id, List<TwitchChoice> Outcomes)> CreatePredictionAsync(string title, IList<string> outcomes, int seconds)
        {
            Calls.Add($"prediction {title} [{string.Join("|", outcomes)}] {seconds}");
            return Task.FromResult((Id("prediction"), TwitchLimits.Choices(outcomes.Take(TwitchLimits.MaxOutcomes), TwitchLimits.Outcome).Select(c => new TwitchChoice { Id = Id("outcome"), Title = c }).ToList()));
        }
        public Task EndPredictionAsync(string id, string status, string? winningOutcomeId = null) { Calls.Add($"{status.ToLowerInvariant()} {id}{(winningOutcomeId != null ? " " + winningOutcomeId : "")}"); return Task.CompletedTask; }
        public Task<List<TwitchChoice>> RewardsAsync() => Task.FromResult(new List<TwitchChoice>());
        public Task<string> CreateRewardAsync(string title, int cost, string prompt, bool inputRequired) { Calls.Add($"reward {title} {cost}"); return Task.FromResult(Id("reward")); }
        public Task UpdateRedemptionAsync(string rewardId, string redemptionId, bool fulfilled) { Calls.Add($"{(fulfilled ? "fulfilled" : "refunded")} {redemptionId}"); return Task.CompletedTask; }
        public Task SendChatAsync(string text) { ChatSent.Add(text); return Task.CompletedTask; }
        public Task SubscribeAsync(string type, string version, object condition, string sessionId) { Calls.Add("subscribe " + type); return Task.CompletedTask; }
    }
}
