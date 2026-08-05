using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Jitzu.Shell.Infrastructure.Update;

public static class SelfUpdater
{
    private static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "jz-updater" },
            { "Accept", "application/vnd.github+json" }
        }
    };

    public static async Task RunAsync(bool force)
    {
        // Detect package manager installations
        if (!force)
        {
            var processPath = Environment.ProcessPath ?? "";
            if (processPath.Contains("scoop", StringComparison.OrdinalIgnoreCase)
                && processPath.Contains("apps", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Jitzu was installed via Scoop. Run: scoop update jz");
                return;
            }

        }

        Console.WriteLine("Checking for updates...");

        GitHubRelease? release;
        try
        {
            release = await Http.GetFromJsonAsync(
                "https://api.github.com/repos/jitzulang/jitzu/releases/latest",
                GitHubJsonContext.Default.GitHubRelease);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to check for updates: {ex.Message}");
            return;
        }

        if (release is null)
        {
            Console.WriteLine("No release information available.");
            return;
        }

        var latestTag = release.TagName.TrimStart('v');
        if (!Version.TryParse(latestTag, out var latestVersion))
        {
            Console.WriteLine($"Could not parse version: {release.TagName}");
            return;
        }

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
        if (currentVersion is not null && latestVersion <= currentVersion && !force)
        {
            Console.WriteLine($"Already up to date (v{currentVersion.ToString(3)}).");
            return;
        }

        var rid = GetRuntimeIdentifier();
        if (rid is null)
        {
            Console.WriteLine("Unsupported platform for self-update.");
            return;
        }

        var assetName = $"jitzu-{latestTag}-{rid}.zip";
        var asset = release.Assets?.FirstOrDefault(a =>
            a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase));

        if (asset is null)
        {
            Console.WriteLine($"No asset found for {rid} in release {release.TagName}.");
            return;
        }

        Console.WriteLine($"Downloading v{latestTag} for {rid}...");

        var tempDir = Path.Combine(Path.GetTempPath(), $"jz-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var zipPath = Path.Combine(tempDir, assetName);
            await using (var stream = await Http.GetStreamAsync(asset.BrowserDownloadUrl))
            await using (var fileStream = File.Create(zipPath))
            {
                await stream.CopyToAsync(fileStream);
            }

            ZipFile.ExtractToDirectory(zipPath, tempDir);

            var currentPath = Environment.ProcessPath;
            if (currentPath is null)
            {
                Console.WriteLine("Cannot determine current binary path.");
                return;
            }

            var binaryName = OperatingSystem.IsWindows() ? "jz.exe" : "jz";
            var newBinaryPath = Path.Combine(tempDir, binaryName);

            if (!File.Exists(newBinaryPath))
            {
                Console.WriteLine($"Expected binary '{binaryName}' not found in archive.");
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                ReplaceWindowsBinary(currentPath, newBinaryPath);
            }
            else
            {
                // Unix: write to temp file in same dir, chmod +x, rename (atomic)
                var targetDir = Path.GetDirectoryName(currentPath)!;
                var tempBinary = Path.Combine(targetDir, $".jz-update-{Guid.NewGuid():N}");
                File.Copy(newBinaryPath, tempBinary);
                File.SetUnixFileMode(tempBinary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(tempBinary, currentPath, overwrite: true);
            }

            Console.WriteLine($"Successfully upgraded to v{latestTag}!");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string? GetRuntimeIdentifier()
    {
        var arch = RuntimeInformation.ProcessArchitecture;

        if (OperatingSystem.IsWindows() && arch is Architecture.X64)
            return "win-x64";
        if (OperatingSystem.IsMacOS() && arch is Architecture.Arm64)
            return "osx-arm64";
        if (OperatingSystem.IsMacOS() && arch is Architecture.X64)
            return "osx-x64";
        if (OperatingSystem.IsLinux() && arch is Architecture.X64)
            return "linux-x64";

        return null;
    }

    internal static string GetAvailableOldPath(string currentPath)
    {
        var oldPath = currentPath + ".old";
        if (!File.Exists(oldPath))
            return oldPath;

        for (var i = 2; ; i++)
        {
            var candidate = $"{oldPath}.{i}";
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    internal static IEnumerable<string> GetOldPathsToClean(string currentPath)
    {
        var fullPath = Path.GetFullPath(currentPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory))
            yield break;

        var oldName = Path.GetFileName(fullPath) + ".old";
        var matches = new List<(int Order, string Path)>();
        foreach (var candidate in Directory.EnumerateFiles(directory, oldName + "*"))
        {
            var name = Path.GetFileName(candidate);
            if (name.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add((1, candidate));
                continue;
            }

            var suffix = name.AsSpan(oldName.Length);
            if (suffix.Length > 1 && suffix[0] == '.'
                && int.TryParse(suffix[1..], out var order) && order >= 2)
                matches.Add((order, candidate));
        }

        foreach (var match in matches.OrderBy(match => match.Order).ThenBy(match => match.Path, StringComparer.Ordinal))
            yield return match.Path;
    }

    internal static void CleanupOldBinaries(string currentPath)
    {
        foreach (var oldPath in GetOldPathsToClean(currentPath).ToArray())
        {
            try { File.Delete(oldPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void ReplaceWindowsBinary(string currentPath, string newBinaryPath,
        Action? afterCurrentMoved = null, Action? beforeRollback = null)
    {
        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(currentPath))!;
        var stagedPath = Path.Combine(targetDirectory, $".jz-update-{Guid.NewGuid():N}.tmp");
        var oldPath = GetAvailableOldPath(currentPath);
        var currentMoved = false;
        try
        {
            using (var source = new FileStream(newBinaryPath, FileMode.Open, FileAccess.Read,
                       FileShare.Read, 4096, FileOptions.SequentialScan))
            using (var staged = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                source.CopyTo(staged);
                staged.Flush(flushToDisk: true);
            }

            File.Move(currentPath, oldPath);
            currentMoved = true;
            afterCurrentMoved?.Invoke();
            File.Move(stagedPath, currentPath, overwrite: false);
        }
        catch (Exception updateFailure)
        {
            if (currentMoved && !File.Exists(currentPath))
            {
                try
                {
                    beforeRollback?.Invoke();
                    File.Move(oldPath, currentPath, overwrite: false);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(
                        "The update failed and the original binary could not be restored; the .old copy was retained.",
                        updateFailure, rollbackFailure);
                }
            }
            throw;
        }
        finally
        {
            try { File.Delete(stagedPath); } catch { }
        }
    }
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";
}

[JsonSerializable(typeof(GitHubRelease))]
internal partial class GitHubJsonContext : JsonSerializerContext;
