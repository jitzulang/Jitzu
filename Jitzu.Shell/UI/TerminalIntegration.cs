namespace Jitzu.Shell.UI;

public static class TerminalIntegration
{
    public static void ReportCurrentDirectory()
    {
        if (Console.IsOutputRedirected)
            return;

        var currentDirectory = Environment.CurrentDirectory;
        Console.Write($"\e]9;9;\"{currentDirectory}\"\e\\");
    }

    public static void SetTitle(string title)
    {
        WriteTitle(Console.Out, title, Console.IsOutputRedirected);
    }

    internal static void WriteTitle(TextWriter writer, string title, bool isOutputRedirected)
    {
        if (isOutputRedirected)
            return;

        writer.Write(FormatTitleSequence(title));
    }

    internal static string FormatTitleSequence(string title)
    {
        return $"\e]2;{SanitizeTitle(title)}\e\\";
    }

    private static string SanitizeTitle(string title)
    {
        return string.Create(title.Length, title, static (chars, source) =>
        {
            var index = 0;
            foreach (var character in source)
            {
                if (!char.IsControl(character))
                    chars[index++] = character;
            }

            chars[index..].Clear();
        }).TrimEnd('\0');
    }
}
