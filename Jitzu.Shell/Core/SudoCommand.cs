using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Jitzu.Shell.Core;

/// <summary>
/// Implements sudo for privilege elevation on Windows (via UAC) and Linux/macOS (via native sudo).
/// </summary>
public class SudoCommand
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    public async Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (!OperatingSystem.IsWindows())
            return await DelegateToNativeSudo(args);

        return ExecuteWindows(args);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private ShellResult ExecuteWindows(ReadOnlyMemory<string> args)
    {
        var span = args.Span;

        // No args = elevated shell (same as -s)
        if (span.Length == 0)
            return LaunchElevatedShell(login: false, preserveEnv: false);

        // Parse flags
        var preserveEnv = false;
        var commandStartIndex = 0;

        for (var i = 0; i < span.Length; i++)
        {
            var arg = span[i];
            switch (arg)
            {
                case "-l":
                    return CheckAdminStatus();

                case "-s":
                    return LaunchElevatedShell(login: false, preserveEnv);

                case "-i":
                    return LaunchElevatedShell(login: true, preserveEnv);

                case "-E":
                    preserveEnv = true;
                    commandStartIndex = i + 1;
                    continue;

                case "-u":
                    return new ShellResult(ResultType.Error, null,
                        new Exception("sudo: -u (run as another user) is not supported on Windows"));

                default:
                    if (arg.StartsWith('-'))
                    {
                        return new ShellResult(ResultType.Error, null,
                            new Exception($"sudo: unknown option: {arg}"));
                    }
                    commandStartIndex = i;
                    goto doneParsingFlags;
            }
        }
        doneParsingFlags:

        if (commandStartIndex >= span.Length)
            return LaunchElevatedShell(login: false, preserveEnv);

        var command = string.Join(' ', span[commandStartIndex..].ToArray());
        return RunElevatedCommand(command, preserveEnv);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ShellResult CheckAdminStatus()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        var username = Environment.UserName;

        return new ShellResult(ResultType.Jitzu,
            isAdmin
                ? $"User {username} is a member of the Administrators group\nThis shell is running with elevated privileges"
                : $"User {username} is a member of the Users group\nThis shell is NOT running with elevated privileges",
            null);
    }

    private static ShellResult RunElevatedCommand(string command, bool preserveEnv)
    {
        if (IsAlreadyElevated())
            return new ShellResult(ResultType.Jitzu, null,
                new Exception("sudo: shell is already elevated — run the command directly"));

        return LaunchElevatedChild(
            $"--sudo-exec \"{EscapeCommandArg(command)}\" --parent-pid {Environment.ProcessId}",
            preserveEnv);
    }

    private static ShellResult LaunchElevatedShell(bool login, bool preserveEnv)
    {
        if (IsAlreadyElevated())
            return new ShellResult(ResultType.Jitzu, "sudo: shell is already elevated", null);

        var flags = login ? "--sudo-login" : "--sudo-shell";
        if (preserveEnv) flags += " --sudo-preserve-env";
        flags += $" --parent-pid {Environment.ProcessId}";

        return LaunchElevatedChild(flags, preserveEnv);
    }

    private static ShellResult LaunchElevatedChild(string arguments, bool preserveEnv)
    {
        try
        {
            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? "jz.exe";

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = false,
            };

            var process = Process.Start(startInfo);
            if (process == null)
                return new ShellResult(ResultType.Error, null,
                    new Exception("sudo: failed to start elevated process"));

            if (arguments.Contains("--sudo-exec"))
            {
                process.WaitForExit();
                return new ShellResult(ResultType.Jitzu, null, null);
            }

            // Shell takeover: block forever — the child will kill this process
            Thread.Sleep(Timeout.Infinite);
            return new ShellResult(ResultType.Jitzu, null, null); // unreachable
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ShellResult(ResultType.Error, null,
                new Exception("sudo: operation cancelled by user"));
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, null,
                new Exception($"sudo: elevation failed: {ex.Message}"));
        }
    }

    // --- Helpers ---

    private static bool IsAlreadyElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string EscapeCommandArg(string command)
    {
        return command.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static async Task<ShellResult> DelegateToNativeSudo(ReadOnlyMemory<string> args)
    {
        var span = args.Span;
        var allArgs = span.Length > 0 ? string.Join(' ', span.ToArray()) : "-s";

        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/sudo",
            Arguments = allArgs,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return new ShellResult(ResultType.Error, null,
                    new Exception("sudo: failed to start /usr/bin/sudo"));

            await process.WaitForExitSuppressingCancelAsync();
            return new ShellResult(ResultType.Jitzu, null,
                process.ExitCode != 0 ? new Exception($"sudo: command exited with code {process.ExitCode}") : null);
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, null,
                new Exception($"sudo: {ex.Message}"));
        }
    }

    // --- Console Takeover (called from elevated child process) ---

    public static bool AttachToParentConsole(int parentPid)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        try
        {
            FreeConsole();

            if (!AttachConsole((uint)parentPid))
            {
                var error = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"sudo: AttachConsole failed (error {error})");
                return false;
            }

            var stdOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stdIn = new StreamReader(Console.OpenStandardInput());
            Console.SetOut(stdOut);
            Console.SetIn(stdIn);
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"sudo: console attach failed: {ex.Message}");
            return false;
        }
    }
}
