using System.Text;

namespace Jitzu.Shell.Infrastructure;

internal static class StartupFileReader
{
    public const int ThemeMaxBytes = 1 * 1024 * 1024;
    public const int AliasMaxBytes = 1 * 1024 * 1024;
    public const int ConfigMaxBytes = 1 * 1024 * 1024;
    public const int HistoryMaxBytes = 8 * 1024 * 1024;

    public static byte[] ReadAllBytes(string path, int maxBytes)
    {
        // Reparse points are intentionally followed for roaming-profile compatibility.
        // The target is still length-checked and the read itself is capped to handle growth races.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
            throw TooLarge(path, maxBytes);

        var bytes = new byte[Math.Min((int)stream.Length, maxBytes)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                break;
            offset += read;
        }

        if (offset == maxBytes && stream.ReadByte() != -1)
            throw TooLarge(path, maxBytes);

        return offset == bytes.Length ? bytes : bytes[..offset];
    }

    public static string[] ReadAllLines(string path, int maxBytes)
    {
        var text = Encoding.UTF8.GetString(ReadAllBytes(path, maxBytes));
        return text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
    }

    private static InvalidDataException TooLarge(string path, int maxBytes) =>
        new($"Startup file '{path}' exceeds the {maxBytes:N0}-byte limit.");
}
