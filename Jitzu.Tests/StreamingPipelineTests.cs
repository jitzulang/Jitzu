using Jitzu.Shell.Core;
using Shouldly;

namespace Jitzu.Tests;

public class StreamingPipelineTests
{
    [Test]
    public async Task FirstAsync_ReturnsOnlyFirstLine()
    {
        // Arrange
        var lines = GenerateLines(1000);

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.FirstAsync(lines))
        {
            result.Add(line);
        }

        // Assert
        result.Count.ShouldBe(1);
        result[0].ShouldBe("Line 0");
    }

    [Test]
    public async Task LastAsync_ReturnsOnlyLastLine()
    {
        // Arrange
        var lines = GenerateLines(100);

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.LastAsync(lines))
        {
            result.Add(line);
        }

        // Assert
        result.Count.ShouldBe(1);
        result[0].ShouldBe("Line 99");
    }

    [Test]
    public async Task NthAsync_ReturnsCorrectLine()
    {
        // Arrange
        var lines = GenerateLines(100);

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.NthAsync(lines, 42))
        {
            result.Add(line);
        }

        // Assert
        result.Count.ShouldBe(1);
        result[0].ShouldBe("Line 42");
    }

    [Test]
    public async Task GrepAsync_FiltersCorrectly()
    {
        // Arrange
        var lines = GenerateLines(100);

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.GrepAsync(lines, "Line 5"))
        {
            result.Add(line);
        }

        // Assert - Should match: Line 5, Line 50-59
        result.Count.ShouldBe(11);
        result.ShouldContain("Line 5");
        result.ShouldContain("Line 50");
    }

    [Test]
    public async Task GrepAsync_IsCaseSensitiveByDefault()
    {
        // Arrange - input has "Line" with capital L
        var lines = GenerateLinesAsync(["Line 1", "line 2", "LINE 3", "Line 4"]);

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.GrepAsync(lines, "Line"))
        {
            result.Add(line);
        }

        // Assert - only "Line" (exact case) should match
        result.Count.ShouldBe(2);
        result.ShouldContain("Line 1");
        result.ShouldContain("Line 4");
        result.ShouldNotContain("line 2");
        result.ShouldNotContain("LINE 3");
    }

    [Test]
    public async Task GrepAsync_WithIgnoreCase_MatchesAllCasings()
    {
        // Arrange
        var lines = GenerateLinesAsync(["Line 1", "line 2", "LINE 3", "other 4"]);

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.GrepAsync(lines, "line", ignoreCase: true))
        {
            result.Add(line);
        }

        // Assert - all three case variants should match
        result.Count.ShouldBe(3);
        result.ShouldContain("Line 1");
        result.ShouldContain("line 2");
        result.ShouldContain("LINE 3");
    }

    [Test]
    public async Task GrepAsync_UsesRegexMatching()
    {
        // Arrange - regex pattern that only substring matching could not handle
        var lines = GenerateLinesAsync(["Line 1", "Line 10", "Line 2", "foo bar"]);

        // Act - anchor to end of string: "Line [0-9]$" matches only single-digit lines
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.GrepAsync(lines, @"Line [0-9]$"))
        {
            result.Add(line);
        }

        // Assert - only "Line 1" and "Line 2" match (not "Line 10")
        result.Count.ShouldBe(2);
        result.ShouldContain("Line 1");
        result.ShouldContain("Line 2");
        result.ShouldNotContain("Line 10");
    }

    [Test]
    public async Task GrepAsync_WithInvalidRegex_FallsBackToLiteralMatch()
    {
        // Arrange - "[invalid" is an invalid regex pattern
        var lines = GenerateLinesAsync(["has [invalid] text", "no match here", "[invalid more"]);

        // Act - should not throw; falls back to literal substring match
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.GrepAsync(lines, "[invalid"))
        {
            result.Add(line);
        }

        // Assert - literal substring "[invalid" matches two lines
        result.Count.ShouldBe(2);
        result.ShouldContain("has [invalid] text");
        result.ShouldContain("[invalid more");
    }

    [Test]
    public async Task GrepAsync_WithEmptyPattern_YieldsNothing()
    {
        // Arrange
        var lines = GenerateLinesAsync(["Line 1", "Line 2", "Line 3"]);

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.GrepAsync(lines, ""))
        {
            result.Add(line);
        }

        // Assert - empty pattern should match nothing (not everything)
        result.Count.ShouldBe(0);
    }

    [Test]
    public async Task HeadAsync_ReturnsFirstNLines()
    {
        // Arrange
        var lines = GenerateLines(1000);

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.HeadAsync(lines, 10))
        {
            result.Add(line);
        }

        // Assert
        result.Count.ShouldBe(10);
        result[0].ShouldBe("Line 0");
        result[9].ShouldBe("Line 9");
    }

    [Test]
    public async Task TailAsync_ReturnsLastNLines()
    {
        // Arrange
        var lines = GenerateLines(100);

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.TailAsync(lines, 5))
        {
            result.Add(line);
        }

        // Assert
        result.Count.ShouldBe(5);
        result[0].ShouldBe("Line 95");
        result[4].ShouldBe("Line 99");
    }

    [Test]
    public async Task SortAsync_SortsLines()
    {
        // Arrange
        var lines = GenerateLinesAsync(new[] { "zebra", "apple", "monkey", "banana" });

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.SortAsync(lines))
        {
            result.Add(line);
        }

        // Assert
        result.Count.ShouldBe(4);
        result[0].ShouldBe("apple");
        result[1].ShouldBe("banana");
        result[2].ShouldBe("monkey");
        result[3].ShouldBe("zebra");
    }

    [Test]
    public async Task UniqAsync_RemovesDuplicates()
    {
        // Arrange
        var lines = GenerateLinesAsync(new[] { "a", "a", "b", "b", "b", "c", "a" });

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.UniqAsync(lines))
        {
            result.Add(line);
        }

        // Assert - a, b, c, a (last 'a' is different run)
        result.Count.ShouldBe(4);
        result[0].ShouldBe("a");
        result[1].ShouldBe("b");
        result[2].ShouldBe("c");
        result[3].ShouldBe("a");
    }

    [Test]
    public async Task ChainedPipeline_WorksCorrectly()
    {
        // Arrange - simulate: seq 1 100 | grep '5' | head 3
        var lines = GenerateLines(100);

        // Act
        var filtered = StreamingPipeFunctions.GrepAsync(lines, "5");
        var limited = StreamingPipeFunctions.HeadAsync(filtered, 3);

        var result = new List<string>();
        await foreach (var line in limited)
        {
            result.Add(line);
        }

        // Assert
        result.Count.ShouldBe(3);
        result[0].ShouldBe("Line 5");
        result[1].ShouldBe("Line 15");
        result[2].ShouldBe("Line 25");
    }

    [Test]
    public async Task MaterializeAsync_ConvertsToString()
    {
        // Arrange
        var lines = GenerateLines(5);

        // Act
        var result = await StreamingPipeline.MaterializeAsync(lines);

        // Assert
        result.ShouldBe("Line 0\nLine 1\nLine 2\nLine 3\nLine 4");
    }

    [Test]
    public async Task StreamFromStringAsync_ParsesCorrectly()
    {
        // Arrange
        var input = "Line 1\nLine 2\nLine 3";

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeline.StreamFromStringAsync(input))
        {
            result.Add(line);
        }

        // Assert
        result.Count.ShouldBe(3);
        result[0].ShouldBe("Line 1");
        result[1].ShouldBe("Line 2");
        result[2].ShouldBe("Line 3");
    }

    [Test]
    public async Task EarlyTermination_StopsProcessing()
    {
        // Arrange
        var processedCount = 0;
        var largeStream = GenerateLinesWithCounter(10000, () => processedCount++);

        // Act
        var result = new List<string>();
        await foreach (var line in StreamingPipeFunctions.HeadAsync(largeStream, 5))
        {
            result.Add(line);
        }

        // Assert
        result.Count.ShouldBe(5);
        processedCount.ShouldBe(5);
    }

    // Helper methods
    private static async IAsyncEnumerable<string> GenerateLines(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return $"Line {i}";
            await Task.Yield(); // Allow async processing
        }
    }

    private static async IAsyncEnumerable<string> GenerateLinesAsync(string[] lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<string> GenerateLinesWithCounter(int count, Action onGenerate)
    {
        for (var i = 0; i < count; i++)
        {
            onGenerate();
            yield return $"Line {i}";
            await Task.Yield();
        }
    }
}
