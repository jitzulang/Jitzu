using Shouldly;

namespace Jitzu.Tests;

public class Issue15RegressionTests
{
    [Test]
    public async Task MatchArm_BlockBody_Runs()
    {
        const string source = """
                              let result = match 1 {
                                  1 => { print("side effect"); "matched" },
                                  _ => "other",
                              }
                              print(result)
                              """;

        var output = await InterpreterTestHarness.RunAsync(source);

        output.ShouldBe("side effect\nmatched");
    }

    [Test]
    public async Task Result_CanBeMatchedAndTryUnwrapped()
    {
        const string source = """
                              fun value(): Result<Int, String> {
                                  return Ok(21)
                              }

                              let result = value()
                              let unwrapped = try result
                              let matched = match result {
                                  Ok(_) => 1,
                                  Err(_) => 0,
                              }
                              print(unwrapped)
                              print(matched)
                              """;

        var output = await InterpreterTestHarness.RunAsync(source);

        output.ShouldBe("21\n1");
    }

    [Test]
    public async Task Result_ErrVariant_IsWrappedAndMatched()
    {
        const string source = """
                              fun failure(): Result<Int, String> {
                                  return Err("nope")
                              }

                              let result = failure()
                              let message = match result {
                                  Ok(_) => "ok",
                                  Err(_) => "error",
                              }
                              print(message)
                              """;

        var output = await InterpreterTestHarness.RunAsync(source);

        output.ShouldBe("error");
    }

    [Test]
    public async Task Option_VariantConstructedInsideFunction_IsWrappedAndMatched()
    {
        const string source = """
                              fun maybe(): Option<Int> {
                                  return Some(7)
                              }

                              let result = maybe()
                              let value = match result {
                                  Some(_) => 1,
                                  None => 0,
                              }
                              print(value)
                              """;

        var output = await InterpreterTestHarness.RunAsync(source);

        output.ShouldBe("1");
    }

    [Test]
    public async Task BclProcessAndEnumInstanceMethods_Run()
    {
        const string source = """
                              let process = Process.GetCurrentProcess()
                              print(process.Id > 0)
                              print(FileAttributes.ReadOnly.HasFlag(FileAttributes.ReadOnly))
                              """;

        var output = await InterpreterTestHarness.RunAsync(source);

        output.ShouldBe("True\nTrue");
    }

    [Test]
    public async Task ForIn_StringArrayLiteral_Runs()
    {
        const string source = """
                              for item in ["a", "b"] {
                                  print(item)
                              }
                              """;

        var output = await InterpreterTestHarness.RunAsync(source);

        output.ShouldBe("a\nb");
    }
}
