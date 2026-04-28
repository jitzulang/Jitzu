using System.Reflection;

namespace Jitzu.Core.Types;

/// <summary>
/// Coerces values across the BCL boundary. Inside Jitzu, null does not exist —
/// nullable BCL members surface as <see cref="Option{T}"/>. Going the other way,
/// an <see cref="Option{T}"/> argument is unwrapped to a raw value or null when
/// passed to a parameter typed <c>T</c> or <c>T?</c>.
/// </summary>
public static class OptionBridge
{
    /// <summary>
    /// Wraps a raw BCL value into <see cref="Option{T}"/> when the declared site is nullable.
    /// Returns the value unchanged otherwise.
    /// </summary>
    public static object? WrapIfNullable(object? value, Type declaredType, ICustomAttributeProvider site, NullabilityInfoContext? ctx = null)
    {
        if (!IsNullableSite(declaredType, site, ctx))
            return value;

        var elementType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        return value is null ? MakeNone(elementType) : MakeSome(elementType, value);
    }

    /// <summary>
    /// Unwraps an <see cref="Option{T}"/> argument to its inner value (or null).
    /// Returns the value unchanged if it isn't an Option.
    /// </summary>
    public static object? UnwrapOption(object? value)
    {
        if (value is null)
            return null;

        var t = value.GetType();
        if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(Option<>))
            return value;

        var innerProp = t.GetProperty(nameof(Option<int>.Value))!;
        var inner = innerProp.GetValue(value);

        return inner switch
        {
            null => null,
            None => null,
            var some => some.GetType().GetProperty(nameof(Some<int>.Value))!.GetValue(some),
        };
    }

    /// <summary>
    /// True if the declared site is <c>Nullable&lt;T&gt;</c>, or a reference type with
    /// <see cref="NullableAttribute"/>/<see cref="NullableContextAttribute"/> marking it nullable.
    /// </summary>
    public static bool IsNullableSite(Type declaredType, ICustomAttributeProvider site, NullabilityInfoContext? ctx = null)
    {
        if (Nullable.GetUnderlyingType(declaredType) is not null)
            return true;

        if (declaredType.IsValueType)
            return false;

        // Skip wrap on Jitzu-internal union members (Some<T>.Value, Option<T>.Value, etc).
        // Their declared types resolve as Nullable due to unconstrained generic params,
        // but we don't want to re-wrap them — they're already in the Option/IUnion world.
        var declaringType = (site as MemberInfo)?.DeclaringType;
        if (declaringType is not null && IsJitzuUnionType(declaringType))
            return false;

        return site switch
        {
            PropertyInfo p   => (ctx ?? new NullabilityInfoContext()).Create(p).ReadState   == NullabilityState.Nullable,
            FieldInfo f      => (ctx ?? new NullabilityInfoContext()).Create(f).ReadState   == NullabilityState.Nullable,
            ParameterInfo pa => (ctx ?? new NullabilityInfoContext()).Create(pa).ReadState  == NullabilityState.Nullable,
            _                => false,
        };
    }

    private static bool IsJitzuUnionType(Type t)
    {
        if (typeof(IUnion).IsAssignableFrom(t))
            return true;

        var def = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
        return def == typeof(Some<>)
            || def == typeof(Ok<>)
            || def == typeof(Err<>)
            || def == typeof(None);
    }

    public static object MakeSome(Type element, object value)
    {
        var someType   = typeof(Some<>).MakeGenericType(element);
        var optionType = typeof(Option<>).MakeGenericType(element);
        var some       = Activator.CreateInstance(someType, value)!;
        return Activator.CreateInstance(optionType, some)!;
    }

    public static object MakeNone(Type element)
    {
        var optionType = typeof(Option<>).MakeGenericType(element);
        return Activator.CreateInstance(optionType, None.Instance)!;
    }
}
