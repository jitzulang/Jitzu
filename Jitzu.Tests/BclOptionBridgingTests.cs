using Jitzu.Core.Types;
using Shouldly;

namespace Jitzu.Tests;

public class BclOptionBridgingTests
{
    [Test]
    public async Task NullableMethodReturn_NullValue_PatternMatchesAsNone()
    {
        const string varName = "JITZU_BRIDGE_TEST_DEFINITELY_NOT_SET_a8f3";
        Environment.SetEnvironmentVariable(varName, null);

        var source = $$"""
                       let v = Environment.GetEnvironmentVariable("{{varName}}")
                       let label = match v {
                           Some(s) => `got: {s}`,
                           None    => "missing",
                       }
                       print(label)
                       """;

        var output = await InterpreterTestHarness.RunAsync(source);
        output.ShouldBe("missing");
    }

    [Test]
    public async Task NullableMethodReturn_NonNullValue_PatternMatchesAsSome()
    {
        const string varName = "JITZU_BRIDGE_TEST_PRESENT_b21c";
        Environment.SetEnvironmentVariable(varName, "hello");
        try
        {
            var source = $$"""
                           let v = Environment.GetEnvironmentVariable("{{varName}}")
                           let label = match v {
                               Some(s) => `got: {s}`,
                               None    => "missing",
                           }
                           print(label)
                           """;

            var output = await InterpreterTestHarness.RunAsync(source);
            output.ShouldBe("got: hello");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Test]
    public async Task OptionString_PassedTo_NullableStringParameter_IsUnwrapped()
    {
        // args[0] is Option<String>; Path.GetDirectoryName takes string?.
        // The arg should unwrap Some(path) -> path; the path-separator-bearing return is wrapped as Option<String>.
        var input = Path.Combine("a", "b", "c");
        var expected = Path.GetDirectoryName(input);
        var source = $$"""
                       let label = match Path.GetDirectoryName(args[0]) {
                           Some(d) => `dir: {d}`,
                           None    => "no-dir",
                       }
                       print(label)
                       """;

        var output = await InterpreterTestHarness.RunAsync(source, [input]);
        output.ShouldBe($"dir: {expected}");
    }

    [Test]
    public async Task OptionString_FromIndexerOutOfBounds_PassedToBcl_RoundTripsAsNone()
    {
        // Out-of-bounds indexer returns None; passing to Path.GetDirectoryName(string?) should unwrap to null,
        // GetDirectoryName(null) returns null, re-wrapped as None.
        const string source = """
                              let label = match Path.GetDirectoryName(args[5]) {
                                  Some(_) => "got-some",
                                  None    => "got-none",
                              }
                              print(label)
                              """;

        var output = await InterpreterTestHarness.RunAsync(source, ["only"]);
        output.ShouldBe("got-none");
    }

    [Test]
    public void NullableProperty_OnBclType_SurfacesAsOption_DirectUnit()
    {
        // FileInfo.LinkTarget is a string? property — exercise the wrap helper directly.
        var prop = typeof(System.IO.FileInfo).GetProperty(nameof(System.IO.FileInfo.LinkTarget))!;
        OptionBridge.IsNullableSite(prop.PropertyType, prop).ShouldBeTrue();

        var noneResult = OptionBridge.WrapIfNullable(null, prop.PropertyType, prop);
        noneResult!.GetType().GetGenericTypeDefinition().ShouldBe(typeof(Option<>));
        ((IUnion)noneResult).Value.ShouldBeOfType<None>();

        var someResult = OptionBridge.WrapIfNullable("/some/target", prop.PropertyType, prop);
        someResult!.GetType().GetGenericTypeDefinition().ShouldBe(typeof(Option<>));
        ((IUnion)someResult).Value.ShouldBeOfType<Some<string>>();
    }

    [Test]
    public void NullableProperty_OnJitzuInternalType_DoesNotWrap()
    {
        // Some<T>.Value is typed T (unconstrained) → NRT reads as Nullable, but we must NOT
        // re-wrap it as Option<T> because we're already inside the Option/IUnion world.
        var prop = typeof(Some<string>).GetProperty(nameof(Some<string>.Value))!;
        OptionBridge.IsNullableSite(prop.PropertyType, prop).ShouldBeFalse();
    }

    [Test]
    public void OptionBridge_WrapsNullableValueType()
    {
        // int? → Option<Int>. Tests Nullable<T> handling in addition to NRT for refs.
        Type intQ = typeof(int?);
        var provider = new NoOpAttributeProvider();
        OptionBridge.IsNullableSite(intQ, provider).ShouldBeTrue();

        var wrappedSome = OptionBridge.WrapIfNullable(42, intQ, provider);
        wrappedSome!.GetType().GetGenericTypeDefinition().ShouldBe(typeof(Option<>));
        ((IUnion)wrappedSome).Value.ShouldBeOfType<Some<int>>();

        var wrappedNone = OptionBridge.WrapIfNullable(null, intQ, provider);
        wrappedNone!.GetType().GetGenericTypeDefinition().ShouldBe(typeof(Option<>));
        ((IUnion)wrappedNone).Value.ShouldBeOfType<None>();
    }

    [Test]
    public void OptionBridge_UnwrapOption()
    {
        var some = OptionBridge.MakeSome(typeof(string), "hi");
        OptionBridge.UnwrapOption(some).ShouldBe("hi");

        var none = OptionBridge.MakeNone(typeof(string));
        OptionBridge.UnwrapOption(none).ShouldBeNull();

        OptionBridge.UnwrapOption("not-an-option").ShouldBe("not-an-option");
        OptionBridge.UnwrapOption(null).ShouldBeNull();
    }
}

internal sealed class NoOpAttributeProvider : System.Reflection.ICustomAttributeProvider
{
    public object[] GetCustomAttributes(bool inherit) => [];
    public object[] GetCustomAttributes(Type attributeType, bool inherit) => [];
    public bool IsDefined(Type attributeType, bool inherit) => false;
}
