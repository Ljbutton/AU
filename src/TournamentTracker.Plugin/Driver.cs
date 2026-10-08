using System;
using System.Collections.Generic;
using TournamentTracker.Broadcast;
using TournamentTracker.Voice;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// Per-frame work: polls the game phase for automute, backs up the hooks by noticing
    /// meetings and game starts they missed, and delivers chat replies. The Unity component
    /// only forwards to this class, which keeps its state in plain managed statics.
    /// </summary>
    internal static class Driver
    {
        private const float TickSeconds = 0.2f;
        private const float PublicChatGap = 3.1f;   // the game's own chat cooldown

        /// <summary>
        /// The tick (every 0.2 s) in three steps on three frames in a row: players and voice; the
        /// caster feed; the live status, overlay, live data and app status (their JSON is made off
        /// the main thread).
        /// </summary>
        private static readonly TickStagger Ticks = new TickStagger(TickSeconds, 3);
        private static VoicePhase _tickPhase = VoicePhase.Menu;
        private static float _nextPublicChat;
        private static VoicePhase _lastPhase = VoicePhase.Menu;
        private static readonly Queue<string> PublicQueue = new Queue<string>();
        private static bool _loggedError;
        private static float _nextLockCheck;
        private static bool _loggedReplay;
        private static bool _loggedTheater;
        private static bool _loggedFeed;
        private static bool _loggedOverlay;
        private static bool _loggedReferee;

        /// <summary>Set once a game has ended, until the lobby returns, so the start fallback can't reopen it.</summary>
        private static bool _roundOver;

        /// <summary>Set when !setup changed the code; the session is rebuilt on the next frame.</summary>
        public static bool RestartRequested;

        /// <summary>After the camera has moved this frame: the spectator view's vision follows it.</summary>
        public static void LateUpdate()
        {
            if (ReplayTheater.Active) return;
            try { using (FrameProfiler.Time(FrameProfiler.Part.OverlayLate)) SpectatorOverlay.LateUpdate(); }
            catch (Exception e) { if (!_loggedOverlay) TournamentPlugin.Logger.Error("Spectator view failed: " + e); _loggedOverlay = true; }
        }

        public static void Update()
        {
            FrameProfiler.BeginFrame();
            if (RestartRequested)
            {
                RestartRequested = false;
                Restart();
            }
            try { using (FrameProfiler.Time(FrameProfiler.Part.Theatre)) ReplayTheater.Update(); }
            catch (Exception e) { if (!_loggedTheater) TournamentPlugin.Logger.Error("Replay theatre failed: " + e); _loggedTheater = true; }
            if (ReplayTheater.Active) return;
            try { using (FrameProfiler.Time(FrameProfiler.Part.Nameplates)) Nameplates.Update(); }
            catch (Exception e) { if (!_loggedOverlay) TournamentPlugin.Logger.Error("Nameplates failed: " + e); _loggedOverlay = true; }
            try { using (FrameProfiler.Time(FrameProfiler.Part.OverlayUpdate)) SpectatorOverlay.Update(); }
            catch (Exception e) { if (!_loggedOverlay) TournamentPlugin.Logger.Error("Spectator view failed: " + e); _loggedOverlay = true; }
            try { using (FrameProfiler.Time(FrameProfiler.Part.Zoom)) GhostZoom.Update(); }
            catch (Exception e) { if (!_loggedError) TournamentPlugin.Logger.Error("Zoom failed: " + e); _loggedError = true; }
            try { using (FrameProfiler.Time(FrameProfiler.Part.Referee)) { RefSlot.Update(); RefereeHider.Update(); } }
            catch (Exception e) { if (!_loggedReferee) TournamentPlugin.Logger.Error("Referee ghost failed: " + e); _loggedReferee = true; }

            var session = TournamentPlugin.Session;
            if (session == null) return;
            try
            {
                using (FrameProfiler.Time(FrameProfiler.Part.Pump))
                    foreach (var reply in session.Pump())
                    {
                        if (reply.Public && Game.Phase() == VoicePhase.Lobby) PublicQueue.Enqueue(reply.Text);
                        else Game.LocalChat(reply.Text);
                    }
                if (PublicQueue.Count > 0 && Time.unscaledTime >= _nextPublicChat)
                {
                    if (!Game.PublicChat(PublicQueue.Peek())) Game.LocalChat(PublicQueue.Peek());
                    PublicQueue.Dequeue();
                    _nextPublicChat = Time.unscaledTime + PublicChatGap;
                }

                if (Input.GetKeyDown(KeyCode.F9) && session.AutoMute != null)
                {
                    session.UnmuteEveryone();
                    Game.LocalChat("Everyone unmuted; automute is OFF. Type !automute on to resume.");
                }

                try { using (FrameProfiler.Time(FrameProfiler.Part.Replay)) ReplayCapture.Update(); }
                catch (Exception e) { if (!_loggedReplay) TournamentPlugin.Logger.Error("Replay recording failed: " + e); _loggedReplay = true; }

                switch (Ticks.Next(Time.unscaledTime))
                {
                    case 0: Tick(session); break;
                    case 1: FeedStep(session); break;
                    case 2: using (FrameProfiler.Time(FrameProfiler.Part.Publish)) session.PublishTick(); break;
                }
            }
            catch (Exception e)
            {
                if (!_loggedError) TournamentPlugin.Logger.Error("Tracker update failed: " + e);
                _loggedError = true;
            }
        }

        private static void Tick(TournamentSession session)
        {
            var phase = Game.Phase();
            List<PlayerSnapshot> players;
            using (FrameProfiler.Time(FrameProfiler.Part.Players))
                players = phase == VoicePhase.Menu ? new List<PlayerSnapshot>() : Game.Players();
            _tickPhase = phase;

            if (phase == VoicePhase.Lobby || phase == VoicePhase.Menu) _roundOver = false;
            if (phase == VoicePhase.Lobby && Game.IsHost && Time.unscaledTime >= _nextLockCheck)
            {
                _nextLockCheck = Time.unscaledTime + 1f;
                var locked = session.LockedSettings;
                if (locked != null) session.SettingsRestored(LobbyLock.Enforce(locked));
            }
            if (phase == VoicePhase.Tasks && !session.Tracker.InGame && ShipStatus.Instance != null)
                StartGame();

            if (phase == VoicePhase.Meeting && _lastPhase != VoicePhase.Meeting)
                session.MeetingCalled(null, null);           // no-op when the hook already recorded it
            if (phase != VoicePhase.Meeting && _lastPhase == VoicePhase.Meeting)
                session.MeetingClosed();

            // Back in the lobby or menu with a game still open: the end-of-game hook never fired
            // (host left mid-game, or the lobby was closed). Keep the record, don't count it.
            if ((phase == VoicePhase.Lobby || phase == VoicePhase.Menu) && session.Tracker.InGame)
                session.GameAbandoned(players);

            using (FrameProfiler.Time(FrameProfiler.Part.Voice))
            {
                // The status, overlay and live data follow two frames later (step 2).
                if (phase == VoicePhase.Menu) session.VoiceTick(phase, players, publish: false);
                else session.VoiceTick(phase, players, Game.LobbyCode(), Game.MapName(), publish: false);
            }
            _lastPhase = phase;
        }

        /// <summary>The tick's second step, on the next frame: the caster feed.</summary>
        private static void FeedStep(TournamentSession session)
        {
            try
            {
                FeedFrame frame;
                using (FrameProfiler.Time(FrameProfiler.Part.FeedRead)) frame = FeedReader.Frame(_tickPhase);
                using (FrameProfiler.Time(FrameProfiler.Part.FeedTick)) session.FeedTick(frame);
            }
            catch (Exception e) { if (!_loggedFeed) TournamentPlugin.Logger.Error("Caster feed failed: " + e); _loggedFeed = true; }
        }

        /// <summary>Starts tracking a game if it isn't tracked yet. Safe to call from any hook.</summary>
        public static void StartGame()
        {
            var session = TournamentPlugin.Session;
            if (session == null || session.Tracker.InGame || _roundOver || !Game.IsHost) return;
            var players = Game.Players();
            // Before roles are handed out everyone reads as a crewmate; wait for the real teams.
            if (!players.Exists(p => p.IsImpostor)) return;
            session.GameStarted(Game.LobbyCode(), Game.MapName(), players);
            try { session.CheckSettings(LobbyLock.Read()); }
            catch (Exception e) { TournamentPlugin.Logger.Error("Settings check failed: " + e); }
            ReplayCapture.CaptureMap();
            try { RefSlot.MakeGhost(); }
            catch (Exception e) { TournamentPlugin.Logger.Error("Referee ghost failed: " + e); }
        }

        public static void EndGame(string reason)
        {
            var session = TournamentPlugin.Session;
            if (session == null || !Game.IsHost) return;
            _roundOver = true;
            session.GameEnded(reason, Game.Players());
        }

        private static void Restart()
        {
            var old = TournamentPlugin.Session;
            try
            {
                // Hand everyone's voice back and let the old session's posts finish first.
                old?.ShutdownAsync(TimeSpan.FromSeconds(2)).Wait(TimeSpan.FromSeconds(3));
                old?.Dispose();
                TournamentPlugin.StartSession();
                foreach (var reply in old?.Pump() ?? Array.Empty<ChatReply>()) Game.LocalChat(reply.Text);
                Game.LocalChat($"Tracker restarted: {(TournamentPlugin.Session.Setup?.Describe() ?? "using the settings file")}.");
            }
            catch (Exception e)
            {
                TournamentPlugin.Logger.Error("Restart after !setup failed: " + e);
            }
        }

        public static void Shutdown()
        {
            try
            {
                TournamentPlugin.Session?.ShutdownAsync(TimeSpan.FromSeconds(3)).Wait(TimeSpan.FromSeconds(4));
            }
            catch (Exception e)
            {
                TournamentPlugin.Logger.Error("Shutdown: " + e.Message);
            }
        }
    }
}
