namespace TournamentTracker.Voice
{
    public enum VoicePhase
    {
        /// <summary>Not in a lobby (main menu, or not the host). Everyone talks.</summary>
        Menu,
        Lobby,
        Tasks,
        Meeting,
        GameOver,
    }

    public readonly struct VoiceState : System.IEquatable<VoiceState>
    {
        public static readonly VoiceState Open = new VoiceState(false, false);

        public VoiceState(bool mute, bool deaf)
        {
            Mute = mute;
            Deaf = deaf;
        }

        public bool Mute { get; }
        public bool Deaf { get; }

        public bool Equals(VoiceState other) => Mute == other.Mute && Deaf == other.Deaf;
        public override bool Equals(object? obj) => obj is VoiceState v && Equals(v);
        public override int GetHashCode() => (Mute ? 1 : 0) | (Deaf ? 2 : 0);
        public static bool operator ==(VoiceState a, VoiceState b) => a.Equals(b);
        public static bool operator !=(VoiceState a, VoiceState b) => !a.Equals(b);
        public override string ToString() => Mute ? (Deaf ? "muted+deafened" : "muted") : Deaf ? "deafened" : "open";
    }

    /// <summary>The automute rules: who may speak and hear in each phase of the game.</summary>
    public static class MutePlanner
    {
        public static VoiceState Plan(VoicePhase phase, bool alive, AutoMuteSettings s)
        {
            switch (phase)
            {
                case VoicePhase.Tasks:
                    if (alive) return new VoiceState(true, s.DeafenAliveDuringTasks);
                    return s.DeadCanTalkDuringTasks ? VoiceState.Open : new VoiceState(true, false);
                case VoicePhase.Meeting:
                    if (alive) return VoiceState.Open;
                    return s.MuteDeadDuringMeetings ? new VoiceState(true, false) : VoiceState.Open;
                default:
                    return VoiceState.Open;
            }
        }
    }
}
