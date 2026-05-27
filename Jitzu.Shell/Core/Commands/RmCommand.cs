namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Removes files or directories.
/// </summary>
public class RmCommand : CommandBase
{
    public RmCommand(CommandContext context) : base(context) { }

    public override Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (args.Length == 0)
            return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception("Usage: rm [-r] [-f] <path> [path2 ...]")));

        try
        {
            var recursive = false;
            var force = false;
            var paths = new List<string>();

            foreach (var arg in args.Span)
            {
                if (arg is "--recursive")
                    recursive = true;
                else if (arg is "--force")
                    force = true;
                else if (arg.StartsWith('-') && arg.Length > 1)
                {
                    foreach (var option in arg.AsSpan(1))
                    {
                        switch (option)
                        {
                            case 'r':
                                recursive = true;
                                break;
                            case 'f':
                                force = true;
                                break;
                            default:
                                return Task.FromResult(new ShellResult(ResultType.Error, "",
                                    new Exception($"rm: invalid option -- '{option}'")));
                        }
                    }
                }
                else
                    paths.Add(arg);
            }

            if (paths.Count == 0)
                return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception("No path specified")));

            foreach (var p in paths)
            {
                var path = ExpandPath(p);

                if (Directory.Exists(path))
                {
                    if (!recursive)
                        return Task.FromResult(new ShellResult(ResultType.Error, "",
                            new Exception($"'{p}' is a directory (use -r to remove)")));
                    if (force)
                        ClearDeleteBlockingAttributes(path);
                    Directory.Delete(path, true);
                }
                else if (File.Exists(path))
                {
                    if (force)
                        ClearDeleteBlockingAttributes(path);
                    File.Delete(path);
                }
                else if (!force)
                {
                    return Task.FromResult(new ShellResult(ResultType.Error, "",
                        new Exception($"No such file or directory: {p}")));
                }
            }

            return Task.FromResult(new ShellResult(ResultType.Jitzu, "", null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ShellResult(ResultType.Error, "", ex));
        }
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
            foreach (var entry in EnumerateFileSystemEntriesDepthFirst(directory))
                yield return entry;

            yield return directory;
        }

        yield return path;
    }
}
