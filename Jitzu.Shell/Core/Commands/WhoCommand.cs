using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Inspects a process by PID, or lists processes locking a file path.
/// </summary>
public class WhoCommand : CommandBase
{
    public WhoCommand(CommandContext context) : base(context) { }

    public override async Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (args.Length == 0)
            return new ShellResult(ResultType.Error, "", new Exception("Usage: who <pid|file>"));

        var arg = args.Span[0];

        if (int.TryParse(arg, out var pid))
            return DescribeProcess(pid);

        var path = ExpandPath(arg);
        if (File.Exists(path))
            return await DescribeFileLocksAsync(path);

        if (Directory.Exists(path))
            return await DescribeDirectoryLocksAsync(path);

        return new ShellResult(ResultType.Error, "", new Exception($"who: '{arg}' is not a PID or existing path"));
    }

    private ShellResult DescribeProcess(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            var sb = new StringBuilder();
            var label = Theme["ls.config"];
            var reset = ThemeConfig.Reset;

            sb.AppendLine($"{label}    PID:{reset} {p.Id}");
            sb.AppendLine($"{label}   Name:{reset} {p.ProcessName}");

            string? path = null;
            string? args = null;
            try { path = p.MainModule?.FileName; } catch { }
            if (path is null && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var exeLink = $"/proc/{pid}/exe";
                try { path = File.ResolveLinkTarget(exeLink, true)?.FullName; } catch { }
            }
            if (path is not null)
                sb.AppendLine($"{label}   Path:{reset} {path}");

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var cmdline = File.ReadAllText($"/proc/{pid}/cmdline");
                    args = cmdline.Replace('\0', ' ').Trim();
                }
                catch { }
            }
            if (!string.IsNullOrEmpty(args))
                sb.AppendLine($"{label}   Args:{reset} {args}");

            try { sb.AppendLine($"{label}Started:{reset} {p.StartTime:yyyy-MM-dd HH:mm:ss}"); } catch { }
            try { sb.AppendLine($"{label}    CPU:{reset} {p.TotalProcessorTime}"); } catch { }
            try { sb.AppendLine($"{label} Memory:{reset} {FormatFileSize(p.WorkingSet64)}"); } catch { }
            try { sb.AppendLine($"{label}Threads:{reset} {p.Threads.Count}"); } catch { }

            return new ShellResult(ResultType.OsCommand, sb.ToString().TrimEnd(), null);
        }
        catch (ArgumentException)
        {
            return new ShellResult(ResultType.Error, "", new Exception($"who: no process with pid {pid}"));
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, "", ex);
        }
    }

    private async Task<ShellResult> DescribeFileLocksAsync(string path)
    {
        try
        {
            var holders = await GetProcessesLockingFileAsync(path);

            if (holders.Count == 0)
                return new ShellResult(ResultType.OsCommand, $"No processes hold a lock on '{path}'.", null);

            var sb = new StringBuilder();
            var label = Theme["ls.config"];
            var reset = ThemeConfig.Reset;
            sb.AppendLine($"{label}File:{reset} {path}");
            sb.AppendLine($"{label}Held by {holders.Count} process(es):{reset}");
            foreach (var (hpid, hname) in holders)
                sb.AppendLine($"  {hpid,8}  {hname}");

            return new ShellResult(ResultType.OsCommand, sb.ToString().TrimEnd(), null);
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, "", ex);
        }
    }

    private async Task<ShellResult> DescribeDirectoryLocksAsync(string path)
    {
        try
        {
            var lockedFiles = new List<(string Path, List<(int Pid, string Name)> Holders)>();
            var protectedEntries = new List<(string Path, FileAttributes Attributes)>();
            var checkedFiles = 0;

            foreach (var entry in EnumerateFileSystemEntriesSafe(path))
            {
                var attributes = GetAttributesOrDefault(entry);
                if ((attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0)
                    protectedEntries.Add((entry, attributes));

                if (!Directory.Exists(entry))
                {
                    checkedFiles++;
                    var holders = await GetProcessesLockingFileAsync(entry);
                    if (holders.Count > 0)
                        lockedFiles.Add((entry, holders));
                }
            }

            if (lockedFiles.Count == 0 && protectedEntries.Count == 0)
                return new ShellResult(ResultType.OsCommand,
                    $"No processes hold a lock on files under '{path}' ({checkedFiles} file(s) checked).", null);

            var sb = new StringBuilder();
            var label = Theme["ls.config"];
            var reset = ThemeConfig.Reset;
            var totalHolders = lockedFiles.Sum(file => file.Holders.Count);

            sb.AppendLine($"{label}Directory:{reset} {path}");
            sb.AppendLine($"{label}Checked:{reset} {checkedFiles} file(s)");
            if (lockedFiles.Count > 0)
                sb.AppendLine($"{label}Held by {totalHolders} process(es) across {lockedFiles.Count} file(s):{reset}");
            else
                sb.AppendLine($"{label}Process locks:{reset} none found");

            foreach (var lockedFile in lockedFiles)
            {
                sb.AppendLine(Path.GetRelativePath(path, lockedFile.Path));
                foreach (var (hpid, hname) in lockedFile.Holders)
                    sb.AppendLine($"  {hpid,8}  {hname}");
            }

            if (protectedEntries.Count > 0)
            {
                sb.AppendLine($"{label}Delete-blocking attributes on {protectedEntries.Count} entr{(protectedEntries.Count == 1 ? "y" : "ies")}:{reset}");
                foreach (var (entry, attributes) in protectedEntries)
                    sb.AppendLine($"  {FormatAttributes(attributes),-16} {Path.GetRelativePath(path, entry)}");
            }

            return new ShellResult(ResultType.OsCommand, sb.ToString().TrimEnd(), null);
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, "", ex);
        }
    }

    private static async Task<List<(int Pid, string Name)>> GetProcessesLockingFileAsync(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WhoWindowsLocks.GetProcessesLockingFile(path);

        return await GetProcessesLockingFileUnixAsync(path);
    }

    private static IEnumerable<string> EnumerateFileSystemEntriesSafe(string path)
    {
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(current).ToArray(); }
            catch { files = Array.Empty<string>(); }

            foreach (var file in files)
                yield return file;

            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(current).ToArray(); }
            catch { directories = Array.Empty<string>(); }

            foreach (var directory in directories)
            {
                yield return directory;
                pending.Push(directory);
            }
        }
    }

    private static FileAttributes GetAttributesOrDefault(string path)
    {
        try { return File.GetAttributes(path); }
        catch { return default; }
    }

    private static string FormatAttributes(FileAttributes attributes)
    {
        var parts = new List<string>();
        if (attributes.HasFlag(FileAttributes.ReadOnly)) parts.Add("ReadOnly");
        if (attributes.HasFlag(FileAttributes.System)) parts.Add("System");
        return string.Join(",", parts);
    }

    private static async Task<List<(int Pid, string Name)>> GetProcessesLockingFileUnixAsync(string path)
    {
        var result = new List<(int, string)>();

        // Prefer /proc scan on Linux (no external deps); fall back to lsof.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && Directory.Exists("/proc"))
        {
            var target = Path.GetFullPath(path);
            foreach (var procDir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(procDir);
                if (!int.TryParse(name, out var pid)) continue;

                var fdDir = Path.Combine(procDir, "fd");
                try
                {
                    foreach (var fd in Directory.EnumerateFiles(fdDir))
                    {
                        try
                        {
                            var link = File.ResolveLinkTarget(fd, true)?.FullName;
                            if (link == target)
                            {
                                string pname = "?";
                                try { pname = File.ReadAllText(Path.Combine(procDir, "comm")).Trim(); } catch { }
                                result.Add((pid, pname));
                                break;
                            }
                        }
                        catch { }
                    }
                }
                catch { } // permission denied for other users' processes
            }
            return result;
        }

        // macOS / fallback: lsof
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "lsof",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-Fpcn");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi);
            if (proc is null) return result;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            int curPid = 0;
            string curName = "";
            foreach (var line in output.Split('\n'))
            {
                if (line.Length < 2) continue;
                switch (line[0])
                {
                    case 'p':
                        if (curPid != 0) result.Add((curPid, curName));
                        int.TryParse(line.AsSpan(1), out curPid);
                        curName = "";
                        break;
                    case 'c':
                        curName = line[1..];
                        break;
                }
            }
            if (curPid != 0) result.Add((curPid, curName));
        }
        catch { }

        return result;
    }
}
