using Jitzu.Core.Runtime;
using Shouldly;

namespace Jitzu.Tests;

public class BinaryEqualityTests
{
    [Test]
    public void Equal_BoolBool_True()
    {
        BinaryExpressionEvaluator.Equal(Value.FromBool(true), Value.FromBool(true)).B.ShouldBeTrue();
        BinaryExpressionEvaluator.Equal(Value.FromBool(false), Value.FromBool(false)).B.ShouldBeTrue();
    }

    [Test]
    public void Equal_BoolBool_False()
    {
        BinaryExpressionEvaluator.Equal(Value.FromBool(true), Value.FromBool(false)).B.ShouldBeFalse();
    }

    [Test]
    public void Equal_IntDouble_NumericEquality()
    {
        BinaryExpressionEvaluator.Equal(Value.FromInt(2), Value.FromDouble(2.0)).B.ShouldBeTrue();
        BinaryExpressionEvaluator.Equal(Value.FromDouble(2.0), Value.FromInt(2)).B.ShouldBeTrue();
        BinaryExpressionEvaluator.Equal(Value.FromDouble(2.0), Value.FromDouble(2.0)).B.ShouldBeTrue();
        BinaryExpressionEvaluator.Equal(Value.FromDouble(2.0), Value.FromDouble(3.0)).B.ShouldBeFalse();
    }

    [Test]
    public void Equal_StringString_StructuralEquality()
    {
        BinaryExpressionEvaluator.Equal(Value.FromRef("a"), Value.FromRef("a")).B.ShouldBeTrue();
        BinaryExpressionEvaluator.Equal(Value.FromRef("a"), Value.FromRef("b")).B.ShouldBeFalse();
    }

    [Test]
    public async Task IfBoolEqTrue_TakesThenBranch()
    {
        const string source = """
                              let r = true
                              if r == true { print("yes") } else { print("no") }
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("yes");
    }

    [Test]
    public async Task IfBoolEqFalse_TakesThenBranch()
    {
        const string source = """
                              let r = false
                              if r == false { print("yes") } else { print("no") }
                              """;
        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("yes");
    }
}
