using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Jitzu.StartupBenchmark;

internal sealed class ConPtyProcess : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int StartfUseStdHandles = 0x00000100;
    private const int JobObjectBasicAccountingInformationClass = 1;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private static readonly IntPtr PseudoConsoleAttribute = (IntPtr)0x00020016;

    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly IntPtr _process;
    private readonly IntPtr _pseudoConsole;
    private readonly IntPtr _job;

    public int ProcessId { get; }

    private bool _disposed;
    private int _terminationStarted;

    public ConPtyProcess(string executable, string arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables) :
        this(executable, arguments, workingDirectory, environmentVariables, ConPtyFailurePoint.None, null)
    {
    }

    internal ConPtyProcess(string executable, string arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables, ConPtyFailurePoint failurePoint,
        Action<int>? processCreated)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The startup benchmark currently requires Windows ConPTY.");

        IntPtr childInput = IntPtr.Zero, parentInput = IntPtr.Zero;
        IntPtr parentOutput = IntPtr.Zero, childOutput = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        IntPtr processThread = IntPtr.Zero;
        var processAssignedToJob = false;
        FileStream? input = null;
        FileStream? output = null;
        try
        {
            CreatePipe(out childInput, out parentInput, IntPtr.Zero, 0).ThrowIfFalse("CreatePipe(input)");
            CreatePipe(out parentOutput, out childOutput, IntPtr.Zero, 0).ThrowIfFalse("CreatePipe(output)");
            CreatePseudoConsole(new Coord(120, 30), childInput, childOutput, 0, out _pseudoConsole)
                .ThrowIfFailed("CreatePseudoConsole");

            nuint attributeBytes = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
            attributes = Marshal.AllocHGlobal((nint)attributeBytes);
            InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeBytes)
                .ThrowIfFalse("InitializeProcThreadAttributeList");
            UpdateProcThreadAttribute(attributes, 0, PseudoConsoleAttribute, _pseudoConsole,
                    (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)
                .ThrowIfFalse("UpdateProcThreadAttribute");

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Cb = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles
                },
                AttributeList = attributes
            };
            environment = BuildEnvironmentBlock(environmentVariables);
            var commandLine = new StringBuilder($"\"{executable}\" {arguments}");
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            SetInformationJobObject(_job, JobObjectExtendedLimitInformationClass, ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())
                .ThrowIfFalse("SetInformationJobObject");
            CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended, environment,
                    workingDirectory, ref startup, out var info)
                .ThrowIfFalse("CreateProcess");

            _process = info.Process;
            processThread = info.Thread;
            ProcessId = info.ProcessId;
            processCreated?.Invoke(ProcessId);
            if (failurePoint == ConPtyFailurePoint.Assign)
                throw new InvalidOperationException("Injected AssignProcessToJobObject failure.");
            AssignProcessToJobObject(_job, _process).ThrowIfFalse("AssignProcessToJobObject");
            processAssignedToJob = true;
            if (failurePoint == ConPtyFailurePoint.Resume)
                throw new InvalidOperationException("Injected ResumeThread failure.");
            if (ResumeThread(processThread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread");
            if (failurePoint == ConPtyFailurePoint.PostCreate)
                throw new InvalidOperationException("Injected post-create failure.");
            CloseHandle(processThread);
            processThread = IntPtr.Zero;
            CloseHandle(childInput);
            childInput = IntPtr.Zero;
            CloseHandle(childOutput);
            childOutput = IntPtr.Zero;
            input = new FileStream(new SafeFileHandle(parentInput, ownsHandle: true), FileAccess.Write, 4096, false);
            parentInput = IntPtr.Zero;
            output = new FileStream(new SafeFileHandle(parentOutput, ownsHandle: true), FileAccess.Read, 4096, false);
            parentOutput = IntPtr.Zero;
            _input = input;
            _output = output;
            input = null;
            output = null;
        }
        catch (Exception startupFailure)
        {
            Exception? cleanupFailure = null;
            input?.Dispose();
            output?.Dispose();
            if (_process != IntPtr.Zero)
            {
                try
                {
                    var terminated = processAssignedToJob
                        ? TerminateJobObject(_job, 1)
                        : TerminateProcess(_process, 1);
                    if (!terminated)
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (!GetExitCodeProcess(_process, out var exitCode) || exitCode == 259)
                            throw new Win32Exception(error, processAssignedToJob
                                ? "TerminateJobObject during startup cleanup"
                                : "TerminateProcess during startup cleanup");
                    }

                    var wait = WaitForSingleObject(_process, 5000);
                    if (wait != 0)
                        throw wait == 258
                            ? new TimeoutException("The suspended child did not terminate during startup cleanup.")
                            : new Win32Exception(Marshal.GetLastWin32Error(),
                                "WaitForSingleObject during startup cleanup");
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                }
                CloseHandle(_process);
            }
            if (_job != IntPtr.Zero)
                CloseHandle(_job);
            if (_pseudoConsole != IntPtr.Zero)
                ClosePseudoConsole(_pseudoConsole);
            if (cleanupFailure is not null)
                throw new AggregateException("ConPTY startup failed and child cleanup could not be verified.",
                    startupFailure, cleanupFailure);
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (environment != IntPtr.Zero)
                Marshal.FreeHGlobal(environment);
            if (childInput != IntPtr.Zero) CloseHandle(childInput);
            if (childOutput != IntPtr.Zero) CloseHandle(childOutput);
            if (parentInput != IntPtr.Zero) CloseHandle(parentInput);
            if (parentOutput != IntPtr.Zero) CloseHandle(parentOutput);
            if (processThread != IntPtr.Zero) CloseHandle(processThread);
        }
    }

    public async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _input.WriteAsync(bytes, cancellationToken);
        await _input.FlushAsync(cancellationToken);
    }

    public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        _output.ReadAsync(buffer, cancellationToken).AsTask();

    public bool WaitForExit(TimeSpan timeout) =>
        WaitForSingleObject(_process, checked((uint)timeout.TotalMilliseconds)) == 0;

    public int GetExitCode()
    {
        GetExitCodeProcess(_process, out var code).ThrowIfFalse("GetExitCodeProcess");
        return unchecked((int)code);
    }

    public bool TerminateTreeAndWait(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        var firstTermination = Interlocked.Exchange(ref _terminationStarted, 1) == 0;
        if (firstTermination)
            TerminateJobObject(_job, 1);

        var rootExited = WaitForSingleObject(_process, checked((uint)timeout.TotalMilliseconds)) == 0;
        if (firstTermination)
        {
            ClosePseudoConsole(_pseudoConsole);
            try { _output.Dispose(); } catch { }
        }
        while (rootExited && GetActiveProcessCount() != 0 && stopwatch.Elapsed < timeout)
            Thread.Sleep(10);
        return rootExited && GetActiveProcessCount() == 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (WaitForSingleObject(_process, 0) != 0)
            TerminateTreeAndWait(TimeSpan.FromSeconds(5));
        _input.Dispose();
        _output.Dispose();
        if (_terminationStarted == 0)
            ClosePseudoConsole(_pseudoConsole);
        CloseHandle(_process);
        CloseHandle(_job);
    }

    private uint GetActiveProcessCount()
    {
        if (!QueryInformationJobObject(_job, JobObjectBasicAccountingInformationClass,
                out JobObjectBasicAccounting information,
                (uint)Marshal.SizeOf<JobObjectBasicAccounting>(), out _))
            return uint.MaxValue;
        return information.ActiveProcesses;
    }

    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environmentVariables)
    {
        var entries = new Dictionary<string, string>(environmentVariables, StringComparer.OrdinalIgnoreCase);
        entries["JITZU_STARTUP_PROFILE"] = "terminal";
        var block = string.Join('\0', entries.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coord(short X, short Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved2Pointer, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr attributes, uint size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags,
        out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
        IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass,
        ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass,
        out JobObjectBasicAccounting information, uint informationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}

internal enum ConPtyFailurePoint
{
    None,
    Assign,
    Resume,
    PostCreate
}

internal static class NativeResultExtensions
{
    public static void ThrowIfFalse(this bool result, string operation)
    {
        if (!result)
            throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }

    public static void ThrowIfFailed(this int hresult, string operation)
    {
        if (hresult < 0)
            Marshal.ThrowExceptionForHR(hresult, new IntPtr(-1));
    }
}
