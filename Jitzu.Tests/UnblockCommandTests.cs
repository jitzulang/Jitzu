using Jitzu.Shell;
using Jitzu.Shell.Core;
using Jitzu.Shell.Core.Commands;
using Shouldly;

namespace Jitzu.Tests;

public class UnblockCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UnblockCommand _cmd;

    public UnblockCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jitzu_unblock_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var context = new CommandContext(new ShellSession(), ThemeConfig.CreateDefault());
        _cmd = new UnblockCommand(context);
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_tempDir))
                return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(_tempDir, "*", SearchOption.AllDirectories))
                ClearAllAttributes(entry);

            ClearAllAttributes(_tempDir);
            Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Test]
    public async Task Unblock_NoArgs_ReturnsError()
    {
        var result = await _cmd.ExecuteAsync(ReadOnlyMemory<string>.Empty);

        result.Type.ShouldBe(ResultType.Error);
    }

    [Test]
    public async Task Unblock_File_ClearsReadOnlyAttribute()
    {
        var file = Path.Combine(_tempDir, "blocked.txt");
        await File.WriteAllTextAsync(file, "data");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        var result = await _cmd.ExecuteAsync(new[] { file }.AsMemory());

        result.Type.ShouldBe(ResultType.OsCommand);
        result.Output!.ShouldContain("Unblocked 1 of 1 entry");
        File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly).ShouldBeFalse();
    }

    [Test]
    public async Task Unblock_Directory_ClearsNestedDeleteBlockingAttributes()
    {
        var nested = Path.Combine(_tempDir, ".git", "objects");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "object");
        await File.WriteAllTextAsync(file, "data");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        if (OperatingSystem.IsWindows())
            File.SetAttributes(nested, File.GetAttributes(nested) | FileAttributes.System);

        var result = await _cmd.ExecuteAsync(new[] { _tempDir }.AsMemory());

        result.Type.ShouldBe(ResultType.OsCommand);
        result.Output!.ShouldContain(OperatingSystem.IsWindows()
            ? "Unblocked 2"
            : "Unblocked 1");
        File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly).ShouldBeFalse();
        File.GetAttributes(nested).HasFlag(FileAttributes.System).ShouldBeFalse();
    }

    [Test]
    public async Task Unblock_MissingPath_ReturnsError()
    {
        var missing = Path.Combine(_tempDir, "missing");

        var result = await _cmd.ExecuteAsync(new[] { missing }.AsMemory());

        result.Type.ShouldBe(ResultType.Error);
        result.Error!.Message.ShouldContain("No such file or directory");
    }

    [Test]
    public async Task Unblock_ClearPath_ReportsNoDeleteBlockingAttributes()
    {
        var file = Path.Combine(_tempDir, "clear.txt");
        await File.WriteAllTextAsync(file, "data");

        var result = await _cmd.ExecuteAsync(new[] { file }.AsMemory());

        result.Type.ShouldBe(ResultType.OsCommand);
        result.Output!.ShouldContain("No delete-blocking attributes found in 1 entry");
    }

    [Test]
    public async Task Unblock_AttributesOnly_ReportsHolderWithoutTerminatingIt()
    {
        var file = Path.Combine(_tempDir, "locked.txt");
        await File.WriteAllTextAsync(file, "data");
        var inspector = new StubFileLockInspector(file, 4242, "agent-brain");
        var terminated = new List<int>();
        var context = new CommandContext(new ShellSession(), ThemeConfig.CreateDefault());
        var cmd = new UnblockCommand(context, inspector, terminated.Add);

        var result = await cmd.ExecuteAsync(new[] { "--attributes-only", file }.AsMemory());

        result.Type.ShouldBe(ResultType.OsCommand);
        result.Output!.ShouldContain("Process locks remain");
        result.Output!.ShouldContain("4242");
        result.Output!.ShouldContain("agent-brain");
        result.Output!.ShouldContain("without --attributes-only");
        terminated.ShouldBeEmpty();
    }

    [Test]
    public async Task Unblock_TerminatesEachLockingProcessOnceByDefault()
    {
        var nested = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "locked.txt");
        await File.WriteAllTextAsync(file, "data");
        var inspector = new StubFileLockInspector(file, 4242, "agent-brain", duplicateHolder: true);
        var terminated = new List<int>();
        var context = new CommandContext(new ShellSession(), ThemeConfig.CreateDefault());
        var cmd = new UnblockCommand(context, inspector, terminated.Add);

        var result = await cmd.ExecuteAsync(new[] { _tempDir }.AsMemory());

        result.Type.ShouldBe(ResultType.OsCommand);
        result.Output!.ShouldContain("Terminated 1 locking process");
        result.Output!.ShouldContain("agent-brain");
        terminated.ShouldBe([4242]);
        inspector.Paths.ShouldContain(_tempDir);
        inspector.Paths.ShouldContain(nested);
        inspector.Paths.ShouldContain(file);
    }

    [Test]
    public async Task Unblock_KillOption_IsRejectedWithoutTakingAction()
    {
        var file = Path.Combine(_tempDir, "locked.txt");
        await File.WriteAllTextAsync(file, "data");
        var inspector = new StubFileLockInspector(file, 4242, "agent-brain");
        var terminated = new List<int>();
        var context = new CommandContext(new ShellSession(), ThemeConfig.CreateDefault());
        var cmd = new UnblockCommand(context, inspector, terminated.Add);

        var result = await cmd.ExecuteAsync(new[] { "--kill", file }.AsMemory());

        result.Type.ShouldBe(ResultType.Error);
        result.Error!.Message.ShouldContain("unknown option '--kill'");
        inspector.Paths.ShouldBeEmpty();
        terminated.ShouldBeEmpty();
    }

    private static void ClearAllAttributes(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) &
                ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
        }
        catch { }
    }

    private sealed class StubFileLockInspector(
        string lockedPath,
        int pid,
        string name,
        bool duplicateHolder = false) : IFileLockInspector
    {
        public IReadOnlyList<string> Paths { get; private set; } = [];

        public Task<List<(int Pid, string Name)>> GetProcessesLockingFileAsync(string path) =>
            Task.FromResult(new List<(int Pid, string Name)>());

        public Task<List<(string Path, List<(int Pid, string Name)> Holders)>> FindLockedPathsAsync(
            IReadOnlyList<string> paths)
        {
            Paths = paths;
            var holders = new List<(int Pid, string Name)> { (pid, name) };
            if (duplicateHolder)
                holders.Add((pid, name));

            return Task.FromResult(new List<(string Path, List<(int Pid, string Name)> Holders)>
            {
                (lockedPath, holders)
            });
        }
    }
}
