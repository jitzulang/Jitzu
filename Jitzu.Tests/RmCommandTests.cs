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

    [Test]
    public async Task Rm_Interactive_RejectsAFile()
    {
        var file = Path.Combine(_tempDir, "keep.txt");
        await File.WriteAllTextAsync(file, "data");
        var cmd = CreateInteractiveCommand(_ => false);

        var result = await cmd.ExecuteAsync(new[] { "-i", file }.AsMemory());

        result.Type.ShouldBe(ResultType.Error);
        result.Error!.Message.ShouldContain("can only be used with a directory");
        File.Exists(file).ShouldBeTrue();
    }

    [Test]
    public async Task Rm_Interactive_DeletesOnlySelectedFile()
    {
        var directory = Path.Combine(_tempDir, "tree");
        Directory.CreateDirectory(directory);
        var keep = Path.Combine(directory, "keep.txt");
        var remove = Path.Combine(directory, "remove.txt");
        await File.WriteAllTextAsync(keep, "keep");
        await File.WriteAllTextAsync(remove, "remove");
        var cmd = CreateInteractiveCommand(selection =>
        {
            selection.Move(2); // root, keep.txt, remove.txt
            selection.Toggle();
            return true;
        });

        var result = await cmd.ExecuteAsync(new[] { "-i", directory }.AsMemory());

        result.Type.ShouldBe(ResultType.Jitzu);
        File.Exists(keep).ShouldBeTrue();
        File.Exists(remove).ShouldBeFalse();
        Directory.Exists(directory).ShouldBeTrue();
    }

    [Test]
    public async Task Rm_Interactive_SelectingFolderDeletesItsWholeSubtree()
    {
        var directory = Path.Combine(_tempDir, "tree");
        var nested = Path.Combine(directory, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "child.txt"), "data");
        await File.WriteAllTextAsync(Path.Combine(directory, "keep.txt"), "keep");
        var cmd = CreateInteractiveCommand(selection =>
        {
            selection.Move(1); // directories sort before files
            selection.Toggle();
            return true;
        });

        var result = await cmd.ExecuteAsync(new[] { "--interactive", directory }.AsMemory());

        result.Type.ShouldBe(ResultType.Jitzu);
        Directory.Exists(nested).ShouldBeFalse();
        File.Exists(Path.Combine(directory, "keep.txt")).ShouldBeTrue();
    }

    [Test]
    public async Task Rm_Interactive_CancelDoesNotDeleteSelection()
    {
        var directory = Path.Combine(_tempDir, "tree");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "keep.txt");
        await File.WriteAllTextAsync(file, "keep");
        var cmd = CreateInteractiveCommand(selection =>
        {
            selection.Move(1);
            selection.Toggle();
            return false;
        });

        var result = await cmd.ExecuteAsync(new[] { "-i", directory }.AsMemory());

        result.Type.ShouldBe(ResultType.Jitzu);
        File.Exists(file).ShouldBeTrue();
    }

    [Test]
    public async Task Rm_Interactive_NestedDirectoriesStartCollapsedAndCanExpand()
    {
        var directory = Path.Combine(_tempDir, "tree");
        var nested = Path.Combine(directory, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "child.txt"), "data");
        var tree = RmTreeNode.Create(directory);
        var selection = new RmTreeSelection(tree);

        selection.VisibleNodes.Count.ShouldBe(2); // root and collapsed nested directory
        selection.Move(1);
        selection.Current.Name.ShouldBe("nested");
        selection.Expand();
        selection.VisibleNodes.Select(node => node.Name).ShouldContain("child.txt");
        selection.Collapse();
        selection.VisibleNodes.Count.ShouldBe(2);
    }

    private static RmCommand CreateInteractiveCommand(Func<RmTreeSelection, bool> select)
    {
        var context = new CommandContext(new ShellSession(), ThemeConfig.CreateDefault());
        return new RmCommand(context, new FakeInteractiveConsole(select));
    }

    private sealed class FakeInteractiveConsole(Func<RmTreeSelection, bool> select) : IRmInteractiveConsole
    {
        public bool IsInteractive => true;
        public bool Select(RmTreeSelection selection, string displayPath) => select(selection);
    }
}
