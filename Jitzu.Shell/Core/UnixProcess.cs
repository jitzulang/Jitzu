using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Jitzu.Shell.Core;

/// <summary>Replaces the shell process, retaining its PID and inherited file descriptors.</summary>
internal static class UnixProcess
{
    public static ShellResult Replace(string[] args)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return Failure("exec is only supported on Linux and macOS", 126);

        if (args.Any(arg => arg.Contains('\0')))
            return Failure("arguments cannot contain a NUL character", 126);

        // .NET maintains its own environment on Unix. Pass the current managed
        // environment explicitly so changes made by export/unset survive exec.
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Select(entry => $"{entry.Key}={entry.Value}").ToArray();
        using var argv = new NativeStrings(args);
        using var envp = new NativeStrings(environment);

        var command = args[0];
        var candidates = command.Contains('/')
            ? [command]
            : (Environment.GetEnvironmentVariable("PATH") ?? "/bin:/usr/bin")
                .Split(':').Select(dir => Path.Combine(dir.Length == 0 ? "." : dir, command)).ToArray();

        Console.Out.Flush();
        Console.Error.Flush();

        // The CLR ignores SIGPIPE. External programs should receive its default
        // disposition; restore the old handler if exec fails and we return.
        var oldPipeHandler = Signal(13, IntPtr.Zero);
        try
        {
            var denied = false;
            foreach (var candidate in candidates)
            {
                Execve(candidate, argv.Pointer, envp.Pointer);
                var error = Marshal.GetLastPInvokeError();

                if (error == 8) // ENOEXEC: executable text file without a shebang
                {
                    using var scriptArgv = new NativeStrings(["sh", candidate, .. args[1..]]);
                    Execve("/bin/sh", scriptArgv.Pointer, envp.Pointer);
                    error = Marshal.GetLastPInvokeError();
                }

                if (error == 13) // EACCES: keep searching PATH for an executable
                    denied = true;
                else if (error is not (2 or 20)) // ENOENT, ENOTDIR
                    return Failure($"{command}: {new Win32Exception(error).Message}", 126);
            }

            return denied
                ? Failure($"{command}: permission denied", 126)
                : Failure($"{command}: command not found", 127);
        }
        finally
        {
            Signal(13, oldPipeHandler);
        }
    }

    private static ShellResult Failure(string message, int exitCode) =>
        new(ResultType.Error, "", new Exception($"exec: {message}")) { Status = exitCode };

    private static int Execve(string path, IntPtr argv, IntPtr envp) => OperatingSystem.IsMacOS()
        ? ExecveMac(path, argv, envp)
        : ExecveLinux(path, argv, envp);

    private static IntPtr Signal(int number, IntPtr handler) => OperatingSystem.IsMacOS()
        ? SignalMac(number, handler)
        : SignalLinux(number, handler);

    [DllImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static extern int ExecveLinux([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr argv, IntPtr envp);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "execve", SetLastError = true)]
    private static extern int ExecveMac([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr argv, IntPtr envp);

    [DllImport("libc", EntryPoint = "signal")]
    private static extern IntPtr SignalLinux(int number, IntPtr handler);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "signal")]
    private static extern IntPtr SignalMac(int number, IntPtr handler);

    private sealed class NativeStrings : IDisposable
    {
        private readonly IntPtr[] _strings;
        public IntPtr Pointer { get; }

        public NativeStrings(string[] strings)
        {
            _strings = new IntPtr[strings.Length];
            Pointer = Marshal.AllocHGlobal((strings.Length + 1) * IntPtr.Size);
            try
            {
                for (var i = 0; i < strings.Length; i++)
                {
                    _strings[i] = Marshal.StringToCoTaskMemUTF8(strings[i]);
                    Marshal.WriteIntPtr(Pointer, i * IntPtr.Size, _strings[i]);
                }
                Marshal.WriteIntPtr(Pointer, strings.Length * IntPtr.Size, IntPtr.Zero);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var value in _strings)
                Marshal.FreeCoTaskMem(value);
            Marshal.FreeHGlobal(Pointer);
        }
    }
}
