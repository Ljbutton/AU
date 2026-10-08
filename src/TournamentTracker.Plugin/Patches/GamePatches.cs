using System;
using System.Collections.Generic;
using Hazel;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using InnerNet;
using TournamentTracker.Stats;

namespace TournamentTracker.Plugin.Patches
{
    // Every hook runs on the host only and never throws into the game: a tracking bug
    // must not be able to break a tournament match. Arguments are bound by position
    // (__0, __1, ...) so a renamed parameter in a game update doesn't break a hook.

    internal static class Hook
    {
        public static void Run(string name, Action action)
        {
            if (!Game.IsHost || TournamentPlugin.Session == null) return;
            try
            {
                action();
            }
            catch (Exception e)
            {
                TournamentPlugin.Logger.Error($"{name} hook failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(IntroCutscene), nameof(IntroCutscene.CoBegin))]
    internal static class GameStartPatch
    {
        public static void Prefix() => Hook.Run("Game start", Driver.StartGame);
    }

    // Roles: the impostor rotation and the referee ghost slot are applied to the game's own role
    // choice before it's sent, so each player gets one role message (see RoleChoice).
    // (The referee becomes a ghost in Driver.StartGame, as the intro begins.)
    [HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
    internal static class RoleChoicePatch
    {
        public static void Prefix() => Hook.Run("Roles", RoleChoice.Begin);
        public static void Postfix()
        {
            // Always let go, even if the hook fails: role messages must never stay held back.
            try { Hook.Run("Roles", RoleChoice.Finish); }
            finally { RoleChoice.Collecting = false; RoleChoice.Sending = false; }
        }
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
    internal static class RoleOncePatch
    {
        /// <summary>While the game hands out roles: hold its message back (RoleChoice sends the final one).</summary>
        public static bool Prefix(PlayerControl __instance, AmongUs.GameOptions.RoleTypes __0, bool __1)
        {
            if (!RoleChoice.Collecting || RoleChoice.Sending || __instance == null) return true;
            RoleChoice.Chosen[__instance.PlayerId] = (__instance, __0, __1);
            return false;
        }
    }

    // Referee ghost slot: their task message (the game's own, the only one) carries no tasks.
    [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Begin))]
    internal static class RefereeTasksPatch
    {
        public static void Prefix() => RefSlot.HandingOutTasks = Game.IsHost;
        public static void Postfix() => RefSlot.HandingOutTasks = false;
    }

    [HarmonyPatch(typeof(NetworkedPlayerInfo), nameof(NetworkedPlayerInfo.RpcSetTasks))]
    internal static class RefereeNoTasksPatch
    {
        public static void Prefix(NetworkedPlayerInfo __instance, ref Il2CppStructArray<byte> __0)
        {
            if (!RefSlot.HandingOutTasks || __instance == null || TournamentPlugin.Session?.RefSlotKey == null) return;
            try
            {
                if (RefSlot.RefereeId(fresh: true) == __instance.PlayerId) __0 = new Il2CppStructArray<byte>(0);
            }
            catch (Exception e) { TournamentPlugin.Logger.Error("Referee tasks hook failed: " + e); }
        }
    }

    // Referee ghost slot: the host turns down any kill on the referee (an impostor whose game still
    // shows them can try), the same way the game turns down a kill on someone already dead.
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
    internal static class RefereeNoKillPatch
    {
        public static bool Prefix(PlayerControl __instance, PlayerControl __0)
        {
            if (!Game.IsHost || __instance == null || __0 == null || TournamentPlugin.Session?.RefSlotKey == null) return true;
            try
            {
                if (RefSlot.RefereeId(fresh: true) != __0.PlayerId) return true;
                TournamentPlugin.Logger.Warn($"Referee ghost: turned down {__instance.Data?.PlayerName}'s kill on the referee (dead here: {__0.Data?.IsDead}).");
                __instance.RpcMurderPlayer(__0, false);
                return false;
            }
            catch (Exception e)
            {
                TournamentPlugin.Logger.Error("Referee kill hook failed: " + e);
                return true;
            }
        }
    }

    // Second and third fences, in case the kill comes some other way: the host never sends a
    // successful kill on the referee, and its own game never plays one.
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcMurderPlayer))]
    internal static class RefereeNoKillSentPatch
    {
        public static bool Prefix(PlayerControl __instance, PlayerControl __0, bool __1)
        {
            if (!__1 || !Game.IsHost || __instance == null || __0 == null || TournamentPlugin.Session?.RefSlotKey == null) return true;
            try
            {
                if (RefSlot.RefereeId(fresh: true) != __0.PlayerId) return true;
                TournamentPlugin.Logger.Warn($"Referee ghost: stopped a kill on the referee by {__instance.Data?.PlayerName} before it was sent.");
                __instance.RpcMurderPlayer(__0, false);
                return false;
            }
            catch (Exception e)
            {
                TournamentPlugin.Logger.Error("Referee kill hook failed: " + e);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
    internal static class RefereeNoDeathPatch
    {
        public static bool Prefix(PlayerControl __instance, PlayerControl __0)
        {
            if (!Game.IsHost || __0 == null || TournamentPlugin.Session?.RefSlotKey == null) return true;
            try
            {
                if (RefSlot.RefereeId(fresh: true) != __0.PlayerId) return true;
                TournamentPlugin.Logger.Warn($"Referee ghost: a kill on the referee by {__instance?.Data?.PlayerName} reached the host's game; not played here.");
                return false;
            }
            catch (Exception e)
            {
                TournamentPlugin.Logger.Error("Referee kill hook failed: " + e);
                return true;
            }
        }
    }

    // Referee ghost: the chat stays (the game hides it for a player whose role isn't a ghost role,
    // which the referee's isn't).
    [HarmonyPatch(typeof(ChatController), nameof(ChatController.SetVisible))]
    internal static class RefereeChatPatch
    {
        public static bool Prefix(ChatController __instance, bool __0)
        {
            if (__0 || __instance == null) return true;
            try
            {
                if (!RefSlot.LocalIsRefereeGhost()) return true;
                __instance.SetVisible(true);
                return false;
            }
            catch (Exception) { return true; }
        }
    }

    // The referee's mini chat: every message this game shows in its chat.
    [HarmonyPatch(typeof(ChatController), nameof(ChatController.AddChat))]
    internal static class MiniChatPatch
    {
        public static void Postfix(PlayerControl __0, string __1)
        {
            try { MiniChat.Add(__0, __1); }
            catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
    internal static class KillPatch
    {
        public static void Postfix(PlayerControl __instance, PlayerControl __0) => Hook.Run("Kill", () =>
        {
            // A kill blocked by a guardian angel shield still calls MurderPlayer; only count real deaths.
            if (__instance == null || __0 == null || __0.Data == null || !__0.Data.IsDead) return;
            Driver.StartGame();
            TournamentTracker.Broadcast.FeedPlace? place = null;
            try { place = FeedReader.Place(__0); }
            catch (Exception e) { TournamentPlugin.Logger.Warn("Caster feed: couldn't read where the kill was (" + e.Message + ")."); }
            TournamentPlugin.Session.Kill(__instance.PlayerId, __0.PlayerId, place);
            // Spectator view: did a crewmate have the killer in sight?
            try { SpectatorOverlay.OnKill(__instance, __0); }
            catch (Exception e) { TournamentPlugin.Logger.Warn("Spectator view: couldn't check who saw the kill (" + e.Message + ")."); }
        });
    }

    // The host starts every meeting (it approves reports and buttons) through RpcStartMeeting;
    // StartMeeting is the receiving side. Both are hooked; the tracker ignores the second call.
    internal static class MeetingHook
    {
        public static void Record(PlayerControl caller, NetworkedPlayerInfo body) => Hook.Run("Meeting", () =>
        {
            if (caller == null) return;
            Driver.StartGame();
            TournamentPlugin.Session.MeetingCalled(caller.PlayerId, body == null ? null : body.PlayerId);
        });
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcStartMeeting))]
    internal static class RpcStartMeetingPatch
    {
        public static void Prefix(PlayerControl __instance, NetworkedPlayerInfo __0) => MeetingHook.Record(__instance, __0);
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
    internal static class StartMeetingPatch
    {
        public static void Prefix(PlayerControl __instance, NetworkedPlayerInfo __0) => MeetingHook.Record(__instance, __0);
    }

    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.VotingComplete))]
    internal static class VotingCompletePatch
    {
        public static void Postfix(Il2CppStructArray<MeetingHud.VoterState> __0, NetworkedPlayerInfo __1, bool __2) => Hook.Run("Votes", () =>
        {
            var votes = new List<VoteCast>();
            if (__0 != null)
            {
                for (int i = 0; i < __0.Length; i++)
                    votes.Add(new VoteCast(__0[i].VoterId, __0[i].VotedForId));
            }
            TournamentPlugin.Session.VotingComplete(votes, __1 == null ? null : __1.PlayerId, __2);
        });
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CompleteTask))]
    internal static class TaskPatch
    {
        public static void Postfix(PlayerControl __instance) => Hook.Run("Task", () =>
        {
            if (__instance != null) TournamentPlugin.Session.TaskCompleted(__instance.PlayerId);
        });
    }

