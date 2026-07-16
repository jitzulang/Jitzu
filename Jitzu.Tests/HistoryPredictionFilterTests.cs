using Jitzu.Shell;
using Shouldly;

namespace Jitzu.Tests;

public class HistoryPredictionFilterTests
{
    [Test]
    public void IsValid_QuotedRelativePath_ResolvesWithoutQuotes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jz_test_{Guid.NewGuid():N}");
        var targetDir = Path.Combine(tempDir, "my folder");
        Directory.CreateDirectory(targetDir);

        try
        {
            HistoryPredictionFilter.IsValid("cd \"my folder\"", tempDir).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void NonCdCommands_AlwaysValid()
    {
        HistoryPredictionFilter.IsValid("ls -la", "/nonexistent").ShouldBeTrue();
        HistoryPredictionFilter.IsValid("git push", "/nonexistent").ShouldBeTrue();
        HistoryPredictionFilter.IsValid("echo hello", "/nonexistent").ShouldBeTrue();
    }

    [Test]
    public void CdWithAbsoluteUnixPath_AlwaysValid()
    {
        HistoryPredictionFilter.IsValid("cd /usr/bin", "/nonexistent").ShouldBeTrue();
        HistoryPredictionFilter.IsValid("cd /", "/nonexistent").ShouldBeTrue();
    }

    [Test]
    public void CdWithAbsoluteWindowsPath_AlwaysValid()
    {
        HistoryPredictionFilter.IsValid("cd C:\\Users", "/nonexistent").ShouldBeTrue();
        HistoryPredictionFilter.IsValid("cd D:/ProgramData", "/nonexistent").ShouldBeTrue();
    }

    [Test]
    public void CdWithTildePath_AlwaysValid()
    {
        HistoryPredictionFilter.IsValid("cd ~/Documents", "/nonexistent").ShouldBeTrue();
        HistoryPredictionFilter.IsValid("cd ~\\Downloads", "/nonexistent").ShouldBeTrue();
        HistoryPredictionFilter.IsValid("cd ~", "/nonexistent").ShouldBeTrue();
    }

    [Test]
    public void CdWithLabelPath_AlwaysValid()
    {
        HistoryPredictionFilter.IsValid("cd git:Jitzu", "/nonexistent").ShouldBeTrue();
        HistoryPredictionFilter.IsValid("cd myLabel:subdir", "/nonexistent").ShouldBeTrue();
    }

    [Test]
    public void CdWithValidRelativePath_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jz_test_{Guid.NewGuid():N}");
        var subDir = Path.Combine(tempDir, "Bar");
        Directory.CreateDirectory(subDir);

        try
        {
            HistoryPredictionFilter.IsValid("cd Bar", tempDir).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void CdWithInvalidRelativePath_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jz_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            HistoryPredictionFilter.IsValid("cd NonExistentDir", tempDir).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void CdWithParentRelativePath_ValidatesCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jz_test_{Guid.NewGuid():N}");
        var childDir = Path.Combine(tempDir, "child");
        Directory.CreateDirectory(childDir);

        try
        {
            // From child, cd .. should resolve to tempDir which exists
            HistoryPredictionFilter.IsValid("cd ..", childDir).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void CdWithEmptyArgument_ReturnsTrue()
    {
        // "cd " with trailing space but no argument — valid (cd to home)
        HistoryPredictionFilter.IsValid("cd ", "/nonexistent").ShouldBeTrue();
    }

    [Test]
    public void CdCaseInsensitive_StillFilters()
    {
        HistoryPredictionFilter.IsValid("CD /usr/bin", "/nonexistent").ShouldBeTrue();
        HistoryPredictionFilter.IsValid("Cd ~/foo", "/nonexistent").ShouldBeTrue();
    }

    [Test]
    public void EmptyPrediction_AlwaysValid()
    {
        HistoryPredictionFilter.IsValid("", "/some/dir").ShouldBeTrue();
    }

    [Test]
    public void CdWithDotPath_ValidatesAgainstCwd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jz_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            HistoryPredictionFilter.IsValid("cd .", tempDir).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void CdWithNestedRelativePath_ValidatesFullChain()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"jz_test_{Guid.NewGuid():N}");
        var nested = Path.Combine(tempDir, "a", "b");
        Directory.CreateDirectory(nested);

        try
        {
            HistoryPredictionFilter.IsValid("cd a/b", tempDir).ShouldBeTrue();
            HistoryPredictionFilter.IsValid("cd a/nonexistent", tempDir).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
