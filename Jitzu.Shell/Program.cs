using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Jitzu.Shell.Core;
using Jitzu.Shell.UI;
using Jitzu.Shell;
using Jitzu.Core;
using Jitzu.Core.Common.Logging;
using Jitzu.Core.Language;
using Jitzu.Core.Logging;
using Jitzu.Core.Runtime;
using Jitzu.Core.Runtime.Compilation;
using Jitzu.Shell.Infrastructure.Logging;
using Jitzu.Shell.Models;
using System.Reflection;

Console.OutputEncoding = Encoding.UTF8;
EnableAnsiSupport();

var (hostArgs, scriptArgs) = JitzuOptions.SplitArgs(args);
var options = JitzuOptions.Parse(hostArgs);
if (scriptArgs.Length > 0)
    options.ScriptArgs = scriptArgs;

// Clean up leftover upgrade file from previous self-update (Windows)
CleanupOldBinary();

DebugLogger.SetIsEnabled(options.Debug);
Telemetry.SetIsEnabled(options.Telemetry);

// 1. Sudo gate — must be first, re-launched elevated child
if (options.SudoExec is not null || options.SudoShell || options.SudoLogin)
{
    await HandleElevatedEntry(options);
    return;
}

// 2. --install-path → print dir, exit
if (options.InstallPath)
{
    Console.WriteLine(AppDomain.CurrentDomain.BaseDirectory);
    return;
}

// 3. -c "command" → execute via Shell's ExecutionStrategy
if (options.Command is { } command)
{
    await ExecuteCommand(command, options.Persist);
    return;
}

// 4. ScriptPath == "upgrade" → self-update
if (options.ScriptPath is "upgrade")
{
    await Jitzu.Shell.Infrastructure.Update.SelfUpdater.RunAsync(force: false);
    return;
}

// 5. ScriptPath exists → full compilation pipeline (Interpreter path)
if (options.ScriptPath is { } scriptPath)
{
    if (File.Exists(Path.ChangeExtension(scriptPath, "jz")) || File.Exists(scriptPath))
    {
        var exitCode = await RunScript(scriptPath, options);
        Console.Out.Flush();
        Environment.Exit(exitCode);
        return;
    }

    Console.WriteLine($"File not found: {scriptPath}");
    Environment.Exit(1);
    return;
}

// 6. Default (no args) → Shell REPL
await RunReplAsync(options);
return;

async Task<int> RunScript(string filePath, JitzuOptions opts)
{
    ConsoleEx.ConfigureOutput();

    var entryPointPath = Path.ChangeExtension(filePath, "jz");

    if (entryPointPath.StartsWith('~'))
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        entryPointPath = Path.Join(profile, entryPointPath[1..]);
    }

    if (!File.Exists(entryPointPath))
    {
        Console.WriteLine($"Entry point: {entryPointPath} does not exist");
        return 1;
    }

    var entryPoint = new FileInfo(entryPointPath);
    if (entryPoint.Length is 0)
        return 0;

    try
    {
        DebugLogger.WriteLine("Running Jitzu Interpreter");

        var ast = ParseProgram(entryPoint);
        var program = await ProgramBuilder.Build(ast);
        var analyser = new SemanticAnalyser(program);
        ast = analyser.AnalyseScript(ast);

        if (opts.Debug)
            Console.WriteLine(ExpressionFormatter.Format(ast));

        var script = new ByteCodeCompiler(program).Compile(ast.Body);
        if (opts.BytecodeOutputPath is not null)
            ByteCodeWriter.WriteToFile(opts.BytecodeOutputPath, script);

        var interpreter = new ByteCodeInterpreter(program, script, opts.ScriptArgs, opts.Debug);
        interpreter.Evaluate();
        return 0;
    }
    catch (JitzuException ex)
    {
        ExceptionPrinter.Print(ex);
        return 1;
    }
}

static ScriptExpression ParseProgram(FileInfo entryPoint)
{
    DebugLogger.WriteLine($"Parsing: {entryPoint.FullName}");
    if (entryPoint.Length is 0)
    {
        DebugLogger.WriteLine("File is empty... skipping");
        return ScriptExpression.Empty;
    }

    var startTime = Stopwatch.GetTimestamp();
    try
    {
        ReadOnlySpan<char> fileContents = File.ReadAllText(entryPoint.FullName);
        if (fileContents.Length is 0)
        {
            DebugLogger.WriteLine("File is empty... skipping");
            return ScriptExpression.Empty;
        }

        StatsLogger.LogTime("File Read", Stopwatch.GetElapsedTime(startTime));

        startTime = Stopwatch.GetTimestamp();
        var lexer = new Lexer(Path.GetFullPath(entryPoint.FullName), fileContents);
        var tokens = lexer.Lex();
        StatsLogger.LogTime("Lexing", Stopwatch.GetElapsedTime(startTime));

        DebugLogger.WriteTokens(tokens);

        startTime = Stopwatch.GetTimestamp();
        var parser = new Parser(tokens);
        var program = new ScriptExpression
        {
            Body = parser.Parse(),
        };

        return program;
    }
    finally
    {
        StatsLogger.LogTime("Parsing", Stopwatch.GetElapsedTime(startTime));
    }
}

