namespace Jitzu.Shell;

internal static class CdPathHint
{
    internal const string Prefix = " -> ";

    public static string? GetHint(string input, LabelManager? labelManager, string workingDirectory, int maxPathLength = 48)
    {
        if (!TryGetCdArgument(input.AsSpan(), out var argument))
            return null;

        var target = argument.Length == 0
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : ShellPathResolver.ExpandPath(argument, labelManager, workingDirectory);

        if (!Directory.Exists(target))
            return null;

        return Prefix + CompactPath(target, maxPathLength);
    }

    internal static bool TryGetCdArgument(ReadOnlySpan<char> input, out string argument)
    {
        argument = "";
        input = input.Trim();

        if (input.IsEmpty)
            return false;

        if (!ConsumeToken(input, out var command, out var position))
            return false;

        if (!command.Equals("cd", StringComparison.OrdinalIgnoreCase))
            return false;

        input = input[position..];
        var rest = input.TrimStart();

        if (rest.IsEmpty)
            return true;

        if (!ConsumeToken(rest, out argument, out position))
            return false;

        if (argument == "-")
            return false;

        rest = rest[position..].TrimStart();
        return rest.IsEmpty;
    }

    internal static string CompactPath(string path, int maxLength)
    {
        if (maxLength <= 0 || path.Length <= maxLength)
            return path;

        var root = Path.GetPathRoot(path) ?? "";
        var remainder = path[root.Length..].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segments = remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return path.Length <= maxLength ? path : path[^Math.Min(path.Length, maxLength)..];

        for (var take = Math.Min(segments.Length, 3); take >= 1; take--)
        {
            var tail = Path.Combine(segments[^take..]);
            var compact = root.Length > 0
                ? Path.Combine(root, "...", tail)
                : Path.Combine("...", tail);

            if (compact.Length <= maxLength || take == 1)
                return compact;
        }

        return path;
    }

    private static bool ConsumeToken(ReadOnlySpan<char> input, out string token, out int position)
    {
        token = "";
        position = 0;

        if (input.IsEmpty)
            return false;

        var quote = input[0] is '"' or '\'' or '`' ? input[0] : '\0';
        var start = quote == '\0' ? 0 : 1;
        var current = start;

        while (current < input.Length)
        {
            var ch = input[current];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    token = input[start..current].ToString();
                    position = current + 1;
                    return true;
                }
            }
            else if (char.IsWhiteSpace(ch))
            {
                break;
            }

            current++;
        }

        if (quote != '\0')
            return false;

        token = input[start..current].ToString();
        position = current;
        return true;
    }
}
