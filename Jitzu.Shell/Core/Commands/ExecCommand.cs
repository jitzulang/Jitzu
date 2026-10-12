using System.Text;

namespace Jitzu.Shell.Core.Commands;

/// <summary>Replaces the shell with an external command.</summary>
public class ExecCommand(CommandContext context) : CommandBase(context)
{
    internal static bool IsExec(string input)
    {
        var trimmed = input.AsSpan().TrimStart();
        return trimmed.SequenceEqual("exec") ||
            (trimmed.StartsWith("exec") && trimmed.Length > 4 && char.IsWhiteSpace(trimmed[4]));
    }

    // Display managers use shell quoting, including single quotes and escaped
    // spaces. System.CommandLine's splitter only understands double quotes.
    internal static string[] SplitArguments(string command)
    {
        var args = new List<string>();
        var word = new StringBuilder();
        var quote = '\0';
        var started = false;
        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (quote == '\0' && char.IsWhiteSpace(ch))
            {
                if (started)
                {
                    args.Add(word.ToString());
                    word.Clear();
                    started = false;
                }
                continue;
            }

            started = true;
            if (ch == quote)
            {
                quote = '\0';
                continue;
            }
            if (quote == '\0' && ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }
            if (ch == '\\' && quote != '\'')
            {
                if (i + 1 == command.Length)
                    throw new ArgumentException("exec: trailing escape");
                var next = command[i + 1];
                if (quote == '\0' || next is '$' or '`' or '"' or '\\' or '\n')
                {
                    i++;
                    if (next != '\n') word.Append(next);
                    continue;
                }
            }
            word.Append(ch);
        }

        if (quote != '\0')
            throw new ArgumentException("exec: unterminated quote");
        if (started) args.Add(word.ToString());
        return args.ToArray();
    }

    public override Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (args.Length > 0 && args.Span[0] == "--")
            args = args[1..];
        else if (args.Length > 0 && args.Span[0].StartsWith('-'))
            return Task.FromResult(new ShellResult(ResultType.Error, "",
                new Exception("Usage: exec [--] command [arguments]")) { Status = 2 });

        if (args.Length == 0)
            return Task.FromResult(new ShellResult(ResultType.Jitzu, "", null));

        return Task.FromResult(UnixProcess.Replace(args.ToArray()));
    }
}
