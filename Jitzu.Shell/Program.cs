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

StartupProfiler.Mark("managed-entry");
Console.OutputEncoding = Encoding.UTF8;
EnableAnsiSupport();

var (hostArgs, scriptArgs) = JitzuOptions.SplitArgs(args);
var options = JitzuOptions.Parse(hostArgs);
StartupProfiler.Mark("options-parsed");
if (scriptArgs.Length > 0)
    options.ScriptArgs = scriptArgs;

DebugLogger.SetIsEnabled(options.Debug);
Telemetry.SetIsEnabled(options.Telemetry);

try
{
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
        Environment.ExitCode = await ExecuteCommand(command, options.Persist, options.Config);
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
            Environment.ExitCode = await RunScript(scriptPath, options);
            Console.Out.Flush();
            return;
        }

        Console.WriteLine($"File not found: {scriptPath}");
        Environment.ExitCode = 1;
        return;
    }

    // 6. Default (no args) → Shell REPL
    await RunReplAsync(options);
}
finally
{
    CleanupOldBinary();
}
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

static async Task<int> ExecuteCommand(string command, bool persist, bool loadUserConfig)
{
    var theme = ThemeConfig.Load(loadUserConfig);
    try
    {
        var session = new ShellSession();
        var aliasManager = new AliasManager(persist);
        aliasManager.Initialise();
        var labelManager = new LabelManager();
        var builtins = new BuiltinCommands(session, theme, aliasManager, labelManager);
        var strategy = new ExecutionStrategy(session, builtins, aliasManager, labelManager);
        builtins.SetStrategy(strategy);

        var result = await strategy.ExecuteAsync(command);

        DisplayResult(result, theme);

        return result.Error is null ? 0 : 1;
    }
    finally
    {
        theme.EnsureDefaultFile();
    }
}

