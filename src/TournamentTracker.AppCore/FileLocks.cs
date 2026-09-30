using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace TournamentTracker.App
{
    /// <summary>
    /// Which programs have a file open, from Windows' Restart Manager (the same thing the
    /// "file in use" dialogs use). Empty on other systems or when Windows won't say.
    /// </summary>
    public static class FileLocks
    {
        public static IReadOnlyList<string> Users(string path)
        {
            if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
            try { return Query(path); }
            catch (Exception) { return Array.Empty<string>(); }
        }

        private static List<string> Query(string path)
        {
            var names = new List<string>();
            if (RmStartSession(out uint session, 0, Guid.NewGuid().ToString("N")) != 0) return names;
            try
            {
                if (RmRegisterResources(session, 1, new[] { path }, 0, null, 0, null) != 0) return names;
                uint needed = 0, count = 0;
                uint reasons = 0;
                int result = RmGetList(session, out needed, ref count, null, ref reasons);
                if (result == ErrorMoreData && needed > 0)
                {
                    var info = new RM_PROCESS_INFO[needed];
                    count = needed;
                    if (RmGetList(session, out needed, ref count, info, ref reasons) == 0)
                        names.AddRange(info.Take((int)count).Select(i => i.strAppName).Where(n => !string.IsNullOrWhiteSpace(n)));
                }
            }
            finally { RmEndSession(session); }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private const int ErrorMoreData = 234;

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications,
            RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);
    }
}
