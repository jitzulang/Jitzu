using System.Diagnostics;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jitzu.StartupBenchmark;

const int MaxTranscriptBytes = 2 * 1024 * 1024;
var options = Options.Parse(args);
using var isolation = new BenchmarkIsolation(options.ProfileMode);
using var profileState = new ProfileStateGuard(options.ProfileMode, isolation.StartupStateFiles);
var profileBefore = profileState.Before;
using var baseline = ArtifactSnapshot.Create("baseline", options.Baseline, options.BaselineCommit, isolation.Root);
using var candidate = ArtifactSnapshot.Create("candidate", options.Candidate, options.CandidateCommit, isolation.Root);
var artifacts = new[] { baseline, candidate };
var samples = new List<Sample>();
var random = new Random(options.Seed);

foreach (var artifact in artifacts)
    for (var i = 0; i < options.Warmups; i++)
    {
        samples.Add(await MeasureAsync(artifact, true, i, options, isolation));
        profileState.VerifyUnchanged();
    }

for (var pair = 0; pair < options.Runs; pair++)
{
    var order = random.Next(2) == 0 ? artifacts : [artifacts[1], artifacts[0]];
    for (var position = 0; position < order.Length; position++)
    {
        samples.Add(await MeasureAsync(order[position], false, pair * 2 + position, options, isolation));
        profileState.VerifyUnchanged();
    }
}

foreach (var artifact in artifacts)
    artifact.VerifyUnchanged();
var profileAfter = profileState.VerifyUnchanged();

var measured = samples.Where(sample => !sample.Warmup).ToArray();
var report = new Report(5, DateTimeOffset.UtcNow,
    new EnvironmentInfo(Environment.OSVersion.ToString(), RuntimeInformation.FrameworkDescription,
        RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount),
    new PublicOptions(options.Runs, options.Warmups, options.Seed, options.Timeout.TotalSeconds,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.ShellArguments))),
        MaxTranscriptBytes, "isolated allow-list", options.ShellArguments.Contains("--no-config", StringComparison.Ordinal),
        options.SdkVersion, options.ReadinessPolicy, options.ProfileMode),
    new ProfileEvidence(options.ProfileMode, profileState.LockPolicy, profileBefore, profileAfter),
    artifacts.Select(a => a.Public).ToArray(),
    artifacts.Select(a => Summary.Create(a.Label,
        measured.Where(s => s.Artifact == a.Label).ToArray())).ToArray(),
    PairedComparison.Create(measured), samples);

var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output))!);
await File.WriteAllTextAsync(options.Output, json);
Console.WriteLine(json);
return 0;

