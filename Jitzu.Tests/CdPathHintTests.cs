using Jitzu.Shell;
using Shouldly;

namespace Jitzu.Tests;

public class CdPathHintTests
{
    [Test]
    public void TryGetCdArgument_ReturnsRelativePath()
    {
        CdPathHint.TryGetCdArgument("cd ../..", out var argument).ShouldBeTrue();

        argument.ShouldBe("../..");
    }

    [Test]
    public void TryGetCdArgument_ReturnsQuotedPath()
    {
        CdPathHint.TryGetCdArgument("cd \"../my folder\"", out var argument).ShouldBeTrue();

        argument.ShouldBe("../my folder");
    }

    [Test]
    public void TryGetCdArgument_RejectsCompoundCommand()
    {
        CdPathHint.TryGetCdArgument("cd .. && ls", out _).ShouldBeFalse();
    }

    [Test]
    public void TryGetCdArgument_RejectsPreviousDirectory()
    {
        CdPathHint.TryGetCdArgument("cd -", out _).ShouldBeFalse();
    }

    [Test]
    public void GetHint_ReturnsCompactResolvedDestinationForRelativePath()
    {
        using var temp = new TempDirectory();
        var nested = Path.Combine(temp.Path, "one", "two", "three");
        Directory.CreateDirectory(nested);

        var hint = CdPathHint.GetHint("cd ../..", null, nested, maxPathLength: 24);

        hint.ShouldNotBeNull();
        hint.ShouldStartWith(CdPathHint.Prefix);
        hint.ShouldContain("one");
        hint.Length.ShouldBeLessThanOrEqualTo(CdPathHint.Prefix.Length + 24);
    }

    [Test]
    public void GetHint_ReturnsHomeForCdWithoutArgument()
    {
        using var temp = new TempDirectory();

        var hint = CdPathHint.GetHint("cd", null, temp.Path);

        hint.ShouldNotBeNull();
        hint.ShouldBe(CdPathHint.Prefix + CdPathHint.CompactPath(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 48));
    }

    [Test]
    public void GetHint_ExpandsLabelPrefixedPath()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(target);
        var labels = new LabelManager();
        labels.Set("git", temp.Path);

        var hint = CdPathHint.GetHint("cd git:repo", labels, Directory.GetCurrentDirectory());

        hint.ShouldBe(CdPathHint.Prefix + CdPathHint.CompactPath(target, 48));
    }

    [Test]
    public void GetHint_ReturnsNullForMissingDirectory()
    {
        using var temp = new TempDirectory();

        CdPathHint.GetHint("cd missing", null, temp.Path).ShouldBeNull();
    }

    [Test]
    public void CompactPath_ReturnsShortPathUnchanged()
    {
        CdPathHint.CompactPath(Path.Combine("a", "b"), 20).ShouldBe(Path.Combine("a", "b"));
    }

    [Test]
    public void CompactPath_UsesEllipsisForLongPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "alpha", "bravo", "charlie", "delta");

        var compact = CdPathHint.CompactPath(path, 24);

        compact.ShouldContain("...");
        compact.ShouldEndWith("delta");
        compact.Length.ShouldBeLessThanOrEqualTo(24);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jitzu-{Guid.NewGuid():N}");

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
