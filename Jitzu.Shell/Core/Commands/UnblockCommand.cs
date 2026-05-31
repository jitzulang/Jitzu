using System.Text;

namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Clears delete-blocking file attributes from files and directories.
/// </summary>
public class UnblockCommand : CommandBase
{
    private const FileAttributes DeleteBlockingAttributes = FileAttributes.ReadOnly | FileAttributes.System;

    public UnblockCommand(CommandContext context) : base(context) { }

    public override Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (args.Length == 0)
            return Task.FromResult(new ShellResult(ResultType.Error, "",
                new Exception("Usage: unblock <path> [path2 ...]")));

        var summary = new Summary();
        var errors = new List<string>();

        foreach (var arg in args.Span)
        {
            var path = ExpandPath(arg);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                summary.Failed++;
                errors.Add($"unblock: cannot access '{arg}': No such file or directory");
                continue;
            }

            UnblockPath(path, summary, errors);
        }

        var output = FormatSummary(summary);
        if (errors.Count > 0)
        {
            var message = string.Join(Environment.NewLine, errors);
            return Task.FromResult(new ShellResult(ResultType.Error, output, new Exception(message)));
        }

        return Task.FromResult(new ShellResult(ResultType.OsCommand, output, null));
    }

    private static void UnblockPath(string path, Summary summary, List<string> errors)
    {
        foreach (var entry in EnumerateFileSystemEntriesSafe(path))
        {
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

    private static string FormatSummary(Summary summary)
    {
        var sb = new StringBuilder();
        if (summary.Changed == 0)
            sb.Append($"No delete-blocking attributes found in {summary.Checked} {EntryWord(summary.Checked)}.");
        else
            sb.Append($"Unblocked {summary.Changed} of {summary.Checked} {EntryWord(summary.Checked)}.");

        if (summary.Failed > 0)
            sb.Append($" Failed: {summary.Failed}.");

        return sb.ToString();
    }

    private static string EntryWord(int count) => count == 1 ? "entry" : "entries";

    private sealed class Summary
    {
        public int Checked { get; set; }
        public int Changed { get; set; }
        public int Failed { get; set; }
    }
}
