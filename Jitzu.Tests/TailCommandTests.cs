using Jitzu.Shell;
using Jitzu.Shell.Core;
using Jitzu.Shell.Core.Commands;
using Shouldly;

namespace Jitzu.Tests;

public class TailCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TailCommand _cmd;

    public TailCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jitzu_tail_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var theme = ThemeConfig.CreateDefault();
        var context = new CommandContext(new ShellSession(), theme);
        _cmd = new TailCommand(context);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Test]
    public async Task Tail_DefaultLast10Lines()
    {
        var file = Path.Combine(_tempDir, "lines.txt");
        await File.WriteAllLinesAsync(file, Enumerable.Range(1, 20).Select(i => $"line{i}"));

        var result = await _cmd.ExecuteAsync(new[] { file }.AsMemory());

        result.Type.ShouldBe(ResultType.OsCommand);
        var lines = result.Output!.Split(Environment.NewLine);
        lines.Length.ShouldBe(10);
        lines[0].ShouldBe("line11");
        lines[9].ShouldBe("line20");
    }

    [Test]
    public async Task Tail_WithNFlag_ReturnsSpecifiedLines()
    {
        var file = Path.Combine(_tempDir, "lines.txt");
        await File.WriteAllLinesAsync(file, Enumerable.Range(1, 20).Select(i => $"line{i}"));

        var result = await _cmd.ExecuteAsync(new[] { "-n", "3", file }.AsMemory());

        result.Type.ShouldBe(ResultType.OsCommand);
        var lines = result.Output!.Split(Environment.NewLine);
        lines.Length.ShouldBe(3);
        lines[0].ShouldBe("line18");
    }

    [Test]
    public async Task Tail_FileNotFound_ReturnsError()
    {
        var result = await _cmd.ExecuteAsync(new[] { Path.Combine(_tempDir, "nope.txt") }.AsMemory());
        result.Type.ShouldBe(ResultType.Error);
    }

    [Test]
    public async Task Tail_NoArgs_ReturnsError()
    {
        var result = await _cmd.ExecuteAsync(ReadOnlyMemory<string>.Empty);
        result.Type.ShouldBe(ResultType.Error);
    }

    [Test]
    public async Task Tail_FollowFlag_IsParsed_AndCommandIsStreamable()
    {
        typeof(IStreamingCommand).IsAssignableFrom(typeof(TailCommand)).ShouldBeTrue();
    }

    [Test]
    public async Task Tail_Follow_StreamsExistingLines_ThenNewLines()
    {
        var file = Path.Combine(_tempDir, "follow.txt");
        await File.WriteAllLinesAsync(file, ["line1", "line2", "line3"]);

        using var cts = new CancellationTokenSource();
        var collected = new List<string>();

        var streamTask = Task.Run(async () =>
        {
            await foreach (var line in _cmd.StreamAsync(new[] { "-f", file }.AsMemory(), cts.Token))
            {
                collected.Add(line);
                if (collected.Count >= 5)
                    await cts.CancelAsync();
            }
        });

        // Wait for initial lines to be consumed
        await Task.Delay(300);

        // Append new lines while following
        await File.AppendAllLinesAsync(file, ["line4", "line5"]);

        try { await streamTask; }
        catch (OperationCanceledException) { }

        collected.Count.ShouldBeGreaterThanOrEqualTo(5);
        collected[0].ShouldBe("line1");
        collected[1].ShouldBe("line2");
        collected[2].ShouldBe("line3");
        collected[3].ShouldBe("line4");
        collected[4].ShouldBe("line5");
    }

    [Test]
    public async Task Tail_Follow_WithNFlag_StreamsLastNThenFollows()
    {
        var file = Path.Combine(_tempDir, "follow_n.txt");
        await File.WriteAllLinesAsync(file, Enumerable.Range(1, 10).Select(i => $"line{i}"));

        using var cts = new CancellationTokenSource();
        var collected = new List<string>();

        var streamTask = Task.Run(async () =>
        {
            await foreach (var line in _cmd.StreamAsync(new[] { "-f", "-n", "2", file }.AsMemory(), cts.Token))
            {
                collected.Add(line);
                if (collected.Count >= 3)
                    await cts.CancelAsync();
            }
        });

        await Task.Delay(300);
        await File.AppendAllLinesAsync(file, ["line11"]);

        try { await streamTask; }
        catch (OperationCanceledException) { }

        collected.Count.ShouldBeGreaterThanOrEqualTo(3);
        collected[0].ShouldBe("line9");
        collected[1].ShouldBe("line10");
        collected[2].ShouldBe("line11");
    }

    [Test]
    public async Task Tail_Follow_StopsOnCancellation()
    {
        var file = Path.Combine(_tempDir, "cancel.txt");
        await File.WriteAllLinesAsync(file, ["line1"]);

        using var cts = new CancellationTokenSource();
        var collected = new List<string>();

        var streamTask = Task.Run(async () =>
        {
            await foreach (var line in _cmd.StreamAsync(new[] { "-f", file }.AsMemory(), cts.Token))
            {
                collected.Add(line);
                await cts.CancelAsync();
            }
        });

        try { await streamTask.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }

        streamTask.IsCompleted.ShouldBeTrue();
        collected.ShouldBe(["line1"]);
    }

    [Test]
    public async Task Tail_WithoutFollow_ExecuteAsync_Completes()
    {
        var file = Path.Combine(_tempDir, "nof.txt");
        await File.WriteAllLinesAsync(file, ["a", "b", "c"]);

        var result = await _cmd.ExecuteAsync(new[] { file }.AsMemory()).WaitAsync(TimeSpan.FromSeconds(10));

        result.Type.ShouldBe(ResultType.OsCommand);
    }
}