static async Task<Sample> MeasureAsync(ArtifactSnapshot artifact, bool warmup, int order, Options options,
    BenchmarkIsolation isolation)
{
    var sentinel = $"__JITZU_STARTUP_{Guid.NewGuid():N}__";
    var stopwatch = Stopwatch.StartNew();
    using var process = new ConPtyProcess(artifact.ExecutionPath, options.ShellArguments,
        isolation.WorkingDirectory, isolation.Environment);
    using var cancellation = new CancellationTokenSource(options.Timeout);
    using var terminationRegistration = cancellation.Token.Register(() =>
        process.TerminateTreeAndWait(TimeSpan.FromSeconds(5)));
    using var capture = new TranscriptCapture(MaxTranscriptBytes);
    var buffer = new byte[8192];
    double? firstPromptMs = null;
    double? roundTripMs = null;
    var commandSent = false;
    var promptObserved = false;

    try
    {
        while (roundTripMs is null)
        {
            var read = await process.ReadAsync(buffer, cancellation.Token);
            if (read == 0) break;
            capture.Append(buffer.AsSpan(0, read));
            promptObserved |= TerminalText.PromptRegex().IsMatch(capture.SearchText);

            var ready = options.ReadinessPolicy == "external-prompt"
                || capture.Markers.ContainsKey("input-ready-interactive");
            if (!commandSent && promptObserved && ready)
            {
                firstPromptMs = stopwatch.Elapsed.TotalMilliseconds;
                await process.WriteAsync($"echo {sentinel}\r", cancellation.Token);
                commandSent = true;
            }

            if (commandSent && FindSecond(capture.SearchText, sentinel) is var second and >= 0
                && TerminalText.PromptRegex().IsMatch(capture.SearchText[(second + sentinel.Length)..]))
                roundTripMs = stopwatch.Elapsed.TotalMilliseconds;
        }
    }
    catch (Exception ex) when (cancellation.IsCancellationRequested
                               && ex is OperationCanceledException or IOException or ObjectDisposedException)
    {
        var terminated = process.TerminateTreeAndWait(TimeSpan.FromSeconds(5));
        await WriteTimeoutEvidenceAsync(options, artifact, warmup, order, capture.ByteCount,
            promptObserved, commandSent, capture.Markers.Count, terminated);
        throw new TimeoutException($"{artifact.Label} sample {order} timed out after {options.Timeout}; " +
            $"bytes={capture.ByteCount}, prompt={promptObserved}, commandSent={commandSent}, " +
            $"markers={capture.Markers.Count}, processTreeTerminated={terminated}.");
    }

    terminationRegistration.Dispose();
    if (cancellation.IsCancellationRequested)
    {
        var terminated = process.TerminateTreeAndWait(TimeSpan.FromSeconds(5));
        await WriteTimeoutEvidenceAsync(options, artifact, warmup, order, capture.ByteCount,
            promptObserved, commandSent, capture.Markers.Count, terminated);
        throw new TimeoutException($"{artifact.Label} sample {order} timed out after {options.Timeout}; " +
            $"bytes={capture.ByteCount}, prompt={promptObserved}, commandSent={commandSent}, " +
            $"markers={capture.Markers.Count}, processTreeTerminated={terminated}.");
    }
    if (firstPromptMs is null || roundTripMs is null)
        throw new InvalidOperationException($"{artifact.Label} sample {order} ended before completing the validated round-trip.");

    await process.WriteAsync("exit\r", CancellationToken.None);
    if (!process.WaitForExit(options.Timeout))
    {
        var terminated = process.TerminateTreeAndWait(TimeSpan.FromSeconds(5));
        await WriteTimeoutEvidenceAsync(options, artifact, warmup, order, capture.ByteCount,
            promptObserved, commandSent, capture.Markers.Count, terminated);
        throw new TimeoutException($"{artifact.Label} sample {order} did not exit within {options.Timeout}; " +
            $"processTreeTerminated={terminated}.");
    }
    var exitCode = process.GetExitCode();
    var unexpectedError = capture.UnexpectedError;
    if (exitCode != 0 || unexpectedError)
        throw new InvalidOperationException($"{artifact.Label} sample {order} failed validation " +
            $"(exit={exitCode}, unexpectedError={unexpectedError}).");

    var markers = capture.Markers;
    var readyMarker = markers.GetValueOrDefault("input-ready-interactive",
        markers.GetValueOrDefault("first-prompt-rendered", double.NaN));
    var phases = PhaseTimings.Create(firstPromptMs.Value, markers, readyMarker);
    return new Sample(artifact.Label, warmup, order, firstPromptMs.Value, roundTripMs.Value,
        exitCode, true, markers.ContainsKey("input-ready-interactive"),
        FindSecond(capture.SearchText, sentinel) >= 0, true, !unexpectedError,
        capture.GetHash(), capture.ByteCount, phases, new SortedDictionary<string, double>(markers));
}

