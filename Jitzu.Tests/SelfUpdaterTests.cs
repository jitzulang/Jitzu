using Jitzu.Shell.Infrastructure.Update;
using Shouldly;

namespace Jitzu.Tests;

public sealed class SelfUpdaterTests : IDisposable
{
    private readonly string _tempDir;

    public SelfUpdaterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jitzu_update_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Test]
    public async Task GetAvailableOldPath_ReturnsOldWhenAvailable()
    {
        var currentPath = Path.Combine(_tempDir, "jz.exe");
        await File.WriteAllTextAsync(currentPath, "");

        var oldPath = SelfUpdater.GetAvailableOldPath(currentPath);

        oldPath.ShouldBe(currentPath + ".old");
    }

    [Test]
    public async Task GetAvailableOldPath_UsesNumberedSuffixWhenOldExists()
    {
        var currentPath = Path.Combine(_tempDir, "jz.exe");
        await File.WriteAllTextAsync(currentPath, "");
        await File.WriteAllTextAsync(currentPath + ".old", "");
        await File.WriteAllTextAsync(currentPath + ".old.2", "");

        var oldPath = SelfUpdater.GetAvailableOldPath(currentPath);

        oldPath.ShouldBe(currentPath + ".old.3");
    }

    [Test]
    public async Task GetOldPathsToClean_ReturnsExistingOldPaths()
    {
        var currentPath = Path.Combine(_tempDir, "jz.exe");
        await File.WriteAllTextAsync(currentPath, "");
        await File.WriteAllTextAsync(currentPath + ".old", "");
        await File.WriteAllTextAsync(currentPath + ".old.2", "");
        await File.WriteAllTextAsync(currentPath + ".old.3", "");

        var oldPaths = SelfUpdater.GetOldPathsToClean(currentPath).ToArray();

        oldPaths.ShouldBe([
            currentPath + ".old",
            currentPath + ".old.2",
            currentPath + ".old.3"
        ]);
    }

    [Test]
    public void GetOldPathsToClean_ReturnsEmptyWhenOldPathIsMissing()
    {
        var currentPath = Path.Combine(_tempDir, "jz.exe");

        var oldPaths = SelfUpdater.GetOldPathsToClean(currentPath).ToArray();

        oldPaths.ShouldBeEmpty();
    }

    [Test]
    public async Task GetOldPathsToClean_FindsNumberedOrphansAcrossGaps()
    {
        var currentPath = Path.Combine(_tempDir, "jz.exe");
        await File.WriteAllTextAsync(currentPath + ".old.3", "old-three");
        await File.WriteAllTextAsync(currentPath + ".old.8", "old-eight");
        await File.WriteAllTextAsync(currentPath + ".old.invalid", "unrelated");

        SelfUpdater.GetOldPathsToClean(currentPath).ToArray().ShouldBe([
            currentPath + ".old.3",
            currentPath + ".old.8"
        ]);
    }

    [Test]
    public async Task ReplaceWindowsBinary_RollsBackWhenInstallMoveFails()
    {
        var currentPath = Path.Combine(_tempDir, "jz.exe");
        var newPath = Path.Combine(_tempDir, "download.exe");
        await File.WriteAllTextAsync(currentPath, "original");
        await File.WriteAllTextAsync(newPath, "replacement");

        Should.Throw<InvalidOperationException>(() =>
            SelfUpdater.ReplaceWindowsBinary(currentPath, newPath,
                () => throw new InvalidOperationException("injected install failure")));

        (await File.ReadAllTextAsync(currentPath)).ShouldBe("original");
        Directory.GetFiles(_tempDir, "jz.exe.old*").ShouldBeEmpty();
        Directory.GetFiles(_tempDir, ".jz-update-*.tmp").ShouldBeEmpty();
    }

    [Test]
    public async Task ReplaceWindowsBinary_RetainsOldCopyWhenRollbackFails()
    {
        var currentPath = Path.Combine(_tempDir, "jz.exe");
        var newPath = Path.Combine(_tempDir, "download.exe");
        await File.WriteAllTextAsync(currentPath, "original");
        await File.WriteAllTextAsync(newPath, "replacement");

        var error = Should.Throw<AggregateException>(() =>
            SelfUpdater.ReplaceWindowsBinary(currentPath, newPath,
                afterCurrentMoved: () => throw new InvalidOperationException("injected install failure"),
                beforeRollback: () => File.WriteAllText(currentPath, "rollback blocker")));

        error.InnerExceptions.Count.ShouldBe(2);
        (await File.ReadAllTextAsync(currentPath)).ShouldBe("rollback blocker");
        var oldPath = Directory.GetFiles(_tempDir, "jz.exe.old*").ShouldHaveSingleItem();
        (await File.ReadAllTextAsync(oldPath)).ShouldBe("original");
        Directory.GetFiles(_tempDir, ".jz-update-*.tmp").ShouldBeEmpty();
    }

    [Test]
    public async Task CleanupOldBinaries_RemovesAllRecognizedOrphans()
    {
        var currentPath = Path.Combine(_tempDir, "jz.exe");
        await File.WriteAllTextAsync(currentPath + ".old", "old");
        await File.WriteAllTextAsync(currentPath + ".old.4", "old-four");
        await File.WriteAllTextAsync(currentPath + ".old.keep", "unrelated");

        SelfUpdater.CleanupOldBinaries(currentPath);

        File.Exists(currentPath + ".old").ShouldBeFalse();
        File.Exists(currentPath + ".old.4").ShouldBeFalse();
        File.Exists(currentPath + ".old.keep").ShouldBeTrue();
    }
}
