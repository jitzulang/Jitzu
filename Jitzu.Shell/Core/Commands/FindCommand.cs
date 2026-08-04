using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Recursively searches for files and directories.
/// </summary>
public class FindCommand : CommandBase
{
    public FindCommand(CommandContext context) : base(context) { }

    public override Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (args.Length == 0)
            return Task.FromResult(new ShellResult(ResultType.Error, "",
                new Exception("Usage: find <path> [-name pattern] [-type f|d] [-ext .cs]")));

        try
        {
            string? searchPath = null;
            string? namePattern = null;
            string? extension = null;
            char? typeFilter = null; // 'f' for file, 'd' for directory
            var useGitIgnore = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args.Span[i];
                switch (arg)
                {
                    case "-i":
                    case "--gitignore":
                        useGitIgnore = true;
                        break;
                    case "-name" when i + 1 < args.Length:
                        namePattern = args.Span[++i];
                        break;
                    case "-type" when i + 1 < args.Length:
                        typeFilter = args.Span[++i][0];
                        break;
                    case "-ext" when i + 1 < args.Length:
                        extension = args.Span[++i];
                        if (!extension.StartsWith('.')) extension = "." + extension;
                        break;
                    default:
                        searchPath ??= arg;
                        break;
                }
            }

            searchPath ??= ".";
            var fullPath = ExpandPath(searchPath);

            if (!Directory.Exists(fullPath))
                return Task.FromResult(new ShellResult(ResultType.Error, "",
                    new Exception($"No such directory: {searchPath}")));

            var sb = new StringBuilder();
            var dirColor = Theme["ls.directory"];
            var reset = ThemeConfig.Reset;
            var count = 0;
            var ignoreMatcher = useGitIgnore ? GitIgnoreMatcher.TryCreate(fullPath) : null;

            foreach (var entry in EnumerateEntries(fullPath, ignoreMatcher))
            {
                var isDir = Directory.Exists(entry);
                var name = Path.GetFileName(entry);

                // Type filter
                if (typeFilter == 'f' && isDir) continue;
                if (typeFilter == 'd' && !isDir) continue;

                // Name pattern (supports * and ?)
                if (namePattern != null && !MatchGlob(name, namePattern))
                    continue;

                // Extension filter
                if (extension != null && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    continue;

                var relative = Path.GetRelativePath(Environment.CurrentDirectory, entry);
                if (isDir)
                    sb.AppendLine($"{dirColor}{relative}/{reset}");
                else
                    sb.AppendLine(relative);

                count++;
                if (count >= 1000)
                {
                    sb.AppendLine($"{ThemeConfig.Dim}... truncated at 1000 results{reset}");
                    break;
                }
            }

            if (count == 0)
                return Task.FromResult(new ShellResult(ResultType.OsCommand, "No matches found.", null));

            return Task.FromResult(new ShellResult(ResultType.OsCommand, sb.ToString().TrimEnd(), null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ShellResult(ResultType.Error, "", ex));
        }
    }

    /// <summary>
    /// Matches a filename against a glob pattern (supports * and ?).
    /// Uses the built-in FileSystemName.MatchesSimpleExpression for correct glob semantics.
    /// </summary>
    private static bool MatchGlob(string name, string pattern) =>
        FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

    private static IEnumerable<string> EnumerateEntries(string root, GitIgnoreMatcher? ignoreMatcher)
    {
        var pending = new Stack<string>();
        if (ignoreMatcher?.IsIgnoredDirectory(root) == true)
            yield break;
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var isDirectory = Directory.Exists(entry);
                if (isDirectory && ignoreMatcher?.IsIgnoredDirectory(entry) == true)
                    continue;

                yield return entry;
                if (isDirectory)
                    pending.Push(entry);
            }
        }
    }

    private sealed class GitIgnoreMatcher
    {
        private readonly string _root;
        private readonly Regex[] _patterns;

        private GitIgnoreMatcher(string root, Regex[] patterns)
        {
            _root = root;
            _patterns = patterns;
        }

        public static GitIgnoreMatcher? TryCreate(string searchPath)
        {
            var root = FindRepositoryRoot(searchPath);
            if (root is null)
                return null;

            var ignoreFile = Path.Combine(root, ".gitignore");
            if (!File.Exists(ignoreFile))
                return new GitIgnoreMatcher(root, []);

            var patterns = File.ReadLines(ignoreFile)
                .Select(ToDirectoryRegex)
                .Where(pattern => pattern is not null)
                .Select(pattern => pattern!)
                .ToArray();
            return new GitIgnoreMatcher(root, patterns);
        }

        public bool IsIgnoredDirectory(string path)
        {
            if (string.Equals(Path.GetFileName(path), ".git", StringComparison.OrdinalIgnoreCase))
                return true;

            var relative = Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
            return _patterns.Any(pattern => pattern.IsMatch(relative));
        }

        private static string? FindRepositoryRoot(string path)
        {
            var directory = new DirectoryInfo(path);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, ".git")) ||
                    Directory.Exists(Path.Combine(directory.FullName, ".git")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            return null;
        }

        private static Regex? ToDirectoryRegex(string line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!'))
                return null;

            line = line.TrimEnd('/');
            if (line.Length == 0)
                return null;

            var anchored = line.StartsWith('/');
            line = line.TrimStart('/');
            var expression = GlobToRegex(line, anchored);
            return new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string GlobToRegex(string glob, bool anchored)
        {
            var result = new StringBuilder("^");
            if (!anchored) result.Append("(?:.*/)?");

            for (var i = 0; i < glob.Length; i++)
            {
                if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    result.Append(".*");
                    i++;
                }
                else if (glob[i] == '*') result.Append("[^/]*");
                else if (glob[i] == '?') result.Append("[^/]");
                else result.Append(Regex.Escape(glob[i].ToString()));
            }

            return result.Append("(?:/.*)?$").ToString();
        }
    }
}