static Task WriteTimeoutEvidenceAsync(Options options, ArtifactSnapshot artifact, bool warmup, int order,
    int bytes, bool promptObserved, bool commandSent, int markerCount, bool processTreeTerminated)
{
    var evidence = new
    {
        SchemaVersion = 1,
        GeneratedUtc = DateTimeOffset.UtcNow,
        Failure = "timeout",
        Artifact = artifact.Public,
        warmup,
        order,
        TimeoutSeconds = options.Timeout.TotalSeconds,
        bytes,
        promptObserved,
        commandSent,
        markerCount,
        processTreeTerminated,
        options.SdkVersion,
        options.ReadinessPolicy,
        options.ProfileMode,
        Privacy = "No command line, paths, environment, terminal content, user, or machine name are retained."
    };
    var path = options.Output + ".timeout.json";
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    return File.WriteAllTextAsync(path,
        JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
}

static int FindSecond(string text, string value)
{
    var first = text.IndexOf(value, StringComparison.Ordinal);
    return first < 0 ? -1 : text.IndexOf(value, first + value.Length, StringComparison.Ordinal);
}

internal sealed class TranscriptCapture : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _search = new();
    private readonly StringBuilder _markerWindow = new();
    private readonly int _limit;
    private int _bytes;

    public TranscriptCapture(int limit) => _limit = limit;
    public int ByteCount => _bytes;
    public string SearchText => _search.ToString();
    public bool UnexpectedError { get; private set; }
    public Dictionary<string, double> Markers { get; } = new(StringComparer.Ordinal);

    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_bytes > _limit - bytes.Length)
            throw new InvalidDataException($"Terminal output exceeded the {_limit}-byte safety limit.");
        _bytes += bytes.Length;
        _hash.AppendData(bytes);
        Span<char> chars = stackalloc char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = _decoder.GetChars(bytes, chars, false);
        var chunk = new string(chars[..count]);
        var plainChunk = TerminalText.Strip(chunk);
        _search.Append(plainChunk);
        if (_search.Length > 64 * 1024)
            _search.Remove(0, _search.Length - 32 * 1024);
        var searchable = _search.ToString();
        UnexpectedError |= searchable.Contains("Unexpected error:", StringComparison.OrdinalIgnoreCase)
                           || searchable.Contains("Error loading config:", StringComparison.OrdinalIgnoreCase)
                           || searchable.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase)
                           || searchable.Contains("[ERR]", StringComparison.OrdinalIgnoreCase);
        _markerWindow.Append(chunk);
        foreach (Match match in TerminalText.MarkerRegex().Matches(_markerWindow.ToString()))
        {
            var stage = match.Groups[2].Value;
            if (StartupPhasePolicy.Allowed.Contains(stage))
                Markers.TryAdd(stage, double.Parse(match.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture));
        }
        if (_markerWindow.Length > 512)
            _markerWindow.Remove(0, _markerWindow.Length - 256);
    }

    public string GetHash() => Convert.ToHexString(_hash.GetHashAndReset());
    public void Dispose() => _hash.Dispose();
}

internal static class StartupPhasePolicy
{
    public static readonly FrozenSet<string> Allowed = new[]
    {
        "managed-entry", "options-parsed", "repl-entry", "theme-loaded", "history-loaded",
        "aliases-loaded", "state-loaded", "commands-registered", "config-loaded",
        "first-prompt-rendered", "input-ready-interactive", "input-ready", "input-accepted",
        "first-result-displayed"
    }.ToFrozenSet(StringComparer.Ordinal);
}

internal static partial class TerminalText
{
    [GeneratedRegex("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))")]
    private static partial Regex AnsiRegex();
    [GeneratedRegex("(?:^|\\n)[>#] ", RegexOptions.Multiline)]
    public static partial Regex PromptRegex();
    [GeneratedRegex("\\x1B\\]1337;JitzuStartup=([0-9.]+);([a-z0-9-]+)\\x07")]
    public static partial Regex MarkerRegex();
    public static string Strip(string value) => AnsiRegex().Replace(value, "").Replace("\r", "");
}

