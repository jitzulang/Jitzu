using System.Runtime.InteropServices;
using System.Collections.Frozen;
using Jitzu.Shell.Core.Commands;

namespace Jitzu.Shell.Core;

/// <summary>
/// Manages registration and execution of shell built-in commands.
/// </summary>
public class BuiltinCommands
{
    private readonly Dictionary<string, IBuiltinCommand> _commandInstances = new();
    private readonly FrozenSet<string> _commands;
    private static readonly string[] CommandNamesCore =
    [
        "cd", "exit", "quit", "clear", "help", "reset", "vars", "types", "functions",
        "alias", "unalias", "aliases", "label", "unlabel", "labels", "mkdir", "cat", "pwd",
        "echo", "touch", "rm", "mv", "cp", "history", "env", "head", "tail", "export", "unset",
        "grep", "wc", "sort", "uniq", "find", "diff", "time", "watch", "more", "less", "jobs",
        "fg", "bg", "wget", "kill", "killall", "tee", "ln", "stat", "chmod", "unblock", "whoami",
        "who", "hostname", "uptime", "sleep", "yes", "basename", "dirname", "du", "df", "tr", "cut",
        "seq", "rev", "tac", "paste", "date", "mktemp", "true", "false", "monitor", "sudo", "where",
        "neofetch", "upgrade", "path", "view"
    ];
    private readonly CommandContext _context;
    private SudoCommand? _sudo;

    /// <summary>
    /// Set after construction to break circular dependency (ExecutionStrategy → BuiltinCommands → ExecutionStrategy).
    /// Required for `time` and `watch` commands which need to execute arbitrary commands.
    /// </summary>
    public void SetStrategy(ExecutionStrategy strategy) => _context.Strategy = strategy;

    public BuiltinCommands(ShellSession session, ThemeConfig theme, AliasManager? aliasManager = null, LabelManager? labelManager = null, HistoryManager? historyManager = null)
    {
        _context = new CommandContext(session, theme, aliasManager, labelManager, historyManager);
        _context.BuiltinCommands = this;

        _commands = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? CommandNamesCore.Append("ls").ToFrozenSet(StringComparer.Ordinal)
            : CommandNamesCore.ToFrozenSet(StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> CommandNames => _commands;
    public bool ExitRequested => _context.ExitRequested;

    public string? FindNearest(string lastWord)
    {
        return _commands.FirstOrDefault(x => x.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsBuiltin(string command) => _commands.Contains(command);

    public async Task<ShellResult> ExecuteAsync(string command, ReadOnlyMemory<string> args)
    {
        if (command == "sudo")
            return await GetOrCreateSudo().ExecuteAsync(args);

        if (_commands.Contains(command))
            return await GetOrCreate(command).ExecuteAsync(args);

        return new ShellResult(
            ResultType.Error,
            "",
            new Exception($"Unknown builtin: {command}")
        );
    }

    /// <summary>
    /// Sets piped input for the 'more'/'less' pager command.
    /// </summary>
    public void SetPagerInput(string input) => ((MoreCommand)GetOrCreate("more")).SetPagerInput(input);

    /// <summary>
    /// Sets piped input for the 'tee' command.
    /// </summary>
    public void SetTeeInput(string input) => ((TeeCommand)GetOrCreate("tee")).SetTeeInput(input);

    internal IBuiltinCommand GetOrCreate(string command)
    {
        lock (_commandInstances)
        {
            var key = command switch { "quit" => "exit", "less" => "more", _ => command };
            if (_commandInstances.TryGetValue(key, out var instance))
                return instance;

            instance = key switch
            {
            "cd" => new CdCommand(_context),
            "exit" => new ExitCommand(_context),
            "clear" => new ClearCommand(_context),
            "help" => new HelpCommand(_context),
            "reset" => new ResetCommand(_context),
            "vars" => new ShowVariablesCommand(_context),
            "types" => new ShowTypesCommand(_context),
            "functions" => new ShowFunctionsCommand(_context),
            "alias" => new AliasCommand(_context),
            "unalias" => new UnaliasCommand(_context),
            "aliases" => new ListAliasesCommand(_context),
            "label" => new LabelCommand(_context),
            "unlabel" => new UnlabelCommand(_context),
            "labels" => new ListLabelsCommand(_context),
            "mkdir" => new MkdirCommand(_context),
            "cat" => new CatCommand(_context),
            "pwd" => new PwdCommand(_context),
            "echo" => new EchoCommand(_context),
            "touch" => new TouchCommand(_context),
            "rm" => new RmCommand(_context),
            "mv" => new MvCommand(_context),
            "cp" => new CpCommand(_context),
            "history" => new HistoryCommand(_context),
            "env" => new EnvCommand(_context),
            "head" => new HeadCommand(_context),
            "tail" => new TailCommand(_context),
            "export" => new ExportCommand(_context),
            "unset" => new UnsetCommand(_context),
            "grep" => new GrepCommand(_context),
            "wc" => new WcCommand(_context),
            "sort" => new SortCommand(_context),
            "uniq" => new UniqCommand(_context),
            "find" => new FindCommand(_context),
            "diff" => new DiffCommand(_context),
            "time" => new TimeCommand(_context),
            "watch" => new WatchCommand(_context),
            "more" => new MoreCommand(_context),
            "jobs" => new JobsCommand(_context),
            "fg" => new FgCommand(_context),
            "bg" => new BgCommand(_context),
            "wget" => new WgetCommand(_context),
            "kill" => new KillCommand(_context),
            "killall" => new KillAllCommand(_context),
            "tee" => new TeeCommand(_context),
            "ln" => new LnCommand(_context),
            "stat" => new StatCommand(_context),
            "chmod" => new ChmodCommand(_context),
            "unblock" => new UnblockCommand(_context),
            "whoami" => new WhoamiCommand(_context),
            "who" => new WhoCommand(_context),
            "hostname" => new HostnameCommand(_context),
            "uptime" => new UptimeCommand(_context),
            "sleep" => new SleepCommand(_context),
            "yes" => new YesCommand(_context),
            "basename" => new BasenameCommand(_context),
            "dirname" => new DirnameCommand(_context),
            "du" => new DuCommand(_context),
            "df" => new DfCommand(_context),
            "tr" => new TrCommand(_context),
            "cut" => new CutCommand(_context),
            "seq" => new SeqCommand(_context),
            "rev" => new RevCommand(_context),
            "tac" => new TacCommand(_context),
            "paste" => new PasteCommand(_context),
            "date" => new DateCommand(_context),
            "mktemp" => new MktempCommand(_context),
            "true" => new TrueCommand(_context),
            "false" => new FalseCommand(_context),
            "monitor" => new MonitorCommand(_context),
            "ls" => new LsCommand(_context),
            "where" => new WhereCommand(_context),
            "neofetch" => new NeofetchCommand(_context),
            "upgrade" => new UpgradeCommand(_context),
            "path" => new PathCommand(_context),
            "view" => new ViewCommand(_context),
            _ => throw new InvalidOperationException($"Unknown builtin: {command}")
            };
            _commandInstances.Add(key, instance);
            return instance;
        }
    }

    private SudoCommand GetOrCreateSudo()
    {
        lock (_commandInstances)
            return _sudo ??= new SudoCommand(_context.HistoryManager);
    }
}
