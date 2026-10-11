using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// Before a hook goes in: its arguments bound by position (__0, __1, …) and __instance must have
    /// the game method's own types. A game update that changed one (byte → PlayerId in CastVote)
    /// made Harmony build a method that threw on every call, so the game's own code never ran.
    /// </summary>
    internal static class HookCheck
    {
        /// <summary>What doesn't match, or null when the hook fits (or its target can't be found: Harmony says so itself).</summary>
        public static string? Mismatch(Type hook)
        {
            var info = HarmonyMethodExtensions.GetMergedFromType(hook);
            if (info?.declaringType == null || info.methodName == null && info.methodType == null) return null;
            MethodBase? target = info.methodType switch
            {
                MethodType.Getter => AccessTools.PropertyGetter(info.declaringType, info.methodName),
                MethodType.Setter => AccessTools.PropertySetter(info.declaringType, info.methodName),
                MethodType.Constructor => AccessTools.Constructor(info.declaringType, info.argumentTypes),
                _ => info.methodName == null ? null : AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes),
            };
            if (target == null) return null;
            var real = target.GetParameters();
            foreach (var m in hook.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name is not ("Prefix" or "Postfix" or "Finalizer")) continue;
                foreach (var p in m.GetParameters())
                {
                    var mine = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
                    if (p.Name == "__instance")
                    {
                        if (!mine.IsAssignableFrom(target.DeclaringType)) return $"{m.Name}'s __instance is {mine.Name}, the game's is {target.DeclaringType?.Name}.";
                        continue;
                    }
                    if (p.Name == null || !p.Name.StartsWith("__") || !int.TryParse(p.Name.Substring(2), out int i)) continue;
                    if (i >= real.Length) return $"{m.Name} reads argument {i}, but the game's {target.Name} has {real.Length}.";
                    var theirs = real[i].ParameterType.IsByRef ? real[i].ParameterType.GetElementType()! : real[i].ParameterType;
                    if (mine != theirs && !(mine == typeof(object) && !theirs.IsValueType))
                        return $"{m.Name}'s argument {i} is {mine.Name}, the game's {target.Name} takes {theirs.Name}.";
                }
            }
            return null;
        }
    }
}
