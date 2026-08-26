using System.Diagnostics;
using Jitzu.Shell;
using Shouldly;

namespace Jitzu.Tests;

public class GitStatusCacheTests
{
    [Test]
    public void PromptUsesNonBlockingGitStatusCache()
    {
        var programPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Jitzu.Shell", "Program.cs"));
        var source = File.ReadAllText(programPath);

        source.ShouldContain("var status = gitCache.GetGitStatus(gitRepoRoot.FullName);");
        source.ShouldNotContain("GetGitStatusAsync(gitRepoRoot.FullName)");
    }

    [Test]
    public async Task GetGitStatus_ReturnsWithoutWaiting_AndReusesCompletedSnapshot()
    {
        using var repo = new TempGitRepository();
        var reads = 0;
        var started = NewSignal();
        var release = NewSignal<GitStatus>();
        await using var cache = new GitStatusCache(
            TimeSpan.FromMinutes(1),
            (_, cancellationToken) =>
            {
                Interlocked.Increment(ref reads);
                started.TrySetResult();
                return release.Task.WaitAsync(cancellationToken);
            });

        var stopwatch = Stopwatch.StartNew();
        var initial = cache.GetGitStatus(repo.Path);
        stopwatch.Stop();

        initial.ShouldBe(default);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(250));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        reads.ShouldBe(1);

        cache.GetGitStatus(repo.Path).ShouldBe(default);
        release.TrySetResult(new GitStatus(HasUntracked: true));
        var completed = await WaitForStatusAsync(cache, repo.Path, status => status.HasUntracked);
        completed.HasUntracked.ShouldBeTrue();

        for (var i = 0; i < 100; i++)
            cache.GetGitStatus(repo.Path).HasUntracked.ShouldBeTrue();
        reads.ShouldBe(1);
    }

    [Test]
    public async Task InvalidateStatus_HidesStaleSnapshot_AndPublishesReplacement()
    {
        using var repo = new TempGitRepository();
        var reads = 0;
        var secondStarted = NewSignal();
        var secondRelease = NewSignal<GitStatus>();
        await using var cache = new GitStatusCache(
            TimeSpan.FromMinutes(1),
            (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                    return Task.FromResult(new GitStatus(HasDirty: true));

                secondStarted.TrySetResult();
                return secondRelease.Task.WaitAsync(cancellationToken);
            });

        var initial = await WaitForStatusAsync(cache, repo.Path, status => status.HasDirty);
        initial.HasDirty.ShouldBeTrue();

        cache.InvalidateStatus(repo.Path);
        cache.GetGitStatus(repo.Path).ShouldBe(default);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        reads.ShouldBe(2);

        secondRelease.TrySetResult(new GitStatus(HasUntracked: true));
        var replacement = await WaitForStatusAsync(cache, repo.Path, status => status.HasUntracked);
        replacement.HasDirty.ShouldBeFalse();
        replacement.HasUntracked.ShouldBeTrue();
    }

    [Test]
    public async Task InvalidateStatus_DiscardsRefreshThatCompletesAfterCommand()
    {
        using var repo = new TempGitRepository();
        var reads = 0;
        var firstStarted = NewSignal();
        var firstRelease = NewSignal<GitStatus>();
        var secondStarted = NewSignal();
        var secondRelease = NewSignal<GitStatus>();
        await using var cache = new GitStatusCache(
            TimeSpan.FromMinutes(1),
            (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    firstStarted.TrySetResult();
                    return firstRelease.Task.WaitAsync(cancellationToken);
                }

                secondStarted.TrySetResult();
                return secondRelease.Task.WaitAsync(cancellationToken);
            });

        cache.GetGitStatus(repo.Path).ShouldBe(default);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cache.InvalidateStatus(repo.Path);

        // Complete the pre-command read after invalidation. Its result must not leak into the
        // prompt, and completion should hand off to a refresh for the new generation.
        firstRelease.TrySetResult(new GitStatus(HasDirty: true));
        cache.GetGitStatus(repo.Path).ShouldBe(default);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        reads.ShouldBe(2);

        secondRelease.TrySetResult(new GitStatus(HasUntracked: true));
        var replacement = await WaitForStatusAsync(cache, repo.Path, status => status.HasUntracked);
        replacement.HasDirty.ShouldBeFalse();
    }

    [Test]
    public async Task DisposeAsync_CancelsAndAwaitsOwnedRefresh()
    {
        using var repo = new TempGitRepository();
        var started = NewSignal();
        var cancelled = NewSignal();
        await using var cache = new GitStatusCache(
            TimeSpan.FromMinutes(1),
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                }

                return default;
            });

        cache.GetGitStatus(repo.Path).ShouldBe(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cache.GetGitStatus(repo.Path).ShouldBe(default);
    }

    [Test]
    public async Task GetGitStatusAsync_ReflectsChangesOnEveryRead()
    {
        using var repo = new TempGitRepository();

        var initial = await GitStatusCache.GetGitStatusAsync(repo.Path);
        initial.ShouldBe(default);

        await File.WriteAllTextAsync(System.IO.Path.Combine(repo.Path, "untracked.txt"), "new");

        var changed = await GitStatusCache.GetGitStatusAsync(repo.Path);
        changed.HasUntracked.ShouldBeTrue();
    }

    private static async Task<GitStatus> WaitForStatusAsync(
        GitStatusCache cache,
        string repoPath,
        Func<GitStatus, bool> predicate)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var status = cache.GetGitStatus(repoPath);
            if (predicate(status))
                return status;

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for the background git status refresh.");
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TempGitRepository : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"jitzu-git-{Guid.NewGuid():N}");

        public TempGitRepository()
        {
            Directory.CreateDirectory(Path);
            RunGit("init", "--quiet");
        }

        private void RunGit(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start git.");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"git failed: {error}");
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
