using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;

string? Option(string name) => args.Zip(args.Skip(1)).FirstOrDefault(pair => pair.First == name).Second;
var artifactCommit = Option("--artifact-commit")
                     ?? throw new ArgumentException("--artifact-commit is required");
var artifactPath = Option("--artifact") ?? throw new ArgumentException("--artifact is required");
using var artifact = ArtifactBinding.Open(artifactPath, artifactCommit, Option("--artifact-sha256"));

if (args.Length >= 3 && args[0] == "--host")
{
    await SummarizeHostTraceAsync(args[1], args[2], artifact.Evidence);
    return;
}
if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal))
    throw new ArgumentException("Usage: Jitzu.StartupTraceSummary <input.nettrace> <output.json> --artifact <exe> --artifact-commit <commit> [--artifact-sha256 <expected>] | --host <trace.txt> <output.json> --artifact <exe> --artifact-commit <commit> [--artifact-sha256 <expected>]");

using var traceSnapshot = TraceSnapshot.Create(args[0]);
var input = traceSnapshot.Path;
var output = Path.GetFullPath(args[1]);
var phases = new SortedDictionary<string, double>(StringComparer.Ordinal);
var runtimeEvents = 0;
var jitEvents = 0;
var moduleEvents = 0;
var assemblyEvents = 0;
var runtimeStartEvents = 0;
double? firstRuntimeMs = null, lastRuntimeMs = null;
double? firstRuntimeStartMs = null, lastRuntimeStartMs = null;
double? firstJitMs = null, lastJitMs = null;
double? firstModuleMs = null, lastModuleMs = null;
double? firstAssemblyMs = null, lastAssemblyMs = null;

using (var source = new EventPipeEventSource(input))
{
    source.Clr.RuntimeStart += data =>
    {
        runtimeStartEvents++;
        firstRuntimeStartMs ??= data.TimeStampRelativeMSec;
        lastRuntimeStartMs = data.TimeStampRelativeMSec;
        firstRuntimeMs ??= data.TimeStampRelativeMSec;
        lastRuntimeMs = data.TimeStampRelativeMSec;
    };
    source.Clr.MethodJittingStarted += data =>
    {
        jitEvents++;
        firstJitMs ??= data.TimeStampRelativeMSec;
        lastJitMs = data.TimeStampRelativeMSec;
        firstRuntimeMs ??= data.TimeStampRelativeMSec;
        lastRuntimeMs = data.TimeStampRelativeMSec;
    };
    source.Clr.LoaderModuleLoad += data =>
    {
        moduleEvents++;
        firstModuleMs ??= data.TimeStampRelativeMSec;
        lastModuleMs = data.TimeStampRelativeMSec;
        firstRuntimeMs ??= data.TimeStampRelativeMSec;
        lastRuntimeMs = data.TimeStampRelativeMSec;
    };
    source.Clr.LoaderAssemblyLoad += data =>
    {
        assemblyEvents++;
        firstAssemblyMs ??= data.TimeStampRelativeMSec;
        lastAssemblyMs = data.TimeStampRelativeMSec;
        firstRuntimeMs ??= data.TimeStampRelativeMSec;
        lastRuntimeMs = data.TimeStampRelativeMSec;
    };
    source.Dynamic.All += data =>
    {
        if (data.ProviderName == "Microsoft-Windows-DotNETRuntime")
        {
            runtimeEvents++;
            firstRuntimeMs ??= data.TimeStampRelativeMSec;
            lastRuntimeMs = data.TimeStampRelativeMSec;
        }
        else if (data.ProviderName == "Jitzu-Startup" && data.EventName == "Phase")
        {
            var stage = data.PayloadByName("stage") as string
                        ?? data.PayloadByName("Stage") as string;
            if (stage is not null && AllowedStartupPhases.Names.Contains(stage))
                phases.TryAdd(stage, data.TimeStampRelativeMSec);
        }
    };
    source.Process();
}

var report = new
{
    SchemaVersion = 3,
    Artifact = artifact.Evidence,
    SourceSha256 = traceSnapshot.Sha256,
    SourceLengthBytes = traceSnapshot.Length,
    Runtime = new
    {
        runtimeEvents, firstRuntimeMs, lastRuntimeMs,
        RuntimeStart = new { Count = runtimeStartEvents, FirstMs = firstRuntimeStartMs, LastMs = lastRuntimeStartMs },
        Jit = new { Count = jitEvents, FirstMs = firstJitMs, LastMs = lastJitMs },
        Modules = new { Count = moduleEvents, FirstMs = firstModuleMs, LastMs = lastModuleMs },
        Assemblies = new { Count = assemblyEvents, FirstMs = firstAssemblyMs, LastMs = lastAssemblyMs }
    },
    Phases = phases,
    RuntimeStartToManagedEntryMs = firstRuntimeStartMs is not null
                                   && phases.TryGetValue("managed-entry", out var managedEntry)
        ? managedEntry - firstRuntimeStartMs : null,
    ClockScope = "All reported timings use the EventPipe session clock. Startup suspension makes process-launch to RuntimeStart unsuitable for production timing.",
    Privacy = "Only counts, relative timestamps, known Jitzu phase names, and source digest are retained."
};
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

