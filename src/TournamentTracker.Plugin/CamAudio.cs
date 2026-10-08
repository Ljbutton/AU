using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// The host's game sound while the player camera is on stream: what the followed player
    /// would hear. Sounds are measured from where they stand (room ambience, vents, doors: the
    /// game measures from the local player, and the referee's position is answered with
    /// theirs), their own footsteps play (the game only plays your own), and a kill is heard
    /// only by the killer and the victim, as in the game. With the whole map on stream it's the
    /// referee's: alarms and every kill. Only on the host, as the referee ghost; Red Alert says
    /// which view is on stream.
    /// </summary>
    internal static class CamAudio
    {
        /// <summary>Whose ears the game sound is, while the camera is on stream; null for the whole map's.</summary>
        public static PlayerControl? Listener { get; private set; }

        private static float _next, _nextWatchers;
        private static FootstepWatcher[] _watchers = Array.Empty<FootstepWatcher>();
        private static SkeldShipRoom[] _rooms = Array.Empty<SkeldShipRoom>();
        private static bool _logged;

        public static void Update()
        {
            var session = TournamentPlugin.Session;
            if (session == null || !Game.IsHost || !session.Spectator.Cam || !session.Spectator.CamAudio || !RefSlot.LocalIsRefereeGhost())
            {
                Listener = null;
                return;
            }
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.1f;
            var id = PlayerCamera.Following ?? session.CamPlayer();
            var who = id.HasValue ? Frame.Get(id.Value) : null;
            Listener = who != null && who.Pc != null && !who.Pc.AmOwner ? who.Pc : null;
        }

        /// <summary>
        /// The followed player's footstep (from the PlayStepSound hook): the floor under them
        /// decides the sound, as the game does for your own steps. True if it was played.
        /// </summary>
        public static bool Footstep(PlayerControl player)
        {
            try
            {
                if (player.FootSteps == null) return false;
                if (Time.unscaledTime >= _nextWatchers || _watchers.Length + _rooms.Length == 0)
                {
                    _nextWatchers = Time.unscaledTime + 5f;
                    _watchers = Object.FindObjectsOfType<FootstepWatcher>().OrderByDescending(w => w.Priority).ToArray();
                    _rooms = Object.FindObjectsOfType<SkeldShipRoom>().ToArray();
                }
                SoundGroup? sounds = null;
                foreach (var w in _watchers) { if (w != null && (sounds = w.MakeFootstep(player)) != null) break; }
                if (sounds == null) foreach (var r in _rooms) { if (r != null && (sounds = r.MakeFootstep(player)) != null) break; }
                if (sounds == null) return false;
                var clip = sounds.Random();
                if (clip == null) return false;
                player.FootSteps.clip = clip;
                player.FootSteps.Play();
                return true;
            }
            catch (Exception e)
            {
                if (!_logged) TournamentPlugin.Logger.Warn("Player camera sound: couldn't play a footstep: " + e.Message);
                _logged = true;
                return false;
            }
        }
    }
}
