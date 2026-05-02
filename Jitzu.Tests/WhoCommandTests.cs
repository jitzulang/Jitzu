using System.Diagnostics;
using Jitzu.Shell;
using Jitzu.Shell.Core;
using Jitzu.Shell.Core.Commands;
using Shouldly;

namespace Jitzu.Tests;

public class WhoCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WhoCommand _cmd;

    public WhoCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jitzu_who_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var theme = ThemeConfig.CreateDefault();
        var context = new CommandContext(new ShellSession(), theme);
        _cmd = new WhoCommand(context);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Test]
    public async Task Who_NoArgs_ReturnsError()
    {
        var result = await _cmd.ExecuteAsync(ReadOnlyMemory<string>.Empty);
        result.Type.ShouldBe(ResultType.Error);
    }

    [Test]
    public async Task Who_OwnPid_DescribesProcess()
    {
        var pid = Environment.ProcessId;
        var result = await _cmd.ExecuteAsync(new[] { pid.ToString() }.AsMemory());

        result.Type.ShouldBe(ResultType.OsCommand);
        result.Output.ShouldContain(pid.ToString());
        result.Output.ShouldContain("PID");
        result.Output.ShouldContain("Name");
    }

    [Test]
    public async Task Who_NonexistentPid_ReturnsError()
    {
        var result = await _cmd.ExecuteAsync(new[] { "999999999" }.AsMemory());
        result.Type.ShouldBe(ResultType.Error);
    }

    [Test]
    public async Task Who_NonexistentPath_ReturnsError()
    {
        var result = await _cmd.ExecuteAsync(new[] { Path.Combine(_tempDir, "nope.txt") }.AsMemory());
        result.Type.ShouldBe(ResultType.Error);
    }

    [Test]
    public async Task Who_UnlockedFile_ReportsNoHolders()
    {
        var file = Path.Combine(_tempDir, "free.txt");
        await File.WriteAllTextAsync(file, "hello");

        var result = await _cmd.ExecuteAsync(new[] { file }.AsMemory());

        result.Type.ShouldBe(ResultType.OsCommand);
        result.Output.ShouldNotBeNull();
    }

    [Test]
    public async Task Who_LockedFile_ReportsHolder()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            return;

        var file = Path.Combine(_tempDir, "locked.txt");
        await File.WriteAllTextAsync(file, "data");

        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await _cmd.ExecuteAsync(new[] { file }.AsMemory());

        result.Type.ShouldBe(ResultType.OsCommand);
        result.Output.ShouldContain(Environment.ProcessId.ToString());
    }
}
