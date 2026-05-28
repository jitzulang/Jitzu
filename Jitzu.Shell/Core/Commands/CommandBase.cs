using System.Text;

namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Base class for built-in commands with shared utilities.
/// </summary>
public abstract class CommandBase : IBuiltinCommand
{
    protected readonly CommandContext Context;

    protected ShellSession Session => Context.Session;
    protected ThemeConfig Theme => Context.Theme;
    protected AliasManager? AliasManager => Context.AliasManager;
    protected LabelManager? LabelManager => Context.LabelManager;
    protected HistoryManager? HistoryManager => Context.HistoryManager;
    protected ExecutionStrategy? Strategy => Context.Strategy;

    protected CommandBase(CommandContext context)
    {
        Context = context;
    }

    public abstract Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args);

    /// <summary>
    /// Expands ~ and labels in a path to a full path.
    /// </summary>
    protected string ExpandPath(string path)
    {
        return ShellPathResolver.ExpandPath(path, LabelManager);
    }

    /// <summary>
    /// Formats a file size in human-readable format (B, K, M, G, T).
    /// </summary>
    protected static string FormatFileSize(long bytes)
    {
        Span<char> units = ['B', 'K', 'M', 'G', 'T'];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:0}{units[unit]}"
            : $"{size:0.#}{units[unit]}";
    }

    /// <summary>
    /// Formats bytes for display (1K, 1M, 1G, etc).
    /// </summary>
    protected static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024}K",
        < 1024 * 1024 * 1024 => $"{bytes / (1024 * 1024)}M",
        _ => $"{bytes / (1024 * 1024 * 1024)}G"
    };

    /// <summary>
    /// Formats a TimeSpan for display.
    /// </summary>
    protected static string FormatTime(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1) return $"{ts.TotalMilliseconds:F0}ms";
        if (ts.TotalMinutes < 1) return $"{ts.TotalSeconds:F2}s";
        return $"{ts.TotalMinutes:F1}m";
    }

    /// <summary>
    /// Checks if a file is likely binary.
    /// </summary>
    protected static bool IsBinaryFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buffer = new byte[512];
            var read = fs.Read(buffer, 0, buffer.Length);
            for (int i = 0; i < read; i++)
                if (buffer[i] == 0)
                    return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Colors a filename based on file type.
    /// </summary>
    protected string ColorFileName(string name, FileSystemInfo entry)
    {
        if (name is "." or "..")
            return $"{ThemeConfig.Dim}{name}/{ThemeConfig.Reset}";

        if (entry is DirectoryInfo)
            return $"{Theme["ls.directory"]}{ThemeConfig.Bold}{name}/{ThemeConfig.Reset}";

        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return $"{Theme["ls.code"]}\e[3m{name}{ThemeConfig.Reset}";

        var ext = Path.GetExtension(name).ToLowerInvariant();
        var color = ext switch
        {
            ".exe" or ".bat" or ".cmd" or ".ps1" or ".sh" or ".com" or ".msi"
                => $"{Theme["ls.executable"]}{ThemeConfig.Bold}",
            ".zip" or ".tar" or ".gz" or ".7z" or ".rar" or ".bz2" or ".xz" or ".tgz" or ".nupkg"
                => Theme["ls.archive"],
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".svg" or ".ico" or ".webp" or ".tiff"
                => Theme["ls.media"],
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".aac" or ".mp4" or ".avi" or ".mkv" or ".mov"
                => Theme["ls.media"],
            ".cs" or ".fs" or ".jz" or ".js" or ".ts" or ".py" or ".rs" or ".go" or ".java" or ".c" or ".cpp" or ".h" or ".rb" or ".lua"
                => Theme["ls.code"],
            ".json" or ".yaml" or ".yml" or ".toml" or ".xml" or ".ini" or ".env" or ".config"
                => Theme["ls.config"],
            ".sln" or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets"
                => Theme["ls.project"],
            ".md" or ".txt" or ".rst" or ".adoc"
                => "",
            ".dll" or ".so" or ".dylib" or ".lib" or ".a" or ".obj" or ".o" or ".pdb"
                => ThemeConfig.Dim,
            ".gitignore" or ".editorconfig" or ".dockerignore" or ".gitattributes"
                => Theme["ls.dim"],
            ".log" or ".tmp" or ".bak" or ".swp"
                => ThemeConfig.Dim,
            _ => ""
        };

        return color.Length > 0 ? $"{color}{name}{ThemeConfig.Reset}" : name;
    }

    /// <summary>
    /// Gets a colored string representing file attributes.
    /// </summary>
    protected string GetAttributeString(FileAttributes attributes)
    {
        var r = ThemeConfig.Reset;
        var d = ThemeConfig.Dim;
        var sb = new StringBuilder();
        sb.Append(attributes.HasFlag(FileAttributes.Directory) ? $"{Theme["ls.directory"]}d{r}" : $"{d}-{r}");
        sb.Append(attributes.HasFlag(FileAttributes.Archive) ? $"{Theme["ls.config"]}a{r}" : $"{d}-{r}");
        sb.Append(attributes.HasFlag(FileAttributes.ReadOnly) ? $"{Theme["ls.archive"]}r{r}" : $"{d}-{r}");
        sb.Append(attributes.HasFlag(FileAttributes.Hidden) ? $"{Theme["ls.dim"]}h{r}" : $"{d}-{r}");
        sb.Append(attributes.HasFlag(FileAttributes.System) ? $"{Theme["ls.archive"]}s{r}" : $"{d}-{r}");
        return sb.ToString();
    }

    /// <summary>
    /// Formats a file system entry for display.
    /// </summary>
    protected string FormatEntry(FileSystemInfo entry, string? displayName = null)
    {
        var name = displayName ?? entry.Name;
        var isDir = entry is DirectoryInfo;
        var attrs = GetAttributeString(entry.Attributes);
        var size = isDir ? "     -" : $"{Theme["ls.size"]}{FormatFileSize(((FileInfo)entry).Length).PadLeft(6)}{ThemeConfig.Reset}";
        var modified = $"{ThemeConfig.Dim}{entry.LastWriteTime:MMM dd HH:mm}{ThemeConfig.Reset}";
        var coloredName = ColorFileName(name, entry);

        return $"{attrs}  {size}  {modified}  {coloredName}";
    }

    /// <summary>
    /// Recursively copies a directory.
    /// </summary>
    protected static void CopyDirectory(string source, string destination)
    {
        var dir = new DirectoryInfo(source);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Source directory not found: {source}");

        Directory.CreateDirectory(destination);

        foreach (var file in dir.GetFiles())
            file.CopyTo(Path.Combine(destination, file.Name));

        foreach (var subDir in dir.GetDirectories())
            CopyDirectory(subDir.FullName, Path.Combine(destination, subDir.Name));
    }

    /// <summary>
    /// Gets the total size of a directory recursively.
    /// </summary>
    protected static long GetDirectorySize(string path)
    {
        var dir = new DirectoryInfo(path);
        long size = 0;

        try
        {
            foreach (var file in dir.GetFiles())
                size += file.Length;

            foreach (var subDir in dir.GetDirectories())
                size += GetDirectorySize(subDir.FullName);
        }
        catch
        {
            // Ignore inaccessible directories
        }

        return size;
    }
}
