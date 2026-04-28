using Jitzu.Core;
using Jitzu.Core.Language;
using Shouldly;

namespace Jitzu.Tests;

public class GenericReturnTypeTests
{
    [Test]
    public void Parse_FunctionWithGenericReturnType_DoesNotThrow()
    {
        const string source = """
                              fun divide(a: Double, b: Double): Result<Double, String> {
                                  return Ok(a)
                              }
                              """;

        Should.NotThrow(() => Parser.Parse("", source));
    }

    [Test]
    public async Task FullPipeline_FunctionWithGenericReturnType_Compiles()
    {
        const string source = """
                              fun id(a: Int): Result<Int, String> {
                                  return Ok(a)
                              }
                              print("ok")
                              """;

        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("ok");
    }
}