internal sealed record Options(string Baseline, string Candidate, int Runs, int Warmups, int Seed,
    TimeSpan Timeout, string Output, string ShellArguments, string? BaselineCommit, string? CandidateCommit,
    string SdkVersion, string ReadinessPolicy, string ProfileMode)
{
    public static Options Parse(string[] args)
    {
        string? Value(string name) => args.Zip(args.Skip(1)).FirstOrDefault(p => p.First == name).Second;
        var result = new Options(
            Path.GetFullPath(Value("--baseline") ?? throw new ArgumentException("--baseline is required")),
            Path.GetFullPath(Value("--candidate") ?? throw new ArgumentException("--candidate is required")),
            int.TryParse(Value("--runs"), out var runs) ? runs : 40,
            int.TryParse(Value("--warmups"), out var warmups) ? warmups : 5,
            int.TryParse(Value("--seed"), out var seed) ? seed : 1729,
            TimeSpan.FromSeconds(double.TryParse(Value("--timeout-seconds"), out var timeout) ? timeout : 15),
            Value("--output") ?? "startup-results.json",
            Value("--shell-arguments") ?? "--no-persist --no-splash",
            Value("--baseline-commit"), Value("--candidate-commit"), Value("--sdk-version") ?? "not-recorded",
            Value("--readiness") ?? "external-prompt", Value("--profile-mode") ?? "isolated");
        if (result.Runs < 1 || result.Warmups < 0 || result.Timeout <= TimeSpan.Zero)
            throw new ArgumentException("Runs and timeout must be positive; warmups cannot be negative.");
        if (result.ReadinessPolicy is not ("external-prompt" or "managed-marker"))
            throw new ArgumentException("--readiness must be external-prompt or managed-marker.");
        if (result.ProfileMode is not ("isolated" or "configured-user"))
            throw new ArgumentException("--profile-mode must be isolated or configured-user.");
        return result;
    }
}

internal sealed class BenchmarkIsolation : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"jitzu-startup-{Guid.NewGuid():N}");
    public string WorkingDirectory => Path.Combine(Root, "cwd");
    public IReadOnlyDictionary<string, string> Environment { get; }
    public IReadOnlyList<StartupStateFile> StartupStateFiles { get; }

    public BenchmarkIsolation(string profileMode)
    {
        Directory.CreateDirectory(WorkingDirectory);
        var isolatedProfile = Path.Combine(Root, "profile");
        var temp = Path.Combine(Root, "temp");
        Directory.CreateDirectory(isolatedProfile);
        Directory.CreateDirectory(temp);
        var profile = profileMode == "configured-user"
            ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
            : isolatedProfile;
        var appData = profileMode == "configured-user"
            ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData)
            : Path.Combine(profile, "AppData", "Roaming");
        var localAppData = profileMode == "configured-user"
            ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(profile, "AppData", "Local");
        if (string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(appData))
            throw new InvalidOperationException("The configured user profile could not be resolved.");
        var systemRoot = System.Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot, ["WINDIR"] = systemRoot,
            ["ComSpec"] = Path.Combine(systemRoot, "System32", "cmd.exe"),
            ["PATH"] = Path.Combine(systemRoot, "System32"), ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["TEMP"] = temp, ["TMP"] = temp, ["USERPROFILE"] = profile, ["HOME"] = profile,
            ["APPDATA"] = appData,
            ["LOCALAPPDATA"] = localAppData,
            ["USERNAME"] = "benchmark", ["JITZU_STARTUP_PROFILE"] = "terminal"
        };
        StartupStateFiles =
        [
            new("config", Path.Combine(profile, ".jitzu", "config.jz"), CountExecutableLines: true),
            new("colours", Path.Combine(profile, ".jitzu", "colours.json"), CountExecutableLines: false)
        ];
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

internal sealed record StartupStateFile(string Label, string Path, bool CountExecutableLines);

internal sealed class ProfileStateGuard : IDisposable
{
    private readonly List<(StartupStateFile Descriptor, FileStream? Lock)> _files = [];
    public StartupFileEvidence[] Before { get; }
    public string LockPolicy { get; }

