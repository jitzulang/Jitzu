using System.Runtime.ExceptionServices;

namespace Jitzu.Shell;

public class HistoryManager
{
    private static readonly HashSet<string> IgnoredCommands = ["exit", "clear"];

    private readonly string _historyFile;
    private readonly bool _persist;
    private readonly object _sync = new();
    private readonly List<string> _history = [];
    private readonly HashSet<string> _historySet = [];
    private readonly Infrastructure.PersistentFileGuard _fileGuard;
    private Task? _persistenceTask;
    private bool _persistencePending;
    private Exception? _persistenceFailure;

    public int Count
    {
        get
        {
            lock (_sync)
                return _history.Count;
        }
    }

    public string this[int historyIndex]
    {
        get
        {
            lock (_sync)
                return _history[historyIndex];
        }
    }

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

        lock (_sync)
        {
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
        }
        Infrastructure.Logging.StartupProfiler.Mark("history-loaded");
    }

    public int SearchBackward(string query, int startIndex)
    {
        if (string.IsNullOrEmpty(query))
            return -1;

        lock (_sync)
        {
            for (var i = Math.Min(startIndex, _history.Count - 1); i >= 0; i--)
            {
                if (_history[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        return -1;
    }

    public List<string> GetPredictions(ReadOnlySpan<char> prefix, int maxCount, Func<string, bool>? filter = null,
        string? workingDirectory = null, Func<string, string>? pathResolver = null)
    {
        if (prefix.IsEmpty) return [];

        lock (_sync)
        {
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
        Task? persistenceTask;
        lock (_sync)
        {
            if (!_historySet.Contains(entry))
                return;

            _historySet.Remove(entry);
            _history.Remove(entry);

            persistenceTask = _persist ? QueuePersistenceNoLock() : null;
        }

        if (_persist && persistenceTask is null)
            ThrowReadOnly();

        if (persistenceTask is not null)
            await FlushAsync();
    }

    /// <summary>
    /// Removes a command immediately and schedules the resulting history snapshot in
    /// the background. This is used by the interactive history picker, whose input
    /// loop cannot await a persistence operation.
    /// </summary>
    public void QueueRemove(string entry)
    {
        lock (_sync)
        {
            if (!_historySet.Contains(entry))
                return;

            _historySet.Remove(entry);
            _history.Remove(entry);
            if (_persist)
                QueuePersistenceNoLock();
        }
    }

    public async Task WriteAsync(string historyItem)
    {
        if (string.IsNullOrWhiteSpace(historyItem))
            return;

        Task? persistenceTask;
        lock (_sync)
        {
            RecordNoLock(historyItem);
            persistenceTask = _persist ? QueuePersistenceNoLock() : null;
        }

        if (_persist && persistenceTask is null)
            ThrowReadOnly();

        if (persistenceTask is not null)
            await FlushAsync();
    }

    /// <summary>
    /// Records a command immediately and schedules its durable history update in the
    /// background. The interactive input path uses this method so it never waits for
    /// a full history-file replacement. Call <see cref="FlushAsync"/> before shutdown.
    /// </summary>
    public void QueueWrite(string historyItem)
    {
        if (string.IsNullOrWhiteSpace(historyItem))
            return;

        lock (_sync)
        {
            RecordNoLock(historyItem);
            if (_persist)
                QueuePersistenceNoLock();
        }
    }

    /// <summary>
    /// Waits for all history updates queued so far (and any updates queued while the
    /// worker is draining) to reach durable storage.
    /// </summary>
    public async Task FlushAsync()
    {
        if (!_persist)
            return;

        while (true)
        {
            Task? persistenceTask;
            lock (_sync)
            {
                ThrowPersistenceFailureNoLock();
                persistenceTask = _persistenceTask;
                if (!_persistencePending && persistenceTask is null)
                    return;
            }

            if (persistenceTask is not null)
                await persistenceTask.ConfigureAwait(false);
            else
                await Task.Yield();
        }
    }

    public void Record(string historyItem)
    {
        if (string.IsNullOrWhiteSpace(historyItem))
            return;

        lock (_sync)
            RecordNoLock(historyItem);
    }

    private void RecordNoLock(string historyItem)
    {
        // Move existing entry to end, or add new
        if (!_historySet.Add(historyItem))
            _history.Remove(historyItem);

        _history.Add(historyItem);
    }

    private Task? QueuePersistenceNoLock()
    {
        if (!_fileGuard.CanWrite)
            return null;

        _persistencePending = true;
        return _persistenceTask ??= Task.Run(PersistPendingAsync);
    }

    private async Task PersistPendingAsync()
    {
        while (true)
        {
            try
            {
                byte[] content;
                lock (_sync)
                {
                    if (!_persistencePending)
                    {
                        _persistenceTask = null;
                        return;
                    }

                    _persistencePending = false;
                    content = SerializeHistoryNoLock();
                }

                await _fileGuard.ReplaceAtomicallyAsync(content).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _persistenceFailure ??= ex;
                    _persistencePending = false;
                    _persistenceTask = null;
                    _fileGuard.Degrade(_fileGuard.DegradedReason ?? ex.Message);
                }
                return;
            }
        }
    }

    private byte[] SerializeHistoryNoLock()
    {
        var content = string.Concat(_history.Select(line => line + Environment.NewLine));
        return System.Text.Encoding.UTF8.GetBytes(content);
    }

    private void ThrowReadOnly()
    {
        throw new InvalidOperationException($"Persistent state is read-only: {_fileGuard.DegradedReason}");
    }

    private void ThrowPersistenceFailureNoLock()
    {
        if (_persistenceFailure is not null)
            ExceptionDispatchInfo.Capture(_persistenceFailure).Throw();
    }
}
