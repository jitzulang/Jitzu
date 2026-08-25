using System.Text;

namespace Jitzu.Shell;

/// <summary>
/// Expands Jitzu's interactive shortcuts for parts of the previous command.
/// </summary>
internal static class HistoryExpansion
{
    public static bool TryExpand(string input, HistoryManager history, out string expanded, out string? error)
    {
        expanded = input;
        error = null;

        var builder = new StringBuilder(input.Length);
        List<string>? previousWords = null;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (ch == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;

            if (inSingleQuote || ch != '^' || !IsTokenStart(input, i))
            {
                builder.Append(ch);
                continue;
            }

            var designatorEnd = GetDesignatorEnd(input, i);
            if (designatorEnd < 0 || !IsTokenEnd(input, designatorEnd))
            {
                builder.Append(ch);
                continue;
            }

            if (history.Count == 0)
            {
                error = "history expansion: no previous command";
                return false;
            }

            var previousCommand = history[history.Count - 1];
            switch (input[i + 1])
            {
                case 'p':
                    builder.Append(previousCommand);
                    break;

                case 'l':
                    previousWords ??= GetWords(previousCommand);
                    if (previousWords.Count < 2)
                    {
                        error = "history expansion: previous command has no arguments";
                        return false;
                    }
                    builder.Append(previousWords[^1]);
                    break;

                case 'a':
                    previousWords ??= GetWords(previousCommand);
                    var indexSpan = input.AsSpan(i + 2, designatorEnd - i - 2);
                    if (!int.TryParse(indexSpan, out var argumentIndex)
                        || argumentIndex >= previousWords.Count - 1)
                    {
                        error = $"history expansion: argument {indexSpan.ToString()} not found";
                        return false;
                    }
                    builder.Append(previousWords[argumentIndex + 1]);
                    break;
            }

            i = designatorEnd - 1;
        }

        expanded = builder.ToString();
        return true;
    }

    private static int GetDesignatorEnd(string input, int start)
    {
        if (start + 1 >= input.Length)
            return -1;

        if (input[start + 1] is 'l' or 'p')
            return start + 2;

        if (input[start + 1] != 'a' || start + 2 >= input.Length || !char.IsAsciiDigit(input[start + 2]))
            return -1;

        var end = start + 3;
        while (end < input.Length && char.IsAsciiDigit(input[end]))
            end++;
        return end;
    }

    private static bool IsTokenStart(string input, int index) =>
        index == 0 || char.IsWhiteSpace(input[index - 1]) || IsCommandSeparator(input[index - 1]);

    private static bool IsTokenEnd(string input, int index) =>
        index == input.Length || char.IsWhiteSpace(input[index]) || IsCommandSeparator(input[index]);

    private static bool IsCommandSeparator(char ch) => ch is '|' or '&' or ';' or '<' or '>';

    private static List<string> GetWords(string command)
    {
        var words = new List<string>();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var wordStart = -1;

        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (ch == '\\' && !inSingleQuote && i + 1 < command.Length)
            {
                if (wordStart < 0)
                    wordStart = i;
                i++;
                continue;
            }

            if (ch == '\'' && !inDoubleQuote)
                inSingleQuote = !inSingleQuote;
            else if (ch == '"' && !inSingleQuote)
                inDoubleQuote = !inDoubleQuote;

            if (char.IsWhiteSpace(ch) && !inSingleQuote && !inDoubleQuote)
            {
                if (wordStart >= 0)
                {
                    words.Add(command[wordStart..i]);
                    wordStart = -1;
                }
            }
            else if (wordStart < 0)
            {
                wordStart = i;
            }
        }

        if (wordStart >= 0)
            words.Add(command[wordStart..]);

        return words;
    }
}
