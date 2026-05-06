using System.Diagnostics;
using System.Runtime.CompilerServices;
using Jitzu.Core.Logging;
using Jitzu.Core.Types;

namespace Jitzu.Core.Runtime;

public static class GlobalFunctions
{
    private static readonly AsyncLocal<TextWriter?> OutputOverride = new();

    /// <summary>
    /// Gets the current output writer. Returns the async-local override if set, otherwise Console.Out.
    /// </summary>
    public static TextWriter Output => OutputOverride.Value ?? Console.Out;

    /// <summary>
    /// Redirects print output to the given writer for the current async context.
    /// Call with null to restore default Console.Out behavior.
    /// </summary>
    public static void SetOutput(TextWriter? writer) => OutputOverride.Value = writer;

    public static object Or(this object instance, object fallback)
    {
        return instance switch
        {
            ICanFallback f => f.Fallback(fallback),
            _ => instance
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void PrintStatic(params object?[] objects)
    {
        var output = Output;
        switch (objects.Length)
        {
            case 0:
                output.WriteLine();
                break;
            case 1:
                output.WriteLine(ValueFormatter.Format(objects[0]));
                break;
            default:
                output.WriteLine(ValueFormatter.Format(objects));
                break;
        }
    }

    public static int RandStatic(object?[] objects)
    {
        return objects switch
        {
            [int max] => Random.Shared.Next(max),
            [int max, int min] => Random.Shared.Next(max, min),
            _ => Random.Shared.Next(),
        };
    }

    public static string FirstStatic(string input)
    {
        var lines = SplitLines(input);
        return lines.Length > 0 ? lines[0] : "";
    }

    public static string LastStatic(string input)
    {
        var lines = SplitLines(input);
        return lines.Length > 0 ? lines[^1] : "";
    }

    public static string NthStatic(string input, int index)
    {
        var lines = SplitLines(input);
        return index >= 0 && index < lines.Length ? lines[index] : "";
    }

    public static string GrepStatic(string input, string pattern)
    {
        var lines = SplitLines(input);
        var matched = lines.Where(line => line.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        return string.Join('\n', matched);
    }

    public static ProcessOutput RunStatic(string file, object? argsObj)
    {
        var args = CoerceArgs(argsObj);
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
                return new ProcessOutput("", $"Failed to start: {file}", -1);

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            return new ProcessOutput(stdout, stderr, p.ExitCode);
        }
        catch (Exception ex)
        {
            return new ProcessOutput("", ex.Message, -1);
        }
    }

    private static List<string> CoerceArgs(object? argsObj) => argsObj switch
    {
        null => new List<string>(),
        string s => new List<string> { s },
        IEnumerable<object?> objs => objs.Select(Stringify).ToList(),
        System.Collections.IEnumerable e => e.Cast<object?>().Select(Stringify).ToList(),
        _ => new List<string> { Stringify(argsObj) }
    };

    private static string Stringify(object? o) => o switch
    {
        null => "",
        Value v => v.AsObject()?.ToString() ?? "",
        _ => o.ToString() ?? ""
    };

    private static string[] SplitLines(string input)
    {
        return input.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
    }
}