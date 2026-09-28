using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace TournamentTracker.Plugin
{
    [BepInPlugin(Id, "Tournament Tracker", Version)]
    [BepInProcess("Among Us.exe")]
    public sealed class TournamentPlugin : BasePlugin
    {
        public const string Id = "com.ljbutton.tournamenttracker";
        public const string Version = "1.0.0";

        internal static TournamentSession Session = null!;
        internal static ILog Logger = NullLog.Instance;

        private readonly Harmony _harmony = new Harmony(Id);

        public override void Load()
        {
            Logger = new DelegateLog(m => Log.LogInfo(m), m => Log.LogWarning(m), m => Log.LogError(m));
            var settings = ConfigBinder.Bind(Config);
            string dataDir = Path.Combine(Paths.ConfigPath, "TournamentTracker");
            Session = new TournamentSession(settings, dataDir, Logger);

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
            Log.LogInfo($"Tournament Tracker {Version} loaded for \"{settings.TournamentName}\". " +
                        $"Data in {dataDir}.{(failed > 0 ? $" {failed} hook(s) failed." : "")}");
        }

        public override bool Unload()
        {
            Driver.Shutdown();
            _harmony.UnpatchSelf();
            return true;
        }
    }
}
