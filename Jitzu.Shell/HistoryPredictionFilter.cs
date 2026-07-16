namespace Jitzu.Shell;

/// <summary>
/// Filters history predictions based on contextual validity.
/// For cd commands, only predictions with paths reachable from the current directory are shown.
/// </summary>
public static class HistoryPredictionFilter
{
    /// <summary>
    /// Returns true if the prediction is contextually valid for the current working directory.
    /// Non-cd commands always pass. For cd commands, relative paths must resolve to an existing directory.
    /// Absolute paths, ~-prefixed paths, and label-prefixed paths always pass.
    /// </summary>
    public static bool IsValid(ReadOnlySpan<char> prediction, ReadOnlySpan<char> workingDirectory)
    {
        if (!CdPathHint.TryGetCdArgument(prediction, out var cdArgument))
            return true;

        var argument = cdArgument.AsSpan();

        if (argument.IsEmpty)
            return true;

        if (IsAbsoluteOrSpecialPath(argument))
            return true;

        // Relative path — check if it resolves to an existing directory from cwd
        var fullPath = Path.Join(workingDirectory, argument);
        return Directory.Exists(fullPath);
    }

    private static bool IsAbsoluteOrSpecialPath(ReadOnlySpan<char> path)
    {
        // Tilde-prefixed: ~/foo, ~\foo
        if (path[0] is '~')
            return true;

        // Unix absolute: /foo
        if (path[0] is '/')
            return true;

        // Windows absolute: C:\, D:/, etc.
        if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] is ':' && path[2] is '/' or '\\')
            return true;

        // Label-prefixed: contains ':' before any path separator (e.g., git:Jitzu)
        var colonIdx = path.IndexOf(':');
        if (colonIdx > 0)
        {
            var beforeColon = path[..colonIdx];
            return !beforeColon.Contains('/') && !beforeColon.Contains('\\');
        }

        return false;
    }
}
