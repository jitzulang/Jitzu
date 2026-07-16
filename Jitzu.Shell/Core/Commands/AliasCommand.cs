namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Creates or lists command aliases.
/// </summary>
public class AliasCommand : CommandBase
{
    public AliasCommand(CommandContext context) : base(context) { }

    public override async Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (AliasManager == null)
            return new ShellResult(ResultType.Error, "", new Exception("Alias manager not available"));

        if (args.Length == 0)
        {
            var listCommand = new ListAliasesCommand(Context);
            return await listCommand.ExecuteAsync(args);
        }

        // Join all args to handle: alias ll="ls -la" or alias ll=ls -la
        var input = string.Join(' ', args.ToArray());
        var eqIndex = input.IndexOf('=');
        if (eqIndex <= 0)
            return new ShellResult(ResultType.Error, "", new Exception("Usage: alias name=\"command\""));

        var name = input[..eqIndex].Trim();
        var value = input[(eqIndex + 1)..].Trim();

        // Strip surrounding quotes if present
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];

        if (AliasManager.Set(name, value))
            await AliasManager.SaveAsync();
        return new ShellResult(ResultType.Jitzu, $"Alias set: {name} → {value}", null);
    }
}
