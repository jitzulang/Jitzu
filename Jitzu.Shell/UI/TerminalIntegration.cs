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
}
