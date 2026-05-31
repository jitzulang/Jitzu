using System.Runtime.InteropServices;
using Jitzu.Shell.Core.Commands;

namespace Jitzu.Shell.Core;

/// <summary>
/// Manages registration and execution of shell built-in commands.
/// </summary>
public class BuiltinCommands
{
    private readonly Dictionary<string, IBuiltinCommand> _commandInstances = new();
    private readonly Dictionary<string, Func<ReadOnlyMemory<string>, Task<ShellResult>>> _commands;
    private readonly CommandContext _context;
    private readonly SudoCommand _sudo;

    // Command instances that need special handling for piped input
    private readonly MoreCommand _moreCommand;
    private readonly TeeCommand _teeCommand;

    /// <summary>
    /// Set after construction to break circular dependency (ExecutionStrategy → BuiltinCommands → ExecutionStrategy).
    /// Required for `time` and `watch` commands which need to execute arbitrary commands.
    /// </summary>
    public void SetStrategy(ExecutionStrategy strategy) => _context.Strategy = strategy;

    public BuiltinCommands(ShellSession session, ThemeConfig theme, AliasManager? aliasManager = null, LabelManager? labelManager = null, HistoryManager? historyManager = null)
    {
        _context = new CommandContext(session, theme, aliasManager, labelManager, historyManager);
        _context.BuiltinCommands = this;
        _sudo = new SudoCommand(historyManager);

        // Instantiate all command classes
        var cdCommand = new CdCommand(_context);
        var exitCommand = new ExitCommand(_context);
        var clearCommand = new ClearCommand(_context);
        var helpCommand = new HelpCommand(_context);
        var resetCommand = new ResetCommand(_context);
        var showVariablesCommand = new ShowVariablesCommand(_context);
        var showTypesCommand = new ShowTypesCommand(_context);
        var showFunctionsCommand = new ShowFunctionsCommand(_context);
        var aliasCommand = new AliasCommand(_context);
        var unaliasCommand = new UnaliasCommand(_context);
        var listAliasesCommand = new ListAliasesCommand(_context);
        var labelCommand = new LabelCommand(_context);
        var unlabelCommand = new UnlabelCommand(_context);
        var listLabelsCommand = new ListLabelsCommand(_context);
        var mkdirCommand = new MkdirCommand(_context);
        var catCommand = new CatCommand(_context);
        var pwdCommand = new PwdCommand(_context);
        var echoCommand = new EchoCommand(_context);
        var touchCommand = new TouchCommand(_context);
        var rmCommand = new RmCommand(_context);
        var mvCommand = new MvCommand(_context);
        var cpCommand = new CpCommand(_context);
        var historyCommand = new HistoryCommand(_context);
        var envCommand = new EnvCommand(_context);
        var headCommand = new HeadCommand(_context);
        var tailCommand = new TailCommand(_context);
        var exportCommand = new ExportCommand(_context);
        var unsetCommand = new UnsetCommand(_context);
        var grepCommand = new GrepCommand(_context);
        var wcCommand = new WcCommand(_context);
        var sortCommand = new SortCommand(_context);
        var uniqCommand = new UniqCommand(_context);
        var findCommand = new FindCommand(_context);
        var diffCommand = new DiffCommand(_context);
        var timeCommand = new TimeCommand(_context);
        var watchCommand = new WatchCommand(_context);
        _moreCommand = new MoreCommand(_context);
        var jobsCommand = new JobsCommand(_context);
        var fgCommand = new FgCommand(_context);
        var bgCommand = new BgCommand(_context);
        var wgetCommand = new WgetCommand(_context);
        var killCommand = new KillCommand(_context);
        var killAllCommand = new KillAllCommand(_context);
        _teeCommand = new TeeCommand(_context);
        var lnCommand = new LnCommand(_context);
        var statCommand = new StatCommand(_context);
        var chmodCommand = new ChmodCommand(_context);
        var unblockCommand = new UnblockCommand(_context);
        var whoamiCommand = new WhoamiCommand(_context);
        var whoCommand = new WhoCommand(_context);
        var hostnameCommand = new HostnameCommand(_context);
        var uptimeCommand = new UptimeCommand(_context);
        var sleepCommand = new SleepCommand(_context);
        var yesCommand = new YesCommand(_context);
        var basenameCommand = new BasenameCommand(_context);
        var dirnameCommand = new DirnameCommand(_context);
        var duCommand = new DuCommand(_context);
        var dfCommand = new DfCommand(_context);
        var trCommand = new TrCommand(_context);
        var cutCommand = new CutCommand(_context);
        var seqCommand = new SeqCommand(_context);
        var revCommand = new RevCommand(_context);
        var tacCommand = new TacCommand(_context);
        var pasteCommand = new PasteCommand(_context);
        var dateCommand = new DateCommand(_context);
        var mktempCommand = new MktempCommand(_context);
        var trueCommand = new TrueCommand(_context);
        var falseCommand = new FalseCommand(_context);
        var monitorCommand = new MonitorCommand(_context);
        var lsCommand = new LsCommand(_context);
        var whereCommand = new WhereCommand(_context);
        var neofetchCommand = new NeofetchCommand(_context);
        var upgradeCommand = new UpgradeCommand(_context);
        var pathCommand = new PathCommand(_context);
        var viewCommand = new ViewCommand(_context);

        // Register commands in dictionary
        _commands = new()
        {
            ["cd"] = cdCommand.ExecuteAsync,
            ["exit"] = exitCommand.ExecuteAsync,
            ["quit"] = exitCommand.ExecuteAsync,
            ["clear"] = clearCommand.ExecuteAsync,
            ["help"] = helpCommand.ExecuteAsync,
            ["reset"] = resetCommand.ExecuteAsync,
            ["vars"] = showVariablesCommand.ExecuteAsync,
            ["types"] = showTypesCommand.ExecuteAsync,
            ["functions"] = showFunctionsCommand.ExecuteAsync,
            ["alias"] = aliasCommand.ExecuteAsync,
            ["unalias"] = unaliasCommand.ExecuteAsync,
            ["aliases"] = listAliasesCommand.ExecuteAsync,
            ["label"] = labelCommand.ExecuteAsync,
            ["unlabel"] = unlabelCommand.ExecuteAsync,
            ["labels"] = listLabelsCommand.ExecuteAsync,
            ["mkdir"] = mkdirCommand.ExecuteAsync,
            ["cat"] = catCommand.ExecuteAsync,
            ["pwd"] = pwdCommand.ExecuteAsync,
            ["echo"] = echoCommand.ExecuteAsync,
            ["touch"] = touchCommand.ExecuteAsync,
            ["rm"] = rmCommand.ExecuteAsync,
            ["mv"] = mvCommand.ExecuteAsync,
            ["cp"] = cpCommand.ExecuteAsync,
            ["history"] = historyCommand.ExecuteAsync,
            ["env"] = envCommand.ExecuteAsync,
            ["head"] = headCommand.ExecuteAsync,
            ["tail"] = tailCommand.ExecuteAsync,
            ["export"] = exportCommand.ExecuteAsync,
            ["unset"] = unsetCommand.ExecuteAsync,
            ["grep"] = grepCommand.ExecuteAsync,
            ["wc"] = wcCommand.ExecuteAsync,
            ["sort"] = sortCommand.ExecuteAsync,
            ["uniq"] = uniqCommand.ExecuteAsync,
            ["find"] = findCommand.ExecuteAsync,
            ["diff"] = diffCommand.ExecuteAsync,
            ["time"] = timeCommand.ExecuteAsync,
            ["watch"] = watchCommand.ExecuteAsync,
            ["more"] = _moreCommand.ExecuteAsync,
            ["less"] = _moreCommand.ExecuteAsync,
            ["jobs"] = jobsCommand.ExecuteAsync,
            ["fg"] = fgCommand.ExecuteAsync,
            ["bg"] = bgCommand.ExecuteAsync,
            ["wget"] = wgetCommand.ExecuteAsync,
            ["kill"] = killCommand.ExecuteAsync,
            ["killall"] = killAllCommand.ExecuteAsync,
            ["tee"] = _teeCommand.ExecuteAsync,
            ["ln"] = lnCommand.ExecuteAsync,
            ["stat"] = statCommand.ExecuteAsync,
            ["chmod"] = chmodCommand.ExecuteAsync,
            ["unblock"] = unblockCommand.ExecuteAsync,
            ["whoami"] = whoamiCommand.ExecuteAsync,
            ["who"] = whoCommand.ExecuteAsync,
            ["hostname"] = hostnameCommand.ExecuteAsync,
            ["uptime"] = uptimeCommand.ExecuteAsync,
            ["sleep"] = sleepCommand.ExecuteAsync,
            ["yes"] = yesCommand.ExecuteAsync,
            ["basename"] = basenameCommand.ExecuteAsync,
            ["dirname"] = dirnameCommand.ExecuteAsync,
            ["du"] = duCommand.ExecuteAsync,
            ["df"] = dfCommand.ExecuteAsync,
            ["tr"] = trCommand.ExecuteAsync,
            ["cut"] = cutCommand.ExecuteAsync,
            ["seq"] = seqCommand.ExecuteAsync,
            ["rev"] = revCommand.ExecuteAsync,
            ["tac"] = tacCommand.ExecuteAsync,
            ["paste"] = pasteCommand.ExecuteAsync,
            ["date"] = dateCommand.ExecuteAsync,
            ["mktemp"] = mktempCommand.ExecuteAsync,
            ["true"] = trueCommand.ExecuteAsync,
            ["false"] = falseCommand.ExecuteAsync,
            ["monitor"] = monitorCommand.ExecuteAsync,
            ["sudo"] = _sudo.ExecuteAsync,
            ["where"] = whereCommand.ExecuteAsync,
            ["neofetch"] = neofetchCommand.ExecuteAsync,
            ["upgrade"] = upgradeCommand.ExecuteAsync,
            ["path"] = pathCommand.ExecuteAsync,
            ["view"] = viewCommand.ExecuteAsync
        };

        // Platform-specific: On Windows, use our built-in ls instead of external command
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _commands["ls"] = lsCommand.ExecuteAsync;
    }

    public IReadOnlyCollection<string> CommandNames => _commands.Keys;

    public string? FindNearest(string lastWord)
    {
        return _commands.FirstOrDefault(x => x.Key.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase)).Key;
    }

    public bool IsBuiltin(string command) => _commands.ContainsKey(command);

    public async Task<ShellResult> ExecuteAsync(string command, ReadOnlyMemory<string> args)
    {
        if (_commands.TryGetValue(command, out var handler))
            return await handler(args);

        return new ShellResult(
            ResultType.Error,
            "",
            new Exception($"Unknown builtin: {command}")
        );
    }

    /// <summary>
    /// Sets piped input for the 'more'/'less' pager command.
    /// </summary>
    public void SetPagerInput(string input) => _moreCommand.SetPagerInput(input);

    /// <summary>
    /// Sets piped input for the 'tee' command.
    /// </summary>
    public void SetTeeInput(string input) => _teeCommand.SetTeeInput(input);
}
