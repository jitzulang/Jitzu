namespace Jitzu.Benchmarking.Benchmarks;

public record ResultSummary
{
    public required string Script { get; set; }
    public required string Run { get; init; }
    public required int Iterations { get; init; }
    public required TimeSpan MeanTime { get; init; }
    public required TimeSpan MedianTime { get; init; }
    public required TimeSpan P95Time { get; init; }
    public required TimeSpan Error { get; init; }
    public required TimeSpan StdDev { get; init; }
}
