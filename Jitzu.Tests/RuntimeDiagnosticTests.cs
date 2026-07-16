using Jitzu.Core;
using Jitzu.Core.Language;
using Jitzu.Core.Runtime;
using Jitzu.Core.Runtime.Compilation;
using Shouldly;

namespace Jitzu.Tests;

public class RuntimeDiagnosticTests
{
    [Test]
    public async Task RuntimeFailure_HidesVmStateWithoutDebug()
    {
        var (error, diagnostic) = await RunFailure(debug: false);

        diagnostic.ShouldBeEmpty();
        error.Message.ShouldContain("types 'Bool' and 'Bool'");
        error.Message.ShouldNotContain("Value(");
    }

    [Test]
    public async Task RuntimeFailure_ShowsVmStateInDebugMode()
    {
        var (error, diagnostic) = await RunFailure(debug: true);

        diagnostic.ShouldContain("Error occurred at IP:");
        diagnostic.ShouldContain("Stack:");
        diagnostic.ShouldContain("FrameTop=");
        error.Message.ShouldContain("types 'Bool' and 'Bool'");
    }

    [Test]
    public void UnsupportedOperation_DoesNotExposeReferenceValue()
    {
        var left = Value.FromRef(new SensitiveValue("top-secret"));

        var error = Should.Throw<OperationNotSupportedException>(
            () => BinaryExpressionEvaluator.Add(left, Value.FromInt(1)));

        error.Message.ShouldContain("types 'SensitiveValue' and 'Int'");
        error.Message.ShouldNotContain("top-secret");
        error.Message.ShouldNotContain("Value(");
    }

    private static async Task<(JitzuException Error, string Diagnostic)> RunFailure(bool debug)
    {
        var ast = new ScriptExpression
        {
            Body = Parser.Parse("diagnostic.jz", "true + false")
        };

        var program = await ProgramBuilder.Build(ast);
        ast = new SemanticAnalyser(program).AnalyseScript(ast);
        var script = new ByteCodeCompiler(program).Compile(ast.Body);

        using var writer = new StringWriter { NewLine = "\n" };
        GlobalFunctions.SetOutput(writer);
        try
        {
            var interpreter = new ByteCodeInterpreter(program, script, [], debug);
            JitzuException? error = null;
            try
            {
                interpreter.Evaluate();
            }
            catch (JitzuException ex)
            {
                error = ex;
            }

            error.ShouldNotBeNull();
            return (error, writer.ToString());
        }
        finally
        {
            GlobalFunctions.SetOutput(null);
        }
    }

    private sealed record SensitiveValue(string Value)
    {
        public override string ToString() => Value;
    }
}
