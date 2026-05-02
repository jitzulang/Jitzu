using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Uses the Windows Restart Manager to enumerate processes holding handles to a file.
/// </summary>
internal static class WhoWindowsLocks
{
    public static List<(int Pid, string Name)> GetProcessesLockingFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new();
        return GetProcessesLockingFileWindows(path);
    }

    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;
    private const int RmRebootReasonNone = 0;
    private const int ERROR_MORE_DATA = 234;

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

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    [SupportedOSPlatform("windows")]
    private static List<(int Pid, string Name)> GetProcessesLockingFileWindows(string path)
    {
        var results = new List<(int, string)>();

        var key = Guid.NewGuid().ToString();
        if (RmStartSession(out var session, 0, key) != 0)
            return results;

        try
        {
            if (RmRegisterResources(session, 1, new[] { path }, 0, null, 0, null) != 0)
                return results;

            uint procInfoNeeded = 0;
            uint procInfo = 0;
            uint rebootReasons = RmRebootReasonNone;

            var probe = RmGetList(session, out procInfoNeeded, ref procInfo, null, ref rebootReasons);
            if (probe != 0 && probe != ERROR_MORE_DATA) return results;
            if (procInfoNeeded == 0) return results;

            var infoArray = new RM_PROCESS_INFO[procInfoNeeded];
            procInfo = procInfoNeeded;

            if (RmGetList(session, out procInfoNeeded, ref procInfo, infoArray, ref rebootReasons) != 0)
                return results;

            for (var i = 0; i < procInfo; i++)
            {
                var info = infoArray[i];
                int pid = info.Process.dwProcessId;
                var name = info.strAppName;
                if (string.IsNullOrEmpty(name))
                {
                    try { name = Process.GetProcessById(pid).ProcessName; }
                    catch { name = "?"; }
                }
                results.Add((pid, name));
            }
        }
        finally
        {
            RmEndSession(session);
        }

        return results;
    }
}
