using System.Text.Json.Serialization;

namespace Jitzu.Benchmarking.Benchmarks;

public record BenchmarkConfig
{
    [JsonPropertyName("warmups")]
    public int Warmups { get; init; } = 2;

    [JsonPropertyName("iterations")]
    public int Iterations { get; init; } = 15;

    [JsonPropertyName("runs")]
    public int[]? Runs { get; init; }

    [JsonPropertyName("args")]
    public string[] Args { get; init; } = [];

    [JsonPropertyName("addOns")]
    public BenchmarkAddons? AddOns { get; init; } = new();
}
