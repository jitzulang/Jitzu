using Shouldly;

namespace Jitzu.Tests;

public class BlockBodySemicolonTests
{
    [Test]
    public async Task FunctionBody_StatementSemicolonExpression_ParsesAndRuns()
    {
        const string source = """
                              fun go() {
                                  print("a"); print("b")
                              }
                              go()
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("a\nb");
    }

    [Test]
    public async Task MatchArm_BlockWithSemicolon_ParsesAndRuns()
    {
        const string source = """
                              fun side() { print("ran side") }
                              let x = 1
                              let result = match x {
                                  1 => { side(); "one" },
                                  _ => "other",
                              }
                              print(result)
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("ran side\none");
    }
}
