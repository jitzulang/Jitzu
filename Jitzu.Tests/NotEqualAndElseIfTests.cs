using Shouldly;

namespace Jitzu.Tests;

public class NotEqualAndElseIfTests
{
    [Test]
    public async Task NotEqual_Ints()
    {
        const string source = """
                              if 1 != 2 { print("yes") }
                              if 2 != 2 { print("no") }
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("yes");
    }

    [Test]
    public async Task NotEqual_GreaterThanOrEqual()
    {
        const string source = """
                              if 5 >= 5 { print("ok") }
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("ok");
    }

    [Test]
    public async Task ElseIf_ChainsCorrectly()
    {
        const string source = """
                              let x = 2
                              if x == 1 {
                                  print("one")
                              } else if x == 2 {
                                  print("two")
                              } else {
                                  print("other")
                              }
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("two");
    }
}
