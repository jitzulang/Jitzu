using Jitzu.Core.Runtime;
using Shouldly;

namespace Jitzu.Tests;

public class RunFunctionTests
{
    [Test]
    public void RunStatic_ReturnsStdoutStderrAndExitCode()
    {
        var output = GlobalFunctions.RunStatic("dotnet", new[] { "--version" });

        output.ExitCode.ShouldBe(0);
        output.Stdout.Trim().ShouldNotBeEmpty();
        output.Stderr.ShouldBe("");
        output.ToString().ShouldBe(output.Stdout);
    }

    [Test]
    public void RunStatic_MissingExecutable_ReturnsFailureResult()
    {
        var output = GlobalFunctions.RunStatic("jitzu-missing-executable-0000", null);

        output.ExitCode.ShouldBe(-1);
        output.Stdout.ShouldBe("");
        output.Stderr.ShouldNotBeEmpty();
    }
}