static async Task ExecuteCommand(string command, bool persist)
{
    var theme = await ThemeConfig.LoadAsync();
    var session = await ShellSession.CreateAsync();
    var aliasManager = new AliasManager(persist);
    await aliasManager.InitialiseAsync();
    var labelManager = new LabelManager();
    var builtins = new BuiltinCommands(session, theme, aliasManager, labelManager);
    var strategy = new ExecutionStrategy(session, builtins, aliasManager, labelManager);
    builtins.SetStrategy(strategy);

    var result = await strategy.ExecuteAsync(command);

    DisplayResult(result, theme);

    Environment.Exit(result.Error is null ? 0 : 1);
}

static async Task RunReplAsync(JitzuOptions options)
{
    // Initialize session and components
    var theme = await ThemeConfig.LoadAsync();
    var session = await ShellSession.CreateAsync();
    var persist = options.Persist;
    var history = new HistoryManager(persist);
    var aliasManager = new AliasManager(persist);
    var labelManager = new LabelManager();
    var builtins = new BuiltinCommands(session, theme, aliasManager, labelManager, history);
    var strategy = new ExecutionStrategy(session, builtins, aliasManager, labelManager);
    builtins.SetStrategy(strategy);
    var completionManager = new CompletionManager(session, builtins, labelManager);
    var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    await history.InitialiseAsync();
    await aliasManager.InitialiseAsync();
    var readLine = new ReadLine(history, theme, completionManager.GetCompletions,
        prediction => HistoryPredictionFilter.IsValid(prediction, Directory.GetCurrentDirectory()));

    // Load config file (~/.jitzu/config.jz) like .bashrc
    var configPath = Path.Combine(userProfilePath, ".jitzu", "config.jz");
    if (File.Exists(configPath))
    {
        try
        {
            var configLines = await File.ReadAllLinesAsync(configPath);
            foreach (var line in configLines)
            {
                if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("//"))
                    await strategy.ExecuteAsync(line);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{theme["error"]}Error loading config: {ex.Message}{ThemeConfig.Reset}");
        }
    }

    // Display welcome banner
    if (options.Splash)
    {
        PrintSplash();
    }

    // State tracked between iterations for enhanced prompt
    var lastCommandSuccess = true;
    var lastCommandDuration = TimeSpan.Zero;
    var user = Environment.UserName;
    var host = Environment.MachineName;
    var gitCache = new GitStatusCache();
    var promptSb = new StringBuilder();
    var cachedPadding = "";

    // Detect if running elevated (for prompt indicator)
    var isElevated = false;
    if (OperatingSystem.IsWindows())
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    // When stdin is redirected (piped input), skip the interactive prompt and read lines directly
    var isInteractive = !Console.IsInputRedirected;

    // Main REPL loop
    while (true)
    {
        try
        {
            string? line;

            if (!isInteractive)
            {
                line = Console.ReadLine();
                if (line is null)
                    return;
            }
            else
            {
                Console.Write("\e[?25l"); // hide cursor during prompt build + render

                // Notify about completed background jobs
                var jobNotice = strategy.CheckCompletedJobs();
                if (jobNotice is not null)
                    Console.WriteLine(jobNotice);

                var dir = Environment.CurrentDirectory.Replace(userProfilePath, "~");

                // Trims path to root of Git repository
                var gitRepoRoot = gitCache.FindGitRepoFolder(Environment.CurrentDirectory);
                if (gitRepoRoot is not null)
                    dir = dir.Replace(gitRepoRoot.FullName, gitRepoRoot.Name);

                var branchSuffix = "";
                if (gitRepoRoot is not null)
                {
                    var branch = gitCache.GetGitBranch(gitRepoRoot.FullName);
                    if (branch is not null)
                    {
                        var status = await GetGitStatusAsync(gitRepoRoot.FullName);

                        promptSb.Clear();
                        if (status.HasDirty) promptSb.Append($"{theme["git.dirty"]}*{ThemeConfig.Reset}");
                        if (status.HasStaged) promptSb.Append($"{theme["git.staged"]}+{ThemeConfig.Reset}");
                        if (status.HasUntracked) promptSb.Append($"{theme["git.untracked"]}?{ThemeConfig.Reset}");
                        var statusStr = promptSb.Length > 0 ? $" {promptSb}" : "";

                        promptSb.Clear();
                        if (status.Ahead > 0) promptSb.Append($"↑{status.Ahead}");
                        if (status.Behind > 0) promptSb.Append($"↓{status.Behind}");
                        var remoteStr = promptSb.Length > 0 ? $" {theme["git.remote"]}{promptSb}{ThemeConfig.Reset}" : "";

                        branchSuffix = $" {theme["git.branch"]}({branch}){ThemeConfig.Reset}{statusStr}{remoteStr}";
                    }
                }

                // Build line 1: user@host dir (branch)*+? ↑1          HH:mm
                var elevatedTag = isElevated ? $" {theme["prompt.error"]}[sudo]{ThemeConfig.Reset}" : "";
                var leftPart = $"{theme["prompt.user"]}{user}@{host}{ThemeConfig.Reset} {theme["prompt.directory"]}{dir}{ThemeConfig.Reset}{branchSuffix}{elevatedTag}";
                var visibleLeft = Markup.Remove(leftPart).Length;
                var timeStr = DateTime.Now.ToString("HH:mm");
                var bufferWidth = Console.BufferWidth;
                var padding = Math.Max(1, bufferWidth - visibleLeft - timeStr.Length);
                if (cachedPadding.Length != padding)
                    cachedPadding = new string(' ', padding);
                var line1 = $"{leftPart}{cachedPadding}{theme["prompt.time"]}{timeStr}{ThemeConfig.Reset}";

                // Build line 2 (optional): [N] took Xs
                promptSb.Clear();
                var activeJobs = strategy.Jobs.Count(j => !j.Process.HasExited);
                if (activeJobs > 0)
                    promptSb.Append($"{theme["prompt.jobs"]}[{activeJobs}]{ThemeConfig.Reset} ");

                if (lastCommandDuration.TotalSeconds >= 2)
                {
                    var durationStr = lastCommandDuration.TotalMinutes >= 1
                        ? $"{(int)lastCommandDuration.TotalMinutes}m {lastCommandDuration.Seconds}s"
                        : $"{(int)lastCommandDuration.TotalSeconds}s";
                    promptSb.Append($"{theme["prompt.duration"]}took {durationStr}{ThemeConfig.Reset}");
                }

                var line2 = promptSb.Length > 0 ? $"{promptSb}\n" : "";

                // Build line 3: arrow colored by last command success
                var arrowColor = lastCommandSuccess ? theme["prompt.arrow"] : theme["prompt.error"];
                var promptChar = isElevated ? "#" : ">";

                var prompt = $"{line1}\n{line2}{arrowColor}{ThemeConfig.Bold}{promptChar}{ThemeConfig.Reset} ";
                line = readLine.Read(prompt);
            }

            if (line is "exit")
                return;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (isInteractive)
                await history.WriteAsync(line);

            var sw = Stopwatch.StartNew();
            var result = await strategy.ExecuteAsync(line);
            sw.Stop();

            lastCommandDuration = sw.Elapsed;
            lastCommandSuccess = result.Error is null;

            DisplayResult(result, theme);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{theme["error"]}Unexpected error: {ex.Message}{ThemeConfig.Reset}");
        }
    }
}

