namespace Jitzu.Core.Runtime;

public sealed record ProcessOutput(string Stdout, string Stderr, int ExitCode)
{
    public override string ToString() => Stdout;
}