    public ProfileStateGuard(string mode, IEnumerable<StartupStateFile> files)
    {
        LockPolicy = mode == "configured-user"
            ? "Config and colours are the startup-affecting files for --no-persist; existing files are held with FileShare.Read, and missing files are rechecked after every sample and at the end."
            : "No external profile files used.";
        if (mode != "configured-user")
        {
            Before = [];
            return;
        }

        try
        {
            foreach (var descriptor in files)
            {
                FileStream? fileLock = null;
                try
                {
                    fileLock = new FileStream(descriptor.Path, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 4096, FileOptions.SequentialScan);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                _files.Add((descriptor, fileLock));
            }

            if (_files.Single(file => file.Descriptor.Label == "colours").Lock is null)
                throw new InvalidOperationException(
                    "Configured-profile measurement requires an existing colours file because the baseline creates it when missing.");
            Before = Snapshot();
        }
        catch
        {
            foreach (var file in _files)
                file.Lock?.Dispose();
            throw;
        }
    }

    public StartupFileEvidence[] VerifyUnchanged()
    {
        var after = Snapshot();
        if (!Before.SequenceEqual(after))
            throw new IOException("Configured startup state changed during measurement.");
        return after;
    }

    private StartupFileEvidence[] Snapshot() => _files.Select(file =>
    {
        if (file.Lock is null)
        {
            if (File.Exists(file.Descriptor.Path))
                throw new IOException($"Configured startup state '{file.Descriptor.Label}' was created during measurement.");
            return new StartupFileEvidence(file.Descriptor.Label, false, null, null, null);
        }

        file.Lock.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(file.Lock));
        int? executableLines = null;
        if (file.Descriptor.CountExecutableLines)
        {
            file.Lock.Position = 0;
            using var reader = new StreamReader(file.Lock, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);
            executableLines = 0;
            while (reader.ReadLine() is { } line)
                if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    executableLines++;
        }
        return new StartupFileEvidence(file.Descriptor.Label, true, file.Lock.Length, hash, executableLines);
    }).ToArray();

    public void Dispose()
    {
        foreach (var file in _files)
            file.Lock?.Dispose();
    }
}

internal sealed class ArtifactSnapshot : IDisposable
{
    private readonly FileStream _lock;
    public string Label { get; }
    [JsonIgnore] public string ExecutionPath { get; }
    public PublicArtifact Public { get; }

    private ArtifactSnapshot(string label, string path, FileStream fileLock, PublicArtifact artifact)
        => (Label, ExecutionPath, _lock, Public) = (label, path, fileLock, artifact);

    public static ArtifactSnapshot Create(string label, string source, string? commit, string root)
    {
        var path = Path.Combine(root, $"{label}.exe");
        using (var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            sourceStream.CopyTo(destination);
        var fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(fileLock));
            var productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            var embeddedCommit = productVersion?.Split('+', 2).ElementAtOrDefault(1);
            bool? commitMatches = commit is null ? null
                : embeddedCommit?.StartsWith(commit, StringComparison.OrdinalIgnoreCase) == true;
            if (commitMatches == false)
                throw new InvalidDataException($"The caller-supplied {label} commit does not match its embedded ProductVersion.");
            fileLock.Position = 0;
            return new ArtifactSnapshot(label, path, fileLock,
                new PublicArtifact(label, fileLock.Length, hash, commit, "caller-supplied",
                    productVersion, commitMatches));
        }
        catch
        {
            fileLock.Dispose();
            throw;
        }
    }

    public void VerifyUnchanged()
    {
        _lock.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(_lock));
        if (!hash.Equals(Public.Sha256, StringComparison.Ordinal))
            throw new IOException($"The immutable {Label} artifact changed during measurement.");
    }
    public void Dispose() => _lock.Dispose();
}

internal sealed record PublicArtifact(string Label, long SizeBytes, string Sha256, string? Commit,
    string CommitSource, string? EmbeddedProductVersion, bool? CommitMatchesEmbeddedProductVersion);
internal sealed record PublicOptions(int Runs, int Warmups, int Seed, double TimeoutSeconds,
    string ShellArgumentsSha256, int TranscriptLimitBytes, string EnvironmentPolicy, bool UserConfigDisabled,
    string SdkVersion, string ReadinessPolicy, string ProfileMode);
internal sealed record StartupFileEvidence(string Label, bool Exists, long? SizeBytes, string? Sha256,
    int? ExecutableLineCount);
internal sealed record ProfileEvidence(string Mode, string LockPolicy, StartupFileEvidence[] Before,
    StartupFileEvidence[] After);
internal sealed record EnvironmentInfo(string OS, string Framework, string Architecture, int ProcessorCount);

