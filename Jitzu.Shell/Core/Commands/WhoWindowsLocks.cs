using System.Diagnostics;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RestartManager;

namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Uses the Windows Restart Manager to enumerate processes holding handles to a file.
/// </summary>
internal static class WhoWindowsLocks
{
    public static List<(int Pid, string Name)> GetProcessesLockingFile(string path)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
            return new();
        return QueryProcessesLockingFilesWindows([path], 0, 1).Holders;
    }

    public static List<(string Path, List<(int Pid, string Name)> Holders)> GetLockedFiles(IReadOnlyList<string> paths)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000) || paths.Count == 0)
            return new List<(string Path, List<(int Pid, string Name)> Holders)>();

        return GetLockedFilesWindows(paths);
    }

    [SupportedOSPlatform("windows6.0.6000")]
    private static List<(string Path, List<(int Pid, string Name)> Holders)> GetLockedFilesWindows(IReadOnlyList<string> paths)
    {
        var results = new List<(string Path, List<(int Pid, string Name)> Holders)>();

        var batches = Enumerable.Range(0, (paths.Count + MaxResourcesPerQuery - 1) / MaxResourcesPerQuery)
            .Select(index => index * MaxResourcesPerQuery);

        Parallel.ForEach(
            batches,
            new ParallelOptions { MaxDegreeOfParallelism = RestartManagerParallelism },
            () => new List<(string Path, List<(int Pid, string Name)> Holders)>(),
            (offset, _, localResults) =>
            {
                var count = Math.Min(MaxResourcesPerQuery, paths.Count - offset);
                FindLockedFilesWindows(paths, offset, count, localResults);
                return localResults;
            },
            localResults =>
            {
                lock (results)
                    results.AddRange(localResults);
            });

        return results;
    }

    private const int MaxResourcesPerQuery = 64;
    private const int RestartManagerParallelism = 4;

    [SupportedOSPlatform("windows6.0.6000")]
    private static void FindLockedFilesWindows(
        IReadOnlyList<string> paths,
        int start,
        int count,
        List<(string Path, List<(int Pid, string Name)> Holders)> results)
    {
        var (succeeded, holders) = QueryProcessesLockingFilesWindows(paths, start, count);
        if (!succeeded && count > 1)
        {
            var splitCount = count / 2;
            FindLockedFilesWindows(paths, start, splitCount, results);
            FindLockedFilesWindows(paths, start + splitCount, count - splitCount, results);
            return;
        }

        if (!succeeded)
            return;

        if (holders.Count == 0)
            return;

        if (count == 1)
        {
            results.Add((paths[start], holders));
            return;
        }

        var leftCount = count / 2;
        FindLockedFilesWindows(paths, start, leftCount, results);
        FindLockedFilesWindows(paths, start + leftCount, count - leftCount, results);
    }

    [SupportedOSPlatform("windows6.0.6000")]
    private static (bool Succeeded, List<(int Pid, string Name)> Holders) QueryProcessesLockingFilesWindows(IReadOnlyList<string> paths, int start, int count)
    {
        var results = new List<(int, string)>();
        if (count == 0)
            return (true, results);

        var resources = new string[count];
        for (var i = 0; i < count; i++)
            resources[i] = Path.GetFullPath(paths[start + i]);

        Span<char> sessionKey = stackalloc char[64];
        if (PInvoke.RmStartSession(out var session, sessionKey) != WIN32_ERROR.NO_ERROR)
            return (false, results);

        try
        {
            if (PInvoke.RmRegisterResources(
                    session,
                    resources,
                    ReadOnlySpan<RM_UNIQUE_PROCESS>.Empty,
                    ReadOnlySpan<string>.Empty) != WIN32_ERROR.NO_ERROR)
            {
                return (false, results);
            }

            uint procInfoNeeded = 0;
            uint procInfo = 0;
            uint rebootReasons = 0;

            var probe = PInvoke.RmGetList(session, out procInfoNeeded, ref procInfo, Span<RM_PROCESS_INFO>.Empty, out rebootReasons);
            if (probe != WIN32_ERROR.NO_ERROR && probe != WIN32_ERROR.ERROR_MORE_DATA) return (false, results);
            if (procInfoNeeded == 0) return (true, results);

            var infoArray = new RM_PROCESS_INFO[procInfoNeeded];
            procInfo = procInfoNeeded;

            if (PInvoke.RmGetList(session, out procInfoNeeded, ref procInfo, infoArray, out rebootReasons) != WIN32_ERROR.NO_ERROR)
                return (false, results);

            var seen = new HashSet<int>();
            for (var i = 0; i < procInfo; i++)
            {
                var info = infoArray[i];
                int pid = (int)info.Process.dwProcessId;
                if (!seen.Add(pid))
                    continue;

                var name = info.strAppName.ToString();
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
            PInvoke.RmEndSession(session);
        }

        return (true, results);
    }
}
