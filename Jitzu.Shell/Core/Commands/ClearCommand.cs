namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Clears the console screen and scrollback buffer.
/// </summary>
public class ClearCommand : CommandBase
{
    public ClearCommand(CommandContext context) : base(context) { }

    public override Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (!Console.IsOutputRedirected)
        {
            Console.Clear();
            // Console.Clear() can leave saved lines in the terminal's scrollback buffer.
            Console.Write("\e[3J");
        }
        return Task.FromResult(new ShellResult(ResultType.Jitzu, "", null));
    }
}
