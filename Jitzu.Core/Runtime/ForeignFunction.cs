using System.Reflection;
using Jitzu.Core.Logging;
using Jitzu.Core.Types;

namespace Jitzu.Core.Runtime;

public class ForeignFunction(MethodInfo methodInfo) : IShellFunction
{
    public MethodInfo MethodInfo { get; } = methodInfo;

    public ForeignFunction(Delegate @delegate) : this(@delegate.Method)
    {
    }

    public object? Invoke(Value[] args) => InvokeMethodInfo(MethodInfo, args);

    public static object? InvokeMethodInfo(MethodInfo methodInfo, Span<Value> args)
    {
        Span<ParameterInfo> parameters = methodInfo.GetParameters();
        var cursor = 0;

        try
        {
            object? instance = null;
            if (!methodInfo.IsStatic)
            {
                instance = args[0].AsObject();
                cursor++;
            }

            // Indices into parameters[]: cursor is also the args[] index, but the
            // parameter index is (cursor - instanceOffset).
            var instanceOffset = methodInfo.IsStatic ? 0 : 1;
            Span<object?> arguments = new object?[parameters.Length];

            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                if (parameter.HasDefaultValue)
                    arguments[index] = parameter.DefaultValue;
            }

            for (; cursor < args.Length; cursor++)
            {
                var paramIdx = cursor - instanceOffset;
                if (paramIdx >= parameters.Length)
                    break;

                var parameter = parameters[paramIdx];

                if (parameter.IsOptional)
                    continue;

                if (paramIdx == parameters.Length - 1 && parameter.ParameterType.IsArray)
                {
                    var elementType = parameter.ParameterType.GetElementType()!;
                    var length = args.Length - cursor;

                    var array = Array.CreateInstance(elementType, length);
                    for (var j = 0; j < length; j++)
                        array.SetValue(args[cursor + j].AsObject(), j);

                    arguments[paramIdx] = array;
                    break;
                }

                arguments[paramIdx] = CoerceArgument(args[cursor].AsObject(), parameter.ParameterType);
            }

            return methodInfo.Invoke(instance, arguments.ToArray());
        }
        catch (TargetInvocationException ex)
        {
            // Unwrap inner exceptions from reflection calls
            return new Err<string>(
                $"Error running method: {methodInfo.Name}: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            return new Err<string>($"Error running method: {methodInfo.Name}: {ex.Message}");
        }
    }

    private static object? CoerceArgument(object? value, Type parameterType)
    {
        // Pass-through if the parameter actually wants an Option<T>.
        if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(Option<>))
            return value;

        // Otherwise unwrap any Option<T> to its inner value (or null).
        if (value is not null
            && value.GetType() is { IsGenericType: true } vt
            && vt.GetGenericTypeDefinition() == typeof(Option<>))
            return OptionBridge.UnwrapOption(value);

        return value;
    }

    public override string ToString() => ValueFormatter.FormatMethod(MethodInfo);
}