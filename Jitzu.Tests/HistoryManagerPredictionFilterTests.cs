using Jitzu.Shell;
using Shouldly;

namespace Jitzu.Tests;

public class HistoryManagerPredictionFilterTests
{
    private static async Task<HistoryManager> CreateWithHistory(params string[] commands)
    {
        var manager = new HistoryManager(persist: false);
        await manager.InitialiseAsync();
        foreach (var cmd in commands)
            await manager.WriteAsync(cmd);
        return manager;
    }

    [Test]
    public async Task GetPredictions_WithoutFilter_ReturnsAllMatches()
    {
        var manager = await CreateWithHistory("cd Foo", "cd Bar", "cd Baz");

        var predictions = manager.GetPredictions("cd ", 5);

        predictions.Count.ShouldBe(3);
    }

    [Test]
    public async Task GetPredictions_WithFilter_ExcludesRejected()
    {
        var manager = await CreateWithHistory("cd Foo", "cd Bar", "cd Baz");

        var predictions = manager.GetPredictions("cd ", 5, p => p != "cd Bar");

        predictions.Count.ShouldBe(2);
        predictions.ShouldNotContain("cd Bar");
    }

    [Test]
    public async Task GetPredictions_FilterRejectsAll_ReturnsEmpty()
    {
        var manager = await CreateWithHistory("cd Foo", "cd Bar");

        var predictions = manager.GetPredictions("cd ", 5, _ => false);

        predictions.ShouldBeEmpty();
    }

    [Test]
    public async Task GetPredictions_NullFilter_BehavesLikeNoFilter()
    {
        var manager = await CreateWithHistory("cd Foo", "cd Bar");

        var withNull = manager.GetPredictions("cd ", 5, null);
        var without = manager.GetPredictions("cd ", 5);

        withNull.Count.ShouldBe(without.Count);
    }

    [Test]
    public async Task GetPredictions_FilterWithMaxCount_RespectsLimit()
    {
        var manager = await CreateWithHistory("cd A", "cd B", "cd C", "cd D");

        // Filter passes all, but maxCount is 2
        var predictions = manager.GetPredictions("cd ", 2, _ => true);

        predictions.Count.ShouldBe(2);
    }

    [Test]
    public async Task GetPredictions_FilterWithMaxCount_CountsOnlyPassingItems()
    {
        var manager = await CreateWithHistory("cd A", "cd B", "cd C", "cd D");

        // Filter rejects "cd B" and "cd D", maxCount is 3
        var predictions = manager.GetPredictions("cd ", 3, p => p is not "cd B" and not "cd D");

        predictions.Count.ShouldBe(2);
        predictions.ShouldContain("cd C");
        predictions.ShouldContain("cd A");
    }

    [Test]
    public async Task GetPredictions_EmptyPrefix_ReturnsEmpty()
    {
        var manager = await CreateWithHistory("cd Foo");

        var predictions = manager.GetPredictions("", 5, _ => true);

        predictions.ShouldBeEmpty();
    }

    [Test]
    public async Task GetPredictions_NonCdCommands_NotAffectedByFilter()
    {
        var manager = await CreateWithHistory("ls -la", "ls -R");

        // Filter that rejects everything — but non-cd commands should still use it
        // The filter applies to ALL predictions, not just cd
        var predictions = manager.GetPredictions("ls ", 5, _ => false);

        predictions.ShouldBeEmpty();
    }

    [Test]
    public async Task GetPredictions_IntegrationWithHistoryPredictionFilter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jz_test_{Guid.NewGuid():N}");
        var subDir = Path.Combine(tempDir, "ValidDir");
        Directory.CreateDirectory(subDir);

        try
        {
            var manager = await CreateWithHistory("cd ValidDir", "cd GoneDir", "cd /absolute");

            var predictions = manager.GetPredictions("cd ", 5,
                p => HistoryPredictionFilter.IsValid(p, tempDir));

            predictions.ShouldContain("cd ValidDir");
            predictions.ShouldContain("cd /absolute");
            predictions.ShouldNotContain("cd GoneDir");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task GetPredictions_CdQueryFindsAbsoluteHistoryPathAndMakesItRelative()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jz_test_{Guid.NewGuid():N}");
        var currentDir = Path.Combine(tempDir, "candc");
        var targetDir = Path.Combine(tempDir, "personal", "Languages", "Jitzu");
        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            var manager = await CreateWithHistory($"cd {targetDir}");

            var predictions = manager.GetPredictions("cd Jitzu", 5,
                p => HistoryPredictionFilter.IsValid(p, currentDir), currentDir);

            predictions.ShouldContain($"cd {Path.GetRelativePath(currentDir, targetDir)}");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task GetPredictions_CdPathSearchIsCaseInsensitive()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jz_test_{Guid.NewGuid():N}");
        var currentDir = Path.Combine(tempDir, "current");
        var targetDir = Path.Combine(tempDir, "Jitzu");
        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            var manager = await CreateWithHistory($"cd {targetDir}");

            var predictions = manager.GetPredictions("cd jitzu", 5, workingDirectory: currentDir);

            predictions.ShouldContain($"cd {Path.GetRelativePath(currentDir, targetDir)}");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task GetPredictions_CdQueryResolvesLabelHistoryPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jz_test_{Guid.NewGuid():N}");
        var currentDir = Path.Combine(tempDir, "current");
        var targetDir = Path.Combine(tempDir, "personal", "Languages", "Jitzu");
        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            var manager = await CreateWithHistory("cd git:personal\\Languages\\Jitzu\\");

            var predictions = manager.GetPredictions("cd Jitzu", 5, workingDirectory: currentDir,
                pathResolver: _ => targetDir);

            predictions.ShouldContain($"cd {Path.GetRelativePath(currentDir, targetDir)}");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task GetPredictions_DeduplicatesCdCommandsByResolvedTarget()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jz_test_{Guid.NewGuid():N}");
        var currentDir = Path.Combine(tempDir, "current");
        var targetDir = Path.Combine(tempDir, "git");
        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            var relativePath = Path.GetRelativePath(currentDir, targetDir);
            var manager = await CreateWithHistory(
                $"cd {targetDir}",
                $"cd {relativePath}",
                "cd git:"
            );

            string Resolve(string path) => path == "git:"
                ? targetDir
                : Path.GetFullPath(path, currentDir);

            var predictions = manager.GetPredictions("cd ", 5, workingDirectory: currentDir,
                pathResolver: Resolve);

            predictions.Count.ShouldBe(1);
            predictions[0].ShouldBe("cd git:");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
