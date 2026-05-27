using Jitzu.Shell;
using Jitzu.Shell.Core;
using Jitzu.Shell.Core.Commands;
using Shouldly;

namespace Jitzu.Tests;

public class RmCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RmCommand _cmd;

    public RmCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jitzu_rm_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var context = new CommandContext(new ShellSession(), ThemeConfig.CreateDefault());
        _cmd = new RmCommand(context);
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_tempDir))
                return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(_tempDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
            Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Test]
    public async Task Rm_RecursiveForce_RemovesReadOnlyNestedFile()
    {
        var directory = Path.Combine(_tempDir, "repo");
        var nested = Path.Combine(directory, ".git", "objects");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "6b78e8ecafdac572119ccfab36ceb330f92190");
        await File.WriteAllTextAsync(file, "data");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        var result = await _cmd.ExecuteAsync(new[] { "-rf", directory }.AsMemory());

        result.Type.ShouldBe(ResultType.Jitzu);
        Directory.Exists(directory).ShouldBeFalse();
    }

    [Test]
    public async Task Rm_Force_IgnoresMissingPath()
    {
        var missing = Path.Combine(_tempDir, "missing");

        var result = await _cmd.ExecuteAsync(new[] { "-f", missing }.AsMemory());

        result.Type.ShouldBe(ResultType.Jitzu);
    }

    [Test]
    public async Task Rm_CombinedFlags_AcceptsFr()
    {
        var directory = Path.Combine(_tempDir, "repo");
        var nested = Path.Combine(directory, ".git", "objects");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "readonly");
        await File.WriteAllTextAsync(file, "data");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        var result = await _cmd.ExecuteAsync(new[] { "-fr", directory }.AsMemory());

        result.Type.ShouldBe(ResultType.Jitzu);
        Directory.Exists(directory).ShouldBeFalse();
    }
}
