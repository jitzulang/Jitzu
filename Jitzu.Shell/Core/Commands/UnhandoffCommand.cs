namespace Jitzu.Shell.Core.Commands;

public class UnhandoffCommand : CommandBase
{
    public UnhandoffCommand(CommandContext context) : base(context) { }

    public override Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (HandoffRegistry is null)
            return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception("Handoff registry not available")));

        if (args.Length == 0)
            return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception("Usage: unhandoff <command>")));

        var command = args.Span[0];
        return Task.FromResult(HandoffRegistry.Remove(command)
            ? new ShellResult(ResultType.Jitzu, $"Handoff removed: {command}", null)
            : new ShellResult(ResultType.Error, "", new Exception($"Handoff not found: {command}")));
    }
}
