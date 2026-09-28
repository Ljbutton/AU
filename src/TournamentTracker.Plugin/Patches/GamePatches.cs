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

    // Referee ghost slot: fix the referee's role once roles are chosen, clear their tasks once
    // tasks are handed out. (They become a ghost in Driver.StartGame, as the intro begins.)
    [HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
    internal static class RefereeRolePatch
    {
        public static void Postfix()
        {
            Hook.Run("Impostor rotation", ImpostorRotation.Apply);
            Hook.Run("Referee role", RefSlot.KeepCrewmate);
        }
    }

    [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Begin))]
    internal static class RefereeTasksPatch
    {
        public static void Postfix() => Hook.Run("Referee tasks", RefSlot.ClearTasks);
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
    internal static class KillPatch
    {
        public static void Postfix(PlayerControl __instance, PlayerControl __0) => Hook.Run("Kill", () =>
        {
            // A kill blocked by a guardian angel shield still calls MurderPlayer; only count real deaths.
            if (__instance == null || __0 == null || __0.Data == null || !__0.Data.IsDead) return;
            Driver.StartGame();
            TournamentPlugin.Session.Kill(__instance.PlayerId, __0.PlayerId);
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

    [HarmonyPatch(typeof(ChatController), nameof(ChatController.AddChat))]
    internal static class ChatPatch
    {
        public static void Postfix(PlayerControl __0, string __1) => Hook.Run("Chat", () =>
        {
            if (__0 == null || __0.Data == null || string.IsNullOrEmpty(__1)) return;
            var local = PlayerControl.LocalPlayer;
            bool fromHost = local != null && __0.PlayerId == local.PlayerId;
            TournamentPlugin.Session.HandleChat(Game.Snapshot(__0.Data), fromHost, __1);
        });
    }
}
