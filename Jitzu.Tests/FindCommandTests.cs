using Jitzu.Shell;
using Jitzu.Shell.Core;
using Jitzu.Shell.Core.Commands;
using Shouldly;

namespace Jitzu.Tests;

public class FindCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FindCommand _cmd;

    public FindCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jitzu_find_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var theme = ThemeConfig.CreateDefault();
        var context = new CommandContext(new ShellSession(), theme);
        _cmd = new FindCommand(context, () => null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private void CreateFile(string relativePath)
    {
        var full = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
    }

    private void CreateDir(string relativePath)
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, relativePath));
    }

    private string Abs(string relative) => Path.Combine(_tempDir, relative);

    private async Task<(string Output, ResultType Type)> Run(params string[] args)
    {
        var result = await _cmd.ExecuteAsync(args);
        return (StripAnsi(result.Output ?? ""), result.Type);
    }

    private async Task<(string Output, ResultType Type)> RunWithIgnoreList(string ignoreList, params string[] args)
    {
        var context = new CommandContext(new ShellSession(), ThemeConfig.CreateDefault());
        var command = new FindCommand(context, () => ignoreList);
        var result = await command.ExecuteAsync(args);
        return (StripAnsi(result.Output ?? ""), result.Type);
    }

    private static string StripAnsi(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\e\[[^m]*m", "");

    // --- Basic usage ---

    [Test]
    public async Task NoArgs_ReturnsError()
    {
        var (_, type) = await Run();
        type.ShouldBe(ResultType.Error);
    }

    [Test]
    public async Task NonexistentPath_ReturnsError()
    {
        var (_, type) = await Run(Abs("nonexistent"));
        type.ShouldBe(ResultType.Error);
    }

    [Test]
    public async Task EmptyDirectory_ReturnsNoMatches()
    {
        CreateDir("empty");
        var (output, _) = await Run(Abs("empty"));
        output.ShouldBe("No matches found.");
    }

    // --- File discovery ---

    [Test]
    public async Task FindsFilesInSubdirectories()
    {
        CreateFile("a.txt");
        CreateFile("sub/b.txt");
        CreateFile("sub/deep/c.txt");

        var (output, _) = await Run(_tempDir);
        output.ShouldContain("a.txt");
        output.ShouldContain("b.txt");
        output.ShouldContain("c.txt");
    }

    [Test]
    public async Task FindsDirectoriesInOutput()
    {
        CreateDir("mydir");
        CreateFile("mydir/file.txt");

        var (output, _) = await Run(_tempDir);
        output.ShouldContain("mydir/");
    }

    // --- -type filter ---

    [Test]
    public async Task TypeF_OnlyReturnsFiles()
    {
        CreateDir("dir1");
        CreateFile("dir1/file.txt");

        var (output, _) = await Run(_tempDir, "-type", "f");
        output.ShouldContain("file.txt");
        output.ShouldNotContain("dir1/\n");
    }

    [Test]
    public async Task TypeD_OnlyReturnsDirectories()
    {
        CreateDir("dir1");
        CreateFile("dir1/file.txt");

        var (output, _) = await Run(_tempDir, "-type", "d");
        output.ShouldContain("dir1/");
        output.ShouldNotContain("file.txt");
    }

    // --- -ext filter ---

    [Test]
    public async Task ExtFilter_MatchesExtension()
    {
        CreateFile("code.cs");
        CreateFile("notes.txt");
        CreateFile("data.json");

        var (output, _) = await Run(_tempDir, "-ext", ".cs");
        output.ShouldContain("code.cs");
        output.ShouldNotContain("notes.txt");
        output.ShouldNotContain("data.json");
    }

    [Test]
    public async Task ExtFilter_WorksWithoutLeadingDot()
    {
        CreateFile("code.cs");
        CreateFile("notes.txt");

        var (output, _) = await Run(_tempDir, "-ext", "cs");
        output.ShouldContain("code.cs");
        output.ShouldNotContain("notes.txt");
    }

    [Test]
    public async Task ExtFilter_IsCaseInsensitive()
    {
        CreateFile("readme.TXT");
        CreateFile("code.cs");

        var (output, _) = await Run(_tempDir, "-ext", ".txt");
        output.ShouldContain("readme.TXT");
        output.ShouldNotContain("code.cs");
    }

    // --- -name filter ---

    [Test]
    public async Task LongNameFilter_OnlyReturnsExactMatchesInSubdirectories()
    {
        CreateFile("dist/float-setup.exe");
        CreateFile("dist/other.exe");
        CreateFile("float-setup.exe.bak");

        var (output, type) = await Run(_tempDir, "--name", "float-setup.exe");

        type.ShouldBe(ResultType.OsCommand);
        output.ShouldBe(Path.GetRelativePath(Environment.CurrentDirectory, Abs("dist/float-setup.exe")));
    }

    [Test]
    [Arguments("-name")]
    [Arguments("--name")]
    [Arguments("-type")]
    [Arguments("--type")]
    [Arguments("-ext")]
    [Arguments("--ext")]
    [Arguments("--nmae")]
    public async Task InvalidOrIncompleteOption_ReturnsError(string option)
    {
        CreateFile("unrelated.txt");
        var (output, type) = await Run(_tempDir, option);
        type.ShouldBe(ResultType.Error);
        output.ShouldBe("");
    }

    [Test]
    public async Task LongFilters_CanBeCombined()
    {
        CreateFile("dist/float-setup.exe");
        CreateFile("dist/float-setup.txt");
        CreateDir("other/float-setup.exe");

        var (output, type) = await Run(_tempDir, "--name", "float-*", "--ext", "exe", "--type", "f");

        type.ShouldBe(ResultType.OsCommand);
        output.ShouldBe(Path.GetRelativePath(Environment.CurrentDirectory, Abs("dist/float-setup.exe")));
    }

    [Test]
    public async Task NameFilter_ExactMatch()
    {
        CreateFile("target.cs");
        CreateFile("other.cs");

        var (output, _) = await Run(_tempDir, "-name", "target.cs");
        output.ShouldContain("target.cs");
        output.ShouldNotContain("other.cs");
    }

    [Test]
    public async Task NameFilter_WildcardStar()
    {
        CreateFile("foo.cs");
        CreateFile("bar.cs");
        CreateFile("foo.txt");

        var (output, _) = await Run(_tempDir, "-name", "*.cs");
        output.ShouldContain("foo.cs");
        output.ShouldContain("bar.cs");
        output.ShouldNotContain("foo.txt");
    }

    [Test]
    public async Task NameFilter_WildcardQuestion()
    {
        CreateFile("a1.txt");
        CreateFile("a2.txt");
        CreateFile("abc.txt");

        var (output, _) = await Run(_tempDir, "-name", "a?.txt");
        output.ShouldContain("a1.txt");
        output.ShouldContain("a2.txt");
        output.ShouldNotContain("abc.txt");
    }

    [Test]
    public async Task NameFilter_IsCaseInsensitive()
    {
        CreateFile("README.md");
        CreateFile("other.md");

        var (output, _) = await Run(_tempDir, "-name", "readme.md");
        output.ShouldContain("README.md");
    }

    [Test]
    public async Task NameFilter_DotInFilenameIsLiteral()
    {
        CreateFile("file.txt");
        CreateFile("filextxt");

        var (output, _) = await Run(_tempDir, "-name", "file.txt");
        output.ShouldContain("file.txt");
        // The dot in "file.txt" should NOT match arbitrary characters
        output.ShouldNotContain("filextxt");
    }

    [Test]
    public async Task NameFilter_StarDoesNotMatchPartialExtension()
    {
        CreateFile("test.cs");
        CreateFile("test.css");

        var (output, _) = await Run(_tempDir, "-name", "*.cs");
        output.ShouldContain("test.cs");
        output.ShouldNotContain("test.css");
    }

    // --- Combined filters ---

    [Test]
    public async Task CombinedNameAndType()
    {
        CreateDir("src");
        CreateFile("src/main.cs");
        CreateFile("src/test.txt");

        var (output, _) = await Run(_tempDir, "-name", "*.cs", "-type", "f");
        output.ShouldContain("main.cs");
        output.ShouldNotContain("test.txt");
    }

    [Test]
    public async Task CombinedExtAndType()
    {
        CreateDir("logs");
        CreateFile("logs/app.log");

        var (output, _) = await Run(_tempDir, "-ext", ".log", "-type", "f");
        output.ShouldContain("app.log");
    }

    // --- Searching specific subdirectory ---

    [Test]
    public async Task SearchesSpecificSubdirectory()
    {
        CreateFile("a/file.txt");
        CreateFile("b/file.txt");

        var (output, _) = await Run(Abs("a"));
        output.ShouldContain("file.txt");
        // Should not include files from b/
        output.ShouldNotContain(Path.Combine("b", "file.txt"));
    }

    // --- Edge cases ---

    [Test]
    public async Task NamePatternWithMultipleStars()
    {
        CreateFile("test_file_v2.cs");
        CreateFile("test.cs");
        CreateFile("other.txt");

        var (output, _) = await Run(_tempDir, "-name", "*file*");
        output.ShouldContain("test_file_v2.cs");
        output.ShouldNotContain("other.txt");
    }

    [Test]
    public async Task DeeplyNestedFiles()
    {
        CreateFile("a/b/c/d/e/deep.txt");

        var (output, _) = await Run(_tempDir);
        output.ShouldContain("deep.txt");
    }

    [Test]
    public async Task NameWithSpecialRegexChars_TreatedAsLiteral()
    {
        CreateFile("file(1).txt");
        CreateFile("other.txt");

        var (output, _) = await Run(_tempDir, "-name", "file(1).txt");
        output.ShouldContain("file(1).txt");
        output.ShouldNotContain("other.txt");
    }

    [Test]
    public async Task NameWithBracketsInPattern()
    {
        CreateFile("data[0].json");
        CreateFile("data.json");

        var (output, _) = await Run(_tempDir, "-name", "data[0].json");
        output.ShouldContain("data[0].json");
    }

    // --- Configured ignores and optional .gitignore ---

    [Test]
    [Arguments("Cargo.toml", "release")]
    [Arguments("Cargo.toml", "target/release")]
    [Arguments("App.csproj", "bin")]
    [Arguments("App.csproj", "obj")]
    [Arguments("App.sln", "src/App/obj")]
    [Arguments("App.slnx", "src/App/bin")]
    [Arguments("package.json", "node_modules")]
    public async Task ProjectDefaults_SkipGeneratedFoldersAndAllowOverrides(string marker, string ignoredFolder)
    {
        CreateDir(".git");
        CreateFile(marker);
        CreateFile($"{ignoredFolder}/generated.txt");
        CreateFile("dist/Float.Setup.exe");

        var (output, type) = await Run(_tempDir);
        type.ShouldBe(ResultType.OsCommand);
        output.ShouldNotContain("generated.txt");
        output.ShouldContain("Float.Setup.exe");

        var (included, _) = await Run(_tempDir, "--include-ignored");
        included.ShouldContain("generated.txt");

        var (custom, _) = await RunWithIgnoreList(".git", _tempDir);
        custom.ShouldContain("generated.txt");
    }

    [Test]
    public async Task NoProjectMarkers_KeepOrdinaryFoldersSearchable()
    {
        CreateDir(".git");
        CreateFile("release/rust.txt");
        CreateFile("bin/binary.txt");
        CreateFile("obj/object.txt");
        CreateFile("node_modules/module.txt");

        var (output, _) = await Run(_tempDir);
        output.ShouldContain("rust.txt");
        output.ShouldContain("binary.txt");
        output.ShouldContain("object.txt");
        output.ShouldContain("module.txt");
    }

    [Test]
    public async Task NestedSearch_InheritsProjectMarkersFromAncestors()
    {
        CreateFile("Cargo.toml");
        CreateFile("target/release/generated.txt");
        CreateFile("target/debug/visible.txt");

        var (output, _) = await Run(Abs("target"));
        output.ShouldNotContain("generated.txt");
        output.ShouldContain("visible.txt");

        var (ignoredRoot, _) = await Run(Abs("target/release"));
        ignoredRoot.ShouldBe("No matches found.");
    }

    [Test]
    public async Task NestedProjects_ApplyRulesOnlyToTheirOwnSubtrees()
    {
        CreateDir(".git");
        CreateFile("backend/App.csproj");
        CreateFile("backend/bin/dotnet-generated.txt");
        CreateFile("frontend/package.json");
        CreateFile("frontend/node_modules/node-generated.txt");
        CreateFile("frontend/bin/node-script.txt");
        CreateFile("native/Cargo.toml");
        CreateFile("native/release/rust-generated.txt");
        CreateFile("docs/release/release-notes.txt");

        var (output, _) = await Run(_tempDir);
        output.ShouldNotContain("dotnet-generated.txt");
        output.ShouldNotContain("node-generated.txt");
        output.ShouldNotContain("rust-generated.txt");
        output.ShouldContain("node-script.txt");
        output.ShouldContain("release-notes.txt");
    }

    [Test]
    public async Task MixedProject_CombinesDetectedRules()
    {
        CreateFile("App.csproj");
        CreateFile("package.json");
        CreateFile("Cargo.toml");
        CreateFile("obj/dotnet-generated.txt");
        CreateFile("node_modules/node-generated.txt");
        CreateFile("release/rust-generated.txt");
        CreateFile("dist/Float.Setup.exe");

        var (output, _) = await Run(_tempDir);
        output.ShouldNotContain("generated.txt");
        output.ShouldContain("Float.Setup.exe");
    }

    [Test]
    public async Task NestedRepository_DoesNotInheritOuterProjectRules()
    {
        CreateFile("App.csproj");
        CreateFile("nested/.git"); // Worktree-style repository marker.
        CreateFile("nested/bin/visible.txt");

        var (output, _) = await Run(Abs("nested"));
        output.ShouldContain("visible.txt");
    }

    [Test]
    public async Task Defaults_SkipNoiseButFindGitignoredBuildArtifacts()
    {
        CreateFile("package.json");
        CreateFile(".git/config");
        CreateFile("nested/node_modules/dependency.js");
        CreateFile("dist/Float.Setup.exe");
        File.WriteAllText(Abs(".gitignore"), "dist/\n");

        var (output, _) = await Run(_tempDir);
        output.ShouldContain(Path.Combine("dist", "Float.Setup.exe"));
        output.ShouldNotContain("dependency.js");
        output.ShouldNotContain("config");
    }

    [Test]
    public async Task ConfiguredPatterns_ReplaceDefaultsAndOnlyExcludeDirectories()
    {
        CreateFile("nested/cache-data/hidden.txt");
        CreateFile("obj/generated.dll");
        CreateFile("node_modules/visible.js");
        CreateFile("cache-file.txt");

        var (output, _) = await RunWithIgnoreList(" cache*, obj, , ", _tempDir);
        output.ShouldNotContain("hidden.txt");
        output.ShouldNotContain("generated.dll");
        output.ShouldContain("visible.js");
        output.ShouldContain("cache-file.txt");
    }

    [Test]
    [Arguments("")]
    [Arguments(",")]
    public async Task EmptyIgnoreList_DisablesDefaultExclusions(string ignoreList)
    {
        CreateFile("node_modules/visible.js");
        var (output, _) = await RunWithIgnoreList(ignoreList, _tempDir);
        output.ShouldContain("visible.js");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task IncludeIgnored_OverridesConfigAndGitIgnoreInEitherOrder(bool includeFirst)
    {
        CreateDir(".git");
        CreateFile("cache/cached.txt");
        CreateFile("dist/Float.Setup.exe");
        File.WriteAllText(Abs(".gitignore"), "dist/\n");

        var (output, _) = await RunWithIgnoreList("cache", _tempDir,
            includeFirst ? "--include-ignored" : "--gitignore",
            includeFirst ? "--gitignore" : "--include-ignored");
        output.ShouldContain("cached.txt");
        output.ShouldContain("Float.Setup.exe");
    }

    [Test]
    public async Task IncludeIgnored_SearchesIgnoredDirectories()
    {
        CreateDir(".git");
        CreateDir("bin");
        CreateFile("bin/generated.dll");
        CreateFile("bin/unrelated.txt");
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "bin/\n");

        var (output, _) = await Run(_tempDir, "--include-ignored", "--name", "*.dll");
        output.ShouldContain("generated.dll");
        output.ShouldNotContain("unrelated.txt");
    }

    [Test]
    [Arguments("-i")]
    [Arguments("--gitignore")]
    public async Task GitIgnore_SkipsIgnoredDirectories(string option)
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        CreateDir("bin");
        CreateFile("bin/generated.dll");
        CreateFile("src/main.cs");
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "bin/\n");

        CreateFile(".git/config");

        var (output, _) = await Run(_tempDir, option);
        output.ShouldNotContain("generated.dll");
        output.ShouldNotContain(".git/");
        output.ShouldNotContain("config");
        output.ShouldContain("main.cs");
    }

    [Test]
    public async Task GitIgnore_DetectsRepositoryFromNestedSearchPath()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        CreateDir("bin");
        CreateFile("bin/generated.dll");
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "bin/\n");

        var (output, _) = await Run(Path.Combine(_tempDir, "bin"), "--gitignore");
        output.ShouldBe("No matches found.");
    }

    [Test]
    public async Task IncludeIgnored_SearchesExplicitIgnoredRoot()
    {
        CreateDir(".git");
        CreateFile("bin/generated.dll");
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "bin/\n");

        var (output, _) = await Run(Abs("bin"), "--include-ignored");
        output.ShouldContain("generated.dll");
    }
}
