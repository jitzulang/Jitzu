using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Jitzu.Shell.Infrastructure;
using Jitzu.Shell.Infrastructure.Logging;

namespace Jitzu.Shell;

/// <summary>
/// Central theme configuration loaded from ~/.jitzu/colours.json.
/// Maps semantic color names (e.g. "syntax.command") to pre-computed ANSI RGB escape codes.
/// </summary>
public sealed class ThemeConfig
{
    public const string Reset = "\e[0m";
    public const string Bold = "\e[1m";
    public const string Dim = "\e[2m";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["syntax.command"]    = "#87af87",
        ["syntax.keyword"]    = "#87afd7",
        ["syntax.string"]     = "#afaf87",
        ["syntax.flag"]       = "#87afaf",
        ["syntax.pipe"]       = "#af87af",
        ["syntax.boolean"]    = "#d7af87",

        ["git.branch"]        = "#808080",
        ["git.dirty"]         = "#d7af87",
        ["git.staged"]        = "#87af87",
        ["git.untracked"]     = "#808080",
        ["git.remote"]        = "#87afaf",

        ["prompt.directory"]  = "#87d7ff",
        ["prompt.arrow"]      = "#5faf5f",
        ["prompt.error"]      = "#d75f5f",
        ["prompt.user"]       = "#5f8787",
        ["prompt.duration"]   = "#d7af87",
        ["prompt.time"]       = "#808080",
        ["prompt.jobs"]       = "#87afaf",

        ["ls.directory"]      = "#87afd7",
        ["ls.executable"]     = "#87af87",
        ["ls.archive"]        = "#d75f5f",
        ["ls.media"]          = "#af87af",
        ["ls.code"]           = "#87afaf",
        ["ls.config"]         = "#d7af87",
        ["ls.project"]        = "#d7af87",
        ["ls.size"]           = "#87af87",
        ["ls.dim"]            = "#808080",

        ["error"]             = "#d75f5f",

        ["prediction.text"]        = "#808080",
        ["prediction.selected.bg"] = "#303050",
        ["prediction.selected.fg"] = "#ffffff",

        ["selection.bg"]      = "#264f78",
        ["selection.fg"]      = "#ffffff",

        ["dropdown.gutter"]   = "#404040",
        ["dropdown.status"]   = "#5f87af",
    };

    private readonly IReadOnlyDictionary<string, string> _overrides;
    private readonly ConcurrentDictionary<string, string> _resolved = new();
    private readonly string? _missingDefaultPath;

    private ThemeConfig(IReadOnlyDictionary<string, string> overrides, string? missingDefaultPath = null) =>
        (_overrides, _missingDefaultPath) = (overrides, missingDefaultPath);

    /// <summary>
    /// Creates a ThemeConfig with default ANSI colors. For tests that need a theme without filesystem access.
    /// </summary>
    internal static ThemeConfig CreateDefault() => new(new Dictionary<string, string>());

    /// <summary>
    /// Gets the ANSI escape code for a semantic color key.
    /// Returns empty string if the key is unknown.
    /// </summary>
    public string this[string key]
    {
        get
        {
            if (_overrides.TryGetValue(key, out var value)) return value;
            if (!Defaults.TryGetValue(key, out var hex)) return "";
            return _resolved.GetOrAdd(key, static (_, state) => HexToAnsi(state.Hex, state.Background),
                (Hex: hex, Background: key.EndsWith(".bg", StringComparison.Ordinal)));
        }
    }

    public static ThemeConfig Load(bool loadUserConfig = true, string? userProfilePath = null)
    {
        var colours = new Dictionary<string, string>();
        string? missingDefaultPath = null;

        if (loadUserConfig)
        {
            var configPath = Path.Combine(userProfilePath
                                          ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".jitzu", "colours.json");
            if (File.Exists(configPath))
                ApplyUserOverridesFromFile(configPath, colours);
            else
                missingDefaultPath = configPath;
        }

        var theme = new ThemeConfig(colours, missingDefaultPath);
        StartupProfiler.Mark("theme-loaded");
        return theme;
    }

    /// <summary>
    /// Restores first-run behaviour without putting directory and file creation on the
    /// first-prompt path. CreateNew preserves a file written by another process/session.
    /// </summary>
    internal void EnsureDefaultFile()
    {
        if (_missingDefaultPath is null || File.Exists(_missingDefaultPath))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_missingDefaultPath)!);
            using var stream = new FileStream(_missingDefaultPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(BuildDefaultJson());
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch (IOException)
        {
            // Another process may have created it, or persistence may be unavailable.
        }
        catch (UnauthorizedAccessException)
        {
            // Non-critical — the in-memory default theme remains usable.
        }
        catch (System.Security.SecurityException)
        {
            // Non-critical — the in-memory default theme remains usable.
        }
    }

    private static void ApplyUserOverridesFromFile(string configPath, Dictionary<string, string> colours)
    {
        try
        {
            var json = StartupFileReader.ReadAllBytes(configPath, StartupFileReader.ThemeMaxBytes);
            ApplyUserOverrides(json, colours);
        }
        catch
        {
            // Malformed config — silently fall back to defaults
        }
    }

    internal static void ApplyUserOverrides(ReadOnlySpan<byte> json, Dictionary<string, string> colours)
    {
        var reader = new Utf8JsonReader(json);
        var path = new List<string>(4);
        string? propertyName = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    propertyName = reader.GetString();
                    break;

                case JsonTokenType.StartObject when propertyName is not null:
                    path.Add(propertyName);
                    propertyName = null;
                    break;

                case JsonTokenType.EndObject:
                    if (path.Count > 0)
                        path.RemoveAt(path.Count - 1);
                    propertyName = null;
                    break;

                case JsonTokenType.String when propertyName is not null:
                    var hex = reader.GetString();
                    if (hex is not null && hex.StartsWith('#') && hex.Length == 7)
                    {
                        var key = path.Count switch
                        {
                            0 => propertyName,
                            1 => string.Concat(path[0], ".", propertyName),
                            _ => string.Concat(string.Join('.', path), ".", propertyName)
                        };
                        colours[key] = HexToAnsi(hex, key.EndsWith(".bg"));
                    }
                    propertyName = null;
                    break;

                default:
                    propertyName = null;
                    break;
            }
        }
    }

    private static string HexToAnsi(string hex, bool background)
    {
        var r = Convert.ToByte(hex[1..3], 16);
        var g = Convert.ToByte(hex[3..5], 16);
        var b = Convert.ToByte(hex[5..7], 16);
        var layer = background ? 48 : 38;
        return $"\e[{layer};2;{r};{g};{b}m";
    }

    /// <summary>
    /// Builds a nested JSON string from the flat defaults dictionary.
    /// Keys like "prompt.arrow" become { "prompt": { "arrow": "#hex" } }.
    /// </summary>
    private static string BuildDefaultJson()
    {
        var root = new Dictionary<string, object>();

        foreach (var (key, value) in Defaults)
        {
            var segments = key.Split('.');
            var current = root;

            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!current.TryGetValue(segments[i], out var next))
                {
                    next = new Dictionary<string, object>();
                    current[segments[i]] = next;
                }
                current = (Dictionary<string, object>)next;
            }

            current[segments[^1]] = value;
        }

        return JsonSerializer.Serialize(root, JsonOptions);
    }
}
