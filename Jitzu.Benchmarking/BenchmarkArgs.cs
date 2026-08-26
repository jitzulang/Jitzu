using Clap.Net;

namespace Jitzu.Benchmarking;

[Command]
public partial class BenchmarkArgs
{
    [Arg(Short = 't', Long = "tests")]
    public string[]? Tests { get; init; }

    [Arg(Short = 'e', Long = "extensions")]
    public string[] Extensions { get; private init; } = ["jz", "ps1", "py"];

    [Arg(Long = "jitzu")]
    public string? JitzuPath { get; init; }

    [Arg(Long = "scripts")]
    public string? ScriptsPath { get; init; }

    [Arg(Long = "hot-paths")]
    public bool HotPaths { get; init; }
}
