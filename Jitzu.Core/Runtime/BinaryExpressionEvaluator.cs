using System.Globalization;
using System.Runtime.CompilerServices;
using Jitzu.Core.Logging;

namespace Jitzu.Core.Runtime;

public static class BinaryExpressionEvaluator
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value NotEqual(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Int, ValueKind.Int) => Value.FromBool(a.I32 != b.I32),
        (ValueKind.Int, ValueKind.Double) => Value.FromBool(a.I32 != b.F64),
        (ValueKind.Double, ValueKind.Int) => Value.FromBool(a.F64 != b.I32),
        (ValueKind.Double, ValueKind.Double) => Value.FromBool(a.F64 != b.F64),
        (ValueKind.Bool, ValueKind.Bool) => Value.FromBool(a.B != b.B),
        (ValueKind.Null, ValueKind.Null) => Value.FromBool(false),
        (ValueKind.Null, _) or (_, ValueKind.Null) => Value.FromBool(true),
        (ValueKind.Ref, ValueKind.Ref) => Value.FromBool(!Equals(a.Ref, b.Ref)),
        _ => Value.FromBool(true)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Equal(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Int, ValueKind.Int) => Value.FromBool(a.I32 == b.I32),
        (ValueKind.Int, ValueKind.Double) => Value.FromBool(a.I32 == b.F64),
        (ValueKind.Double, ValueKind.Int) => Value.FromBool(a.F64 == b.I32),
        (ValueKind.Double, ValueKind.Double) => Value.FromBool(a.F64 == b.F64),
        (ValueKind.Bool, ValueKind.Bool) => Value.FromBool(a.B == b.B),
        (ValueKind.Null, ValueKind.Null) => Value.True,
        (ValueKind.Null, _) or (_, ValueKind.Null) => Value.False,
        (ValueKind.Ref, ValueKind.Ref) => Value.FromBool(Equals(a.Ref, b.Ref)),
        _ => Value.False
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Add(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Int, ValueKind.Int) => Value.FromInt(a.I32 + b.I32),
        (ValueKind.Int, ValueKind.Double) => Value.FromDouble(a.I32 + b.F64),
        (ValueKind.Double, ValueKind.Int) => Value.FromDouble(a.F64 + b.I32),
        (ValueKind.Double, ValueKind.Double) => Value.FromDouble(a.F64 + b.F64),
        (ValueKind.Ref, _) or (_, ValueKind.Ref) => ConcatIfString(a, b),
        _ => Throw("add", a, b)
    };

    // String concatenation for `+`. Auto-stringifies primitives on the other
    // side (int/double/bool) so `"x = " + 42` works. Non-string Ref values
    // still throw — we don't want silent ToString() on arbitrary objects.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Value ConcatIfString(Value a, Value b)
    {
        if (a.Kind == ValueKind.Ref && a.Ref is string sa && TryStringify(b, out var tb))
            return Value.FromRef(string.Concat(sa, tb));

        if (b.Kind == ValueKind.Ref && b.Ref is string sb && TryStringify(a, out var ta))
            return Value.FromRef(string.Concat(ta, sb));

        return Throw("add", a, b);
    }

    private static bool TryStringify(Value v, out string result)
    {
        switch (v.Kind)
        {
            case ValueKind.Int:
                result = v.I32.ToString(CultureInfo.InvariantCulture);
                return true;
            case ValueKind.Double:
                result = v.F64.ToString(CultureInfo.InvariantCulture);
                return true;
            case ValueKind.Bool:
                result = v.B.ToString();
                return true;
            case ValueKind.Ref when v.Ref is string s:
                result = s;
                return true;
            default:
                result = string.Empty;
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Sub(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Int, ValueKind.Int) => Value.FromInt(a.I32 - b.I32),
        (ValueKind.Int, ValueKind.Double) => Value.FromDouble(a.I32 - b.F64),
        (ValueKind.Double, ValueKind.Int) => Value.FromDouble(a.F64 - b.I32),
        (ValueKind.Double, ValueKind.Double) => Value.FromDouble(a.F64 - b.F64),
        _ => Throw("sub", a, b)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Mul(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Int, ValueKind.Int) => Value.FromInt(a.I32 * b.I32),
        (ValueKind.Int, ValueKind.Double) => Value.FromDouble(a.I32 * b.F64),
        (ValueKind.Double, ValueKind.Int) => Value.FromDouble(a.F64 * b.I32),
        (ValueKind.Double, ValueKind.Double) => Value.FromDouble(a.F64 * b.F64),
        _ => Throw("mul", a, b)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Div(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Int, ValueKind.Int) => Value.FromInt(a.I32 / b.I32),
        (ValueKind.Int, ValueKind.Double) => Value.FromDouble(a.I32 / b.F64),
        (ValueKind.Double, ValueKind.Int) => Value.FromDouble(a.F64 / b.I32),
        (ValueKind.Double, ValueKind.Double) => Value.FromDouble(a.F64 / b.F64),
        _ => Throw("div", a, b)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Mod(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Int, ValueKind.Int) => Value.FromInt(a.I32 % b.I32),
        (ValueKind.Int, ValueKind.Double) => Value.FromDouble(a.I32 % b.F64),
        (ValueKind.Double, ValueKind.Int) => Value.FromDouble(a.F64 % b.I32),
        (ValueKind.Double, ValueKind.Double) => Value.FromDouble(a.F64 % b.F64),
        _ => Throw("mod", a, b)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value LessThan(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Int, ValueKind.Int) => Value.FromBool(a.I32 < b.I32),
        (ValueKind.Int, ValueKind.Double) => Value.FromBool(a.I32 < b.F64),
        (ValueKind.Double, ValueKind.Int) => Value.FromBool(a.F64 < b.I32),
        (ValueKind.Double, ValueKind.Double) => Value.FromBool(a.F64 < b.F64),
        _ => Throw("lt", a, b)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value LessThanOrEqual(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Int, ValueKind.Int) => Value.FromBool(a.I32 <= b.I32),
        (ValueKind.Int, ValueKind.Double) => Value.FromBool(a.I32 <= b.F64),
        (ValueKind.Double, ValueKind.Int) => Value.FromBool(a.F64 <= b.I32),
        (ValueKind.Double, ValueKind.Double) => Value.FromBool(a.F64 <= b.F64),
        _ => Throw("lte", a, b)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value GreaterThan(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Int, ValueKind.Int) => Value.FromBool(a.I32 > b.I32),
        (ValueKind.Int, ValueKind.Double) => Value.FromBool(a.I32 > b.F64),
        (ValueKind.Double, ValueKind.Int) => Value.FromBool(a.F64 > b.I32),
        (ValueKind.Double, ValueKind.Double) => Value.FromBool(a.F64 > b.F64),
        _ => Throw("gt", a, b)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value GreaterThanOrEqual(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Int, ValueKind.Int) => Value.FromBool(a.I32 >= b.I32),
        (ValueKind.Int, ValueKind.Double) => Value.FromBool(a.I32 >= b.F64),
        (ValueKind.Double, ValueKind.Int) => Value.FromBool(a.F64 >= b.I32),
        (ValueKind.Double, ValueKind.Double) => Value.FromBool(a.F64 >= b.F64),
        _ => Throw("gte", a, b)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value BitwiseOr(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Int, ValueKind.Int) => Value.FromInt(a.I32 | b.I32),
        _ => Throw("bitwise_or", a, b)
    };

    // Helper to keep exception instantiation out of the mainline JIT path
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Value Throw(string op, Value a, Value b)
    {
        throw new OperationNotSupportedException(op, a, b);
    }
}

public class OperationNotSupportedException(string op, Value left, Value? right) : Exception(
    $"Operation {op} not supported for types '{ValueFormatter.FormatTypeName(left)}' and '{ValueFormatter.FormatTypeName(right)}'");
