using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Jitzu.Shell.Infrastructure.Logging;

internal static class StartupProfiler
{
    private static readonly string? Mode = Environment.GetEnvironmentVariable("JITZU_STARTUP_PROFILE");
    private static readonly bool Enabled = Mode is "1" or "terminal";
    private static readonly bool EventPipeRequested = Environment.GetEnvironmentVariable("JITZU_STARTUP_EVENTPIPE") == "1";
    private static readonly long Start = Stopwatch.GetTimestamp();
    private static readonly HashSet<string>? Seen = Enabled || EventPipeRequested ? [] : null;

    public static void Mark(string stage)
    {
        var eventPipeEnabled = EventPipeRequested && StartupEventSource.Log.IsEnabled();
        if (!Enabled && !eventPipeEnabled)
            return;

        lock (Seen!)
        {
            if (!Seen.Add(stage))
                return;
            var elapsed = Stopwatch.GetElapsedTime(Start).TotalMilliseconds;
            if (eventPipeEnabled)
                StartupEventSource.Log.Phase(stage, elapsed);
            if (Mode == "terminal")
                Console.Error.Write($"\e]1337;JitzuStartup={elapsed:F3};{stage}\a");
            else if (Mode == "1")
                Console.Error.WriteLine($"JITZU_STARTUP {elapsed:F3} {stage}");
        }
    }
}

[EventSource(Name = "Jitzu-Startup")]
internal sealed class StartupEventSource : EventSource
{
    public static readonly StartupEventSource Log = new();
    private StartupEventSource() { }

    [Event(1, Level = EventLevel.Informational)]
    public void Phase(string stage, double managedElapsedMs) => WriteEvent(1, stage, managedElapsedMs);
}
