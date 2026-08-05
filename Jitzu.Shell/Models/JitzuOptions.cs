using Clap.Net;

namespace Jitzu.Shell.Models;

[Command(
    About = "Jitzu - A fast and flexible script execution engine",
    LongAbout = "Jitzu is a modern scripting language literally designed to be full to the brim with syntax sugar, making it fun(?) to write scripts and stuff.\n\nLearn more at https://jitzu.dev/docs"
)]
public partial class JitzuOptions
{
    // Shell options
    [Arg(Long = "splash", Negation = true)]
    public bool Splash { get; init; } = true;

    [Arg(Short = 'c', Long = "command")]
    public string? Command { get; init; }

    [Arg(Long = "sudo-exec")]
    public string? SudoExec { get; init; }

    [Arg(Long = "sudo-shell")]
    public bool SudoShell { get; init; }

    [Arg(Long = "sudo-login")]
    public bool SudoLogin { get; init; }

    [Arg(Long = "parent-pid")]
    public int ParentPid { get; init; }

    [Arg(Long = "sudo-preserve-env")]
    public bool SudoPreserveEnv { get; init; }

    [Arg(Long = "persist", Negation = true, Help = "Disable reading/writing history and alias files")]
    public bool Persist { get; init; } = true;

    [Arg(Long = "config", Negation = true, Help = "Disable loading ~/.jitzu/config.jz")]
    public bool Config { get; init; } = true;

    // Interpreter options
    [Arg(Short = 'd', Long = "debug")]
    public bool Debug { get; init; }

    [Arg(Short = 't', Long = "telemetry")]
    public bool Telemetry { get; init; }

    [Arg(Short = 'b', Help = "If provided, the bytecode of the application will be written to it")]
    public string? BytecodeOutputPath { get; set; }

    [Arg(Long = "install-path")]
    public bool InstallPath { get; init; }

    // Positional args
    public string? ScriptPath { get; init; }

    [Arg(Help = "Additional arguments to pass to the script")]
    public string[] ScriptArgs { get; set; } = [];

    /// <summary>
    /// Splits argv into (hostArgs, scriptArgs) so flags after the script path are
    /// forwarded verbatim to the script instead of being eaten by the jz host parser.
    ///
    /// Rules:
    /// - An explicit `--` separator splits at that token.
    /// - Otherwise the first arg that looks like a script path (ends in .jz, or is the
    ///   literal "upgrade", or is a file that exists) is the boundary; everything after
    ///   it (exclusive) becomes script args.
    /// </summary>
    public static (string[] HostArgs, string[] ScriptArgs) SplitArgs(string[] args)
    {
        var dashDashIndex = Array.IndexOf(args, "--");
        if (dashDashIndex >= 0)
        {
            var host = args[..dashDashIndex];
            var script = args[(dashDashIndex + 1)..];
            return (host, script);
        }

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Length == 0 || a[0] == '-')
                continue;

            var looksLikeScript = a.EndsWith(".jz", StringComparison.OrdinalIgnoreCase)
                || a == "upgrade"
                || File.Exists(a)
                || File.Exists(Path.ChangeExtension(a, "jz"));

            if (!looksLikeScript)
                continue;

            var host = args[..(i + 1)];
            var script = args[(i + 1)..];
            return (host, script);
        }

        return (args, []);
    }
}
