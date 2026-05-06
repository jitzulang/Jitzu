using Shouldly;
using Jitzu.Core;
using Jitzu.Core.Language;

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

    [Test]
    public void Bang_AppliesToFullPostfixFunctionCall()
    {
        var expressions = Parser.Parse("", "!Directory.Exists(\"definitely-not-here\")");

        var unary = expressions.Single().ShouldBeOfType<UnaryExpression>();
        unary.Operator.ShouldBe("!");

        var call = unary.Operand.ShouldBeOfType<FunctionCallExpression>();
        var member = call.Identifier.ShouldBeOfType<SimpleMemberAccessExpression>();
        member.Object.ShouldBeOfType<IdentifierLiteral>().Name.ShouldBe("Directory");
        member.Property.ShouldBeOfType<IdentifierLiteral>().Name.ShouldBe("Exists");
    }
}
