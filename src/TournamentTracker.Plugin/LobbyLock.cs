using System;
using System.Collections.Generic;
using AmongUs.GameOptions;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// Reads and writes the host's game settings for the tournament settings lock. Each
    /// setting is read and written on its own, so one the game renames only stops that one.
    /// </summary>
    internal static class LobbyLock
    {
        private static readonly HashSet<string> Failed = new HashSet<string>();

        public static LobbySettings Read()
        {
            var o = Game.Options();
            var s = new LobbySettings();
            if (o == null) return s;
            s.Impostors = Try("impostors", () => o.GetInt(Int32OptionNames.NumImpostors));
            s.ConfirmEjects = Try("confirm ejects", () => o.GetBool(BoolOptionNames.ConfirmImpostor));
            s.EmergencyMeetings = Try("emergency meetings", () => o.GetInt(Int32OptionNames.NumEmergencyMeetings));
            s.AnonymousVotes = Try("anonymous votes", () => o.GetBool(BoolOptionNames.AnonymousVotes));
            s.EmergencyCooldown = Try("emergency cooldown", () => o.GetInt(Int32OptionNames.EmergencyCooldown));
            s.DiscussionTime = Try("discussion time", () => o.GetInt(Int32OptionNames.DiscussionTime));
            s.VotingTime = Try("voting time", () => o.GetInt(Int32OptionNames.VotingTime));
            s.PlayerSpeed = Try("player speed", () => o.GetFloat(FloatOptionNames.PlayerSpeedMod));
            s.CrewmateVision = Try("crewmate vision", () => o.GetFloat(FloatOptionNames.CrewLightMod));
            s.ImpostorVision = Try("impostor vision", () => o.GetFloat(FloatOptionNames.ImpostorLightMod));
            s.KillCooldown = Try("kill cooldown", () => o.GetFloat(FloatOptionNames.KillCooldown));
            s.KillDistance = Try("kill distance", () => o.GetInt(Int32OptionNames.KillDistance));
            s.VisualTasks = Try("visual tasks", () => o.GetBool(BoolOptionNames.VisualTasks));
            s.CommonTasks = Try("common tasks", () => o.GetInt(Int32OptionNames.NumCommonTasks));
            s.LongTasks = Try("long tasks", () => o.GetInt(Int32OptionNames.NumLongTasks));
            s.ShortTasks = Try("short tasks", () => o.GetInt(Int32OptionNames.NumShortTasks));
            s.RolesOff = Try("roles", () =>
            {
                var roles = o.RoleOptions;
                foreach (var role in SpecialRoles())
                {
                    try
                    {
                        if (roles.GetNumPerGame(role) > 0 && roles.GetChancePerGame(role) > 0) return false;
                    }
                    catch (Exception) { /* a role this version doesn't have */ }
                }
                return true;
            });
            return s;
        }

        /// <summary>Puts back whatever differs from <paramref name="want"/> and sends it to everyone. Returns what changed.</summary>
        public static List<string> Enforce(LobbySettings want)
        {
            var o = Game.Options();
            if (o == null) return new List<string>();
            var changed = want.Differences(Read());
            if (changed.Count == 0) return changed;

            if (want.Impostors.HasValue) Do("impostors", () => o.SetInt(Int32OptionNames.NumImpostors, want.Impostors.Value));
            if (want.ConfirmEjects.HasValue) Do("confirm ejects", () => o.SetBool(BoolOptionNames.ConfirmImpostor, want.ConfirmEjects.Value));
            if (want.EmergencyMeetings.HasValue) Do("emergency meetings", () => o.SetInt(Int32OptionNames.NumEmergencyMeetings, want.EmergencyMeetings.Value));
            if (want.AnonymousVotes.HasValue) Do("anonymous votes", () => o.SetBool(BoolOptionNames.AnonymousVotes, want.AnonymousVotes.Value));
            if (want.EmergencyCooldown.HasValue) Do("emergency cooldown", () => o.SetInt(Int32OptionNames.EmergencyCooldown, want.EmergencyCooldown.Value));
            if (want.DiscussionTime.HasValue) Do("discussion time", () => o.SetInt(Int32OptionNames.DiscussionTime, want.DiscussionTime.Value));
            if (want.VotingTime.HasValue) Do("voting time", () => o.SetInt(Int32OptionNames.VotingTime, want.VotingTime.Value));
            if (want.PlayerSpeed.HasValue) Do("player speed", () => o.SetFloat(FloatOptionNames.PlayerSpeedMod, want.PlayerSpeed.Value));
            if (want.CrewmateVision.HasValue) Do("crewmate vision", () => o.SetFloat(FloatOptionNames.CrewLightMod, want.CrewmateVision.Value));
            if (want.ImpostorVision.HasValue) Do("impostor vision", () => o.SetFloat(FloatOptionNames.ImpostorLightMod, want.ImpostorVision.Value));
            if (want.KillCooldown.HasValue) Do("kill cooldown", () => o.SetFloat(FloatOptionNames.KillCooldown, want.KillCooldown.Value));
            if (want.KillDistance.HasValue) Do("kill distance", () => o.SetInt(Int32OptionNames.KillDistance, want.KillDistance.Value));
            if (want.VisualTasks.HasValue) Do("visual tasks", () => o.SetBool(BoolOptionNames.VisualTasks, want.VisualTasks.Value));
            if (want.CommonTasks.HasValue) Do("common tasks", () => o.SetInt(Int32OptionNames.NumCommonTasks, want.CommonTasks.Value));
            if (want.LongTasks.HasValue) Do("long tasks", () => o.SetInt(Int32OptionNames.NumLongTasks, want.LongTasks.Value));
            if (want.ShortTasks.HasValue) Do("short tasks", () => o.SetInt(Int32OptionNames.NumShortTasks, want.ShortTasks.Value));
            if (want.RolesOff == true)
                Do("roles", () =>
                {
                    var roles = o.RoleOptions;
                    foreach (var role in SpecialRoles())
                    {
                        try { roles.SetRoleRate(role, 0, 0); }
                        catch (Exception) { /* a role this version doesn't have */ }
                    }
                });

            Do("sync", () => GameManager.Instance.LogicOptions.SyncOptions());
            return changed;
        }

        /// <summary>Every role other than plain crewmate, impostor and their ghosts.</summary>
        private static IEnumerable<RoleTypes> SpecialRoles()
        {
            foreach (RoleTypes role in Enum.GetValues(typeof(RoleTypes)))
            {
                string name = role.ToString();
                if (name == "Crewmate" || name == "Impostor" || name.EndsWith("Ghost", StringComparison.Ordinal)) continue;
                yield return role;
            }
        }

        private static T? Try<T>(string what, Func<T> read) where T : struct
        {
            try { return read(); }
            catch (Exception e)
            {
                if (Failed.Add("read " + what)) TournamentPlugin.Logger.Warn($"Settings lock can't read {what}: {e.Message}");
                return null;
            }
        }

        private static void Do(string what, Action write)
        {
            try { write(); }
            catch (Exception e)
            {
                if (Failed.Add("write " + what)) TournamentPlugin.Logger.Warn($"Settings lock can't set {what}: {e.Message}");
            }
        }
    }
}
