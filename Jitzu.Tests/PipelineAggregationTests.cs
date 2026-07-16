using Jitzu.Shell.Core;
using Shouldly;

namespace Jitzu.Tests;

public class PipelineAggregationTests
{
    [Test]
    public async Task NumericAggregators_ReduceTheStream()
    {
        (await Materialize(StreamingPipeFunctions.SumAsync(Lines("1", "2", "3", "4"))))
            .ShouldBe(["10"]);
        (await Materialize(StreamingPipeFunctions.AverageAsync(Lines("1", "2", "3", "4"))))
            .ShouldBe(["2.5"]);
        (await Materialize(StreamingPipeFunctions.MinAsync(Lines("1", "2", "3", "4"))))
            .ShouldBe(["1"]);
        (await Materialize(StreamingPipeFunctions.MaxAsync(Lines("1", "2", "3", "4"))))
            .ShouldBe(["4"]);
        (await Materialize(StreamingPipeFunctions.CountAsync(Lines("1", "2", "3", "4"))))
            .ShouldBe(["4"]);
    }

    [Test]
    public async Task NumericAggregators_RejectNonNumericInput()
    {
        await Should.ThrowAsync<FormatException>(async () =>
            await Materialize(StreamingPipeFunctions.AverageAsync(Lines("1", "not-a-number"))));
    }

    [Test]
    public async Task Average_RejectsAnEmptyStream()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await Materialize(StreamingPipeFunctions.AverageAsync(Lines())));
    }

    [Test]
    public async Task WcFiles_EmitsOneLineCountPerInputFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jitzu_wc_files_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var first = Path.Combine(directory, "first.cs");
        var second = Path.Combine(directory, "second.cs");
        try
        {
            await File.WriteAllTextAsync(first, "one\ntwo\n");
            await File.WriteAllTextAsync(second, "one\ntwo\nthree\nfour\n");

            var counts = await Materialize(StreamingPipeFunctions.WcAsync(
                Lines(first, second),
                linesOnly: true,
                files: true));

            counts.ShouldBe(["2", "4"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string[]> Materialize(IAsyncEnumerable<string> stream) =>
        await StreamingPipeline.MaterializeToArrayAsync(stream);

    private static async IAsyncEnumerable<string> Lines(params string[] lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.Yield();
        }
    }
}
