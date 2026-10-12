using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jitzu.Shell.Core.Commands;

/// <summary>Recursively searches filenames with fd-style syntax and defaults.</summary>
public class FindCommand(CommandContext context) : CommandBase(context)
{
    private const string Help = """
        Usage: find [options] [pattern] [path...]
        Search recursively by filename (regular expression, smart case).
        Hidden files and ignored entries are skipped by default.
          -g, --glob              Match a glob instead of a regular expression
          -F, --fixed-strings     Treat the pattern as literal text
          -i, --ignore-case       Case-insensitive matching
          -s, --case-sensitive    Case-sensitive matching
          -H, --hidden            Include hidden files and directories
          -I, --no-ignore         Include ignored files and directories
          -u, --unrestricted      Include both hidden and ignored entries
          -t, --type f|d|l        Filter files, directories, or symbolic links (repeatable)
          -e, --extension ext     Filter file extensions (repeatable)
          -p, --full-path         Match the full absolute path
          -a, --absolute-path     Print absolute paths
          -d, --max-depth depth   Limit traversal depth
          -E, --exclude glob      Exclude matching entries (repeatable)
          -h, --help              Show this help
        Examples: find readme; find -e cs . src; find -g '*.json'; find -HI
        """;

    public override Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        try
        {
            var options = ParseArgs(args.ToArray());
            if (options.Help)
                return Task.FromResult(new ShellResult(ResultType.OsCommand, Help, null));

            var ignoreCase = options.IgnoreCase ?? !options.Pattern.Any(char.IsUpper);
            var expression = options.Pattern.Length == 0 ? "" : options.Glob ? GlobToRegex(options.Pattern, anchored: true)
                : options.FixedStrings ? Regex.Escape(options.Pattern) : options.Pattern;
            var matcher = new Regex(expression, RegexOptions.CultureInvariant |
                (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(1));
            var excludes = options.Excludes.Select(pattern => IgnoreRule.Create("", pattern)).ToArray();
            var roots = options.Paths.Select(searchPath => (Input: searchPath, Full: ExpandPath(searchPath))).ToArray();
            foreach (var root in roots)
                if (!Directory.Exists(root.Full))
                    throw new DirectoryNotFoundException($"No such directory: {root.Input}");

            var output = new StringBuilder();
            foreach (var root in roots)
            {
                foreach (var entry in EnumerateEntries(root.Full, options, excludes))
                {
                    var isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                    var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                    var type = isLink ? 'l' : isDirectory ? 'd' : 'f';
                    if (options.Types.Count > 0 && !options.Types.Contains(type)) continue;
                    if (options.Extensions.Count > 0 && !options.Extensions.Any(ext =>
                            entry.Name.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!matcher.IsMatch(options.FullPath ? Normalize(entry.FullName) : entry.Name)) continue;

                    var display = options.AbsolutePath || Path.IsPathRooted(root.Input)
                        ? entry.FullName : Path.GetRelativePath(Environment.CurrentDirectory, entry.FullName);
                    output.Append(display);
                    if (isDirectory && !isLink) output.Append(Path.DirectorySeparatorChar);
                    output.AppendLine();
                }
            }

            return Task.FromResult(new ShellResult(ResultType.OsCommand, output.ToString().TrimEnd('\r', '\n'), null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ShellResult(ResultType.Error, "", ex));
        }
    }

    private static Options ParseArgs(string[] args)
    {
        var options = new Options();
        var positional = new List<string>();
        var endOptions = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (endOptions || !arg.StartsWith('-') || arg == "-")
            {
                positional.Add(arg);
                continue;
            }
            if (arg == "--") { endOptions = true; continue; }

            if (arg.StartsWith("--"))
            {
                var split = arg.IndexOf('=');
                var name = split < 0 ? arg : arg[..split];
                var value = split < 0 ? null : arg[(split + 1)..];
                var flag = name switch
                {
                    "--glob" => 'g', "--fixed-strings" => 'F', "--ignore-case" => 'i',
                    "--case-sensitive" => 's', "--hidden" => 'H', "--no-ignore" => 'I',
                    "--unrestricted" => 'u', "--type" => 't', "--extension" => 'e',
                    "--full-path" => 'p', "--absolute-path" => 'a', "--max-depth" => 'd',
                    "--exclude" => 'E', "--help" => 'h',
                    _ => throw new ArgumentException($"Unknown option: {name}. See find --help.")
                };
                if (TakesValue(flag)) value ??= NextValue(args, ref i, name);
                else if (value is not null) throw new ArgumentException($"Option {name} does not take a value.");
                ApplyOption(options, flag, value);
            }
            else
            {
                for (var j = 1; j < arg.Length; j++)
                {
                    var flag = arg[j];
                    string? value = null;
                    if (TakesValue(flag))
                    {
                        value = j + 1 < arg.Length ? arg[(j + 1)..] : NextValue(args, ref i, $"-{flag}");
                        j = arg.Length;
                    }
                    ApplyOption(options, flag, value);
                }
            }
        }
        options.Pattern = positional.FirstOrDefault() ?? "";
        options.Paths.AddRange(positional.Skip(1));
        if (options.Paths.Count == 0) options.Paths.Add(".");
        return options;
    }

    private static bool TakesValue(char flag) => flag is 't' or 'e' or 'd' or 'E';

    private static string NextValue(string[] args, ref int i, string name)
    {
        if (++i >= args.Length || args[i].StartsWith('-'))
            throw new ArgumentException($"Option {name} requires a value.");
        return args[i];
    }

    private static void ApplyOption(Options options, char flag, string? value)
    {
        switch (flag)
        {
            case 'g': options.Glob = true; options.FixedStrings = false; break;
            case 'F': options.FixedStrings = true; options.Glob = false; break;
            case 'i': options.IgnoreCase = true; break;
            case 's': options.IgnoreCase = false; break;
            case 'H': options.Hidden = true; break;
            case 'I': options.NoIgnore = true; break;
            case 'u': options.Hidden = true; options.NoIgnore = true; break;
            case 'p': options.FullPath = true; break;
            case 'a': options.AbsolutePath = true; break;
            case 'h': options.Help = true; break;
            case 't':
                options.Types.Add(value switch
                {
                    "f" or "file" => 'f', "d" or "dir" or "directory" => 'd', "l" or "symlink" => 'l',
                    _ => throw new ArgumentException($"Unsupported file type: {value}. Use f, d, or l.")
                });
                break;
            case 'e':
                var ext = value!.TrimStart('.');
                if (ext.Length == 0) throw new ArgumentException("Extension cannot be empty.");
                options.Extensions.Add(ext);
                break;
            case 'd':
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var depth))
                    throw new ArgumentException("Maximum depth must be a non-negative integer.");
                options.MaxDepth = depth;
                break;
            case 'E': options.Excludes.Add(value!); break;
            default: throw new ArgumentException($"Unknown option: -{flag}. See find --help.");
        }
    }

    private static IEnumerable<FileSystemInfo> EnumerateEntries(string root, Options options, IgnoreRule[] excludes)
    {
        var rules = new List<IgnoreRule>();
        var ancestors = new Stack<string>();
        for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
            ancestors.Push(parent.FullName);
        var inRepository = false;
        if (!options.NoIgnore)
            foreach (var ancestor in ancestors)
            {
                inRepository |= IsRepository(ancestor);
                LoadRules(ancestor, inRepository, rules);
            }

        var pending = new Stack<(string Path, int Depth, List<IgnoreRule> Rules, bool InRepository)>();
        pending.Push((root, 0, rules, inRepository));
        while (pending.TryPop(out var directory))
        {
            if (directory.Depth >= options.MaxDepth) continue;
            var currentRules = new List<IgnoreRule>(directory.Rules);
            var repository = directory.InRepository || IsRepository(directory.Path);
            if (!options.NoIgnore) LoadRules(directory.Path, repository, currentRules);
            // fd-specific and generic ignore files take precedence over Git rules,
            // even when a Git rule comes from a more deeply nested directory.
            currentRules = currentRules.OrderBy(rule => rule.Priority).ToList();

            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(directory.Path).GetFileSystemInfos(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var entry in entries)
            {
                var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                if (!options.Hidden && (entry.Name.StartsWith('.') || entry.Attributes.HasFlag(FileAttributes.Hidden)))
                    continue;
                if (excludes.Any(rule => rule.Matches(Normalize(Path.GetRelativePath(root, entry.FullName)), isDirectory)))
                    continue;
                var ignored = false;
                foreach (var rule in currentRules)
                    if (rule.Matches(Normalize(Path.GetRelativePath(rule.Root, entry.FullName)), isDirectory))
                        ignored = !rule.Negated;
                if (ignored) continue;

                yield return entry;
                // Directory links can point outside the root or form cycles; do not follow them.
                if (isDirectory && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push((entry.FullName, directory.Depth + 1, currentRules, repository));
            }
        }
    }

    private static bool IsRepository(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    private static void LoadRules(string directory, bool inRepository, List<IgnoreRule> rules)
    {
        foreach (var (name, priority) in new[] { (".git/info/exclude", 0), (".gitignore", 1), (".ignore", 2), (".fdignore", 3) })
        {
            if (name == ".gitignore" && !inRepository) continue;
            try
            {
                var file = Path.Combine(directory, name);
                if (!File.Exists(file)) continue;
                foreach (var line in File.ReadLines(file))
                {
                    var pattern = line.TrimEnd();
                    if (pattern.Length == 0 || pattern.StartsWith('#')) continue;
                    rules.Add(IgnoreRule.Create(directory, pattern, priority));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }

    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private sealed record IgnoreRule(string Root, Regex Pattern, bool DirectoryOnly, bool Negated, int Priority)
    {
        public static IgnoreRule Create(string root, string pattern, int priority = 0)
        {
            var negated = pattern.StartsWith('!');
            if (negated) pattern = pattern[1..];
            var directoryOnly = pattern.EndsWith('/');
            pattern = pattern.TrimEnd('/');
            var anchored = pattern.StartsWith('/') || pattern.Contains('/');
            pattern = pattern.TrimStart('/');
            return new IgnoreRule(root, new Regex(GlobToRegex(pattern, anchored), RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)), directoryOnly, negated, priority);
        }

        public bool Matches(string relative, bool isDirectory) =>
            (!DirectoryOnly || isDirectory) && Pattern.IsMatch(relative);
    }

    private static string GlobToRegex(string glob, bool anchored)
    {
        var result = new StringBuilder(anchored ? "^" : "(?:^|/)");
        var braceDepth = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            switch (glob[i])
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') { result.Append("(?:.*/)?"); i++; }
                    else result.Append(".*");
                    break;
                case '*': result.Append("[^/]*"); break;
                case '?': result.Append("[^/]"); break;
                case '{' when glob.IndexOf('}', i + 1) >= 0:
                    result.Append("(?:"); braceDepth++; break;
                case ',' when braceDepth > 0: result.Append('|'); break;
                case '}' when braceDepth > 0: result.Append(')'); braceDepth--; break;
                case '[':
                    var end = glob.IndexOf(']', i + 1);
                    if (end < 0) { result.Append(@"\["); break; }
                    result.Append('[');
                    var content = glob[(i + 1)..end];
                    if (content.StartsWith('!')) content = "^" + content[1..];
                    result.Append(content.Replace(@"\", @"\\")).Append(']');
                    i = end;
                    break;
                case '\\' when i + 1 < glob.Length: result.Append(Regex.Escape(glob[++i].ToString())); break;
                default: result.Append(Regex.Escape(glob[i].ToString())); break;
            }
        }
        return result.Append('$').ToString();
    }

    private sealed class Options
    {
        public string Pattern = "";
        public readonly List<string> Paths = [];
        public readonly HashSet<char> Types = [];
        public readonly List<string> Extensions = [];
        public readonly List<string> Excludes = [];
        public bool? IgnoreCase;
        public bool Glob, FixedStrings, Hidden, NoIgnore, FullPath, AbsolutePath, Help;
        public int MaxDepth = int.MaxValue;
    }
}
