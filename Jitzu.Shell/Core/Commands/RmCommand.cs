namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Removes files or directories.
/// </summary>
public class RmCommand : CommandBase
{
    private readonly IRmInteractiveConsole _console;

    public RmCommand(CommandContext context) : this(context, new SystemRmInteractiveConsole()) { }

    internal RmCommand(CommandContext context, IRmInteractiveConsole console) : base(context)
    {
        _console = console;
    }

    public override Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (args.Length == 0)
            return Task.FromResult(Error("Usage: rm [-r] [-f] [-i] <path> [path2 ...]"));

        try
        {
            var recursive = false;
            var force = false;
            var interactive = false;
            var paths = new List<string>();

            foreach (var arg in args.Span)
            {
                if (arg is "--recursive")
                    recursive = true;
                else if (arg is "--force")
                    force = true;
                else if (arg is "--interactive")
                    interactive = true;
                else if (arg.StartsWith('-') && arg.Length > 1)
                {
                    foreach (var option in arg.AsSpan(1))
                    {
                        switch (option)
                        {
                            case 'r': recursive = true; break;
                            case 'f': force = true; break;
                            case 'i': interactive = true; break;
                            default: return Task.FromResult(Error($"rm: invalid option -- '{option}'"));
                        }
                    }
                }
                else
                    paths.Add(arg);
            }

            if (paths.Count == 0)
                return Task.FromResult(Error("No path specified"));

            if (interactive)
                return Task.FromResult(ExecuteInteractive(paths, force));

            foreach (var p in paths)
            {
                var path = ExpandPath(p);

                if (Directory.Exists(path))
                {
                    if (!recursive)
                        return Task.FromResult(Error($"'{p}' is a directory (use -r to remove)"));
                    DeleteDirectory(path, force);
                }
                else if (File.Exists(path))
                {
                    DeleteFile(path, force);
                }
                else if (!force)
                {
                    return Task.FromResult(Error($"No such file or directory: {p}"));
                }
            }

            return Task.FromResult(Success());
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ShellResult(ResultType.Error, "", ex));
        }
    }

    private ShellResult ExecuteInteractive(List<string> paths, bool force)
    {
        if (paths.Count != 1)
            return Error("rm: -i requires exactly one directory");

        var displayPath = paths[0];
        var path = ExpandPath(displayPath);
        if (File.Exists(path))
            return Error("rm: -i can only be used with a directory");
        if (!Directory.Exists(path))
            return force ? Success() : Error($"No such directory: {displayPath}");
        if (!_console.IsInteractive)
            return Error("rm: -i requires an interactive terminal");

        var tree = RmTreeNode.Create(path);
        var selection = new RmTreeSelection(tree);
        if (!_console.Select(selection, displayPath))
            return Success();

        foreach (var node in selection.GetDeletionRoots().OrderByDescending(node => node.Depth))
        {
            if (node.IsDirectory)
                DeleteDirectory(node.FullPath, force);
            else
                DeleteFile(node.FullPath, force);
        }

        return Success();
    }

    private static ShellResult Success() => new(ResultType.Jitzu, "", null);
    private static ShellResult Error(string message) => new(ResultType.Error, "", new Exception(message));

    private static void DeleteFile(string path, bool force)
    {
        if (force)
            ClearDeleteBlockingAttributes(path);
        File.Delete(path);
    }

    private static void DeleteDirectory(string path, bool force)
    {
        if (force)
            ClearDeleteBlockingAttributes(path);
        Directory.Delete(path, true);
    }

    private static void ClearDeleteBlockingAttributes(string path)
    {
        foreach (var entry in EnumerateFileSystemEntriesDepthFirst(path))
        {
            try
            {
                var attributes = File.GetAttributes(entry);
                var cleared = attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
                if (cleared != attributes)
                    File.SetAttributes(entry, cleared);
            }
            catch
            {
                // Delete will report the remaining access failure with the original path context.
            }
        }
    }

    private static IEnumerable<string> EnumerateFileSystemEntriesDepthFirst(string path)
    {
        if (!Directory.Exists(path))
        {
            yield return path;
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(path))
            yield return file;

        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            // Never walk through a junction or symbolic link into a different tree.
            if (!File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                foreach (var entry in EnumerateFileSystemEntriesDepthFirst(directory))
                    yield return entry;
            }

            yield return directory;
        }

        yield return path;
    }
}
