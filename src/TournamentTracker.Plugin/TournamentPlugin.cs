using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Configuration;
using HarmonyLib;
using TournamentTracker.Setup;
using UnityEngine;

namespace TournamentTracker.Plugin
{
    [BepInPlugin(Id, "Tournament Tracker", Version)]
    [BepInProcess("Among Us.exe")]
    public sealed class TournamentPlugin : BasePlugin
    {
        public const string Id = "com.ljbutton.tournamenttracker";
        public const string Version = "0.1.55";

        internal static TournamentSession Session = null!;
        internal static ILog Logger = NullLog.Instance;

        private readonly Harmony _harmony = new Harmony(Id);

        public override void Load()
        {
            _config = Config;
            Logger = new DelegateLog(m => Log.LogInfo(m), m => Log.LogWarning(m), m => Log.LogError(m));
            FrameProfiler.Enabled = Config.Bind("Debug", "FrameProfiler", false,
                "Log frames where the mod takes over 8 ms, with a breakdown, to LogOutput.log. F10 switches it on and off in the game.").Value;
            StartSession();

            // Patch class by class: if a game update renames one method, only that stat
            // stops being tracked instead of the whole mod failing to load.
            int failed = 0;
            var patchClasses = typeof(TournamentPlugin).Assembly.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0);
            foreach (var type in patchClasses)
            {
                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    failed++;
                    Log.LogError($"Could not hook {type.Name}; that part of tracking is off. {e.Message}");
                }
            }

            AddComponent<TrackerBehaviour>();
            Log.LogInfo($"Tournament Tracker {Version} loaded. Data in {DataDir}.{(failed > 0 ? $" {failed} hook(s) failed." : "")}");
        }

        internal static string DataDir => Path.Combine(Paths.ConfigPath, "TournamentTracker");
        private static ConfigFile? _config;

        /// <summary>
        /// Builds the session from the config file, with the setup code (if any) on top. Called
        /// at load and again after !setup changes the code.
        /// </summary>
        internal static void StartSession()
        {
            var settings = ConfigBinder.Bind(_config!);
            var setup = SetupCode.Load(DataDir, Logger);
            var session = new TournamentSession(settings, DataDir, Logger, setup: setup);
            session.RestartRequested += () => Driver.RestartRequested = true;
            session.Work.Start();          // JSON for the feed, overlay and status off the game's main thread
            Session = session;
            Logger.Info(setup != null ? $"Using setup code: {setup.Describe()}" : "No setup code; using the settings file.");
        }

        public override bool Unload()
        {
            Driver.Shutdown();
            _harmony.UnpatchSelf();
            return true;
        }
    }
}
