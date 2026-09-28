using System;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>Unity hook for per-frame updates and a clean unmute when the game closes.</summary>
    public sealed class TrackerBehaviour : MonoBehaviour
    {
        public TrackerBehaviour(IntPtr ptr) : base(ptr)
        {
        }

        private void Update() => Driver.Update();

        private void OnApplicationQuit() => Driver.Shutdown();
    }
}
