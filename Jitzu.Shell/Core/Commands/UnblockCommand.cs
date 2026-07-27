using System.Diagnostics;
using System.Text;

namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Clears delete-blocking file attributes and optionally terminates processes locking a path.
/// </summary>
public class UnblockCommand : CommandBase
{
    private const FileAttributes DeleteBlockingAttributes = FileAttributes.ReadOnly | FileAttributes.System;
    private const string Usage = "Usage: unblock [--attributes-only|-a] <path> [path2 ...]";

    private readonly IFileLockInspector _fileLockInspector;
    private readonly Action<int> _terminateProcess;

    public UnblockCommand(CommandContext context)
        : this(context, new WhoCommand.PlatformFileLockInspector(), TerminateProcess)
    {
    }

    internal UnblockCommand(
        CommandContext context,
        IFileLockInspector fileLockInspector,
        Action<int> terminateProcess)
        : base(context)
    {
        _fileLockInspector = fileLockInspector;
        _terminateProcess = terminateProcess;
    }

    public override async Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (args.Length == 0)
            return new ShellResult(ResultType.Error, "", new Exception(Usage));

        var summary = new Summary();
        var errors = new List<string>();
        var paths = new List<string>();
        var terminateLockingProcesses = true;

        foreach (var arg in args.Span)
        {
            if (arg is "--attributes-only" or "-a")
                terminateLockingProcesses = false;
            else if (arg.StartsWith('-'))
                return new ShellResult(
                    ResultType.Error,
                    "",
                    new Exception($"unblock: unknown option '{arg}'{Environment.NewLine}{Usage}"));
        }

        foreach (var arg in args.Span)
        {
            if (arg is "--attributes-only" or "-a")
                continue;

            var path = ExpandPath(arg);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                summary.Failed++;
                errors.Add($"unblock: cannot access '{arg}': No such file or directory");
                continue;
            }

            UnblockPath(path, summary, errors, paths);
        }

        if (paths.Count == 0 && errors.Count == 0)
            return new ShellResult(ResultType.Error, "", new Exception(Usage));

        var lockedPaths = paths.Count == 0
            ? []
            : await _fileLockInspector.FindLockedPathsAsync(
                paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

        var holders = lockedPaths
            .SelectMany(lockedPath => lockedPath.Holders)
            .DistinctBy(holder => holder.Pid)
            .OrderBy(holder => holder.Pid)
            .ToArray();

        var terminated = new List<(int Pid, string Name)>();
        if (terminateLockingProcesses)
        {
            foreach (var holder in holders)
            {
                if (holder.Pid == Environment.ProcessId)
                {
                    errors.Add($"unblock: refusing to terminate the current Jitzu process (pid {holder.Pid})");
                    continue;
                }

                try
                {
                    _terminateProcess(holder.Pid);
                    terminated.Add(holder);
                }
                catch (Exception ex)
                {
                    errors.Add($"unblock: failed to terminate {holder.Name} (pid {holder.Pid}): {ex.Message}");
                }
            }
        }

        var output = FormatSummary(summary, holders, terminated, terminateLockingProcesses);
        if (errors.Count > 0)
        {
            var message = string.Join(Environment.NewLine, errors);
            return new ShellResult(ResultType.Error, output, new Exception(message));
        }

        return new ShellResult(ResultType.OsCommand, output, null);
    }

    private static void UnblockPath(
        string path,
        Summary summary,
        List<string> errors,
        List<string> paths)
    {
        foreach (var entry in EnumerateFileSystemEntriesSafe(path))
        {
            paths.Add(entry.Path);
            summary.Checked++;
            var blockedAttributes = entry.Attributes & DeleteBlockingAttributes;
            if (blockedAttributes == 0)
                continue;

            try
            {
                File.SetAttributes(entry.Path, entry.Attributes & ~DeleteBlockingAttributes);
                summary.Changed++;
            }
            catch (Exception ex)
            {
                summary.Failed++;
                errors.Add($"unblock: failed to update '{entry.Path}': {ex.Message}");
            }
        }
    }

    private static IEnumerable<(string Path, FileAttributes Attributes)> EnumerateFileSystemEntriesSafe(string path)
    {
        FileAttributes rootAttributes;
        try { rootAttributes = File.GetAttributes(path); }
        catch { yield break; }

        yield return (path, rootAttributes);

        if ((rootAttributes & FileAttributes.Directory) == 0)
            yield break;

        var pending = new Stack<string>();
        pending.Push(path);
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(current).EnumerateFileSystemInfos("*", options).ToArray(); }
            catch { entries = Array.Empty<FileSystemInfo>(); }

            foreach (var entry in entries)
            {
                var attributes = entry.Attributes;
                yield return (entry.FullName, attributes);

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
                if (isDirectory && !isReparsePoint)
                    pending.Push(entry.FullName);
            }
        }
    }

    private static string FormatSummary(
        Summary summary,
        IReadOnlyList<(int Pid, string Name)> holders,
        IReadOnlyList<(int Pid, string Name)> terminated,
        bool terminateLockingProcesses)
    {
        var sb = new StringBuilder();
        if (summary.Changed == 0)
            sb.Append($"No delete-blocking attributes found in {summary.Checked} {EntryWord(summary.Checked)}.");
        else
            sb.Append($"Unblocked {summary.Changed} of {summary.Checked} {EntryWord(summary.Checked)}.");

        if (summary.Failed > 0)
            sb.Append($" Failed: {summary.Failed}.");

        if (holders.Count > 0)
        {
            sb.AppendLine();
            if (terminateLockingProcesses)
                sb.AppendLine($"Terminated {terminated.Count} locking process{(terminated.Count == 1 ? "" : "es")}:");
            else
                sb.AppendLine($"Process locks remain ({holders.Count} process{(holders.Count == 1 ? "" : "es")}):");

            foreach (var (pid, name) in terminateLockingProcesses ? terminated : holders)
                sb.AppendLine($"  {pid,8}  {name}");

            if (!terminateLockingProcesses)
                sb.Append("Run without --attributes-only to terminate these processes.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string EntryWord(int count) => count == 1 ? "entry" : "entries";

    private static void TerminateProcess(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }

    private sealed class Summary
    {
        public int Checked { get; set; }
        public int Changed { get; set; }
        public int Failed { get; set; }
    }
}
