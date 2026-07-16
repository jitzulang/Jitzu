using Jitzu.Core;
using Shouldly;

namespace Jitzu.Tests;

public class FunctionParameterDiagnosticTests
{
    [Test]
    public void UntypedParameter_ExplainsRequiredAnnotation()
    {
        const string source = """
                              fun double(n) {
                                  return n * 2
                              }
                              """;

        var error = Should.Throw<MissingParameterTypeAnnotationException>(
            () => Parser.Parse("script.jz", source));

        error.Message.ShouldBe(
            "S002: Syntax Error - Function parameter 'n' requires a type annotation. " +
            "Example: fun double(n: Int) { ... }");
        error.Location.Start.Line.ShouldBe(1);
        error.Location.Start.Column.ShouldBe(12);
    }

    [Test]
    public void LaterUntypedParameter_NamesTheCorrectParameter()
    {
        const string source = "fun pair(first: Int, second) { first }";

        var error = Should.Throw<MissingParameterTypeAnnotationException>(
            () => Parser.Parse("script.jz", source));

        error.Message.ShouldContain("parameter 'second'");
        error.Message.ShouldContain("fun pair(second: Int)");
    }
}
