namespace Jitzu.Shell.Core.Commands;

public class HandoffCommand : CommandBase
{
    public HandoffCommand(CommandContext context) : base(context) { }

    public override Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (HandoffRegistry is null)
            return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception("Handoff registry not available")));

        if (args.Length == 0)
            return Task.FromResult(new ShellResult(ResultType.Jitzu, List(HandoffRegistry), null));

        if (args.Length < 3)
            return Task.FromResult(new ShellResult(ResultType.Error, "",
                new Exception("Usage: handoff <command> <variable> <action...>")));

        var handoff = new Handoff(args.Span[0], args.Span[1], args.Span[2..].ToArray());
        HandoffRegistry.Set(handoff);
        return Task.FromResult(new ShellResult(ResultType.Jitzu, Describe(handoff), null));
    }

    private static string List(HandoffRegistry registry) =>
        registry.Handoffs.Count == 0
            ? "No handoffs defined"
            : string.Join(Environment.NewLine, registry.Handoffs.Select(Describe));

    private static string Describe(Handoff handoff) =>
        $"{handoff.Command} ${handoff.Variable} → {string.Join(' ', handoff.Action)}";
}
