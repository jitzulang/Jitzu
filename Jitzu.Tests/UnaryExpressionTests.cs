using Shouldly;

namespace Jitzu.Tests;

public class UnaryExpressionTests
{
    [Test]
    public async Task Bang_NegatesTrue()
    {
        const string source = """
                              let x = true
                              print("" + !x)
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("False");
    }

    [Test]
    public async Task Bang_NegatesFalse()
    {
        const string source = """
                              let x = false
                              print("" + !x)
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("True");
    }

    [Test]
    public async Task Bang_DoubleNegation()
    {
        const string source = """
                              let x = true
                              print("" + !!x)
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("True");
    }

    [Test]
    public async Task UnaryMinus_NegatesInt()
    {
        const string source = """
                              let x = 5
                              print(-x)
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("-5");
    }
}