static async Task HandleElevatedEntry(JitzuOptions options)
{
    var parentPid = options.ParentPid;

    // Attach to parent's console for seamless terminal experience
    if (parentPid > 0)
    {
        if (!SudoCommand.AttachToParentConsole(parentPid))
        {
            Console.Error.WriteLine("sudo: failed to attach to parent console");
            Environment.Exit(1);
            return;
        }
    }

    if (options.SudoExec is { } command)
    {
        // Mode 1: Run single command elevated, then exit
        var theme = await ThemeConfig.LoadAsync();
        var session = await ShellSession.CreateAsync();
        var aliasManager = new AliasManager(options.Persist);
        await aliasManager.InitialiseAsync();
        var labelManager = new LabelManager();
        var builtins = new BuiltinCommands(session, theme, aliasManager, labelManager);
        var strategy = new ExecutionStrategy(session, builtins, aliasManager, labelManager);
        builtins.SetStrategy(strategy);

        var result = await strategy.ExecuteAsync(command);
        DisplayResult(result, theme);

        Environment.Exit(result.Error is null ? 0 : 1);
    }
    else
    {
        // Mode 2: Shell takeover — kill parent and run REPL
        if (parentPid > 0)
        {
            try
            {
                var parent = Process.GetProcessById(parentPid);
                parent.Kill();
            }
            catch
            {
                // Parent may have already exited
            }
        }

        if (options.SudoLogin)
        {
            // Login shell: reset to user profile directory
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Environment.CurrentDirectory = userProfile;
        }

        await RunReplAsync(options);
    }
}

