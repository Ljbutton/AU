using System;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// The referee ghost hears every kill (with the player camera on stream, only one the
    /// followed player is part of). The game only plays the kill sound for the killer and
    /// the victim, so the host's screen (and the stream's whole-map view, whose game sound is the
    /// host's) was silent at the moment that matters most. Only for the referee ghost: a host who
    /// is playing never hears a kill they weren't part of.
    /// </summary>
    internal static class KillSound
    {
        private static bool _logged;

        public static void Play(PlayerControl killer, PlayerControl victim)
        {
            var local = PlayerControl.LocalPlayer;
            if (local == null || killer == local || victim == local || !RefSlot.LocalIsRefereeGhost()) return;
            // The player camera on stream: only the killer and the victim hear it, as in the game.
            var ears = CamAudio.Listener;
            if (ears != null && killer != ears && victim != ears) return;
            try
            {
                var clip = killer.KillSfx;
                if (clip == null || SoundManager.Instance == null) return;
                SoundManager.Instance.PlaySound(clip, false, 0.8f);
            }
            catch (Exception e)
            {
                if (!_logged) TournamentPlugin.Logger.Warn("Referee ghost: couldn't play the kill sound: " + e.Message);
                _logged = true;
            }
        }
    }
}
