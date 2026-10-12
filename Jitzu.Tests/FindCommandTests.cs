using System.Diagnostics;
using Jitzu.Shell;
using Jitzu.Shell.Core;
using Jitzu.Shell.Core.Commands;
using Shouldly;

namespace Jitzu.Tests;

public class FindCommandTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "jitzu_find_" + Guid.NewGuid().ToString("N"));
    private readonly FindCommand _cmd = new(new CommandContext(new ShellSession(), ThemeConfig.CreateDefault()));

    public FindCommandTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string Abs(string relative) => Path.Combine(_tempDir, relative);

    private void CreateFile(string relative, string content = "")
    {
        var path = Abs(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Task<ShellResult> Execute(string[] args) => _cmd.ExecuteAsync(args);

    private async Task<string[]> Search(string pattern = "", params string[] flags)
    {
        var result = await Execute([pattern, _tempDir, .. flags]);
        result.Type.ShouldBe(ResultType.OsCommand, result.Error?.Message);
        return (result.Output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Path.GetRelativePath(_tempDir, line.TrimEnd('\r', '/', '\\')).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
    }

    private async Task<string> Shell(string command)
    {
        using var process = Process.Start(new ProcessStartInfo(ShellTestHarness.GetShellPath())
        {
            WorkingDirectory = _tempDir,
            ArgumentList = { "-c", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        (await stderr).ShouldBeEmpty();
        process.ExitCode.ShouldBe(0);
        return (await stdout).Trim();
    }

    [Test]
    public async Task NoArguments_RecursivelyListsCurrentDirectory()
    {
        CreateFile("top.txt");
        CreateFile("sub/deep.txt");
        var output = await Shell("find");
        output.ShouldContain("top.txt");
        output.ShouldContain(Path.Combine("sub", "deep.txt"));
        output.ShouldNotContain(_tempDir);
    }

    [Test]
    public async Task SingleArgument_IsPatternInCurrentDirectory()
    {
        CreateFile("my-readme.md");
        CreateFile("unrelated.txt");
        (await Shell("find readme")).ShouldBe("my-readme.md");
    }

    [Test]
    public async Task PatternCanAlsoBeAnExistingDirectoryName()
    {
        Directory.CreateDirectory(Abs("src"));
        CreateFile("src/main.cs");
        (await Shell("find src")).ShouldBe("src" + Path.DirectorySeparatorChar);
    }

    [Test]
    public async Task EmptyDirectory_ProducesEmptyOutput() => (await Search()).ShouldBeEmpty();

    [Test]
    public async Task NoMatches_ProducesEmptyOutput()
    {
        CreateFile("other.txt");
        (await Search("missing")).ShouldBeEmpty();
        (await Shell("find missing | wc -l")).ShouldBe("0");
    }

    [Test]
    public async Task SearchesFilesAndDirectoriesRecursively()
    {
        CreateFile("a.txt");
        CreateFile("sub/deep/c.txt");
        (await Search()).ShouldBe(["a.txt", "sub", "sub/deep", "sub/deep/c.txt"]);
    }

    [Test]
    public async Task PlainPattern_IsSubstringWithSmartCase()
    {
        CreateFile("my-README.md");
        CreateFile("readme.txt");
        CreateFile("other.txt");
        (await Search("readme")).ShouldBe(["my-README.md", "readme.txt"]);
        (await Search("README")).ShouldBe(["my-README.md"]);
        (await Search("README", "-i")).ShouldBe(["my-README.md", "readme.txt"]);
        (await Search("readme", "-s")).ShouldBe(["readme.txt"]);
    }

    [Test]
    public async Task RegexPattern_UsesAnchorsAndAlternatives()
    {
        CreateFile("test1.cs");
        CreateFile("test2.rs");
        CreateFile("testx.cs");
        (await Search(@"^test[12]\.(cs|rs)$")).ShouldBe(["test1.cs", "test2.rs"]);
    }

    [Test]
    public async Task GlobPattern_MatchesEntireFilename()
    {
        CreateFile("test.cs");
        CreateFile("test.css");
        CreateFile("nested/next.cs");
        (await Search("*.cs", "-g")).ShouldBe(["nested/next.cs", "test.cs"]);
        (await Search("test", "--glob")).ShouldBeEmpty();
    }

    [Test]
    public async Task GlobPattern_SupportsQuestionAndCharacterClasses()
    {
        CreateFile("a1.txt");
        CreateFile("a2.txt");
        CreateFile("abc.txt");
        (await Search("a?.txt", "-g")).ShouldBe(["a1.txt", "a2.txt"]);
        (await Search("a[12].txt", "-g")).ShouldBe(["a1.txt", "a2.txt"]);
    }

    [Test]
    public async Task GlobPattern_SupportsAlternativesAndEmptyPattern()
    {
        CreateFile("test.cs");
        CreateFile("test.rs");
        CreateFile("test.txt");
        (await Search("*.{cs,rs}", "-g")).ShouldBe(["test.cs", "test.rs"]);
        (await Search("", "-g")).ShouldBe(["test.cs", "test.rs", "test.txt"]);
    }

    [Test]
    public async Task FixedStrings_TreatsRegexCharactersLiterally()
    {
        CreateFile("file(1).txt");
        CreateFile("file1.txt");
        (await Search("file(1).txt", "-F")).ShouldBe(["file(1).txt"]);
    }

    [Test]
    public async Task MatchesFilenameByDefault_AndFullPathOnRequest()
    {
        CreateFile("needle/file.txt");
        (await Search("needle", "-tf")).ShouldBeEmpty();
        (await Search("needle", "-p", "-tf")).ShouldBe(["needle/file.txt"]);
    }

    [Test]
    public async Task TypeAndExtensionFilters_CombineAndRepeat()
    {
        CreateFile("src/main.CS");
        CreateFile("src/lib.rs");
        CreateFile("src/notes.txt");
        Directory.CreateDirectory(Abs("folder.cs"));
        (await Search("", "-tf", "-e", ".cs", "--extension=rs")).ShouldBe(["src/lib.rs", "src/main.CS"]);
        (await Search("", "-e", "cs")).ShouldBe(["folder.cs", "src/main.CS"]);
        (await Search("", "--type", "directory")).ShouldBe(["folder.cs", "src"]);
        (await Search("", "-t", "d", "-t", "f")).Length.ShouldBe(5);
    }

    [Test]
    public async Task HiddenEntriesAndSubtrees_AreSkippedByDefault()
    {
        CreateFile("visible.txt");
        CreateFile(".secret.txt");
        CreateFile(".hidden/nested.txt");
        (await Search()).ShouldBe(["visible.txt"]);
        (await Search("", "-H")).ShouldBe([".hidden", ".hidden/nested.txt", ".secret.txt", "visible.txt"]);
    }

    [Test]
    public async Task GitIgnore_FiltersFilesAndDirectoriesByDefault()
    {
        Directory.CreateDirectory(Abs(".git"));
        CreateFile(".gitignore", "bin/\n*.log\n");
        CreateFile("bin/generated.dll");
        CreateFile("app.log");
        CreateFile("src/main.cs");
        (await Search()).ShouldBe(["src", "src/main.cs"]);
        (await Search("", "-I")).ShouldBe(["app.log", "bin", "bin/generated.dll", "src", "src/main.cs"]);
        (await Search("", "-HI")).ShouldContain(".gitignore");
        (await Search("", "-u")).ShouldContain("bin/generated.dll");
    }

    [Test]
    public async Task HiddenFlag_StillRespectsIgnoreRules()
    {
        Directory.CreateDirectory(Abs(".git"));
        CreateFile(".gitignore", "*.log\n");
        CreateFile(".secret.log");
        CreateFile(".secret.txt");
        (await Search("secret", "-H")).ShouldBe([".secret.txt"]);
    }

    [Test]
    public async Task GitIgnore_RequiresARepository()
    {
        CreateFile(".gitignore", "*.log\n");
        CreateFile("app.log");
        (await Search()).ShouldBe(["app.log"]);
    }

    [Test]
    public async Task IgnoreFiles_WorkOutsideGitRepositories()
    {
        CreateFile(".ignore", "*.log\n");
        CreateFile(".fdignore", "cache/\n");
        CreateFile("app.log");
        CreateFile("cache/blob");
        CreateFile("main.cs");
        (await Search()).ShouldBe(["main.cs"]);
    }

    [Test]
    public async Task NestedIgnoreRulesAndNegations_AreRespected()
    {
        Directory.CreateDirectory(Abs(".git"));
        CreateFile(".gitignore", "*.log\n!keep.log\n/root-only.txt\n");
        CreateFile("drop.log");
        CreateFile("keep.log");
        CreateFile("root-only.txt");
        CreateFile("sub/.gitignore", "!nested.log\nlocal.txt\n");
        CreateFile("sub/nested.log");
        CreateFile("sub/drop.log");
        CreateFile("sub/local.txt");
        CreateFile("sub/root-only.txt");
        (await Search()).ShouldBe(["keep.log", "sub", "sub/nested.log", "sub/root-only.txt"]);
        var result = await Execute(["", Abs("sub")]);
        result.Output!.ShouldContain("nested.log");
        result.Output!.ShouldNotContain("drop.log");
    }

    [Test]
    public async Task DirectoryOnlyIgnore_DoesNotHideFilesWithSameName()
    {
        CreateFile(".ignore", "cache/\n");
        CreateFile("cache");
        CreateFile("sub/cache/generated.txt");
        (await Search()).ShouldBe(["cache", "sub"]);
    }

    [Test]
    public async Task GenericIgnore_TakesPrecedenceOverNestedGitNegation()
    {
        Directory.CreateDirectory(Abs(".git"));
        CreateFile(".ignore", "*.log\n");
        CreateFile("sub/.gitignore", "!keep.log\n");
        CreateFile("sub/keep.log");
        (await Search("", "-tf")).ShouldBeEmpty();
    }

    [Test]
    public async Task GitRepositoryExclude_IsRespected()
    {
        CreateFile(".git/info/exclude", "local.txt\n");
        CreateFile("local.txt");
        CreateFile("visible.txt");
        (await Search()).ShouldBe(["visible.txt"]);
    }

    [Test]
    public async Task Excludes_PruneDirectoriesAndMatchFiles()
    {
        CreateFile("cache/deep.txt");
        CreateFile("src/keep.cs");
        CreateFile("src/backup.bak");
        (await Search("", "-E", "cache", "--exclude", "*.bak", "-I")).ShouldBe(["src", "src/keep.cs"]);
    }

    [Test]
    public async Task MaxDepth_LimitsTraversal()
    {
        CreateFile("top.txt");
        CreateFile("sub/deep.txt");
        (await Search("", "-d1")).ShouldBe(["sub", "top.txt"]);
        (await Search("", "--max-depth=0")).ShouldBeEmpty();
    }

    [Test]
    public async Task MultipleRoots_AreSearched()
    {
        CreateFile("a/target.cs");
        CreateFile("b/target.rs");
        CreateFile("c/target.txt");
        var result = await Execute(["target", Abs("a"), Abs("b")]);
        result.Type.ShouldBe(ResultType.OsCommand);
        result.Output!.ShouldContain("target.cs");
        result.Output!.ShouldContain("target.rs");
        result.Output!.ShouldNotContain("target.txt");
    }

    [Test]
    public async Task DashPattern_CanFollowEndOfOptions()
    {
        CreateFile("-target.txt");
        CreateFile("other.txt");
        var result = await Execute(["--", "-target", _tempDir]);
        result.Output!.ShouldBe(Abs("-target.txt"));
    }

    [Test]
    public async Task AbsolutePathFlag_AndAbsoluteRoot_ProduceAbsolutePaths()
    {
        CreateFile("file.txt");
        (await Shell("find file -a")).ShouldBe(Abs("file.txt"));
        var result = await Execute(["file", _tempDir]);
        result.Output!.ShouldBe(Abs("file.txt"));
    }

    [Test]
    public async Task DoesNotTruncateAtOneThousandResults()
    {
        for (var i = 0; i < 1005; i++) CreateFile($"file{i}.txt");
        (await Search("", "-tf")).Length.ShouldBe(1005);
    }

    [Test]
    public async Task SymlinkDirectories_AreListedWithoutFollowingCycles()
    {
        if (OperatingSystem.IsWindows()) return;
        CreateFile("real/file.txt");
        Directory.CreateSymbolicLink(Abs("real/loop"), _tempDir);
        File.CreateSymbolicLink(Abs("alias.txt"), Abs("real/file.txt"));
        (await Search("", "-tl")).ShouldBe(["alias.txt", "real/loop"]);
        (await Search("", "-tf")).ShouldBe(["real/file.txt"]);
    }

    [Test]
    [Arguments("-e")]
    [Arguments("-t")]
    [Arguments("-d")]
    [Arguments("-E")]
    [Arguments("--unknown")]
    [Arguments("--type=invalid")]
    [Arguments("--max-depth=-1")]
    [Arguments("--hidden=yes")]
    [Arguments("--extension=")]
    public async Task InvalidOptions_ReturnErrors(string option)
    {
        var result = await Execute(["", _tempDir, option]);
        result.Type.ShouldBe(ResultType.Error);
        result.Error.ShouldNotBeNull();
    }

    [Test]
    public async Task InvalidRegex_ReturnsError()
    {
        var result = await Execute(["[", _tempDir]);
        result.Type.ShouldBe(ResultType.Error);
    }

    [Test]
    public async Task NonexistentRoot_ReturnsError()
    {
        var result = await Execute(["", Abs("missing")]);
        result.Type.ShouldBe(ResultType.Error);
        result.Error!.Message.ShouldContain("No such directory");
    }

    [Test]
    public async Task Help_DescribesNewSyntax()
    {
        var result = await Execute(["--help"]);
        result.Type.ShouldBe(ResultType.OsCommand);
        result.Output!.ShouldContain("[pattern] [path...]");
        result.Output!.ShouldContain("--hidden");
    }

    [Test]
    public async Task QuotedGlobAndRegex_WorkThroughShellPipelines()
    {
        CreateFile("src/one.cs");
        CreateFile("src/two.cs");
        CreateFile("root.cs");
        CreateFile("src/other.txt");
        (await Shell("find -g '*.cs' . | wc -l")).ShouldBe("3");
        (await Shell("find 'one|two' src -tf | wc -l")).ShouldBe("2");
    }
}
