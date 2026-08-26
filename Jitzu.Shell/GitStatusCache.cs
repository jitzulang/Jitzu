using System.Diagnostics;

namespace Jitzu.Shell;

/// <summary>
/// Caches git repository information between prompt renders.
/// Repo root is cached until the working directory changes.
/// Branch is cached until .git/HEAD's modification time changes.
/// Working-tree status is refreshed in the background and returned from the latest
/// completed read. Status is invalidated after shell commands so a command never
/// leaves an old snapshot visible in the next prompt.
/// </summary>
internal sealed class GitStatusCache : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan DefaultStatusRefreshInterval = TimeSpan.FromMilliseconds(500);

    private string? _cachedDirectory;
    private DirectoryInfo? _cachedRepoRoot;
    private bool _repoRootResolved;

    private string? _cachedHeadPath;
    private DateTime _cachedHeadWriteTime;
    private string? _cachedBranch;

    private readonly object _statusLock = new();
    private readonly TimeSpan _statusRefreshInterval;
    private readonly Func<string, CancellationToken, Task<GitStatus>> _statusReader;
    private readonly CancellationTokenSource _disposeCts = new();
    private string? _statusRepoPath;
    private GitStatus _status;
    private bool _statusValid;
    private bool _statusRefreshRequested;
    private long _statusGeneration;
    private long _lastStatusRefreshCompleted;
    private Task? _statusRefresh;
    private bool _disposed;
    private Task? _disposeTask;

    public GitStatusCache()
        : this(DefaultStatusRefreshInterval, ReadGitStatusAsync)
    {
    }

    internal GitStatusCache(
        TimeSpan statusRefreshInterval,
        Func<string, CancellationToken, Task<GitStatus>> statusReader)
    {
        _statusRefreshInterval = statusRefreshInterval < TimeSpan.Zero
            ? TimeSpan.Zero
            : statusRefreshInterval;
        _statusReader = statusReader ?? throw new ArgumentNullException(nameof(statusReader));
    }

    /// <summary>
    /// Returns the cached git repo root, only recomputing when the working directory changes.
    /// </summary>
    public DirectoryInfo? FindGitRepoFolder(string currentDirectory)
    {
        if (_repoRootResolved && _cachedDirectory == currentDirectory)
            return _cachedRepoRoot;

        _cachedDirectory = currentDirectory;
        _cachedRepoRoot = FindGitRepoFolderCore(currentDirectory);
        _repoRootResolved = true;
        _cachedHeadPath = null;

        return _cachedRepoRoot;
    }

    /// <summary>
    /// Returns the cached branch name, only re-reading .git/HEAD when its modification time changes.
    /// </summary>
    public string? GetGitBranch(string gitRepoPath)
    {
        var gitPath = Path.Combine(gitRepoPath, ".git");

        // Handle worktrees: .git may be a file containing "gitdir: <path>"
        if (File.Exists(gitPath))
        {
            try
            {
                var gitdirLine = File.ReadAllText(gitPath).Trim();
                if (gitdirLine.StartsWith("gitdir:"))
                    gitPath = gitdirLine["gitdir:".Length..].Trim();
            }
            catch
            {
                return null;
            }
        }

        var headPath = Path.Combine(gitPath, "HEAD");
        if (!File.Exists(headPath))
            return null;

        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(headPath);
            if (_cachedHeadPath == headPath && _cachedHeadWriteTime == lastWrite)
                return _cachedBranch;

            var headContent = File.ReadAllText(headPath).Trim();

            _cachedHeadPath = headPath;
            _cachedHeadWriteTime = lastWrite;

            // Branch changes also change the branch metadata reported by git status.
            // Invalidate the working-tree snapshot without waiting on git here.
            InvalidateStatus(gitRepoPath);

            if (headContent.StartsWith("ref: refs/heads/"))
                _cachedBranch = headContent["ref: refs/heads/".Length..];
            else if (headContent.Length >= 7)
                _cachedBranch = headContent[..7];
            else
                _cachedBranch = null;

            return _cachedBranch;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the latest completed working-tree status without waiting for git. The first read,
    /// periodic reads, and reads requested by <see cref="InvalidateStatus"/> are performed by a
    /// tracked background task. A status invalidated by a command is hidden until its replacement
    /// is complete, so a prompt cannot display a known-stale snapshot.
    /// </summary>
    public GitStatus GetGitStatus(string gitRepoPath)
    {
        if (string.IsNullOrWhiteSpace(gitRepoPath))
            return default;

        lock (_statusLock)
        {
            if (_disposed)
                return default;

            if (!string.Equals(_statusRepoPath, gitRepoPath, StringComparison.Ordinal))
            {
                _statusRepoPath = gitRepoPath;
                _status = default;
                _statusValid = false;
                _statusRefreshRequested = true;
                _statusGeneration++;
            }
            else if ((!_statusValid && _statusRefresh is null) ||
                     (_statusValid && _statusRefresh is null && IsStatusRefreshDue()))
            {
                _statusRefreshRequested = true;
            }

            StartStatusRefreshIfNeeded();
            return _statusValid ? _status : default;
        }
    }

    /// <summary>
    /// Marks the current status snapshot as stale and starts (or queues) a background refresh.
    /// This is intentionally synchronous: command completion must not wait for git before the
    /// next prompt can be rendered.
    /// </summary>
    public void InvalidateStatus(string? gitRepoPath = null)
    {
        lock (_statusLock)
        {
            if (_disposed || _statusRepoPath is null)
                return;

            if (gitRepoPath is not null &&
                !string.Equals(_statusRepoPath, gitRepoPath, StringComparison.Ordinal))
            {
                return;
            }

            _status = default;
            _statusValid = false;
            _statusRefreshRequested = true;
            _statusGeneration++;
            StartStatusRefreshIfNeeded();
        }
    }

    /// <summary>
    /// Reads the current working-tree status without using a cache. This remains available for
    /// callers that explicitly need a completed snapshot; prompt rendering uses GetGitStatus.
    /// </summary>
    public static Task<GitStatus> GetGitStatusAsync(string gitRepoPath)
        => ReadGitStatusAsync(gitRepoPath, CancellationToken.None);

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        lock (_statusLock)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            _disposed = true;
            _statusRefreshRequested = false;
            _disposeCts.Cancel();
            _disposeTask = FinishDisposeAsync(_statusRefresh);
            return new ValueTask(_disposeTask);
        }
    }

    private bool IsStatusRefreshDue()
    {
        return _lastStatusRefreshCompleted is 0
            || Stopwatch.GetElapsedTime(_lastStatusRefreshCompleted) >= _statusRefreshInterval;
    }

    private void StartStatusRefreshIfNeeded()
    {
        if (!_statusRefreshRequested || _statusRefresh is { IsCompleted: false } ||
            _statusRepoPath is null || _disposed)
        {
            return;
        }

        _statusRefreshRequested = false;
        var repoPath = _statusRepoPath;
        var generation = _statusGeneration;
        _statusRefresh = Task.Run(
            () => RefreshStatusAsync(repoPath, generation, _disposeCts.Token),
            CancellationToken.None);
    }

    private async Task RefreshStatusAsync(
        string gitRepoPath,
        long generation,
        CancellationToken cancellationToken)
    {
        GitStatus status = default;
        try
        {
            status = await _statusReader(gitRepoPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Status is best effort. A missing git executable or a transient repository error
            // should never affect prompt input or turn into an unobserved task exception.
        }
        finally
        {
            lock (_statusLock)
            {
                if (!_disposed)
                {
                    var isCurrent = generation == _statusGeneration &&
                                    string.Equals(_statusRepoPath, gitRepoPath, StringComparison.Ordinal);
                    if (isCurrent)
                    {
                        _status = status;
                        _statusValid = true;
                        _lastStatusRefreshCompleted = Stopwatch.GetTimestamp();
                    }

                    // If a command or directory change invalidated this read while it was running,
                    // immediately hand off to a new generation. The old result is never published.
                    // Clear the completed task before starting a queued generation. The current
                    // Task reports IsCompleted == false while its finally block is running.
                    _statusRefresh = null;
                    if (_statusRefreshRequested)
                        StartStatusRefreshIfNeeded();
                }
            }
        }
    }

    private async Task FinishDisposeAsync(Task? refreshTask)
    {
        if (refreshTask is not null)
        {
            try
            {
                await refreshTask.ConfigureAwait(false);
            }
            catch
            {
                // Refresh tasks are best effort and are owned by this cache. Observe all failures
                // before disposing the cancellation source.
            }
        }

        _disposeCts.Dispose();
    }

    private static async Task<GitStatus> ReadGitStatusAsync(
        string gitRepoPath,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startInfo = new ProcessStartInfo
            {
                FileName = "git", RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = gitRepoPath,
            };
            startInfo.ArgumentList.Add("status");
            startInfo.ArgumentList.Add("--porcelain=v1");
            startInfo.ArgumentList.Add("--branch");

            process = Process.Start(startInfo);
            if (process is null)
                return default;

            outputTask = process.StandardOutput.ReadToEndAsync();
            errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StopProcess(process);
                try { await process.WaitForExitAsync().ConfigureAwait(false); }
                catch { }
                try { await outputTask.ConfigureAwait(false); }
                catch { }
                try { await errorTask.ConfigureAwait(false); }
                catch { }
                return default;
            }

            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return default;

            return ParseGitStatus(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch
        {
            return default;
        }
        finally
        {
            if (process is not null)
                StopProcess(process);
            process?.Dispose();
        }
    }

    private static GitStatus ParseGitStatus(string output)
    {
        var status = new GitStatus();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("##"))
            {
                var start = line.IndexOf('[');
                var end = start < 0 ? -1 : line.IndexOf(']', start);
                if (end > start)
                {
                    foreach (var part in line[(start + 1)..end].Split(',', StringSplitOptions.TrimEntries))
                    {
                        if (part.StartsWith("ahead ") && int.TryParse(part.AsSpan(6), out var ahead))
                            status = status with { Ahead = ahead };
                        else if (part.StartsWith("behind ") && int.TryParse(part.AsSpan(7), out var behind))
                            status = status with { Behind = behind };
                    }
                }
                continue;
            }

            if (line.Length < 2)
                continue;
            if (line.StartsWith("??"))
                status = status with { HasUntracked = true };
            else
                status = status with
                {
                    HasStaged = status.HasStaged || line[0] is not ' ' and not '?',
                    HasDirty = status.HasDirty || line[1] is not ' ' and not '?'
                };
        }

        return status;
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort. The process is owned by the current status refresh and is disposed
            // immediately after this method returns.
        }
    }

    private static DirectoryInfo? FindGitRepoFolderCore(string path)
    {
        var dir = new DirectoryInfo(path);
        for (var depth = 0; depth < 64 && dir is not null; depth++, dir = dir.Parent)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir;
        }

        return null;
    }
}

internal readonly record struct GitStatus(
    bool HasStaged = false, bool HasDirty = false, bool HasUntracked = false,
    int Ahead = 0, int Behind = 0);
