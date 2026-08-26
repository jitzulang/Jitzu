using System.Diagnostics;
using Jitzu.Shell;
using Jitzu.Shell.Core;

namespace Jitzu.Benchmarking;

internal static class HotPathBenchmarks
{
    public static async Task RunAsync(string? repositoryPath = null)
    {
        var results = new List<HotPathResult>();
        var repositoryRoot = repositoryPath is { Length: > 0 }
            ? Path.GetFullPath(repositoryPath)
            : FindRepositoryRoot(AppContext.BaseDirectory);
        if (!Directory.Exists(repositoryRoot))
            throw new DirectoryNotFoundException($"Benchmark repository was not found: '{repositoryRoot}'.");

        await MeasureAsync(results, "git/status-refresh", 2, 20,
            async () => _ = await GitStatusCache.GetGitStatusAsync(repositoryRoot));

        await using (var gitCache = new GitStatusCache(
                         TimeSpan.FromMinutes(1),
                         async (_, cancellationToken) =>
                         {
                             await Task.Delay(1, cancellationToken);
                             return new GitStatus(HasDirty: true);
                         }))
        {
            gitCache.GetGitStatus(repositoryRoot);
            for (var attempt = 0; attempt < 200 && !gitCache.GetGitStatus(repositoryRoot).HasDirty; attempt++)
                await Task.Delay(1);
            if (!gitCache.GetGitStatus(repositoryRoot).HasDirty)
                throw new TimeoutException("Timed out priming the prompt Git cache.");

            Measure(results, "prompt/git-status-cache-hit", 10, 1_000,
                () => _ = gitCache.GetGitStatus(repositoryRoot));
        }

        await MeasureAsync(results, "runtime/cold-expression", 2, 10, async () =>
        {
            var session = new ShellSession();
            var result = await session.ExecuteAsync("1 + 2");
            EnsureSuccessful(result);
        });

        var warmSession = new ShellSession();
        EnsureSuccessful(await warmSession.ExecuteAsync("1 + 2"));
        await MeasureAsync(results, "runtime/steady-expression", 10, 100, async () =>
            EnsureSuccessful(await warmSession.ExecuteAsync("1 + 2")));

        MeasureHistoryExpansion(results);
        await MeasureHistoryQueueAsync(results, 2_500);
        await MeasureHistoryPersistenceAsync(results, 100);
        await MeasureHistoryPersistenceAsync(results, 2_500);
        await MeasureHistoryPersistenceAsync(results, 10_000);
        Print(results);
    }

    private static async Task MeasureAsync(List<HotPathResult> results, string name, int warmups, int iterations,
        Func<Task> operation)
    {
        for (var i = 0; i < warmups; i++)
            await operation();

        var samples = new long[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await operation();
            samples[i] = Stopwatch.GetTimestamp() - start;
        }

        results.Add(CreateResult(name, samples, 1));
    }

    private static void Measure(List<HotPathResult> results, string name, int warmups, int iterations,
        Action operation)
    {
        for (var i = 0; i < warmups; i++)
            operation();

        var samples = new long[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            operation();
            samples[i] = Stopwatch.GetTimestamp() - start;
        }

        results.Add(CreateResult(name, samples, 1));
    }

    private static void MeasureHistoryExpansion(List<HotPathResult> results)
    {
        const int operationsPerSample = 10_000;
        var history = new HistoryManager(persist: false);
        history.Record("dotnet build Jitzu.slnx -c Release");

        for (var i = 0; i < 2; i++)
            RunBatch();

        var samples = new long[20];
        for (var i = 0; i < samples.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            RunBatch();
            samples[i] = Stopwatch.GetTimestamp() - start;
        }

        results.Add(CreateResult("repl/history-expansion-no-marker", samples, operationsPerSample));
        return;

        void RunBatch()
        {
            for (var i = 0; i < operationsPerSample; i++)
            {
                if (!HistoryExpansion.TryExpand("dotnet build", history, out _, out var error))
                    throw new InvalidOperationException(error);
            }
        }
    }

    private static async Task MeasureHistoryPersistenceAsync(List<HotPathResult> results, int entries)
    {
        const int iterations = 10;
        var benchmarkDirectory = Path.Combine(Path.GetTempPath(), $"jitzu-history-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(benchmarkDirectory);
        try
        {
            var samples = new long[iterations];
            for (var sample = 0; sample < iterations; sample++)
            {
                var historyPath = Path.Combine(benchmarkDirectory, $"history-{sample}.txt");
                var history = new HistoryManager(persist: true, historyPath);
                for (var i = 0; i < entries; i++)
                    history.Record($"command {i:D5} --with representative arguments");

                await history.WriteAsync("warmup command");
                var start = Stopwatch.GetTimestamp();
                await history.WriteAsync("measured command");
                samples[sample] = Stopwatch.GetTimestamp() - start;
            }

            results.Add(CreateResult($"history/durable-{entries}", samples, 1));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(benchmarkDirectory))
                File.Delete(file);
            Directory.Delete(benchmarkDirectory);
        }
    }

    private static async Task MeasureHistoryQueueAsync(List<HotPathResult> results, int entries)
    {
        const int iterations = 20;
        var benchmarkDirectory = Path.Combine(Path.GetTempPath(), $"jitzu-history-queue-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(benchmarkDirectory);
        try
        {
            var samples = new long[iterations];
            for (var sample = 0; sample < iterations; sample++)
            {
                var historyPath = Path.Combine(benchmarkDirectory, $"history-{sample}.txt");
                var history = new HistoryManager(persist: true, historyPath);
                for (var i = 0; i < entries; i++)
                    history.Record($"command {i:D5} --with representative arguments");

                var start = Stopwatch.GetTimestamp();
                history.QueueWrite("measured command");
                samples[sample] = Stopwatch.GetTimestamp() - start;
                await history.FlushAsync();
            }

            results.Add(CreateResult($"history/queue-{entries}", samples, 1));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(benchmarkDirectory))
                File.Delete(file);
            Directory.Delete(benchmarkDirectory);
        }
    }

    private static HotPathResult CreateResult(string name, long[] timestampSamples, int operationsPerSample)
    {
        var samples = timestampSamples
            .Select(ticks => ticks * 1_000_000d / Stopwatch.Frequency / operationsPerSample)
            .Order()
            .ToArray();
        return new HotPathResult(
            name,
            samples.Average(),
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            samples[^1]);
    }

    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(ordered.Length * percentile) - 1];

    private static void EnsureSuccessful(ExecutionResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException("Hot-path operation failed.", result.Error);
    }

    private static void Print(IEnumerable<HotPathResult> results)
    {
        Console.WriteLine("Hot-path latency (microseconds per operation)");
        Console.WriteLine($"{"Case",-36} {"Mean",10} {"Median",10} {"P95",10} {"Max",10}");
        foreach (var result in results)
            Console.WriteLine($"{result.Name,-36} {result.MeanUs,10:F2} {result.MedianUs,10:F2} {result.P95Us,10:F2} {result.MaxUs,10:F2}");
    }

    private static string FindRepositoryRoot(string startPath)
    {
        for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jitzu.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException($"Could not find Jitzu.slnx above '{startPath}'.");
    }

    private sealed record HotPathResult(string Name, double MeanUs, double MedianUs, double P95Us, double MaxUs);
}