static async Task RunReplAsync(JitzuOptions options)
{
    StartupProfiler.Mark("repl-entry");
    // Initialize session and components
    var persist = options.Persist;
    var history = new HistoryManager(persist);
    var aliasManager = new AliasManager(persist);
    var theme = ThemeConfig.Load(options.Config);
    try
    {
    var session = new ShellSession();
    history.Initialise();
    aliasManager.Initialise();
    if (history.PersistenceWarning is { } historyWarning)
        Console.Error.WriteLine($"Warning: history persistence disabled: {historyWarning}");
    if (aliasManager.PersistenceWarning is { } aliasWarning)
        Console.Error.WriteLine($"Warning: alias persistence disabled: {aliasWarning}");
    StartupProfiler.Mark("state-loaded");
    var labelManager = new LabelManager();
    var builtins = new BuiltinCommands(session, theme, aliasManager, labelManager, history);
    var strategy = new ExecutionStrategy(session, builtins, aliasManager, labelManager);
    builtins.SetStrategy(strategy);
    StartupProfiler.Mark("commands-registered");
    var userProfilePath = GetUserProfilePath();
    var completionManager = new CompletionManager(session, builtins, labelManager, userProfilePath);

    var readLine = new ReadLine(history, theme, completionManager.GetCompletions,
        prediction => HistoryPredictionFilter.IsValid(prediction, Directory.GetCurrentDirectory()),
        input => CdPathHint.GetHint(input, labelManager, Directory.GetCurrentDirectory()),
        path => ShellPathResolver.ExpandPath(path, labelManager, Directory.GetCurrentDirectory()));

    // Load config file (~/.jitzu/config.jz) like .bashrc
    var configPath = Path.Combine(userProfilePath, ".jitzu", "config.jz");
    if (options.Config && File.Exists(configPath))
    {
        aliasManager.BeginSaveBatch();
        try
        {
            // Config must be fully applied before the first prompt. It is a small local
            // file, so synchronous reading avoids async I/O setup on the critical path.
            var configLines = Jitzu.Shell.Infrastructure.StartupFileReader.ReadAllLines(
                configPath, Jitzu.Shell.Infrastructure.StartupFileReader.ConfigMaxBytes);
            foreach (var line in configLines)
            {
                if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("//"))
                {
                    await strategy.ExecuteAsync(line);
                    if (builtins.ExitRequested)
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{theme["error"]}Error loading config: {ex.Message}{ThemeConfig.Reset}");
        }
        finally
        {
            try
            {
                await aliasManager.EndSaveBatchAsync();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Warning: aliases remain read-only: {ex.Message}");
            }
        }
    }
    StartupProfiler.Mark("config-loaded");

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
    await using var gitCache = new GitStatusCache();
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
                StartupProfiler.Mark("input-ready");
                line = Console.ReadLine();
                if (line is null)
                    return;
            }
            else
            {
                var dir = Environment.CurrentDirectory.Replace(userProfilePath, "~");

                // Trims path to root of Git repository
                var gitRepoRoot = gitCache.FindGitRepoFolder(Environment.CurrentDirectory);
                if (gitRepoRoot is not null)
                    dir = dir.Replace(gitRepoRoot.FullName, gitRepoRoot.Name);

                TerminalIntegration.ReportCurrentDirectory();
                TerminalIntegration.SetTitle(dir);
                Console.Write("\e[?25l"); // hide cursor during prompt build + render

                // Notify about completed background jobs
                var jobNotice = strategy.CheckCompletedJobs();
                if (jobNotice is not null)
                {
                    gitCache.InvalidateStatus();
                    Console.WriteLine(jobNotice);
                }

                var branchSuffix = "";
                if (gitRepoRoot is not null)
                {
                    var branch = gitCache.GetGitBranch(gitRepoRoot.FullName);
                    if (branch is not null)
                    {
                        var status = await GitStatusCache.GetGitStatusAsync(gitRepoRoot.FullName);

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

            StartupProfiler.Mark("input-accepted");

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (isInteractive)
            {
                if (!HistoryExpansion.TryExpand(line, history, out var expandedLine, out var expansionError))
                {
                    Console.Error.WriteLine($"jz: {expansionError}");
                    continue;
                }

                if (!string.Equals(line, expandedLine, StringComparison.Ordinal))
                {
                    line = expandedLine;
                    Console.WriteLine(line);
                }
            }

            if (line.Trim() is "exit" or "quit")
                return;

            if (isInteractive && history.PersistenceWarning is null)
            {
                try
                {
                    if (persist)
                        await history.WriteAsync(line);
                    else
                        history.Record(line);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    Console.Error.WriteLine($"Warning: history is now read-only: {ex.Message}");
                }
            }

            var sw = Stopwatch.StartNew();
            ShellResult result;
            try
            {
                result = await strategy.ExecuteAsync(line);
            }
            finally
            {
                // Commands may change files, the index, or the current directory. Invalidate
                // without waiting so the next prompt remains responsive; the cache owns the
                // refresh task and publishes only the newest generation.
                gitCache.InvalidateStatus();
            }
            sw.Stop();

            if (builtins.ExitRequested)
                return;

            lastCommandDuration = sw.Elapsed;
            lastCommandSuccess = result.Error is null;

            DisplayResult(result, theme);
            StartupProfiler.Mark("first-result-displayed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{theme["error"]}Unexpected error: {ex.Message}{ThemeConfig.Reset}");
        }
    }
    }
    finally
    {
        theme.EnsureDefaultFile();
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
            Environment.ExitCode = 1;
            return;
        }
    }

    if (options.SudoExec is { } command)
    {
        // Mode 1: Run single command elevated, then exit
        var theme = ThemeConfig.Load(options.Config);
        try
        {
            var session = new ShellSession();
            var aliasManager = new AliasManager(options.Persist);
            aliasManager.Initialise();
            var labelManager = new LabelManager();
            var builtins = new BuiltinCommands(session, theme, aliasManager, labelManager);
            var strategy = new ExecutionStrategy(session, builtins, aliasManager, labelManager);
            builtins.SetStrategy(strategy);

            var result = await strategy.ExecuteAsync(command);
            DisplayResult(result, theme);

            Environment.ExitCode = result.Error is null ? 0 : 1;
        }
        finally
        {
            theme.EnsureDefaultFile();
        }
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

        Jitzu.Shell.Infrastructure.Update.SelfUpdater.CleanupOldBinaries(processPath);
    }
    catch
    {
        // Best effort — ignore errors
    }
}

static string GetUserProfilePath()
{
    var environmentPath = Environment.GetEnvironmentVariable(
        OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
    return string.IsNullOrWhiteSpace(environmentPath)
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : environmentPath;
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
