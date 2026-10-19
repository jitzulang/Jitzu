using Jitzu.Shell.Core.Completions;
using Jitzu.Shell.UI;
using Shouldly;

namespace Jitzu.Tests;

public class ReadLineCompletionTests
{
    [Test]
    public void MissingFileCompletion_ReturnsTrue()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"jitzu-missing-{Guid.NewGuid():N}");

        ReadLine.IsMissingPathCompletion(new FileCompletion(missingPath)).ShouldBeTrue();
    }

    [Test]
    public void ExistingDirectoryCompletion_ReturnsFalse()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"jitzu-completion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            ReadLine.IsMissingPathCompletion(
                new DirectoryCompletion($"{directory}{Path.DirectorySeparatorChar}")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Test]
    public void PathCompletion_UsesShellPathResolver()
    {
        ReadLine.IsMissingPathCompletion(
            new DirectoryCompletion("label:folder"),
            _ => Path.GetTempPath()).ShouldBeFalse();
    }

    [Test]
    public void NonPathCompletion_DoesNotShowWarning()
    {
        ReadLine.IsMissingPathCompletion(new ExecutableCompletion("missing-command")).ShouldBeFalse();
        ReadLine.IsMissingPathCompletion(new KeywordCompletion("missing-keyword")).ShouldBeFalse();
    }
}
