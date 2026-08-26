using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Jitzu.Benchmarking.Addons;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jitzu.Benchmarking.Benchmarks;

public class Benchmark
{
    private readonly string _directoryName;
    private readonly BenchmarkArgs _args;
    private readonly BenchmarkConfig _config;
    private readonly string _repositoryRoot;

    public Benchmark(string directoryName, BenchmarkArgs args)
    {
        _directoryName = directoryName;
        _args = args;
        _repositoryRoot = FindRepositoryRoot(directoryName);

        var configPath = Path.Combine(directoryName, "config.json");
        _config = File.Exists(configPath)
            ? JsonSerializer.Deserialize<BenchmarkConfig>(File.ReadAllText(configPath))!
            : new BenchmarkConfig();
    }

    public async Task RunAsync(List<RunResult> results)
    {
        var scripts = Directory.GetFiles(_directoryName);
        var disposables = new List<IDisposable>();

        if (_config.AddOns?.WebServer is { } webServerAddon)
        {
            var webAppBuilder = WebApplication.CreateBuilder();
            webAppBuilder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(webServerAddon.Port));
            webAppBuilder.Services.AddRouting();
            var webApp = webAppBuilder.Build();
            webApp.UseRouting();
            webApp.MapGet("/Hello/{name}", (string name) => Results.Json(
                new TestRecord
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Something = Random.Shared.Next()
                }));

            Console.WriteLine("Starting webserver");
            await webApp.StartAsync();
            disposables.Add(webApp);
        }

        if (_config.Runs is { Length: > 0 } runs)
        {
            foreach (var run in runs)
            foreach (var script in scripts)
                await RunScriptAsync(script, _config.Iterations, results, [run.ToString(), .._config.Args]);
        }
        else
        {
            foreach (var script in scripts)
                await RunScriptAsync(script, _config.Iterations, results, _config.Args);
        }

        foreach (var disposable in disposables)
            disposable.Dispose();
    }

    private async Task RunScriptAsync(
        string script,
        int iterations,
        List<RunResult> results,
        params string[] args)
    {
        var scriptName = Path.GetFileName(script);
        var extension = scriptName.Split('.').Last();
        if (!_args.Extensions.Contains(extension)) return;

        var command = extension switch
        {
            "jz" => CreateJitzuCommand(script, args),
            "py" => new Command("python3").WithArguments([script, ..args]),
            "ps1" => new Command("pwsh").WithArguments(["-noprofile", script, ..args]),
            _ => throw new Exception($"Unknown extension: {extension}")
        };

        var runName = string.Join(" ", args);
        var program = Path.GetFileName(command.TargetFilePath);
        Console.WriteLine($"Starting {program} \"{script}\" {runName}: ");

        double totalRunTime = 0;
        for (var i = 0; i < _config.Warmups; i++)
        {
            var warmup = await command
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            EnsureSuccessful(warmup, scriptName, $"warmup {i + 1}");
        }

        for (int i = 0; i < iterations; i++)
        {
            Console.Write($"  > Iteration {i:#000}");

            var result = await command
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            EnsureSuccessful(result, scriptName, $"iteration {i + 1}");

            Console.WriteLine($" Returned: {result.ExitCode}, Took: {result.RunTime}");

            totalRunTime += result.RunTime.TotalMilliseconds;
            results.Add(
                new RunResult
                {
                    Script = scriptName,
                    Iterations = iterations,
                    RunName = runName,
                    Time = result.RunTime,
                });
        }

        Console.WriteLine($"Mean time {totalRunTime / iterations:F}");
        Console.WriteLine();
    }

    private Command CreateJitzuCommand(string script, string[] args)
    {
        var configuredPath = _args.JitzuPath
            ?? Environment.GetEnvironmentVariable("JITZU_BENCHMARK_EXECUTABLE");
        var jitzuPath = configuredPath is { Length: > 0 }
            ? Path.GetFullPath(configuredPath)
            : FindDefaultJitzuPath();

        if (!File.Exists(jitzuPath))
            throw new FileNotFoundException("Jitzu benchmark target was not found. Build Release or pass --jitzu.", jitzuPath);

        string[] hostArgs = ["--no-config", "--no-persist", "--no-splash", script, ..args];
        return string.Equals(Path.GetExtension(jitzuPath), ".dll", StringComparison.OrdinalIgnoreCase)
            ? new Command("dotnet").WithArguments([jitzuPath, ..hostArgs])
            : new Command(jitzuPath).WithArguments(hostArgs);
    }

    private string FindDefaultJitzuPath()
    {
        var releaseDirectory = Path.Combine(_repositoryRoot, "Jitzu.Shell", "bin", "Release", "net10.0");
        var candidates = new[]
        {
            Path.Combine(_repositoryRoot, "Jitzu.Shell", "bin", "Publish", OperatingSystem.IsWindows() ? "jz.exe" : "jz"),
            Path.Combine(releaseDirectory, "jz.dll"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[^1];
    }

    private static void EnsureSuccessful(BufferedCommandResult result, string scriptName, string run)
    {
        if (result.ExitCode == 0)
            return;

        throw new InvalidOperationException(
            $"{scriptName} failed during {run} with exit code {result.ExitCode}."
            + $"{Environment.NewLine}{result.StandardError}{result.StandardOutput}");
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
}
