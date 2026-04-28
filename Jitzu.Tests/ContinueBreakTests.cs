using Shouldly;

namespace Jitzu.Tests;

public class ContinueBreakTests
{
    [Test]
    public async Task Continue_InRangeFor_SkipsRest()
    {
        const string source = """
                              for i in 0..5 {
                                  if i == 2 { continue }
                                  print(i)
                              }
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("0\n1\n3\n4");
    }

    [Test]
    public async Task Break_InRangeFor_ExitsLoop()
    {
        const string source = """
                              for i in 0..10 {
                                  if i == 3 { break }
                                  print(i)
                              }
                              print("done")
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("0\n1\n2\ndone");
    }

    [Test]
    public async Task Continue_InCollectionFor_SkipsRest()
    {
        const string source = """
                              let xs = [1, 2, 3, 4]
                              for x in xs {
                                  if x == 3 { continue }
                                  print(x)
                              }
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("1\n2\n4");
    }

    [Test]
    public async Task Break_InWhile_ExitsLoop()
    {
        const string source = """
                              let mut i = 0
                              while i < 10 {
                                  if i == 4 { break }
                                  print(i)
                                  i++
                              }
                              print("done")
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("0\n1\n2\n3\ndone");
    }

    [Test]
    public async Task Continue_OutsideLoop_FailsAtCompile()
    {
        const string source = """
                              continue
                              """;
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await InterpreterTestHarness.RunAsync(source));
    }
}
