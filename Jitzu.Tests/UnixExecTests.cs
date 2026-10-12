using System.Diagnostics;
using System.Text;
using Shouldly;

namespace Jitzu.Tests;

public class UnixExecTests
{
    [Test]
    public async Task Exec_ReplacesShellWithSamePid()
    {
        if (!OperatingSystem.IsLinux()) return;

        var result = await RunAsync("exec /bin/sh -c \"echo $$\"");

        result.ExitCode.ShouldBe(0);
        result.Output.Trim().ShouldBe(result.Pid.ToString());
        result.Error.ShouldBeEmpty();
    }

    [Test]
    public async Task Exec_SearchesPathAndPreservesChildExitStatus()
    {
        if (!OperatingSystem.IsLinux()) return;

        var result = await RunAsync("exec sh -c \"exit 37\"");

        result.ExitCode.ShouldBe(37);
        result.Output.ShouldBeEmpty();
        result.Error.ShouldBeEmpty();
    }

    [Test]
    public async Task Exec_InheritsStdinAndStopsCommandChain()
    {
        if (!OperatingSystem.IsLinux()) return;

        var result = await RunAsync("exec /bin/cat; echo should-not-run", "inherited input\n");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldBe("inherited input\n");
    }

    [Test]
    public async Task Exec_PreservesWorkingDirectoryAndUpdatedEnvironment()
    {
        if (!OperatingSystem.IsLinux()) return;

        var result = await RunAsync("export JITZU_EXEC_VALUE=updated; exec /bin/sh -c \"pwd; printenv JITZU_EXEC_VALUE\"");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldBe($"{Path.GetTempPath().TrimEnd('/')}\nupdated\n");
    }

    [Test]
    public async Task Exec_PreservesQuotedAndEmptyArguments()
    {
        if (!OperatingSystem.IsLinux()) return;

        var result = await RunAsync("exec /usr/bin/printf \"<%s>\\n\" 'two words' \"\" café 'left|right'");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldBe("<two words>\n<>\n<café>\n<left|right>\n");
    }

    [Test]
    public async Task Exec_WithQuotedExecutablePath()
    {
        if (!OperatingSystem.IsLinux()) return;

        var result = await RunAsync("exec '/usr/bin/printf' \"session-started\"");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldBe("session-started");
    }

    [Test]
    public async Task Exec_MissingCommand_Returns127()
    {
        if (!OperatingSystem.IsLinux()) return;

        var result = await RunAsync("exec /jitzu-no-such-executable");

        result.ExitCode.ShouldBe(127);
        result.Output.ShouldContain("exec:");
        result.Output.ShouldContain("command not found");
    }

    [Test]
    public async Task Exec_NonExecutableFile_Returns126()
    {
        if (!OperatingSystem.IsLinux()) return;

        var path = Path.GetTempFileName();
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var result = await RunAsync($"exec {path}");

            result.ExitCode.ShouldBe(126);
            result.Output.ShouldContain("permission denied");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Exec_NoCommand_ReturnsToShell()
    {
        if (!OperatingSystem.IsLinux()) return;

        var result = await RunAsync("exec; echo still-running");

        result.ExitCode.ShouldBe(0);
        result.Output.Trim().ShouldBe("still-running");
    }

    [Test]
    public async Task Exec_PathSearchSkipsNonExecutableAndUsesExportedPath()
    {
        if (!OperatingSystem.IsLinux()) return;

        var root = Path.Combine(Path.GetTempPath(), $"jitzu-exec-{Guid.NewGuid():N}");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            var blocked = Path.Combine(first, "session-launcher");
            await File.WriteAllTextAsync(blocked, "not executable");
            File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.CreateSymbolicLink(Path.Combine(second, "session-launcher"), "/usr/bin/printf");

            var result = await RunAsync($"export PATH={first}:{second}; exec session-launcher path-search-ok");

            result.ExitCode.ShouldBe(0);
            result.Output.ShouldBe("path-search-ok");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Exec_ExecutableTextWithoutShebang_PreservesArguments()
    {
        if (!OperatingSystem.IsLinux()) return;

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "printf '<%s>\\n' \"$1\"\nexit 23\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await RunAsync($"exec -- {path} 'script argument'");

            result.ExitCode.ShouldBe(23);
            result.Output.ShouldBe("<script argument>\n");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Exec_WithinPipeline_DoesNotReplaceParentShell()
    {
        if (!OperatingSystem.IsLinux()) return;

        var result = await RunAsync("exec /usr/bin/printf pipeline | /bin/cat; echo parent-survived");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldBe("pipelineparent-survived\n");
    }

    [Test]
    public async Task Exec_ResetsSigpipeDisposition()
    {
        if (!OperatingSystem.IsLinux()) return;

        var result = await RunAsync("exec /bin/sh -c \"kill -PIPE $$; echo should-not-run\"");

        result.ExitCode.ShouldBe(141);
        result.Output.ShouldBeEmpty();
    }

    private static async Task<(int Pid, int ExitCode, string Output, string Error)> RunAsync(string command, string input = "")
    {
        var startInfo = new ProcessStartInfo(ShellTestHarness.GetShellPath())
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var arg in new[] { "--no-persist", "--no-splash", "-c", command })
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            return (process.Id, process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
