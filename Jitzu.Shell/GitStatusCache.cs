namespace Jitzu.Shell;

/// <summary>
/// Caches git repository information between prompt renders.
/// Repo root is cached until the working directory changes.
/// Branch is cached until .git/HEAD's modification time changes.
/// Working-tree status refreshes in the background so prompt rendering never waits on git.
/// </summary>
internal class GitStatusCache
{
    private string? _cachedDirectory;
    private DirectoryInfo? _cachedRepoRoot;
    private bool _repoRootResolved;

    private string? _cachedHeadPath;
    private DateTime _cachedHeadWriteTime;
    private string? _cachedBranch;
    private readonly object _statusLock = new();
    private string? _statusRepoPath;
    private GitStatus _status;
    private Task? _statusRefresh;

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

    public GitStatus GetGitStatus(string gitRepoPath)
    {
        lock (_statusLock)
        {
            if (!string.Equals(_statusRepoPath, gitRepoPath, StringComparison.Ordinal))
            {
                _statusRepoPath = gitRepoPath;
                _status = default;
                _statusRefresh = null;
            }

            if (_statusRefresh is null || _statusRefresh.IsCompleted)
                _statusRefresh = Task.Run(() => RefreshStatusAsync(gitRepoPath));

            return _status;
        }
    }

    private async Task RefreshStatusAsync(string gitRepoPath)
    {
        var status = await ReadGitStatusAsync(gitRepoPath).ConfigureAwait(false);
        lock (_statusLock)
        {
            if (string.Equals(_statusRepoPath, gitRepoPath, StringComparison.Ordinal))
                _status = status;
        }
    }

    private static async Task<GitStatus> ReadGitStatusAsync(string gitRepoPath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git", RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = gitRepoPath,
            };
            startInfo.ArgumentList.Add("status");
            startInfo.ArgumentList.Add("--porcelain=v1");
            startInfo.ArgumentList.Add("--branch");

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null) return default;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0) return default;

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

                if (line.Length < 2) continue;
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
        catch
        {
            return default;
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