static async Task SummarizeHostTraceAsync(string inputPath, string outputPath,
    ArtifactEvidence artifact)
{
    using var snapshot = TraceSnapshot.Create(inputPath);
    var lines = await File.ReadAllLinesAsync(snapshot.Path);
    var knownStages = new (string Name, string Pattern)[]
    {
        ("apphost-invoked", "--- Invoked apphost"),
        ("single-file-bundle-detected", "Detected Single-File app bundle"),
        ("internal-fxr-selected", "Using internal fxr"),
        ("bundle-startup-entered", "hostfxr_main_bundle_startupinfo"),
        ("bundle-extraction-directory-configured", "Property NATIVE_DLL_SEARCH_DIRECTORIES"),
        ("managed-assembly-executed", "Execute managed assembly exit code")
    };
    var observed = knownStages.Select(stage => new
    {
        stage.Name,
        FirstLine = Array.FindIndex(lines, line => line.Contains(stage.Pattern, StringComparison.Ordinal)) + 1,
        Count = lines.Count(line => line.Contains(stage.Pattern, StringComparison.Ordinal))
    }).ToArray();
    var result = new
    {
        SchemaVersion = 3,
        Artifact = artifact,
        SourceSha256 = snapshot.Sha256,
        SourceLengthBytes = snapshot.Length,
        TraceHeader = lines.FirstOrDefault()?.StartsWith("Tracing enabled @", StringComparison.Ordinal) == true
            ? lines[0] : null,
        Stages = observed,
        TimingResolution = "The host trace records only a wall-clock header, not per-stage timestamps; stage duration is not derivable.",
        Privacy = "Only known stage names, line positions/counts, source digest/length, and the non-identifying trace header are retained."
    };
    var output = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
}

internal static class AllowedStartupPhases
{
    public static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "managed-entry", "options-parsed", "repl-entry", "theme-loaded", "history-loaded",
        "aliases-loaded", "state-loaded", "commands-registered", "config-loaded",
        "first-prompt-rendered", "input-ready-interactive", "input-ready", "input-accepted",
        "first-result-displayed"
    };
}

internal sealed class TraceSnapshot : IDisposable
{
    private readonly FileStream _lock;
    private readonly string _directory;

    private TraceSnapshot(string directory, string path, FileStream fileLock, long length, string sha256) =>
        (_directory, Path, _lock, Length, Sha256) = (directory, path, fileLock, length, sha256);

    public string Path { get; }
    public long Length { get; }
    public string Sha256 { get; }

    public static TraceSnapshot Create(string sourcePath)
    {
        var snapshotDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"jitzu-trace-{Guid.NewGuid():N}");
        var snapshotPath = System.IO.Path.Combine(snapshotDirectory, "trace.snapshot");
        try
        {
            CreatePrivateDirectory(snapshotDirectory);
            using (var source = new FileStream(System.IO.Path.GetFullPath(sourcePath), FileMode.Open,
                       FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            using (var destination = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 81920, FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            var snapshot = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.SequentialScan);
            try
            {
                var sha256 = Convert.ToHexString(SHA256.HashData(snapshot));
                snapshot.Position = 0;
                return new TraceSnapshot(snapshotDirectory, snapshotPath, snapshot, snapshot.Length, sha256);
            }
            catch
            {
                snapshot.Dispose();
                throw;
            }
        }
        catch
        {
            try { File.Delete(snapshotPath); } catch { }
            try { Directory.Delete(snapshotDirectory); } catch { }
            throw;
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }

        Directory.CreateDirectory(path);
        var user = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public void Dispose()
    {
        _lock.Dispose();
        try { File.Delete(Path); } catch { }
        try { Directory.Delete(_directory); } catch { }
    }
}

internal sealed record ArtifactEvidence(string Commit, string CommitSource, long SizeBytes, string Sha256,
    string? EmbeddedProductVersion, bool CommitMatchesEmbeddedProductVersion);

internal sealed class ArtifactBinding : IDisposable
{
    private readonly FileStream _lock;
    private ArtifactBinding(FileStream fileLock, ArtifactEvidence evidence) =>
        (_lock, Evidence) = (fileLock, evidence);

    public ArtifactEvidence Evidence { get; }

    public static ArtifactBinding Open(string path, string commit, string? expectedSha256)
    {
        var fileLock = new FileStream(System.IO.Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.SequentialScan);
        try
        {
            var sha256 = Convert.ToHexString(SHA256.HashData(fileLock));
            if (expectedSha256 is not null
                && !sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The artifact SHA-256 does not match --artifact-sha256.");
            var productVersion = FileVersionInfo.GetVersionInfo(System.IO.Path.GetFullPath(path)).ProductVersion;
            var embeddedCommit = productVersion?.Split('+', 2).ElementAtOrDefault(1);
            var commitMatches = embeddedCommit?.StartsWith(commit, StringComparison.OrdinalIgnoreCase) == true;
            if (!commitMatches)
                throw new InvalidDataException("The caller-supplied artifact commit does not match its embedded ProductVersion.");
            fileLock.Position = 0;
            return new ArtifactBinding(fileLock, new ArtifactEvidence(commit, "caller-supplied",
                fileLock.Length, sha256, productVersion, commitMatches));
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }
    }

    public void Dispose() => _lock.Dispose();
}
