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
        _cmd = new FindCommand(context);
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

    // --- .gitignore opt-in ---

    [Test]
    public async Task GitIgnore_IsOptIn()
    {
        CreateDir("bin");
        CreateFile("bin/generated.dll");
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "bin/\n");

        var (output, _) = await Run(_tempDir);
        output.ShouldContain("generated.dll");
    }

    [Test]
    public async Task GitIgnore_SkipsIgnoredDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        CreateDir("bin");
        CreateFile("bin/generated.dll");
        CreateFile("src/main.cs");
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "bin/\n");

        var (output, _) = await Run(_tempDir, "--gitignore");
        output.ShouldNotContain("generated.dll");
        output.ShouldContain("main.cs");
    }

    [Test]
    public async Task GitIgnore_DetectsRepositoryFromNestedSearchPath()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        CreateDir("bin");
        CreateFile("bin/generated.dll");
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "bin/\n");

        var (output, _) = await Run(Path.Combine(_tempDir, "bin"), "-i");
        output.ShouldBe("No matches found.");
    }
}
