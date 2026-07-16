using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Jitzu.Core;
using Jitzu.Core.Language;
using Jitzu.Core.Logging;
using Jitzu.Core.Runtime;

namespace Jitzu.Shell.Core;

/// <summary>
/// Determines whether to execute as Jitzu code or OS command.
/// Strategy: Try Jitzu first, fall back to OS command on parse failure.
/// </summary>
public class ExecutionStrategy(ShellSession session, BuiltinCommands builtins, AliasManager? aliasManager = null, LabelManager? labelManager = null)
{
    private static readonly HashSet<string> PipeFunctions =
    [
        "first", "last", "nth", "grep", "print", "head", "tail", "sort", "uniq", "wc",
        "sum", "avg", "min", "max", "count", "more", "less", "tee"
    ];

    private readonly List<BackgroundJob> _jobs = [];
    private int _nextJobId = 1;

    public async Task<ShellResult> ExecuteAsync(string input)
    {
        // Split chain FIRST (quote-aware) so that expanded values containing
        // operators like ; are not misinterpreted as command separators.
        var chain = SplitChain(input);
        if (chain.Count > 1)
            return await ExecuteChainAsync(chain);

        return await ExpandAndExecuteAsync(input);
    }

    private async Task<ShellResult> ExpandAndExecuteAsync(string input)
    {
        input = ExpandAlias(input);
        input = ExpandEnvironmentVariables(input);
        input = await ExpandCommandSubstitutionsAsync(input);

        // PowerShell-style call operator: leading & (e.g. & dotnet build)
        if (input.TrimStart().StartsWith('&') && !input.TrimStart().StartsWith("&&"))
        {
            var rest = input.TrimStart()[1..].TrimStart();
            if (rest.Length > 0)
                return await ExecuteSingleAsync(rest);
        }

        // Background job: trailing &
        if (input.TrimEnd().EndsWith('&') && !input.TrimEnd().EndsWith("&&"))
        {
            var command = input.TrimEnd()[..^1].TrimEnd();
            if (command.Length > 0)
                return LaunchBackgroundJob(command);
        }

        return await ExecuteSingleAsync(input);
    }

    public IReadOnlyList<BackgroundJob> Jobs => _jobs;

    private async Task<ShellResult> ExecuteSingleAsync(string input)
    {
        // Parse I/O redirection before splitting args
        var (command, redirect) = ParseRedirection(input);
        if (redirect.HasRedirection)
            return await ExecuteWithRedirectionAsync(command, redirect);

        var args = CommandLineParser.SplitCommandLine(command).ToArray();
        args = ExpandGlobs(args);
        if (labelManager is not null)
            for (var i = 1; i < args.Length; i++)
                args[i] = labelManager.ExpandLabel(args[i]);

        // Special single file cases, try to execute them
        if (File.Exists(args[0]))
        {
            return Path.GetExtension(args[0]) switch
            {
                null or ".exe" => await ExecuteOsCommandAsync(args),
                _ => ShellExecute(args[0])
            };
        }

        // Handle 'source' command — execute a .jz file in the current session
        if (args[0] is "source" or ".")
        {
            if (args.Length < 2)
                return new ShellResult(ResultType.Error, "", new Exception("Usage: source <file>"));
            return await ExecuteSourceFileAsync(args[1]);
        }

        // 1. Check if it's a builtin command first
        var hasPipe = command.Contains('|');
        if (!hasPipe && builtins.IsBuiltin(args[0]))
            return await builtins.ExecuteAsync(args[0], args[1..].AsMemory());

        // Builtin piped into something: e.g. diff file1 file2 | more
        if (hasPipe && builtins.IsBuiltin(args[0]))
        {
            var pipePos = FindFirstTopLevelPipe(command);
            if (pipePos > 0)
            {
                var leftCmd = command[..pipePos].Trim();
                var rightCmd = command[(pipePos + 1)..].Trim();
                return await ExecuteBuiltinPipelineAsync(leftCmd, rightCmd);
            }
        }

        if (!LooksLikeCode(command))
        {
            if (hasPipe)
            {
                var hybrid = DetectHybridPipeline(command);
                if (hybrid != null)
                    return await ExecuteHybridPipelineAsync(hybrid.Value.OsCommand, hybrid.Value.JitzuSegments);
                return await ExecuteShellPipelineAsync(command);
            }
            return await ExecuteOsCommandAsync(args);
        }

        // 2. Try executing as Jitzu code
        var jitzuResult = await session.ExecuteAsync(command);

        if (!jitzuResult.Success)
        {
            if (hasPipe)
            {
                var hybrid = DetectHybridPipeline(command);
                if (hybrid != null)
                    return await ExecuteHybridPipelineAsync(hybrid.Value.OsCommand, hybrid.Value.JitzuSegments);
                return await ExecuteShellPipelineAsync(command);
            }
            return await ExecuteOsCommandAsync(args);
        }

        var result = jitzuResult.Result switch
        {
            Value v => ValueFormatter.Format(v.AsObject()),
            { } obj => ValueFormatter.Format(obj),
            _ => null,
        };

        return new ShellResult(ResultType.Jitzu, result, null);
    }

    /// <summary>
    /// Splits input by &&, ||, ; operators (respecting quotes).
    /// Returns a list of (command, operator) pairs where operator is the separator AFTER the command.
    /// </summary>
    private static List<(string Command, string Operator)> SplitChain(string input)
    {
        var segments = new List<(string Command, string Operator)>();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var start = 0;

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            switch (ch)
            {
                case '\'' when !inDoubleQuote:
                    inSingleQuote = !inSingleQuote;
                    break;
                case '"' when !inSingleQuote:
                    inDoubleQuote = !inDoubleQuote;
                    break;
            }

            if (inSingleQuote || inDoubleQuote)
                continue;

            if (ch == '&' && i + 1 < input.Length && input[i + 1] == '&')
            {
                segments.Add((input[start..i].Trim(), "&&"));
                i++; // skip second &
                start = i + 1;
            }
            else if (ch == '|' && i + 1 < input.Length && input[i + 1] == '|')
            {
                segments.Add((input[start..i].Trim(), "||"));
                i++; // skip second |
                start = i + 1;
            }
            else if (ch == ';')
            {
                segments.Add((input[start..i].Trim(), ";"));
                start = i + 1;
            }
        }

