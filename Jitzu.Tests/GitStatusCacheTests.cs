using System.Diagnostics;
using Jitzu.Shell;
using Shouldly;

namespace Jitzu.Tests;

public class GitStatusCacheTests
{
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
