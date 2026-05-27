using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Jitzu.Shell.Core;

internal static class PortProcessFinder
{
    public static IReadOnlyList<int> FindTcpListenerPids(int port)
    {
        if (port is < 1 or > 65535)
            return [];

        if (OperatingSystem.IsWindows())
            return FindWindowsTcpListenerPids(port);

        if (OperatingSystem.IsLinux())
            return FindLinuxTcpListenerPids(port);

        return FindTcpListenerPidsWithLsof(port);
    }

    private static IReadOnlyList<int> FindWindowsTcpListenerPids(int port)
    {
        var pids = new HashSet<int>();
        AddWindowsTcpListenerPids(AF_INET, port, pids);
        AddWindowsTcpListenerPids(AF_INET6, port, pids);
        return [.. pids];
    }

    private static void AddWindowsTcpListenerPids(int addressFamily, int port, HashSet<int> pids)
    {
        var size = 0;
        var ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (ret != ERROR_INSUFFICIENT_BUFFER && ret != 0)
            return;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            ret = GetExtendedTcpTable(buffer, ref size, false, addressFamily, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (ret != 0)
                return;

            var rowCount = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;

            if (addressFamily == AF_INET)
            {
                var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (var i = 0; i < rowCount; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    if (ConvertWindowsPort(row.dwLocalPort) == port)
                        pids.Add(unchecked((int)row.dwOwningPid));

                    rowPtr += rowSize;
                }
            }
            else
            {
                var rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
                for (var i = 0; i < rowCount; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                    if (ConvertWindowsPort(row.dwLocalPort) == port)
                        pids.Add(unchecked((int)row.dwOwningPid));

                    rowPtr += rowSize;
                }
            }
        }
        catch
        {
            // Port lookup is best-effort; the caller reports "no listener" if none are found.
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ConvertWindowsPort(uint port) =>
        (int)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    private static IReadOnlyList<int> FindLinuxTcpListenerPids(int port)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        AddLinuxListeningSocketInodes("/proc/net/tcp", port, inodes);
        AddLinuxListeningSocketInodes("/proc/net/tcp6", port, inodes);

        if (inodes.Count == 0)
            return [];

        var pids = new HashSet<int>();

        try
        {
            foreach (var procDir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(procDir), out var pid))
                    continue;

                var fdDir = Path.Combine(procDir, "fd");
                if (!Directory.Exists(fdDir))
                    continue;

                try
                {
                    foreach (var fd in Directory.EnumerateFiles(fdDir))
                    {
                        var target = new FileInfo(fd).LinkTarget;
                        if (target is not null
                            && target.StartsWith("socket:[", StringComparison.Ordinal)
                            && target.EndsWith(']'))
                        {
                            var inode = target[8..^1];
                            if (inodes.Contains(inode))
                            {
                                pids.Add(pid);
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore processes whose fd table cannot be read.
                }
            }
        }
        catch
        {
            return [];
        }

        return [.. pids];
    }

    private static void AddLinuxListeningSocketInodes(string path, int port, HashSet<string> inodes)
    {
        if (!File.Exists(path))
            return;

        try
        {
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length <= 9 || columns[3] != "0A")
                    continue;

                var localAddress = columns[1];
                var portSeparator = localAddress.LastIndexOf(':');
                if (portSeparator < 0)
                    continue;

                var portHex = localAddress[(portSeparator + 1)..];
                if (int.TryParse(portHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var listenerPort)
                    && listenerPort == port)
                {
                    inodes.Add(columns[9]);
                }
            }
        }
        catch
        {
            // Ignore unreadable proc files.
        }
    }

    private static IReadOnlyList<int> FindTcpListenerPidsWithLsof(int port)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "lsof",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("-nP");
            process.StartInfo.ArgumentList.Add($"-iTCP:{port}");
            process.StartInfo.ArgumentList.Add("-sTCP:LISTEN");
            process.StartInfo.ArgumentList.Add("-t");

            if (!process.Start())
                return [];

            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            var pids = new HashSet<int>();
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(line.Trim(), out var pid))
                    pids.Add(pid);
            }

            return [.. pids];
        }
        catch
        {
            return [];
        }
    }

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] ucRemoteAddr;
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, int reserved);
}
