namespace Jitzu.Shell;

public class HistoryManager
{
    private static readonly HashSet<string> IgnoredCommands = ["exit", "clear"];

    private readonly string _historyFile;
    private readonly bool _persist;
    private readonly List<string> _history = [];
    private readonly HashSet<string> _historySet = [];
    private readonly Infrastructure.PersistentFileGuard _fileGuard;

    public int Count => _history.Count;
    public string this[int historyIndex] => _history[historyIndex];
    public string? PersistenceWarning => _fileGuard.DegradedReason;

    public HistoryManager(bool persist = true) : this(persist, persist
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jitzu", "history.txt")
        : "")
    {
    }

    internal HistoryManager(bool persist, string historyFile, Action<string>? beforeAtomicReplace = null,
        Action<string>? afterAtomicReplace = null, Action<string>? afterSuccessfulCommit = null,
        Func<string, ReadOnlyMemory<byte>, Task>? temporaryWriter = null)
    {
        _persist = persist;
        _historyFile = historyFile;
        _fileGuard = new Infrastructure.PersistentFileGuard(
            historyFile, persist, beforeAtomicReplace, afterAtomicReplace, afterSuccessfulCommit, temporaryWriter);
    }

    public void Initialise()
    {
        if (!_persist)
        {
            Infrastructure.Logging.StartupProfiler.Mark("history-loaded");
            return;
        }

        // Startup must consume this small local file before predictions are usable;
        // synchronous I/O avoids paying for the thread-pool I/O machinery on the critical path.
        string[] lines;
        try
        {
            lines = Infrastructure.StartupFileReader.ReadAllLines(
                _historyFile, Infrastructure.StartupFileReader.HistoryMaxBytes);
        }
        catch (FileNotFoundException)
        {
            Infrastructure.Logging.StartupProfiler.Mark("history-loaded");
            return;
        }
        catch (DirectoryNotFoundException)
        {
            Infrastructure.Logging.StartupProfiler.Mark("history-loaded");
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            _fileGuard.Degrade(ex.Message);
            Infrastructure.Logging.StartupProfiler.Mark("history-loaded");
            return;
        }

        // Deduplicate on load - keep only the last occurrence of each command
        var deduplicated = new List<string>();
        var seen = new HashSet<string>();

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) && seen.Add(lines[i]))
                deduplicated.Add(lines[i]);
        }

        deduplicated.Reverse();

        foreach (var entry in deduplicated)
        {
            _history.Add(entry);
            _historySet.Add(entry);
        }
        Infrastructure.Logging.StartupProfiler.Mark("history-loaded");
    }

    public int SearchBackward(string query, int startIndex)
    {
        if (string.IsNullOrEmpty(query))
            return -1;

        for (var i = Math.Min(startIndex, _history.Count - 1); i >= 0; i--)
        {
            if (_history[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    public List<string> GetPredictions(ReadOnlySpan<char> prefix, int maxCount, Func<string, bool>? filter = null,
        string? workingDirectory = null, Func<string, string>? pathResolver = null)
    {
        if (prefix.IsEmpty) return [];

        var results = new List<string>(maxCount);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCdTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cdQuery = "";
        var findCdPaths = workingDirectory is not null
            && CdPathHint.TryGetCdArgument(prefix, out cdQuery)
            && cdQuery.Length > 0;

        for (var i = _history.Count - 1; i >= 0 && results.Count < maxCount; i--)
        {
            var entry = _history[i];
            if (entry.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !entry.AsSpan().Equals(prefix, StringComparison.OrdinalIgnoreCase)
                && !IgnoredCommands.Contains(entry)
                && (filter is null || filter(entry))
                && IsNewPrediction(entry, workingDirectory, pathResolver, seen, seenCdTargets))
                results.Add(entry);

            if (findCdPaths && results.Count < maxCount
                && TryCreateCdPathPrediction(entry, cdQuery, workingDirectory!, pathResolver, out var pathPrediction)
                && (filter is null || filter(pathPrediction))
                && IsNewPrediction(pathPrediction, workingDirectory, pathResolver, seen, seenCdTargets))
                results.Add(pathPrediction);
        }

        return results;
    }

    private static bool IsNewPrediction(string prediction, string? workingDirectory, Func<string, string>? pathResolver,
        HashSet<string> seen, HashSet<string> seenCdTargets)
    {
        if (!seen.Add(prediction))
            return false;

        if (workingDirectory is null || !CdPathHint.TryGetCdArgument(prediction, out var path))
            return true;

        var resolvedPath = pathResolver?.Invoke(path) ?? Path.GetFullPath(path, workingDirectory);
        return seenCdTargets.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolvedPath)));
    }

    private static bool TryCreateCdPathPrediction(string historyEntry, string query, string workingDirectory,
        Func<string, string>? pathResolver, out string prediction)
    {
        prediction = "";

        if (!CdPathHint.TryGetCdArgument(historyEntry, out var historicalPath)
            || !historicalPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;

        var resolvedPath = pathResolver?.Invoke(historicalPath) ?? historicalPath;
        if (!Path.IsPathFullyQualified(resolvedPath) || !Directory.Exists(resolvedPath))
            return false;

        var displayPath = Path.GetRelativePath(workingDirectory, Path.GetFullPath(resolvedPath));
        if (displayPath.Any(char.IsWhiteSpace))
            displayPath = $"\"{displayPath}\"";

        prediction = $"cd {displayPath}";
        return true;
    }

    public async Task RemoveAsync(string entry)
    {
        if (!_historySet.Contains(entry))
            return;

        _historySet.Remove(entry);
        _history.Remove(entry);

        if (_persist)
        {
            var content = string.Concat(_history.Select(line => line + Environment.NewLine));
            await _fileGuard.ReplaceAtomicallyAsync(System.Text.Encoding.UTF8.GetBytes(content));
        }
    }

    public async Task WriteAsync(string historyItem)
    {
        if (string.IsNullOrWhiteSpace(historyItem))
            return;

        Record(historyItem);

        if (_persist)
        {
            var content = string.Concat(_history.Select(line => line + Environment.NewLine));
            await _fileGuard.ReplaceAtomicallyAsync(System.Text.Encoding.UTF8.GetBytes(content));
        }
    }

    public void Record(string historyItem)
    {
        if (string.IsNullOrWhiteSpace(historyItem))
            return;

        // Move existing entry to end, or add new
        if (!_historySet.Add(historyItem))
            _history.Remove(historyItem);

        _history.Add(historyItem);
    }
}
