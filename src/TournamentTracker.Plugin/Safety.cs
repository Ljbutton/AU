using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    /// <summary>
    /// Marks a hook that belongs to a feature which sends things to Among Us's servers (roles, the
    /// referee's record and teleport, the version flag, the meeting result…). One of these that
    /// doesn't go in at startup means the game changed right there, so safe mode turns on.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class RiskyAttribute : Attribute
    {
    }

    /// <summary>
    /// Safe mode in the game (the decision itself is TournamentTracker.SafeModeRules). When Among Us is
    /// newer than the game libraries this DLL was built against, or a risky hook didn't go in, the
    /// host-side features that send things to the server stay off: host authority (+25), the referee
    /// ghost slot (role holding, MakeGhost, the record kept dead, the teleport out of sight), the
    /// impostor rotation's role holding, the settings lock's sync and the host-only message guard.
    /// Tracking, stats, overlays and Discord only read the game, so they keep working.
    /// </summary>
    internal static class Safety
    {
        /// <summary>The key the build writes the game libraries' version under (see the .csproj).</summary>
        private const string BuiltForKey = "GameLibsVersion";

        public static SafeMode? Mode { get; private set; }

        /// <summary>Safe mode is on: the risky features are off. A plain read, cheap enough for every frame.</summary>
        /// <remarks>As it is now: for what's decided in the lobby or the menus (host authority, the settings lock).</remarks>
        public static bool On => Mode?.On ?? false;

        /// <summary>
        /// Safe mode for the game being played: as it stood when its roles were handed out (RoleChoice),
        /// until the lobby; between games, <see cref="On"/>. Everything the referee slot does reads this,
        /// so pressing "Run normally anyway" or "Back to automatic" mid-game never turns a playing host
        /// into the referee (or the referee back into a player) half way through a game.
        /// </summary>
        public static bool GameOn => Mode?.OnThisGame ?? false;

        /// <summary>A game's roles are being handed out: safe mode is held as it is now until the lobby (TournamentSession.VoiceTick lets go).</summary>
        public static void HoldForGame()
        {
            try { Mode?.HoldForGame(); }
            catch (Exception) { }
        }

        /// <summary>
        /// The running game's version: the date in Application.version, which is what the game-libraries
        /// packages are named after (so the two compare like with like), else the date in the version
        /// number it sends to Among Us's servers (Constants.GetBroadcastVersion). That number's date isn't
        /// moved on every release (it said 2026.7.20 in a 2026.8.18 game), so on its own it can miss an
        /// update. Read before the mod's hooks go in, so the host authority hook hasn't touched the number.
        /// </summary>
        public static string? ReadGameVersion(ILog log)
        {
            int broadcast = 0;
            string? app = null;
            try { broadcast = Constants.GetBroadcastVersion(); }
            catch (Exception e) { log.Warn("Safe mode: couldn't read the game's network version (" + e.Message + ")."); }
            try { app = Application.version; }
            catch (Exception e) { log.Warn("Safe mode: couldn't read Application.version (" + e.Message + ")."); }
            var date = GameDate.OfRunningGame(app, broadcast);
            var network = GameDate.FromBroadcastVersion(broadcast);
            log.Info($"Among Us {(date?.ToString() ?? "version unknown")} (Application.version \"{app}\"; network version {broadcast}, dated {(network?.ToString() ?? "unknown")}).");
            return date?.ToString() ?? app;
        }

        /// <summary>The AmongUs.GameLibs.Steam version this DLL was compiled against (written in by the build), or null.</summary>
        public static string? BuiltFor()
        {
            try
            {
                foreach (var attribute in typeof(Safety).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false))
                    if (attribute is AssemblyMetadataAttribute meta && meta.Key == BuiltForKey && !string.IsNullOrWhiteSpace(meta.Value))
                        return meta.Value;
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>Decides once, after the hooks' self-check, and says so in the log.</summary>
        public static void Start(string dataDir, ILog log, string? gameVersion, IReadOnlyCollection<string> riskyHooksFailed)
        {
            try
            {
                Mode = new SafeMode(dataDir, log, gameVersion, BuiltFor(), riskyHooksFailed);
            }
            catch (Exception e)
            {
                log.Error("Safe mode couldn't start (" + e.Message + "); everything runs as normal.");
                return;
            }
            if (Mode.On) log.Warn(Mode.Describe());
            else log.Info(Mode.Describe());
        }

        /// <summary>What the referee slot being off means for the host.</summary>
        public const string RefSlotAdvice = "The referee gets a role and plays as an ordinary player: turn the referee slot off until the mod is updated.";

        /// <summary>A feature didn't run because of safe mode: in the log and on Home, once a session for each.</summary>
        public static void HeldBack(string what, string? advice = null)
        {
            try { TournamentPlugin.Session?.SafeModeHeldBack(what, advice); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// What the mod itself sends to the network (a small ring of the last few), and why Among Us
    /// disconnected this game (the last 20, in disconnects.json in the data folder).
    /// </summary>
    internal static class NetLog
    {
        public static readonly RecentSends Sends = new RecentSends();

        public static DisconnectHistory? History { get; private set; }

        public static void Start(string dataDir, ILog log)
        {
            try { History = new DisconnectHistory(dataDir, log, Sends); }
            catch (Exception e) { log.Error("Disconnect log couldn't start: " + e.Message); }
        }

        /// <summary>The mod is sending this now (a role message, a record sync, a teleport, a settings sync, a chat line…).</summary>
        public static void Sent(string what)
        {
            try { Sends.Add(what); }
            catch (Exception) { }
        }
    }
}
