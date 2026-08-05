namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Shared context and dependencies for built-in commands.
/// </summary>
public class CommandContext
{
    public ShellSession Session { get; }
    public ThemeConfig Theme { get; }
    public AliasManager? AliasManager { get; }
    public LabelManager? LabelManager { get; }
    public HistoryManager? HistoryManager { get; }
    public ExecutionStrategy? Strategy { get; set; }
    public BuiltinCommands? BuiltinCommands { get; set; }
    public bool ExitRequested { get; set; }

    public CommandContext(
        ShellSession session,
        ThemeConfig theme,
        AliasManager? aliasManager = null,
        LabelManager? labelManager = null,
        HistoryManager? historyManager = null)
    {
        Session = session;
        Theme = theme;
        AliasManager = aliasManager;
        LabelManager = labelManager;
        HistoryManager = historyManager;
    }
}
