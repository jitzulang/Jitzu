using System.Buffers;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using Jitzu.Shell.Core;
using Jitzu.Shell.Core.Completions;

namespace Jitzu.Shell.UI;

/// <summary>
/// Handles interactive input with history and tab completion.
/// </summary>
public class CompletionManager
{
    private static readonly SearchValues<char> PathSeparators = SearchValues.Create("\\/");

    private static readonly FrozenSet<string> StrippableExtensions =
        new[] { ".exe", ".cmd", ".bat", ".com", ".ps1" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly ShellSession session;
    private readonly BuiltinCommands builtinCommands;
    private readonly LabelManager? labelManager;
    private readonly string _homePath;
    private readonly string _normalizedHomePath;
    private string[]? _pathDirectories;
    private string[]? _executableExtensions;
    private readonly bool _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static readonly StringComparer FileNameComparer =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private Dictionary<string, (DateTime LastWrite, HashSet<string> FileNames)>? _pathDirectoryCache;

    public CompletionManager(ShellSession session, BuiltinCommands builtinCommands,
        LabelManager? labelManager = null, string? homePath = null)
    {
        this.session = session;
        this.builtinCommands = builtinCommands;
        this.labelManager = labelManager;
        _homePath = homePath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _normalizedHomePath = NormaliseSeparators(_homePath);
    }

    private string[] PathDirectories => _pathDirectories ??=
        Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

    private string[] ExecutableExtensions => _executableExtensions ??=
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Environment.GetEnvironmentVariable("PATHEXT")?.Split(Path.PathSeparator)
              ?? [".EXE", ".CMD", ".BAT", ".COM", ".PS1"]
            : [".exe", ""];

    public string[] GetCompletions(string input)
    {
        var lastWord = ExtractLastWord(input);
        var completions = session.GetCompletionSuggestions(lastWord);

        if (builtinCommands.FindNearest(lastWord) is { } nearestCommand)
            completions.Add(new RuntimeFunctionCompletion(nearestCommand));

        var unquotedWord = lastWord.Trim('"');
        completions.AddRange(GetFileSystemCompletions(unquotedWord));

        if (GetExecutablesFromPath(unquotedWord) is { Length: > 0 } executableCompletions)
            completions.AddRange(executableCompletions);

        completions.Sort(CompletionComparer.Instance);
        return [.. completions.Select(c => c.Value)];
    }

    private Completion[] GetFileSystemCompletions(ReadOnlySpan<char> partial)
    {
        try
        {
            var useTilde = partial.StartsWith('~');
            string? labelPrefix = null;
            string? labelPath = null;

            if (useTilde)
            {
                partial = $"{_homePath}{partial[1..]}";
            }
            else if (labelManager is not null)
            {
                var colonIndex = partial.IndexOf(':');
                if (colonIndex > 1)
                {
                    var labelName = partial[..colonIndex].ToString();
                    if (labelManager.Labels.TryGetValue(labelName, out var lp))
                    {
                        labelPath = NormaliseSeparators(lp);
                        labelPrefix = $"{labelName}:";
                        var rest = partial[(colonIndex + 1)..];
                        partial = rest.Length > 0 ? Path.Join(lp, rest.ToString()) : lp;
                    }
                }
            }

            var fileName = Path.GetFileName(partial);

            if (partial.EndsWith("/.") || partial.EndsWith("\\."))
                return CompleteDotComponent(partial, fileName);

            if (IsExactDirectoryMatch(partial))
            {
                var dirPath = NormaliseSeparators($"{partial}{Path.DirectorySeparatorChar}");
                dirPath = CollapseHome(dirPath, useTilde);
                if (labelPrefix is not null && labelPath is not null)
                    dirPath = CollapseLabel(dirPath, labelPrefix, labelPath);
                return [new DirectoryCompletion(dirPath)];
            }

            var results = CompleteByPrefix(partial, fileName, useTilde);
            if (labelPrefix is not null && labelPath is not null)
                for (var i = 0; i < results.Length; i++)
                    results[i] = results[i] switch
                    {
                        DirectoryCompletion dc => new DirectoryCompletion(CollapseLabel(dc.Value, labelPrefix, labelPath)),
                        FileCompletion fc => new FileCompletion(CollapseLabel(fc.Value, labelPrefix, labelPath)),
                        _ => results[i]
                    };

            return results;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Replaces the expanded label path with the label prefix in completion results.
    /// e.g. "D:\git\subfolder\" becomes "git:subfolder\"
    /// </summary>
    private static string CollapseLabel(string path, string labelPrefix, string labelPath)
    {
        var normalized = NormaliseSeparators(path);
        if (normalized.StartsWith(labelPath, StringComparison.OrdinalIgnoreCase))
        {
            var rest = normalized[labelPath.Length..];
            if (rest.Length > 0 && rest[0] == Path.DirectorySeparatorChar)
                rest = rest[1..];
            return $"{labelPrefix}{rest}";
        }

        return path;
    }

    private Completion[] CompleteDotComponent(ReadOnlySpan<char> partial, ReadOnlySpan<char> fileName)
    {
        var withoutDot = partial[..^2].ToString();

        if (withoutDot.Length == 2 && withoutDot[1] == ':')
            withoutDot = $"{withoutDot}{Path.DirectorySeparatorChar}";

        var dotDir = Directory.Exists(withoutDot) ? withoutDot : Environment.CurrentDirectory;
        var pattern = fileName.Length > 0 ? $"{fileName}*" : "*";

        return EnumerateAsCompletions(dotDir, pattern);
    }

    private static bool IsExactDirectoryMatch(ReadOnlySpan<char> partial) =>
        partial is [_, ':'] && Directory.Exists($"{partial}/")
        || !Path.EndsInDirectorySeparator(partial) && Directory.Exists(partial.ToString());

    private Completion[] CompleteByPrefix(
        ReadOnlySpan<char> partial, ReadOnlySpan<char> fileName, bool useTilde)
    {
        var directory = ResolveSearchDirectory(partial);
        var useRelative = !useTilde && !Path.IsPathRooted(partial);

        var entries = Directory.GetFileSystemEntries(directory, $"{fileName}*");
        var results = new Completion[entries.Length];

        for (var i = 0; i < entries.Length; i++)
        {
            var f = NormaliseSeparators(entries[i]);

            if (useRelative && Path.IsPathRooted(f))
                f = Path.GetRelativePath(Environment.CurrentDirectory, f);

            f = CollapseHome(f, useTilde);
            results[i] = ToCompletion(f);
        }

        return results;
    }

    private static string ResolveSearchDirectory(ReadOnlySpan<char> partial)
    {
        var directory = partial.ToString();

        if (!Directory.Exists(directory) && Path.GetDirectoryName(partial) is { IsEmpty: false } parent)
            directory = parent.ToString();

        if (!Directory.Exists(directory))
            directory = Environment.CurrentDirectory;

        return directory;
    }

    private ExecutableCompletion[] GetExecutablesFromPath(string searchValue)
    {
        if (Path.IsPathRooted(searchValue) || Path.EndsInDirectorySeparator(searchValue))
            return [];

        try
        {
            _pathDirectoryCache ??= new(FileNameComparer);

            HashSet<string>? seen = null;
            List<ExecutableCompletion>? results = null;

            foreach (var ext in ExecutableExtensions)
            {
                var searchTerm = Path.ChangeExtension(searchValue, ext);

                foreach (var dir in PathDirectories)
                {
                    var fileNames = GetCachedFileNames(dir);
                    if (fileNames is null || !fileNames.Contains(searchTerm))
                        continue;

                    var name = StripExtension(searchTerm);
                    seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (seen.Add(name))
                    {
                        results ??= [];
                        results.Add(new ExecutableCompletion(name));
                    }
                }
            }

            return results?.ToArray() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private HashSet<string>? GetCachedFileNames(string directory)
    {
        if (!Directory.Exists(directory))
            return null;

        try
        {
            var lastWrite = Directory.GetLastWriteTimeUtc(directory);

            if (_pathDirectoryCache!.TryGetValue(directory, out var entry) && entry.LastWrite == lastWrite)
                return entry.FileNames;

            var files = Directory.GetFiles(directory);
            var names = new HashSet<string>(files.Length, FileNameComparer);
            foreach (var file in files)
                names.Add(Path.GetFileName(file));

            _pathDirectoryCache[directory] = (lastWrite, names);
            return names;
        }
        catch
        {
            return null;
        }
    }

    private Completion[] EnumerateAsCompletions(string directory, string pattern)
    {
        var entries = Directory.GetFileSystemEntries(directory, pattern);
        var results = new Completion[entries.Length];

        for (var i = 0; i < entries.Length; i++)
            results[i] = ToCompletion(NormaliseSeparators(entries[i]));

        return results;
    }

    private Completion ToCompletion(string displayPath)
    {
        var realPath = displayPath.StartsWith('~') ? $"{_homePath}{displayPath[1..]}" : displayPath;
        return Directory.Exists(realPath)
            ? new DirectoryCompletion($"{displayPath}{Path.DirectorySeparatorChar}")
            : new FileCompletion(displayPath);
    }

    private string CollapseHome(string path, bool useTilde)
    {
        if (!useTilde) return path;
        var normalized = NormaliseSeparators(path);
        return normalized.StartsWith(_normalizedHomePath, StringComparison.OrdinalIgnoreCase)
            ? $"~{normalized[_normalizedHomePath.Length..]}"
            : path;
    }

    private string StripExtension(string fileName)
    {
        if (!_isWindows) return fileName;
        var ext = Path.GetExtension(fileName);
        return StrippableExtensions.Contains(ext) ? Path.GetFileNameWithoutExtension(fileName) : fileName;
    }

    /// <summary>
    /// Extracts the last argument from a command line, respecting quoted strings.
    /// Handles unclosed quotes (e.g. when cursor is inside a quoted path).
    /// </summary>
    private static string ExtractLastWord(string input)
    {
        var inQuote = false;
        var lastWordStart = 0;

        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '"')
                inQuote = !inQuote;
            else if (input[i] == ' ' && !inQuote)
                lastWordStart = i + 1;
        }

        return input[lastWordStart..];
    }

    private static string NormaliseSeparators(ReadOnlySpan<char> path)
    {
        Span<char> output = stackalloc char[path.Length];
        path.ReplaceAny(output, PathSeparators, Path.DirectorySeparatorChar);
        return output.ToString();
    }
}
