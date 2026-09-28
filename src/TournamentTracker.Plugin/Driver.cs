using System;
using System.Collections.Generic;
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

        private static float _nextTick;
        private static float _nextPublicChat;
        private static VoicePhase _lastPhase = VoicePhase.Menu;
        private static readonly Queue<string> PublicQueue = new Queue<string>();
        private static bool _loggedError;

        /// <summary>Set once a game has ended, until the lobby returns, so the start fallback can't reopen it.</summary>
        private static bool _roundOver;

        /// <summary>Set when !setup changed the code; the session is rebuilt on the next frame.</summary>
        public static bool RestartRequested;

        public static void Update()
        {
            if (RestartRequested)
            {
                RestartRequested = false;
                Restart();
            }
            try { GhostZoom.Update(); }
            catch (Exception e) { if (!_loggedError) TournamentPlugin.Logger.Error("Zoom failed: " + e); _loggedError = true; }

            var session = TournamentPlugin.Session;
            if (session == null) return;
            try
            {
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

                if (Time.unscaledTime < _nextTick) return;
                _nextTick = Time.unscaledTime + TickSeconds;
                Tick(session);
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
            var players = phase == VoicePhase.Menu ? new List<PlayerSnapshot>() : Game.Players();

            if (phase == VoicePhase.Lobby || phase == VoicePhase.Menu) _roundOver = false;
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

            if (phase == VoicePhase.Menu) session.VoiceTick(phase, players);
            else session.VoiceTick(phase, players, Game.LobbyCode(), Game.MapName());
            _lastPhase = phase;
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
