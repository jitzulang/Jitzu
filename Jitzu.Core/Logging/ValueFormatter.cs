using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Jitzu.Core.Runtime;
using Jitzu.Core.Types;

namespace Jitzu.Core.Logging;

public static class ValueFormatter 
{
    public static string FormatTypeName(Value? value)
    {
        if (value is not { } v)
            return "None";

        return v.Kind switch
        {
            ValueKind.Null => "None",
            ValueKind.Int => "Int",
            ValueKind.Double => "Double",
            ValueKind.Bool => "Bool",
            ValueKind.Ref when v.Ref is string => "String",
            ValueKind.Ref => TrimGenericArity(v.Ref.GetType().Name),
            _ => v.Kind.ToString()
        };
    }

    private static string TrimGenericArity(string name) =>
        name.IndexOf('`') is var index and >= 0 ? name[..index] : name;

    public static string? Format(params object?[] objects)
    {
        switch (objects.Length)
        {
            case 0:
                return string.Empty;
            case 1:
                return Format(objects[0]);
        }

        using var ms = new MemoryStream();
        var jsonFormatter = new Utf8JsonWriter(ms);
        jsonFormatter.WriteStartArray();
        foreach (var o in objects)
        {
            if (o is null) jsonFormatter.WriteNullValue();
            else jsonFormatter.WriteRawValue(Format(o));
        }
        jsonFormatter.WriteEndArray();
        return jsonFormatter.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static string Format(object? o)
    {
        return o switch
        {
            Value v => v.Kind switch
            {
                ValueKind.Int => $"Value(Int: {v.I32})",
                ValueKind.Bool => $"Value(Bool: {v.B})",
                ValueKind.Double => $"Value(Double: {v.F64})",
                ValueKind.Ref => $"Value(Ref: {Format(v.Ref)})",
                _ => HandleNotImplemented(o) 
            },
            null => "None",
            string s => s,
            int i => i.ToString(),
            double d => d.ToString(CultureInfo.InvariantCulture),
            bool b => b.ToString(),
            IUnion union => union.Format(),
            Unit => "",
            DateOnly dt => dt.ToString("yyyy-MM-dd"),
            DateTimeOffset offset => offset.ToString(),
            TimeSpan timeSpan => timeSpan.ToString(),
            IFormattable f => f.ToString()!,
            JsonElement element => FormatJsonElement(element),
            UserFunction f => f.ToString(),
            ForeignFunction f => FormatMethod(f.MethodInfo),
            MethodInfo m => FormatMethod(m),
            _ => HandleNotImplemented(o)
        };
    }

    private static string FormatJsonElement(JsonElement element)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true
        });

        FormatJsonElement(element, writer);

        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void FormatJsonElement(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                FormatJsonObject(element, writer);
                break;

            case JsonValueKind.Array:
                FormatJsonArray(element, writer);
                break;

            case JsonValueKind.Undefined:
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteNumberValue(element.GetInt32());
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }
    }
    
    private static void FormatJsonObject(JsonElement element, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Undefined)
                continue;

            writer.WritePropertyName(prop.Name);
            FormatJsonElement(prop.Value, writer);
        }

        writer.WriteEndObject();
    }

    private static void FormatJsonArray(JsonElement element, Utf8JsonWriter writer)
    {
        writer.WriteStartArray();

        foreach (var child in element.EnumerateArray())
            FormatJsonElement(child, writer);

        writer.WriteEndArray();
    }

    public static string FormatMethod(MethodInfo methodInfo)
    {
        var parameters = methodInfo.GetParameters()
            .Select(p => $"{p.Name}: {p.ParameterType.Name}")
            .Join(", ");

        return $"{methodInfo.Name}({parameters}): {methodInfo.ReturnType.Name}";
    }

    private static string HandleNotImplemented(object o)
    {
        return o.ToString() ?? o.GetType().Name;
    }
}
