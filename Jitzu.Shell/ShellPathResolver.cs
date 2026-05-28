namespace Jitzu.Shell;

internal static class ShellPathResolver
{
    public static string ExpandPath(string path, LabelManager? labelManager, string? baseDirectory = null)
    {
        if (labelManager is not null)
            path = labelManager.ExpandLabel(path);

        if (path.StartsWith('~'))
            path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..]);

        baseDirectory ??= Directory.GetCurrentDirectory();
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, baseDirectory);
    }
}
