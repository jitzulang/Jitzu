using System.Diagnostics;
using System.Text;
using Shouldly;

namespace Jitzu.Tests;

public class ShellTests
{
    [Test]
    public async Task Shell_StartsSuccessfully()
    {
        await using var harness = new ShellTestHarness();
        await harness.StartAsync(ShellTestHarness.GetShellPath());

        harness.HasExited.ShouldBeFalse();
    }

    [Test]
    public async Task PwdCommand_ReturnsCurrentDirectory()
    {
        await using var harness = new ShellTestHarness(Path.GetTempPath());
        await harness.StartAsync(ShellTestHarness.GetShellPath());

        var output = await harness.SendCommandAsync("pwd");

        output.ShouldContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
    }

    [Test]
    public async Task CdCommand_ChangesDirectory()
    {
        await using var harness = new ShellTestHarness(Path.GetTempPath());
        await harness.StartAsync(ShellTestHarness.GetShellPath());

        await harness.SendCommandAndWaitAsync("cd /");
        var output = await harness.SendCommandAsync("pwd");

        // On Windows, "cd /" goes to the drive root (e.g. "D:\"), on Unix it's "/"
        output.ShouldContain(Path.GetPathRoot(Environment.CurrentDirectory)!.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Test]
    public async Task ExitCommand_ExitsShell()
    {
        await using var harness = new ShellTestHarness();
        await harness.StartAsync(ShellTestHarness.GetShellPath());

        await harness.SendCommandAndWaitAsync("exit", waitMs: 0);

        var sw = Stopwatch.StartNew();
        while (!harness.HasExited && sw.ElapsedMilliseconds < 3000)
            await Task.Delay(50);

        harness.HasExited.ShouldBeTrue();
    }

    [Test]
    public async Task MultipleCommands_ChainedWithSemicolon()
    {
        await using var harness = new ShellTestHarness();
        await harness.StartAsync(ShellTestHarness.GetShellPath());

        // Semicolon-chained commands only display the last command's output
        var output = await harness.SendCommandAsync("echo a; echo b; echo c");

        output.ShouldContain("c");
    }

    [Test]
    public async Task PathCommand_NoArgs_DisplaysPathEntries()
    {
        await using var harness = new ShellTestHarness();
        await harness.StartAsync(ShellTestHarness.GetShellPath());

        var output = await harness.SendCommandAsync("path");

        // Should list PATH entries one per line — at least one entry should exist
        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldNotContain(OperatingSystem.IsWindows() ? ";" : ":");
    }

    [Test]
    public async Task PathCommand_AppendsDirectory()
    {
        await using var harness = new ShellTestHarness();
        await harness.StartAsync(ShellTestHarness.GetShellPath());

        var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        await harness.SendCommandAndWaitAsync($"path {tempDir}");
        var output = await harness.SendCommandAsync("echo $PATH");

        output.ShouldEndWith(tempDir);
    }

    [Test]
    public async Task PathCommand_NonexistentDirectory_ReturnsError()
    {
        await using var harness = new ShellTestHarness();
        await harness.StartAsync(ShellTestHarness.GetShellPath());

        var output = await harness.SendCommandAsync("path /nonexistent/fake/dir");

        output.ShouldContain("Directory not found");
    }

    [Test]
    public async Task Export_SetsEnvironmentVariable()
    {
        await using var harness = new ShellTestHarness();
        await harness.StartAsync(ShellTestHarness.GetShellPath());

        await harness.SendCommandAndWaitAsync("export MY_TEST_VAR=hello123");
        var output = await harness.SendCommandAsync("echo $MY_TEST_VAR");

        output.ShouldBe("hello123");
    }

    [Test]
    public async Task Export_QuotedValueWithSemicolon_PreservesSemicolon()
    {
        await using var harness = new ShellTestHarness();
        await harness.StartAsync(ShellTestHarness.GetShellPath());

        await harness.SendCommandAndWaitAsync("export MY_TEST_VAR=\"aaa;bbb\"");
        var output = await harness.SendCommandAsync("echo $MY_TEST_VAR");

        output.ShouldBe("aaa;bbb");
    }

    [Test]
    public async Task Export_PathAppend_PreservesExistingPath()
    {
        await using var harness = new ShellTestHarness();
        await harness.StartAsync(ShellTestHarness.GetShellPath());

        // Capture original PATH
        var originalPath = await harness.SendCommandAsync("echo $PATH");

        // Re-export PATH with an appended directory
        var separator = OperatingSystem.IsWindows() ? ";" : ":";
        var fakeBin = "/fake/test/bin";
        await harness.SendCommandAndWaitAsync($"export PATH=\"$PATH{separator}{fakeBin}\"");

        var newPath = await harness.SendCommandAsync("echo $PATH");

        newPath.ShouldContain(originalPath);
        newPath.ShouldEndWith($"{separator}{fakeBin}");
    }

    [Test]
    public async Task Export_UnquotedSemicolon_TreatedAsCommandSeparator()
    {
        await using var harness = new ShellTestHarness();
        await harness.StartAsync(ShellTestHarness.GetShellPath());

        // Without quotes, semicolon splits into two commands
        // "export X=aaa" then "bbb" (which fails silently or is treated as a command)
        await harness.SendCommandAndWaitAsync("export MY_SPLIT_VAR=aaa;echo SPLIT_MARKER");
        var output = await harness.SendCommandAsync("echo $MY_SPLIT_VAR");

        // Value should be just "aaa", not "aaa;echo SPLIT_MARKER"
        output.ShouldBe("aaa");
    }

    [Test]
    public async Task PipedInput_ExecutesMultipleCommands()
    {
        var (output, exitCode) = await RunPipedAsync("echo hello\necho world\n");

        exitCode.ShouldBe(0);
        output.ShouldContain("hello");
        output.ShouldContain("world");
    }

    [Test]
    public async Task PipedInput_EmptyInput_ExitsCleanly()
    {
        var (_, exitCode) = await RunPipedAsync("");

        exitCode.ShouldBe(0);
    }

    [Test]
    public async Task PipedInput_ExitCommand_StopsProcessing()
    {
        var (output, exitCode) = await RunPipedAsync("echo before\nexit\necho after\n");

        exitCode.ShouldBe(0);
        output.ShouldContain("before");
        output.ShouldNotContain("after");
    }

    [Test]
    public async Task CommandMode_ExitStopsTheRemainingChain()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"jitzu-exit-chain-{Guid.NewGuid():N}");
        try
        {
            var (_, exitCode) = await RunCommandModeAsync($"exit; touch \"{marker}\"");

            exitCode.ShouldBe(0);
            File.Exists(marker).ShouldBeFalse();
        }
        finally { File.Delete(marker); }
    }

    [Test]
    public async Task PipedInput_AliasedQuitStopsTheRemainingChainAndSession()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"jitzu-exit-pipe-{Guid.NewGuid():N}");
        try
        {
            var (output, exitCode) = await RunPipedAsync(
                $"alias done=quit\ndone; touch \"{marker}\"\necho SHOULD_NOT_RUN\n");

            exitCode.ShouldBe(0);
            File.Exists(marker).ShouldBeFalse();
            output.ShouldNotContain("SHOULD_NOT_RUN");
        }
        finally { File.Delete(marker); }
    }

    private static async Task<(string output, int exitCode)> RunCommandModeAsync(string command)
    {
        var startInfo = CreateRedirectedStartInfo();
        startInfo.ArgumentList.Add("--no-persist");
        startInfo.ArgumentList.Add("--no-splash");
        startInfo.ArgumentList.Add("--no-config");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        using var process = Process.Start(startInfo)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await errorTask;
        if (!string.IsNullOrWhiteSpace(error))
            throw new Exception($"Shell error: {error}");
        return (await outputTask, process.ExitCode);
    }

    private static async Task<(string output, int exitCode)> RunPipedAsync(string stdin)
    {
        var startInfo = CreateRedirectedStartInfo();
        startInfo.ArgumentList.Add("--no-persist");
        startInfo.ArgumentList.Add("--no-splash");
        startInfo.ArgumentList.Add("--no-config");

        using var process = Process.Start(startInfo)!;

        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        if (!string.IsNullOrWhiteSpace(error))
            throw new Exception($"Shell error: {error}");

        return (output, process.ExitCode);
    }

    private static ProcessStartInfo CreateRedirectedStartInfo() => new()
    {
            FileName = ShellTestHarness.GetShellPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
    };
}