        // Last segment
        var last = input[start..].Trim();
        if (last.Length > 0)
            segments.Add((last, ""));

        return segments;
    }

    private async Task<ShellResult> ExecuteChainAsync(List<(string Command, string Operator)> chain)
    {
        ShellResult lastResult = new(ResultType.Jitzu, "", null);

        foreach (var (command, op) in chain)
        {
            if (string.IsNullOrWhiteSpace(command))
                continue;

            lastResult = await ExpandAndExecuteAsync(command);
            var success = lastResult.Error == null;

            switch (op)
            {
                case "&&" when !success:
                    return lastResult; // short-circuit: stop on failure
                case "||" when success:
                    return lastResult; // short-circuit: stop on success
                    // ";" always continues
            }
        }

        return lastResult;
    }

    /// <summary>
    /// Parses I/O redirection operators (>, >>, <) from a command string.
    /// Returns the clean command and redirection info.
    /// </summary>
    private static (string Command, RedirectionInfo Redirect) ParseRedirection(string input)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        string? outputFile = null;
        string? inputFile = null;
        var appendMode = false;
        var cleanParts = new StringBuilder();
        var i = 0;

        while (i < input.Length)
        {
            var ch = input[i];

            if (ch == '\'' && !inDoubleQuote) { inSingleQuote = !inSingleQuote; cleanParts.Append(ch); i++; continue; }
            if (ch == '"' && !inSingleQuote) { inDoubleQuote = !inDoubleQuote; cleanParts.Append(ch); i++; continue; }

            if (inSingleQuote || inDoubleQuote) { cleanParts.Append(ch); i++; continue; }

            if (ch == '>' && i + 1 < input.Length && input[i + 1] == '>')
            {
                appendMode = true;
                i += 2;
                outputFile = ExtractRedirectTarget(input, ref i);
            }
            else if (ch == '>')
            {
                appendMode = false;
                i++;
                outputFile = ExtractRedirectTarget(input, ref i);
            }
            else if (ch == '<')
            {
                i++;
                inputFile = ExtractRedirectTarget(input, ref i);
            }
            else
            {
                cleanParts.Append(ch);
                i++;
            }
        }

        var redirect = new RedirectionInfo(outputFile, inputFile, appendMode);
        return (cleanParts.ToString().Trim(), redirect);
    }

    private static string ExtractRedirectTarget(string input, ref int i)
    {
        // Skip whitespace
        while (i < input.Length && input[i] == ' ') i++;

        var start = i;

        if (i < input.Length && input[i] is '"' or '\'')
        {
            var quoteChar = input[i];
            i++;
            start = i;
            while (i < input.Length && input[i] != quoteChar) i++;
            var result = input[start..i];
            if (i < input.Length) i++; // skip closing quote
            return result;
        }

        while (i < input.Length && input[i] is not ' ' and not '>' and not '<' and not '|' and not '&' and not ';')
            i++;

        return input[start..i];
    }

    private async Task<ShellResult> ExecuteWithRedirectionAsync(string command, RedirectionInfo redirect)
    {
        var originalOut = Console.Out;

        try
        {
            // Handle input redirection — prepend file content as stdin for builtins
            if (redirect.InputFile != null)
            {
                var path = ExpandPathStatic(redirect.InputFile);
                if (!File.Exists(path))
                    return new ShellResult(ResultType.Error, "", new Exception($"No such file: {redirect.InputFile}"));
            }

            // For output redirection, we need to capture output
            var args = CommandLineParser.SplitCommandLine(command).ToArray();
            args = ExpandGlobs(args);

            ShellResult result;

            if (builtins.IsBuiltin(args[0]))
            {
                // Execute builtin and capture its output
                result = await builtins.ExecuteAsync(args[0], args[1..].AsMemory());
            }
            else
            {
                // Execute OS command with captured stdout
                result = await ExecuteOsCommandCapturedAsync(args, redirect.InputFile);
            }

            // Write output to file
            if (redirect.OutputFile != null)
            {
                var outputPath = ExpandPathStatic(redirect.OutputFile);
                var content = result.Output ?? "";
                // Strip ANSI codes for file output
                content = Markup.Remove(content);

                if (redirect.Append)
                    await File.AppendAllTextAsync(outputPath, content + Environment.NewLine);
                else
                    await File.WriteAllTextAsync(outputPath, content + Environment.NewLine);

                return new ShellResult(ResultType.Jitzu, "", null);
            }

            return result;
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private async Task<ShellResult> ExecuteOsCommandCapturedAsync(string[] args, string? inputFile)
    {
        try
        {
            var resolved = ResolveCommand(args[0]);
            var startInfo = new ProcessStartInfo
            {
                FileName = resolved ?? args[0],
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                RedirectStandardInput = inputFile != null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args[1..])
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process == null)
                return new ShellResult(ResultType.Error, "", new Exception($"Failed to start: {args[0]}"));

            if (inputFile != null)
            {
                var inputPath = ExpandPathStatic(inputFile);
                var inputContent = await File.ReadAllTextAsync(inputPath);
                await process.StandardInput.WriteAsync(inputContent);
                process.StandardInput.Close();
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            // stdout is redirected — child runs non-interactively, so Ctrl+C suppression is not needed.
            await process.WaitForExitAsync();

            return new ShellResult(ResultType.OsCommand, output.TrimEnd(), null);
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, "", new Exception($"Command failed: {ex.Message}", ex));
        }
    }

    /// <summary>
    /// Expands glob patterns (*, ?, **) in command arguments.
    /// </summary>
    private static string[] ExpandGlobs(string[] args)
    {
        if (args.Length == 0) return args;

        var expanded = new List<string> { args[0] }; // Don't expand the command itself

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];

            // Skip if no glob chars or if quoted
            if (!ContainsGlobChars(arg) || arg.StartsWith('"') || arg.StartsWith('\''))
            {
                expanded.Add(arg);
                continue;
            }

            var matches = ExpandSingleGlob(arg);
            if (matches.Length > 0)
                expanded.AddRange(matches);
            else
                expanded.Add(arg); // No matches — keep the original pattern
        }

        return expanded.ToArray();
    }

    private static bool ContainsGlobChars(string arg)
    {
        foreach (var ch in arg)
            if (ch is '*' or '?') return true;
        return false;
    }

    private static string[] ExpandSingleGlob(string pattern)
    {
        try
        {
            // Expand tilde
            if (pattern.StartsWith('~'))
                pattern = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), pattern[1..]);

            // Split into directory and file pattern
            var dir = Path.GetDirectoryName(pattern);
            var filePattern = Path.GetFileName(pattern);

            if (string.IsNullOrEmpty(dir))
                dir = ".";

            dir = Path.GetFullPath(dir);

            if (!Directory.Exists(dir))
                return [];

            // Handle ** recursive pattern
            var searchOption = pattern.Contains("**")
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            if (pattern.Contains("**"))
            {
                // For **/*.cs style patterns, search recursively from the base dir
                var baseParts = pattern.Split(["**"], 2, StringSplitOptions.None);
                var baseDir = baseParts[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.IsNullOrEmpty(baseDir)) baseDir = ".";
                baseDir = Path.GetFullPath(baseDir);

                var subPattern = baseParts.Length > 1 ? baseParts[1].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : "*";

                if (!Directory.Exists(baseDir))
                    return [];

                return Directory.GetFileSystemEntries(baseDir, subPattern, SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(Environment.CurrentDirectory, p))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            var results = new List<string>();

            // Match files
            results.AddRange(Directory.GetFiles(dir, filePattern));

            // Match directories
            results.AddRange(Directory.GetDirectories(dir, filePattern));

            return results
                .Select(p => Path.GetRelativePath(Environment.CurrentDirectory, p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private string ExpandPathStatic(string path)
    {
        if (labelManager is not null)
            path = labelManager.ExpandLabel(path);
        if (path.StartsWith('~'))
            path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..]);
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Expands $(command) substitutions by executing the inner command and replacing with its trimmed stdout.
    /// Supports nesting: $(echo $(pwd))
    /// </summary>
    private async Task<string> ExpandCommandSubstitutionsAsync(string input)
    {
        if (!input.Contains("$("))
            return input;

        var sb = new StringBuilder(input.Length);
        var inSingleQuote = false;
        var i = 0;

        while (i < input.Length)
        {
            var ch = input[i];

            if (ch == '\'' && !inSingleQuote) { inSingleQuote = true; sb.Append(ch); i++; continue; }
            if (ch == '\'' && inSingleQuote) { inSingleQuote = false; sb.Append(ch); i++; continue; }

            if (ch == '$' && !inSingleQuote && i + 1 < input.Length && input[i + 1] == '(')
            {
                i += 2; // skip $(
                var depth = 1;
                var cmdStart = i;

                while (i < input.Length && depth > 0)
                {
                    if (input[i] == '(') depth++;
                    else if (input[i] == ')') depth--;
                    if (depth > 0) i++;
                }

                var innerCmd = input[cmdStart..i];
                if (i < input.Length) i++; // skip closing )

                // Recursively expand nested substitutions
                innerCmd = await ExpandCommandSubstitutionsAsync(innerCmd);

                // Execute and capture output
                var output = await CaptureCommandOutputAsync(innerCmd);
                sb.Append(output.TrimEnd('\n', '\r'));
                continue;
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }

    private ShellResult LaunchBackgroundJob(string command)
    {
        // Clean up finished jobs
        _jobs.RemoveAll(j => j.Process.HasExited && j.Notified);

        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd" : "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(isWindows ? "/c" : "-c");
            startInfo.ArgumentList.Add(command);

            var process = Process.Start(startInfo);
            if (process == null)
                return new ShellResult(ResultType.Error, "", new Exception($"Failed to start background job: {command}"));

            var jobId = _nextJobId++;
            var job = new BackgroundJob(jobId, command, process);
            _jobs.Add(job);

            // Capture output asynchronously in background
            _ = Task.Run(async () =>
            {
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();
                job.Output = await outTask;
                job.ErrorOutput = await errTask;
            });

            return new ShellResult(ResultType.OsCommand, $"[{jobId}] {process.Id}", null);
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, "", new Exception($"Failed to start background job: {ex.Message}", ex));
        }
    }

    public ShellResult ListJobs()
    {
        _jobs.RemoveAll(j => j.Process.HasExited && j.Notified);

        if (_jobs.Count == 0)
            return new ShellResult(ResultType.OsCommand, "No active jobs.", null);

        var sb = new StringBuilder();
        foreach (var job in _jobs)
        {
            var status = job.Process.HasExited ? "Done" : "Running";
            sb.AppendLine($"[{job.Id}]  {status,-10} {job.Command}");
            if (job.Process.HasExited)
                job.Notified = true;
        }

        return new ShellResult(ResultType.OsCommand, sb.ToString().TrimEnd(), null);
    }

    public async Task<ShellResult> ForegroundJobAsync(int? jobId)
    {
        var job = jobId.HasValue
            ? _jobs.Find(j => j.Id == jobId.Value && !j.Process.HasExited)
            : _jobs.FindLast(j => !j.Process.HasExited);

        if (job == null)
            return new ShellResult(ResultType.Error, "", new Exception(jobId.HasValue ? $"No such job: {jobId}" : "No active jobs"));

        Console.WriteLine($"[{job.Id}]  {job.Command}");
        await job.Process.WaitForExitSuppressingCancelAsync();

        // Show captured output
        if (!string.IsNullOrWhiteSpace(job.Output))
            Console.Write(job.Output);
        if (!string.IsNullOrWhiteSpace(job.ErrorOutput))
            Console.Write(job.ErrorOutput);

        _jobs.Remove(job);

        return new ShellResult(ResultType.Jitzu, "", null);
    }

    /// <summary>
    /// Checks for completed background jobs and returns notifications.
    /// Call this from the REPL loop before showing the prompt.
    /// </summary>
    public string? CheckCompletedJobs()
    {
        var sb = new StringBuilder();
        foreach (var job in _jobs.Where(j => j.Process.HasExited && !j.Notified))
        {
            sb.AppendLine($"[{job.Id}]  Done    {job.Command}");

            // Show captured output
            if (!string.IsNullOrWhiteSpace(job.Output))
                sb.AppendLine(job.Output.TrimEnd());
            if (!string.IsNullOrWhiteSpace(job.ErrorOutput))
                sb.AppendLine(job.ErrorOutput.TrimEnd());

            job.Notified = true;
        }

        _jobs.RemoveAll(j => j.Process.HasExited && j.Notified);

        return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
    }

    private string ExpandAlias(string input)
    {
        if (aliasManager == null)
            return input;

        var trimmed = input.AsSpan().TrimStart();
        var spaceIndex = trimmed.IndexOf(' ');
        var firstWord = spaceIndex >= 0 ? trimmed[..spaceIndex].ToString() : trimmed.ToString();

        if (aliasManager.TryExpand(firstWord, out var expanded))
        {
            var rest = spaceIndex >= 0 ? trimmed[spaceIndex..].ToString() : "";
            return expanded + rest;
        }

        return input;
    }

    /// <summary>
    /// Expands $VAR and ${VAR} patterns, respecting single quotes (no expansion) and double quotes (expansion).
    /// </summary>
    private static string ExpandEnvironmentVariables(string input)
    {
        if (!input.Contains('$'))
            return input;

        var sb = new StringBuilder(input.Length);
        var inSingleQuote = false;
        var i = 0;

        while (i < input.Length)
        {
            var ch = input[i];

            if (ch == '\'' && !inSingleQuote)
            {
                inSingleQuote = true;
                sb.Append(ch);
                i++;
                continue;
            }

            if (ch == '\'' && inSingleQuote)
            {
                inSingleQuote = false;
                sb.Append(ch);
                i++;
                continue;
            }

            if (ch == '$' && !inSingleQuote && i + 1 < input.Length)
            {
                i++;
                string varName;

                if (input[i] == '{')
                {
                    // ${VAR} form
                    i++;
                    var start = i;
                    while (i < input.Length && input[i] != '}')
                        i++;
                    varName = input[start..i];
                    if (i < input.Length) i++; // skip '}'
                }
                else
                {
                    // $VAR form — consume [A-Za-z0-9_]
                    var start = i;
                    while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_'))
                        i++;
                    varName = input[start..i];
                }

                if (varName.Length > 0)
                {
                    var value = Environment.GetEnvironmentVariable(varName);
                    sb.Append(value ?? "");
                }
                else
                {
                    sb.Append('$');
                }

                continue;
            }

            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }

    private static bool LooksLikeCode(ReadOnlySpan<char> input)
    {
        try
        {
            var tokens = new Lexer("<repl>", input).Lex();
            if (tokens.FirstOrDefault() is { Value: "let" or "match" or "if" or "fun" or "for" or "return" or "while" or "type" })
                return true;

            var parser = new Parser(tokens);
            var firstExpression = parser.Parse().FirstOrDefault();
            return firstExpression
                is LetExpression
                or AssignmentExpression
                or SimpleMemberAccessExpression
                or WhileExpression
                or FunctionCallExpression
                or StringLiteral
                or BooleanLiteral
                or CharLiteral
                or DoubleLiteral
                or IntLiteral
                or IdentifierLiteral
                or TypeDefinitionExpression
                or NewDynamicExpression
                or BlockBodyExpression
                or BinaryExpression
                or FunctionDefinitionExpression
                or TagExpression;
        }
        catch
        {
            return false;
        }
    }

    private static ShellResult ShellExecute(string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true,
            };

            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error opening file: {ex.Message}");
        }
        return new ShellResult(ResultType.OsCommand, "", null);
    }

    private static async Task<ShellResult> ExecuteShellPipelineAsync(string input)
    {
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd" : "/bin/sh",
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            startInfo.ArgumentList.Add(isWindows ? "/c" : "-c");
            startInfo.ArgumentList.Add(input);

            using var process = Process.Start(startInfo);
            if (process == null)
                return new ShellResult(ResultType.Error, "", new Exception($"Failed to start shell pipeline"));

            await process.WaitForExitSuppressingCancelAsync();
            EnsurePromptStartsOnNewLine();
            return new ShellResult(ResultType.OsCommand, "", null);
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, "", new Exception($"Shell pipeline failed: {ex.Message}", ex));
        }
    }

    /// <summary>
    /// Resolves a bare command name to its full path by searching PATH with platform-appropriate extensions.
    /// On Windows, probes .exe, .cmd, .bat, .com, .ps1 (respecting PATHEXT if set).
    /// Returns null if the command cannot be found.
    /// </summary>
    private static string? ResolveCommand(string command)
    {
        // Already a rooted path or contains a directory separator — don't search PATH
        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command) ? command : null;

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT")?.Split(Path.PathSeparator) ?? [".EXE", ".CMD", ".BAT", ".COM", ".PS1"])
            : [""];

        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        foreach (var dir in paths)
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, command + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static async Task<ShellResult> ExecuteOsCommandAsync(string[] args)
    {
        try
        {
            var resolved = ResolveCommand(args[0]);
            var startInfo = new ProcessStartInfo
            {
                FileName = resolved ?? args[0],
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = false,
                Environment =
                {
                    // Ensure color support by setting environment variables
                    ["TERM"] = Environment.GetEnvironmentVariable("TERM") ?? "xterm-256color",
                    ["COLORTERM"] = "truecolor"
                }
            };

            foreach (var arg in args[1..])
                startInfo.ArgumentList.Add(arg);

            // Copy LS_COLORS if available
            var lsColors = Environment.GetEnvironmentVariable("LS_COLORS");
            if (!string.IsNullOrEmpty(lsColors))
                startInfo.Environment["LS_COLORS"] = lsColors;

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new ShellResult(
                    ResultType.Error,
                    "",
                    new Exception($"Failed to start shell process: {args[0]}")
                );
            }

            await process.WaitForExitSuppressingCancelAsync();
            EnsurePromptStartsOnNewLine();

            // Return empty output since we let the command write directly to console
            return new ShellResult(ResultType.OsCommand, "", null);
        }
        catch (Exception ex)
        {
            return new ShellResult(
                ResultType.Error,
                "",
                new Exception($"Command not found or failed to execute: {args[0]}", ex)
            );
        }
    }

    /// <summary>
    /// Detects a hybrid pipeline where OS commands pipe into Jitzu utility functions.
    /// Returns the OS command portion and the Jitzu function segments, or null if not hybrid.
    /// </summary>
    private static (string OsCommand, string[] JitzuSegments)? DetectHybridPipeline(string input)
    {
        var pipePositions = FindTopLevelPipes(input);
        if (pipePositions.Count == 0)
            return null;

        // Walk left-to-right: find first pipe where the right side starts with a known function
        for (var i = 0; i < pipePositions.Count; i++)
        {
            var rightPart = input[(pipePositions[i] + 1)..].TrimStart();
            var funcName = ExtractLeadingIdentifier(rightPart);

            if (funcName != null && PipeFunctions.Contains(funcName))
            {
                // Everything left of this pipe is the OS command
                var osCommand = input[..pipePositions[i]].Trim();
                // Everything from this pipe onward is Jitzu segments
                var jitzuPart = input[(pipePositions[i] + 1)..];
                var segments = SplitJitzuSegments(jitzuPart, pipePositions, i);
                return (osCommand, segments);
            }
        }

        return null;
    }

    /// <summary>
    /// Finds positions of top-level pipe characters, respecting quotes and parentheses.
    /// </summary>
    private static List<int> FindTopLevelPipes(string input)
    {
        var positions = new List<int>();
        var parenDepth = 0;
        var inDoubleQuote = false;
        var inSingleQuote = false;

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            switch (ch)
            {
                case '"' when !inSingleQuote:
                    inDoubleQuote = !inDoubleQuote;
                    break;
                case '\'' when !inDoubleQuote:
                    inSingleQuote = !inSingleQuote;
                    break;
                case '(' when !inDoubleQuote && !inSingleQuote:
                    parenDepth++;
                    break;
                case ')' when !inDoubleQuote && !inSingleQuote:
                    parenDepth--;
                    break;
                case '|' when !inDoubleQuote && !inSingleQuote && parenDepth == 0:
                    positions.Add(i);
                    break;
            }
        }

        return positions;
    }

    private static string? ExtractLeadingIdentifier(ReadOnlySpan<char> text)
    {
        var i = 0;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            i++;
        return i > 0 ? text[..i].ToString() : null;
    }

    /// <summary>
    /// Splits the Jitzu portion of the pipeline into individual segments.
    /// </summary>
    private static string[] SplitJitzuSegments(string jitzuPart, List<int> allPipes, int startPipeIndex)
    {
        // The jitzuPart is everything after the boundary pipe.
        // We need to split it by remaining top-level pipes relative to jitzuPart.
        var segments = new List<string>();
        var innerPipes = FindTopLevelPipes(jitzuPart);

        var start = 0;
        foreach (var pos in innerPipes)
        {
            segments.Add(jitzuPart[start..pos].Trim());
            start = pos + 1;
        }
        segments.Add(jitzuPart[start..].Trim());

        return segments.ToArray();
    }

    /// <summary>
    /// Captures stdout from an OS command or builtin, returning the text output.
    /// </summary>
    private async Task<string> CaptureCommandOutputAsync(string command)
    {
        var trimmed = command.Trim();
        var cmdArgs = CommandLineParser.SplitCommandLine(trimmed).ToArray();

        // Check if it's a shell builtin (like Windows 'ls')
        if (builtins.IsBuiltin(cmdArgs[0]))
        {
            var builtinResult = await builtins.ExecuteAsync(cmdArgs[0], cmdArgs[1..].AsMemory());
            return Markup.Remove(builtinResult.Output ?? "");
        }

        // Otherwise, run as OS command with captured stdout
        try
        {
            var isWindows = OperatingSystem.IsWindows();

            // Use shell to handle full command strings (including args, flags, etc.)
            var startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd" : "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(isWindows ? "/c" : "-c");
            startInfo.ArgumentList.Add(trimmed);

            using var process = Process.Start(startInfo);
            if (process == null)
                return "";

            var output = await process.StandardOutput.ReadToEndAsync();
            // stdout is redirected — child runs non-interactively, so Ctrl+C suppression is not needed.
            await process.WaitForExitAsync();
            return output;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Executes a hybrid pipeline: captures OS command output then chains through Jitzu functions.
    /// Uses streaming for efficient processing with early termination support.
    /// </summary>
    private async Task<ShellResult> ExecuteHybridPipelineAsync(string osCommand, string[] jitzuSegments)
    {
        try
        {
            // Start with streaming from the OS command
            var stream = StreamCommandOutputAsync(osCommand);
            var cts = new CancellationTokenSource();

            try
            {
                // Chain through each Jitzu segment using streaming
                foreach (var segment in jitzuSegments)
                {
                    var (funcName, args) = ParsePipeSegment(segment);
                    if (funcName == null || !PipeFunctions.Contains(funcName))
                        return new ShellResult(ResultType.Error, "", new Exception($"Unknown pipe function: {segment}"));

                    stream = InvokeStreamingPipeFunction(funcName, stream, args, cts.Token);
                }

                // For 'print', the function already writes to console during streaming
                // For other functions, materialize the result
                if (jitzuSegments.Length > 0)
                {
                    var lastFunc = ParsePipeSegment(jitzuSegments[^1]).FuncName;
                    if (lastFunc is "print" or "tee")
                    {
                        // Consume the stream to execute it
                        await foreach (var _ in stream.WithCancellation(cts.Token)) { }
                        return new ShellResult(ResultType.OsCommand, "", null);
                    }
                }

                // Materialize the stream to a string for output
                var result = await StreamingPipeline.MaterializeAsync(stream, cts.Token);
                return new ShellResult(ResultType.Jitzu, result, null);
            }
            finally
            {
                cts.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // Early termination is normal for functions like 'first' or 'head'
            return new ShellResult(ResultType.OsCommand, "", null);
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, "", new Exception($"Pipe execution failed: {ex.Message}", ex));
        }
    }

    /// <summary>
    /// Parses a pipe segment like "grep("test")", "grep "test"", or "first" into function name and arguments.
    /// Supports both Jitzu-style parens and shell-style space-separated args.
    /// </summary>
    private static (string? FuncName, object[] Args) ParsePipeSegment(string segment)
    {
        try
        {
            var trimmed = segment.TrimStart();
            if (trimmed.Length == 0)
                return (null, []);

            // Extract leading identifier as function name
            var nameEnd = 0;
            while (nameEnd < trimmed.Length && (char.IsLetterOrDigit(trimmed[nameEnd]) || trimmed[nameEnd] == '_'))
                nameEnd++;
            if (nameEnd == 0)
                return (null, []);

            var funcName = trimmed[..nameEnd];
            var rest = trimmed[nameEnd..].TrimStart();

            if (rest.StartsWith('('))
            {
                // Jitzu-style: grep("test", 2) — use the language lexer to handle literals/commas.
                var tokens = new Lexer("<pipe>", rest).Lex();
                var args = new List<object>();
                var i = 1; // skip '('
                while (i < tokens.Count && tokens[i].Value != ")")
                {
                    var token = tokens[i];
                    if (token.Value == ",")
                    {
                        i++;
                        continue;
                    }
                    args.Add(ParseTokenValue(token));
                    i++;
                }
                return (funcName, args.ToArray());
            }

            // Shell-style: tee output.txt, grep "test", nth 2.
            // Use CommandLineParser so that filenames with dots/slashes survive intact;
            // the Jitzu lexer would split `output.txt` into three tokens.
            var shellArgs = CommandLineParser.SplitCommandLine(rest)
                .Select(s => (object)(int.TryParse(s, out var n) ? n : s))
                .ToArray();
            return (funcName, shellArgs);
        }
        catch
        {
            return (null, []);
        }
    }

    private static object ParseTokenValue(Jitzu.Core.Language.Token token)
    {
        if (token.Type is Jitzu.Core.Language.TokenType.String)
            return token.Value[1..^1]; // strip surrounding quotes
        if (token.Type is Jitzu.Core.Language.TokenType.Int && int.TryParse(token.Value, out var intVal))
            return intVal;
        if (token.Type is Jitzu.Core.Language.TokenType.Double && double.TryParse(token.Value, out var dblVal))
            return dblVal;
        return token.Value;
    }

    private string InvokePipeFunction(string funcName, string input, object[] args)
    {
        return funcName switch
        {
            "first" => GlobalFunctions.FirstStatic(input),
            "last" => GlobalFunctions.LastStatic(input),
            "nth" => GlobalFunctions.NthStatic(input, args.Length > 0 ? Convert.ToInt32(args[0]) : 0),
            "grep" => GlobalFunctions.GrepStatic(input, args.Length > 0 ? args[0].ToString()! : ""),
            "print" => PrintAndReturn(input),
            "head" => HeadLines(input, args),
            "tail" => TailLines(input, args),
            "sort" => SortLines(input, args),
            "uniq" => UniqLines(input),
            "wc" => WordCount(input, args),
            "more" or "less" => PagerAndReturn(input),
            "tee" => TeeAndReturn(input, args),
            _ => input,
        };
    }

    private string PagerAndReturn(string input)
    {
        builtins.SetPagerInput(input);
        builtins.ExecuteAsync("more", ReadOnlyMemory<string>.Empty).GetAwaiter().GetResult();
        return input;
    }

    private string TeeAndReturn(string input, object[] args)
    {
        builtins.SetTeeInput(input);
        var teeArgs = args.Select(a => a.ToString()!).ToArray();
        builtins.ExecuteAsync("tee", teeArgs.AsMemory()).GetAwaiter().GetResult();
        return input;
    }

    /// <summary>
    /// Extracts the pattern from grep args: the first argument that is not a flag.
    /// </summary>
    private static string ParseGrepPattern(object[] args)
    {
        foreach (var arg in args)
        {
            var s = arg.ToString()!;
            if (!s.StartsWith('-'))
                return s;
        }
        return "";
    }

    /// <summary>
    /// Returns true if the -i / --ignore-case flag is present in grep args.
    /// </summary>
    private static bool ParseGrepIgnoreCase(object[] args) =>
        args.Any(a => a is "-i" or "--ignore-case");

    private static int ParseLineCount(object[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "-n" or "--lines" && i + 1 < args.Length)
                return Convert.ToInt32(args[i + 1]);
            if (args[i] is int n)
                return n;
        }
        return 10;
    }

    private static string HeadLines(string input, object[] args)
    {
        var count = ParseLineCount(args);
        var lines = input.Split('\n');
        return string.Join('\n', lines.Take(count));
    }

    private static string TailLines(string input, object[] args)
    {
        var count = ParseLineCount(args);
        var lines = input.Split('\n');
        return string.Join('\n', lines.TakeLast(count));
    }

    /// <summary>
    /// Finds the position of the first top-level pipe character (not inside quotes/parens).
    /// </summary>
    private static int FindFirstTopLevelPipe(string input)
    {
        var pipes = FindTopLevelPipes(input);
        return pipes.Count > 0 ? pipes[0] : -1;
    }

    /// <summary>
    /// Executes a builtin command and pipes its output into an OS command or pipe function chain.
    /// e.g. diff file1 file2 | more, or cat file.txt | grep("pattern")
    /// </summary>
    private async Task<ShellResult> ExecuteBuiltinPipelineAsync(string builtinCmd, string rightSide)
    {
        // Execute the builtin and capture its output
        var builtinArgs = CommandLineParser.SplitCommandLine(builtinCmd).ToArray();
        builtinArgs = ExpandGlobs(builtinArgs);
        var builtinResult = await builtins.ExecuteAsync(builtinArgs[0], builtinArgs[1..].AsMemory());

        var output = Markup.Remove(builtinResult.Output ?? "");
        if (string.IsNullOrEmpty(output))
            return builtinResult;

        // Check if the right side is a Jitzu pipe function chain
        // If piping into our builtin pager (more/less), feed via SetPagerInput
        var rightTrimmed = rightSide.Trim();
        if (rightTrimmed is "more" or "less")
        {
            builtins.SetPagerInput(output);
            return await builtins.ExecuteAsync(rightTrimmed, ReadOnlyMemory<string>.Empty);
        }

        // If piping into tee, feed via SetTeeInput
        if (rightTrimmed.StartsWith("tee ") || rightTrimmed == "tee")
        {
            builtins.SetTeeInput(output);
            var teeArgs = CommandLineParser.SplitCommandLine(rightTrimmed).Skip(1).ToArray();
            return await builtins.ExecuteAsync("tee", teeArgs.AsMemory());
        }

        var rightSegments = rightSide.Split('|').Select(s => s.Trim()).ToArray();
        var firstFunc = ExtractLeadingIdentifier(rightSegments[0]);
        if (firstFunc != null && PipeFunctions.Contains(firstFunc))
        {
            // Chain through Jitzu pipe functions using streaming
            var stream = StreamingPipeline.StreamFromStringAsync(output);
            var cts = new CancellationTokenSource();

            try
            {
                foreach (var segment in rightSegments)
                {
                    var (funcName, funcArgs) = ParsePipeSegment(segment);
                    if (funcName == null || !PipeFunctions.Contains(funcName))
                        return new ShellResult(ResultType.Error, "", new Exception($"Unknown pipe function: {segment}"));
                    stream = InvokeStreamingPipeFunction(funcName, stream, funcArgs, cts.Token);
                }

                if (rightSegments.Length > 0)
                {
                    var lastFunc = ParsePipeSegment(rightSegments[^1]).FuncName;
                    if (lastFunc is "print" or "tee")
                    {
                        // Consume the stream to execute it
                        await foreach (var _ in stream.WithCancellation(cts.Token)) { }
                        return new ShellResult(ResultType.OsCommand, "", null);
                    }
                }

                var result = await StreamingPipeline.MaterializeAsync(stream, cts.Token);
                return new ShellResult(ResultType.Jitzu, result, null);
            }
            finally
            {
                cts.Dispose();
            }
        }

        // Pipe into OS command via stdin
        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd" : "/bin/sh",
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            startInfo.ArgumentList.Add(isWindows ? "/c" : "-c");
            startInfo.ArgumentList.Add(rightSide);

            using var process = Process.Start(startInfo);
            if (process == null)
                return new ShellResult(ResultType.Error, "", new Exception($"Failed to start: {rightSide}"));

            // Write stdin in background — the receiver (e.g. more, less) may exit early
            // when the user presses 'q', which breaks the pipe. That's normal.
            _ = Task.Run(async () =>
            {
                try
                {
                    await process.StandardInput.WriteAsync(output);
                    process.StandardInput.Close();
                }
                catch (IOException) { } // broken pipe — receiver quit early
            });

            await process.WaitForExitSuppressingCancelAsync();
            EnsurePromptStartsOnNewLine();
            return new ShellResult(ResultType.OsCommand, "", null);
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, "", new Exception($"Pipe failed: {ex.Message}", ex));
        }
    }

    private async Task<ShellResult> ExecuteSourceFileAsync(string filePath)
    {
        var path = ExpandPathStatic(filePath);
        if (!File.Exists(path))
            return new ShellResult(ResultType.Error, "", new Exception($"File not found: {filePath}"));

        try
        {
            var lines = await File.ReadAllLinesAsync(path);
            ShellResult lastResult = new(ResultType.Jitzu, "", null);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//"))
                    continue;
                lastResult = await ExecuteAsync(line);
            }

            return lastResult;
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, "", new Exception($"Error sourcing {filePath}: {ex.Message}", ex));
        }
    }

    private static string SortLines(string input, object[] args)
    {
        var lines = input.Split('\n');
        var reverse = args.Any(a => a is "-r" or "--reverse");
        var sorted = reverse
            ? lines.OrderByDescending(l => l, StringComparer.OrdinalIgnoreCase)
            : lines.OrderBy(l => l, StringComparer.OrdinalIgnoreCase);
        return string.Join('\n', sorted);
    }

    private static string UniqLines(string input)
    {
        var lines = input.Split('\n');
        var result = new List<string>();
        string? prev = null;
        foreach (var line in lines)
        {
            if (line != prev)
                result.Add(line);
            prev = line;
        }
        return string.Join('\n', result);
    }

    private static string WordCount(string input, object[] args)
    {
        var lines = input.Split('\n');
        var lineCount = lines.Length;
        var wordCount = input.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        var charCount = input.Length;

        var showLines = args.Any(a => a is "-l" or "--lines");
        var showWords = args.Any(a => a is "-w" or "--words");
        var showChars = args.Any(a => a is "-c" or "--chars");

        // If no flags, show all
        if (!showLines && !showWords && !showChars)
            return $"{lineCount}\t{wordCount}\t{charCount}";

        var parts = new List<string>();
        if (showLines) parts.Add(lineCount.ToString());
        if (showWords) parts.Add(wordCount.ToString());
        if (showChars) parts.Add(charCount.ToString());
        return string.Join('\t', parts);
    }

    private static string PrintAndReturn(string input)
    {
        Console.WriteLine(input);
        return input;
    }

    /// <summary>
    /// Streams output from an OS command or builtin line-by-line.
    /// Supports early termination via cancellation token.
    /// </summary>
    private async IAsyncEnumerable<string> StreamCommandOutputAsync(
        string command,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var trimmed = command.Trim();
        var cmdArgs = CommandLineParser.SplitCommandLine(trimmed).ToArray();

        // Check if it's a shell builtin
        if (builtins.IsBuiltin(cmdArgs[0]))
        {
            var builtinResult = await builtins.ExecuteAsync(cmdArgs[0], cmdArgs[1..].AsMemory());
            var output = Markup.Remove(builtinResult.Output ?? "");

            await foreach (var line in StreamingPipeline.StreamFromStringAsync(output, cancellationToken))
            {
                yield return line;
            }
            yield break;
        }

        // Stream from OS command
        var isWindows = OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(isWindows ? "/c" : "-c");
        startInfo.ArgumentList.Add(trimmed);

        using var process = Process.Start(startInfo);
        if (process == null)
            yield break;

        await foreach (var line in StreamingPipeline.StreamFromProcessAsync(process, cancellationToken))
        {
            yield return line;
        }

        // stdout is redirected — child is not the console foreground group,
        // so it will not receive SIGINT from Ctrl+C. No suppression needed.
        if (!cancellationToken.IsCancellationRequested)
            await process.WaitForExitAsync(cancellationToken);
    }

    /// <summary>
    /// Invokes a streaming pipe function, returning an IAsyncEnumerable for chaining.
    /// </summary>
    private IAsyncEnumerable<string> InvokeStreamingPipeFunction(
        string funcName,
        IAsyncEnumerable<string> stream,
        object[] args,
        CancellationToken cancellationToken)
    {
        return funcName switch
        {
            "first" => StreamingPipeFunctions.FirstAsync(stream, cancellationToken),
            "last" => StreamingPipeFunctions.LastAsync(stream, cancellationToken),
            "nth" => StreamingPipeFunctions.NthAsync(stream, args.Length > 0 ? Convert.ToInt32(args[0]) : 0, cancellationToken),
            "grep" => StreamingPipeFunctions.GrepAsync(stream, ParseGrepPattern(args), ParseGrepIgnoreCase(args), cancellationToken),
            "head" => StreamingPipeFunctions.HeadAsync(stream, ParseLineCount(args), cancellationToken),
            "tail" => StreamingPipeFunctions.TailAsync(stream, ParseLineCount(args), cancellationToken),
            "sort" => StreamingPipeFunctions.SortAsync(stream, args.Any(a => a is "-r" or "--reverse"), cancellationToken),
            "uniq" => StreamingPipeFunctions.UniqAsync(stream, cancellationToken),
            "wc" => StreamingPipeFunctions.WcAsync(
                stream,
                linesOnly: args.Any(a => a is "-l" or "--lines"),
                wordsOnly: args.Any(a => a is "-w" or "--words"),
                charsOnly: args.Any(a => a is "-c" or "--chars"),
                files: args.Any(a => a is "--files" or "--each-file"),
                cancellationToken),
            "sum" => StreamingPipeFunctions.SumAsync(stream, cancellationToken),
            "avg" => StreamingPipeFunctions.AverageAsync(stream, cancellationToken),
            "min" => StreamingPipeFunctions.MinAsync(stream, cancellationToken),
            "max" => StreamingPipeFunctions.MaxAsync(stream, cancellationToken),
            "count" => StreamingPipeFunctions.CountAsync(stream, cancellationToken),
            "print" => PrintStreamAsync(stream, cancellationToken),
            "tee" => StreamingPipeFunctions.TeeAsync(stream, args.Length > 0 ? args[0].ToString() : null, cancellationToken),
            "more" or "less" => PagerStreamAsync(stream, cancellationToken),
            _ => stream, // Pass through if unknown
        };
    }

    /// <summary>
    /// Prints each line from the stream to console and passes it through.
    /// </summary>
    private async IAsyncEnumerable<string> PrintStreamAsync(
        IAsyncEnumerable<string> stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var line in stream.WithCancellation(cancellationToken))
        {
            Console.WriteLine(line);
            yield return line;
        }
    }

    /// <summary>
    /// Displays stream content in the pager.
    /// </summary>
    private async IAsyncEnumerable<string> PagerStreamAsync(
        IAsyncEnumerable<string> stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Materialize to array for pager (pager needs random access)
        var lines = await StreamingPipeline.MaterializeToArrayAsync(stream, cancellationToken);
        builtins.SetPagerInput(string.Join('\n', lines));
        await builtins.ExecuteAsync("more", ReadOnlyMemory<string>.Empty);

        // Return the lines for potential further processing
        foreach (var line in lines)
            yield return line;
    }

    private static void EnsurePromptStartsOnNewLine()
    {
        if (Console.IsOutputRedirected)
            return;

        try
        {
            if (Console.CursorLeft != 0)
                Console.WriteLine();
        }
        catch (IOException)
        {
            // Some terminal hosts do not expose cursor position.
        }
    }

}

public record ShellResult(ResultType Type, string? Output, Exception? Error);

public record RedirectionInfo(string? OutputFile, string? InputFile, bool Append)
{
    public bool HasRedirection => OutputFile != null || InputFile != null;
}

public enum ResultType
{
    Jitzu,
    OsCommand,
    Error
}

public class BackgroundJob(int id, string command, Process process)
{
    public int Id { get; } = id;
    public string Command { get; } = command;
    public Process Process { get; } = process;
    public bool Notified { get; set; }
    public string? Output { get; set; }
    public string? ErrorOutput { get; set; }
}
