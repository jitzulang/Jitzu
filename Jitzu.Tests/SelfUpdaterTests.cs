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
}
