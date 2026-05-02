using System.Runtime.CompilerServices;

namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Displays the last N lines of a file.
/// With -f flag, follows the file and streams new lines as they're appended.
/// </summary>
public class TailCommand(CommandContext context) : CommandBase(context), IStreamingCommand
{
    private const int DefaultLineCount = 10;
    private const int PollIntervalMs = 200;

    public override async Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (args.Length == 0)
            return new ShellResult(ResultType.Error, "", new Exception("Usage: tail [-f] [-n count] <file>"));

        var (lineCount, follow, filePath) = ParseArgs(args);

        if (filePath == null)
            return new ShellResult(ResultType.Error, "", new Exception("Usage: tail [-f] [-n count] <file>"));

        try
        {
            var fullPath = ExpandPath(filePath);
            if (!File.Exists(fullPath))
                return new ShellResult(ResultType.Error, "", new Exception($"File not found: {filePath}"));

            if (follow)
            {
                // Follow mode: stream initial lines to console, then poll for new content
                var dim = ThemeConfig.Dim;
                var reset = ThemeConfig.Reset;
                Console.WriteLine($"{dim}Following {filePath}... (Ctrl+C to stop){reset}");

                var allLines = await ReadAllLinesSharedAsync(fullPath);
                foreach (var line in allLines.TakeLast(lineCount))
                    Console.WriteLine(line);

                var position = new FileInfo(fullPath).Length;

                while (true)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
                            break;
                        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                            break;
                    }

                    var currentLength = new FileInfo(fullPath).Length;
                    if (currentLength > position)
                    {
                        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        stream.Seek(position, SeekOrigin.Begin);
                        using var reader = new StreamReader(stream);

                        while (await reader.ReadLineAsync() is { } line)
                            Console.WriteLine(line);

                        position = stream.Position;
                    }

                    await Task.Delay(PollIntervalMs);
                }

                return new ShellResult(ResultType.OsCommand, "", null);
            }

            // Non-follow mode: read last N lines and return
            var lines = await ReadAllLinesSharedAsync(fullPath);
            return new ShellResult(ResultType.OsCommand, string.Join(Environment.NewLine, lines.TakeLast(lineCount)), null);
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, "", ex);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ReadOnlyMemory<string> args,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
            yield break;

        var (lineCount, follow, filePath) = ParseArgs(args);

        if (filePath == null)
            yield break;

        var fullPath = ExpandPath(filePath);
        if (!File.Exists(fullPath))
            yield break;

        // Yield last N lines
        var allLines = await ReadAllLinesSharedAsync(fullPath, cancellationToken);
        foreach (var line in allLines.TakeLast(lineCount))
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            yield return line;
        }

        if (!follow)
            yield break;

        // Follow: poll for new content
        var position = new FileInfo(fullPath).Length;

        while (!cancellationToken.IsCancellationRequested)
        {
            var currentLength = new FileInfo(fullPath).Length;
            if (currentLength > position)
            {
                await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);

                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (cancellationToken.IsCancellationRequested) yield break;
                    yield return line;
                }

                position = stream.Position;
            }

            try { await Task.Delay(PollIntervalMs, cancellationToken); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    private static async Task<List<string>> ReadAllLinesSharedAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            lines.Add(line);
        return lines;
    }

    private static (int lineCount, bool follow, string? filePath) ParseArgs(ReadOnlyMemory<string> args)
    {
        var lineCount = DefaultLineCount;
        var follow = false;
        string? filePath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args.Span[i];
            switch (arg)
            {
                case "-f":
                    follow = true;
                    break;
                case "-n" when i + 1 < args.Length:
                    int.TryParse(args.Span[++i], out lineCount);
                    break;
                default:
                    filePath = arg;
                    break;
            }
        }

        return (lineCount, follow, filePath);
    }
}