static void PrintSplash()
{
    var sb = new StringBuilder();
    sb.AppendLine($"jz v{Assembly.GetExecutingAssembly().GetName().Version}");
    sb.AppendLine();
    sb.AppendLine($"• runtime    : {Environment.Version}");
    sb.AppendLine("• config     : ~/.jitzu/config.jz");
    sb.AppendLine($"• platform   : {Environment.OSVersion.Platform}");
    sb.AppendLine();
    sb.AppendLine("Type `help` to get started.");

    Console.WriteLine(sb.ToString());
}


static void DisplayResult(ShellResult result, ThemeConfig theme)
{
    if (result.Error is not null)
    {
        // Display error using ExceptionPrinter if it's a JitzuException
        if (result.Error is JitzuException jitzuEx)
        {
            ExceptionPrinter.Print(jitzuEx);
        }
        else
        {
            Console.WriteLine($"{theme["error"]}{result.Error.Message}{ThemeConfig.Reset}");
        }
    }
    else if (!string.IsNullOrWhiteSpace(result.Output))
    {
        // Display output
        Console.WriteLine(result.Output);
    }
}

static void CleanupOldBinary()
{
    try
    {
        var processPath = Environment.ProcessPath;
        if (processPath is null) return;
        var oldPath = processPath + ".old";
        if (File.Exists(oldPath))
            File.Delete(oldPath);
    }
    catch
    {
        // Best effort — ignore errors
    }
}

/// <summary>
/// Gets git working tree status by running `git status --porcelain --branch`.
/// Returns indicators for dirty, staged, untracked files and ahead/behind counts.
/// </summary>
static async Task<(bool HasStaged, bool HasDirty, bool HasUntracked, int Ahead, int Behind)> GetGitStatusAsync(string gitRepoPath)
{
    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = gitRepoPath,
        };
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--porcelain=v1");
        startInfo.ArgumentList.Add("--branch");

        using var process = Process.Start(startInfo);
        if (process is null)
            return default;

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        await errorTask;

        if (process.ExitCode != 0)
            return default;

        var hasStaged = false;
        var hasDirty = false;
        var hasUntracked = false;
        var ahead = 0;
        var behind = 0;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("##"))
            {
                // Parse: ## branch...origin/branch [ahead N, behind M]
                var bracketStart = line.IndexOf('[');
                if (bracketStart >= 0)
                {
                    var bracketEnd = line.IndexOf(']', bracketStart);
                    if (bracketEnd >= 0)
                    {
                        var info = line[(bracketStart + 1)..bracketEnd];
                        foreach (var part in info.Split(',', StringSplitOptions.TrimEntries))
                        {
                            if (part.StartsWith("ahead ") && int.TryParse(part.AsSpan()[6..], out var a))
                                ahead = a;
                            else if (part.StartsWith("behind ") && int.TryParse(part.AsSpan()[7..], out var b))
                                behind = b;
                        }
                    }
                }
                continue;
            }

            if (line.Length < 2) continue;

            if (line.StartsWith("??"))
            {
                hasUntracked = true;
                continue;
            }

            // First column: staging area status
            if (line[0] is not ' ' and not '?')
                hasStaged = true;

            // Second column: working tree status
            if (line[1] is not ' ' and not '?')
                hasDirty = true;
        }

        return (hasStaged, hasDirty, hasUntracked, ahead, behind);
    }
    catch
    {
        return default;
    }
}

/// <summary>
/// Enables ANSI escape sequence processing on Windows.
/// On non-Windows platforms this is a no-op since terminals natively support ANSI.
/// </summary>
static void EnableAnsiSupport()
{
    if (!OperatingSystem.IsWindows())
        return;

    using var stdout = Windows.Win32.PInvoke.GetStdHandle_SafeHandle(Windows.Win32.System.Console.STD_HANDLE.STD_OUTPUT_HANDLE);
    if (!stdout.IsInvalid)
    {
        if (Windows.Win32.PInvoke.GetConsoleMode(stdout, out var mode))
            Windows.Win32.PInvoke.SetConsoleMode(stdout, mode | Windows.Win32.System.Console.CONSOLE_MODE.ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }

    using var stderr = Windows.Win32.PInvoke.GetStdHandle_SafeHandle(Windows.Win32.System.Console.STD_HANDLE.STD_ERROR_HANDLE);
    if (!stderr.IsInvalid)
    {
        if (Windows.Win32.PInvoke.GetConsoleMode(stderr, out var mode))
            Windows.Win32.PInvoke.SetConsoleMode(stderr, mode | Windows.Win32.System.Console.CONSOLE_MODE.ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }
}
