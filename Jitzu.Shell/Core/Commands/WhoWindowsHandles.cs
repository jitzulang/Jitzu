using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Threading;

namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Scans the Windows handle table once and matches open file handles against known paths.
/// </summary>
internal static class WhoWindowsHandles
{
    public static bool TryGetLockedFiles(
        IReadOnlyList<string> paths,
        out List<(string Path, List<(int Pid, string Name)> Holders)> lockedFiles)
    {
        lockedFiles = new List<(string Path, List<(int Pid, string Name)> Holders)>();
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000) || paths.Count == 0)
            return true;

        try
        {
            return TryGetLockedFilesWindows(paths, out lockedFiles);
        }
        catch
        {
            lockedFiles = new List<(string Path, List<(int Pid, string Name)> Holders)>();
            return false;
        }
    }

    private const int SystemExtendedHandleInformation = 64;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);
    private const int InitialBufferLength = 1 << 20;
    private const int MaxBufferLength = 256 << 20;
    private const int PathBufferLength = 32768;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public IntPtr Object;
        public UIntPtr UniqueProcessId;
        public UIntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [SupportedOSPlatform("windows6.0.6000")]
    private static bool TryGetLockedFilesWindows(
        IReadOnlyList<string> paths,
        out List<(string Path, List<(int Pid, string Name)> Holders)> lockedFiles)
    {
        lockedFiles = new List<(string Path, List<(int Pid, string Name)> Holders)>();

        var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
            targetPaths.Add(Path.GetFullPath(path));

        var matches = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var buffer = IntPtr.Zero;

        try
        {
            buffer = QueryHandleTable();
            if (buffer == IntPtr.Zero)
                return false;

            ScanHandles(buffer, targetPaths, matches);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }

        if (matches.Count == 0)
            return true;

        var processNames = new Dictionary<int, string>();
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            if (!matches.TryGetValue(fullPath, out var pids))
                continue;

            var holders = new List<(int Pid, string Name)>();
            foreach (var pid in pids.Order())
            {
                if (!processNames.TryGetValue(pid, out var name))
                {
                    name = GetProcessName(pid);
                    processNames[pid] = name;
                }

                holders.Add((pid, name));
            }

            lockedFiles.Add((path, holders));
        }

        return true;
    }

    private static IntPtr QueryHandleTable()
    {
        var length = InitialBufferLength;

        while (length <= MaxBufferLength)
        {
            var buffer = Marshal.AllocHGlobal(length);
            var status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, length, out var returnLength);
            if (status == 0)
                return buffer;

            Marshal.FreeHGlobal(buffer);
            if (status != STATUS_INFO_LENGTH_MISMATCH && status != STATUS_BUFFER_OVERFLOW)
                return IntPtr.Zero;

            length = Math.Max(length * 2, returnLength + InitialBufferLength);
        }

        return IntPtr.Zero;
    }

    [SupportedOSPlatform("windows6.0.6000")]
    private static unsafe void ScanHandles(
        IntPtr buffer,
        HashSet<string> targetPaths,
        Dictionary<string, HashSet<int>> matches)
    {
        var handleCount = *(nuint*)buffer;
        var entries = (SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX*)((byte*)buffer + IntPtr.Size * 2);
        var sourceProcesses = new Dictionary<int, HANDLE>();
        var currentProcess = PInvoke.GetCurrentProcess();
        var pathBuffer = new char[PathBufferLength];

        try
        {
            for (nuint i = 0; i < handleCount; i++)
            {
                ref var entry = ref entries[i];

                var pid = (int)entry.UniqueProcessId.ToUInt64();
                if (pid == 0)
                    continue;

                if (!sourceProcesses.TryGetValue(pid, out var sourceProcess))
                {
                    sourceProcess = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE, false, (uint)pid);
                    sourceProcesses[pid] = sourceProcess;
                }

                if ((IntPtr)sourceProcess == IntPtr.Zero)
                    continue;

                var sourceHandle = (HANDLE)(IntPtr)(long)entry.HandleValue.ToUInt64();
                HANDLE duplicatedHandle;
                if (!PInvoke.DuplicateHandle(
                        sourceProcess,
                        sourceHandle,
                        currentProcess,
                        &duplicatedHandle,
                        0,
                        false,
                        DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS))
                {
                    continue;
                }

                try
                {
                    if (PInvoke.GetFileType(duplicatedHandle) != FILE_TYPE.FILE_TYPE_DISK)
                        continue;

                    uint length;
                    fixed (char* pathBufferPtr = pathBuffer)
                    {
                        length = PInvoke.GetFinalPathNameByHandle(
                            duplicatedHandle,
                            pathBufferPtr,
                            (uint)pathBuffer.Length,
                            GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED
                                | GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_DOS);
                    }

                    if (length == 0 || length >= pathBuffer.Length)
                        continue;

                    string path;
                    try { path = NormalizePath(new string(pathBuffer, 0, (int)length)); }
                    catch { continue; }

                    if (!targetPaths.Contains(path))
                        continue;

                    if (!matches.TryGetValue(path, out var pids))
                    {
                        pids = new HashSet<int>();
                        matches[path] = pids;
                    }

                    pids.Add(pid);
                }
                finally
                {
                    PInvoke.CloseHandle(duplicatedHandle);
                }
            }
        }
        finally
        {
            foreach (var sourceProcess in sourceProcesses.Values)
            {
                if ((IntPtr)sourceProcess != IntPtr.Zero)
                    PInvoke.CloseHandle(sourceProcess);
            }
        }
    }

    private static string NormalizePath(string path)
    {
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(extendedPrefix, StringComparison.Ordinal))
            path = path[extendedPrefix.Length..];

        const string uncPrefix = @"UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            path = @"\" + path[uncPrefix.Length..];

        return Path.GetFullPath(path);
    }

    private static string GetProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return "?"; }
    }
}
