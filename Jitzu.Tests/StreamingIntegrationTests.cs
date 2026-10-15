using Shouldly;
using System.Diagnostics;

namespace Jitzu.Tests;

/// <summary>
/// Integration tests for streaming pipeline functionality using the shell's -c flag.
/// Tests end-to-end streaming behavior through direct command execution.
/// </summary>
public class StreamingIntegrationTests
{
    private async Task<string> RunCommandAsync(string command)
    {
        var shellPath = ShellTestHarness.GetShellPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = shellPath,
            ArgumentList = { "-c", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            throw new Exception("Failed to start shell process");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        if (!string.IsNullOrWhiteSpace(error))
            throw new Exception($"Shell error: {error}");

        return output.Trim();
    }

    [Test]
    public async Task StreamingPipeline_FirstCommand_ReturnsFirstLine()
    {
        var output = await RunCommandAsync("seq 1 1000 | first");
        output.ShouldBe("1");
    }

    [Test]
    public async Task StreamingPipeline_HeadCommand_LimitsOutput()
    {
        var output = await RunCommandAsync("seq 1 100 | head 5");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(5);
        lines[0].ShouldBe("1");
        lines[4].ShouldBe("5");
    }

    [Test]
    public async Task StreamingPipeline_ChainedCommands_WorksCorrectly()
    {
        var output = await RunCommandAsync("seq 1 100 | grep 5 | head 3");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(3);
        lines[0].ShouldBe("5");
        lines[1].ShouldBe("15");
        lines[2].ShouldBe("25");
    }

    [Test]
    public async Task StreamingPipeline_LastCommand_ReturnsLastLine()
    {
        var output = await RunCommandAsync("seq 1 10 | last");
        output.ShouldBe("10");
    }

    [Test]
    public async Task StreamingPipeline_NthCommand_ReturnsCorrectLine()
    {
        var output = await RunCommandAsync("seq 1 100 | nth 42");
        output.ShouldBe("43"); // nth is 0-indexed
    }

    [Test]
    public async Task StreamingPipeline_GrepCommand_FiltersLines()
    {
        var output = await RunCommandAsync("seq 1 100 | grep 55");
        output.ShouldContain("55");
    }

    [Test]
    public async Task StreamingPipeline_TailCommand_ReturnsLastNLines()
    {
        var output = await RunCommandAsync("seq 1 10 | tail 3");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(3);
        lines[0].ShouldBe("8");
        lines[1].ShouldBe("9");
        lines[2].ShouldBe("10");
    }

    [Test]
    public async Task StreamingPipeline_SortCommand_SortsOutput()
    {
        var output = await RunCommandAsync("seq 1 11 | sort");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.ShouldBe(["1", "10", "11", "2", "3", "4", "5", "6", "7", "8", "9"]);
    }

    [Test]
    public async Task StreamingPipeline_UniqCommand_RemovesDuplicates()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(tempFile, new[] { "a", "a", "b", "b", "c", "a" });

            var output = await RunCommandAsync($"tail \"{tempFile}\" | uniq");
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            lines.ShouldBe(["a", "b", "c", "a"]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task StreamingPipeline_WcCommand_CountsLinesWordsAndCharacters()
    {
        var output = await RunCommandAsync("seq 1 10 | wc");
        output.ShouldBe("10\t10\t21");
    }

    [Test]
    public async Task FindFiles_WcEachFile_Average_ComputesAverageLineCount()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jitzu_pipeline_avg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "two.cs"), "one\ntwo\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "four.cs"), "one\ntwo\nthree\nfour\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "ignored.txt"), "one\n");

            var output = await RunCommandAsync(
                $"find . \"{directory}\" -t f -e cs | wc -l --files | avg");

            output.ShouldBe("3");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task StreamingPipeline_LargeDataset_Completes()
    {
        var output = await RunCommandAsync("seq 1 10000 | head 1").WaitAsync(TimeSpan.FromSeconds(30));

        output.ShouldBe("1");
    }

    [Test]
    public async Task StreamingPipeline_MultipleFilters_ChainsCorrectly()
    {
        var output = await RunCommandAsync("seq 1 1000 | grep 5 | grep 55 | head 2");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(2);
        lines.ShouldAllBe(l => l.Contains("55"));
    }

    [Test]
    public async Task BuiltinCommand_CatWithPipe_LimitsOutput()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(tempFile, Enumerable.Range(1, 100).Select(i => $"Line {i}"));

            var output = await RunCommandAsync($"cat {tempFile} | head 3");

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Length.ShouldBe(3);
            lines[0].ShouldEndWith("  Line 1");
            lines[1].ShouldEndWith("  Line 2");
            lines[2].ShouldEndWith("  Line 3");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task BuiltinCommand_GrepWithPipe_Streams()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(tempFile, new[]
            {
                "apple pie",
                "banana split",
                "apple tart",
                "cherry pie",
                "apple juice"
            });

            var output = await RunCommandAsync($"grep apple {tempFile} | head 2");

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Length.ShouldBe(2);
            lines.ShouldAllBe(l => l.Contains("apple"));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