    // Sabotages reach the host as UpdateSystem(Sabotage, player, <system>). Depending on the
    // game version that arrives through the MessageReader or the byte overload; both are
    // hooked and the tracker drops the duplicate.
    [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.UpdateSystem), typeof(SystemTypes), typeof(PlayerControl), typeof(MessageReader))]
    internal static class SabotageReaderPatch
    {
        public static void Prefix(SystemTypes __0, PlayerControl __1, MessageReader __2) => Hook.Run("Sabotage", () =>
        {
            if (__0 != SystemTypes.Sabotage || __1 == null || __2 == null) return;
            int position = __2.Position;
            byte system = __2.ReadByte();
            __2.Position = position;
            TournamentPlugin.Session.Sabotage(__1.PlayerId, ((SystemTypes)system).ToString());
        });
    }

    [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.UpdateSystem), typeof(SystemTypes), typeof(PlayerControl), typeof(byte))]
    internal static class SabotageBytePatch
    {
        public static void Prefix(SystemTypes __0, PlayerControl __1, byte __2) => Hook.Run("Sabotage", () =>
        {
            if (__0 != SystemTypes.Sabotage || __1 == null) return;
            TournamentPlugin.Session.Sabotage(__1.PlayerId, ((SystemTypes)__2).ToString());
        });
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
    internal static class PlayerLeftPatch
    {
        public static void Prefix(ClientData __0) => Hook.Run("Player left", () =>
        {
            var character = __0?.Character;
            if (character != null) TournamentPlugin.Session.PlayerLeft(character.PlayerId);
        });
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
    internal static class GameEndPatch
    {
        public static void Postfix(EndGameResult __0) => Hook.Run("Game end", () =>
            Driver.EndGame(__0 == null ? "Unknown" : __0.GameOverReason.ToString()));
    }
}