internal sealed record PhaseTimings(double? LaunchToManagedEntryMs, double? ManagedEntryToOptionsMs,
    double? ApplicationInitializationMs, double? FirstRenderMs, double? ReadyToAcceptedInputMs,
    double? AcceptedInputToResultMs)
{
    public static PhaseTimings Create(double externalReadyMs, IReadOnlyDictionary<string, double> m, double ready)
    {
        double? Delta(string end, string start) => m.TryGetValue(end, out var e) && m.TryGetValue(start, out var s) ? e - s : null;
        double? launchToManaged = m.TryGetValue("managed-entry", out var entry) && !double.IsNaN(ready)
            ? externalReadyMs - (ready - entry) : null;
        return new PhaseTimings(launchToManaged, Delta("options-parsed", "managed-entry"),
            !double.IsNaN(ready) && m.TryGetValue("options-parsed", out var options) ? ready - options : null,
            !double.IsNaN(ready) && m.TryGetValue("config-loaded", out var config) ? ready - config : null,
            !double.IsNaN(ready) && m.TryGetValue("input-accepted", out var accepted) ? accepted - ready : null,
            Delta("first-result-displayed", "input-accepted"));
    }
}

internal sealed record Sample(string Artifact, bool Warmup, int Order, double FirstPromptMs,
    double InputRoundTripMs, int ExitCode, bool FirstPromptObserved, bool ReadyMarkerObserved,
    bool SentinelObserved, bool NextPromptObserved, bool ErrorOutputValid, string TranscriptSha256,
    int TranscriptBytes, PhaseTimings Phases, SortedDictionary<string, double> ManagedMarkersMs);

internal sealed record Distribution(double Median, double P90, double P95, double P99, double Max)
{
    public static Distribution Create(IEnumerable<double> source)
    {
        var v = source.Order().ToArray();
        double Q(double q) => v[Math.Max(0, (int)Math.Ceiling(v.Length * q) - 1)];
        var median = v.Length % 2 == 0 ? (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2 : v[v.Length / 2];
        return new Distribution(median, Q(.90), Q(.95), Q(.99), v[^1]);
    }
}

internal sealed record Summary(string Artifact, int Count, Distribution PromptMs, Distribution RoundTripMs,
    Distribution? LaunchToManagedEntryMs, Distribution? ApplicationInitializationMs)
{
    public static Summary Create(string artifact, Sample[] samples) => new(artifact, samples.Length,
        Distribution.Create(samples.Select(s => s.FirstPromptMs)),
        Distribution.Create(samples.Select(s => s.InputRoundTripMs)),
        Optional(samples.Select(s => s.Phases.LaunchToManagedEntryMs)),
        Optional(samples.Select(s => s.Phases.ApplicationInitializationMs)));
    private static Distribution? Optional(IEnumerable<double?> values)
    {
        var present = values.OfType<double>().ToArray();
        return present.Length == 0 ? null : Distribution.Create(present);
    }
}

internal sealed record PairedComparison(int Count, int CandidatePromptWins, int CandidateRoundTripWins,
    Distribution PromptDeltaCandidateMinusBaselineMs, Distribution RoundTripDeltaCandidateMinusBaselineMs)
{
    public static PairedComparison Create(Sample[] samples)
    {
        var pairs = samples.GroupBy(s => s.Order / 2).Select(group =>
        {
            var baseline = group.Single(s => s.Artifact == "baseline");
            var candidate = group.Single(s => s.Artifact == "candidate");
            return (Prompt: candidate.FirstPromptMs - baseline.FirstPromptMs,
                RoundTrip: candidate.InputRoundTripMs - baseline.InputRoundTripMs);
        }).ToArray();
        return new PairedComparison(pairs.Length, pairs.Count(p => p.Prompt < 0), pairs.Count(p => p.RoundTrip < 0),
            Distribution.Create(pairs.Select(p => p.Prompt)), Distribution.Create(pairs.Select(p => p.RoundTrip)));
    }
}

internal sealed record Report(int SchemaVersion, DateTimeOffset GeneratedUtc, EnvironmentInfo Environment,
    PublicOptions Options, ProfileEvidence Profile, PublicArtifact[] Artifacts, Summary[] Summaries,
    PairedComparison PairedComparison, List<Sample> Samples);
