using System.Diagnostics;
using System.Text;

namespace Jitzu.Shell.Core.Commands;

/// <summary>
/// Kills processes by PID or job ID.
/// </summary>
public class KillCommand : CommandBase
{
    private const string Usage = "Usage: kill [-9] <pid|%jobid> OR kill [-9] (--port|-p) <port>";

    private readonly Func<int, IReadOnlyList<int>> _findPidsByPort;
    private readonly Func<int, bool, ShellResult> _killProcessById;

    public KillCommand(CommandContext context)
        : this(context, PortProcessFinder.FindTcpListenerPids, KillProcessById)
    {
    }

    internal KillCommand(
        CommandContext context,
        Func<int, IReadOnlyList<int>> findPidsByPort,
        Func<int, bool, ShellResult> killProcessById)
        : base(context)
    {
        _findPidsByPort = findPidsByPort;
        _killProcessById = killProcessById;
    }

    public override Task<ShellResult> ExecuteAsync(ReadOnlyMemory<string> args)
    {
        if (args.Length == 0)
            return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception(Usage)));

        try
        {
            var forceKill = false;
            int? targetPid = null;
            int? jobId = null;
            int? targetPort = null;
            var portMode = false;

            foreach (var arg in args.Span)
            {
                if (arg is "-9" or "-KILL" or "-kill")
                    forceKill = true;
                else if (arg is "--port" or "-p")
                    portMode = true;
                else if (arg.StartsWith("--port=", StringComparison.Ordinal))
                {
                    portMode = true;
                    if (!TryParsePort(arg["--port=".Length..], out var port, out var error))
                        return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception(error)));
                    if (targetPort.HasValue)
                        return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception("kill: multiple ports specified")));
                    targetPort = port;
                }
                else if (portMode)
                {
                    if (!TryParsePort(arg, out var port, out var error))
                        return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception(error)));
                    if (targetPort.HasValue)
                        return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception("kill: multiple ports specified")));
                    targetPort = port;
                }
                else if (arg.StartsWith('%'))
                {
                    if (int.TryParse(arg.AsSpan(1), out var jid))
                        jobId = jid;
                    else
                        return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception($"Invalid job ID: {arg}")));
                }
                else if (int.TryParse(arg, out var pid))
                    targetPid = pid;
                else
                    return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception($"Invalid argument: {arg}")));
            }

            if (portMode)
            {
                if (!targetPort.HasValue)
                    return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception(Usage)));
                if (jobId.HasValue || targetPid.HasValue)
                    return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception("kill: --port cannot be combined with a pid or job id")));

                return Task.FromResult(KillByPort(targetPort.Value, forceKill));
            }

            if (jobId.HasValue)
            {
                if (Strategy == null)
                    return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception("kill: execution strategy not available")));

                var job = Strategy.Jobs.FirstOrDefault(j => j.Id == jobId.Value);
                if (job == null)
                    return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception($"kill: no such job %{jobId.Value}")));

                try
                {
                    if (!job.Process.HasExited)
                    {
                        if (forceKill)
                            job.Process.Kill(entireProcessTree: true);
                        else
                            job.Process.Kill();
                    }

                    return Task.FromResult(new ShellResult(ResultType.OsCommand, $"Killed job %{jobId.Value} (pid {job.Process.Id})", null));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception($"kill: failed to kill job %{jobId.Value}: {ex.Message}")));
                }
            }

            if (!targetPid.HasValue)
                return Task.FromResult(new ShellResult(ResultType.Error, "", new Exception(Usage)));

            return Task.FromResult(_killProcessById(targetPid.Value, forceKill));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ShellResult(ResultType.Error, "", ex));
        }
    }

    private ShellResult KillByPort(int port, bool forceKill)
    {
        var pids = _findPidsByPort(port).Distinct().ToArray();
        if (pids.Length == 0)
            return new ShellResult(ResultType.Error, "", new Exception($"kill: no process listening on port {port}"));

        var killedPids = new List<int>();
        var errors = new List<string>();

        foreach (var pid in pids)
        {
            var result = _killProcessById(pid, forceKill);
            if (result.Type == ResultType.Error)
                errors.Add($"  pid {pid}: {result.Error?.Message ?? "unknown error"}");
            else
                killedPids.Add(pid);
        }

        if (killedPids.Count == 0)
        {
            var message = new StringBuilder();
            message.AppendLine($"kill: failed to kill process{(pids.Length != 1 ? "es" : "")} listening on port {port}");
            foreach (var error in errors)
                message.AppendLine(error);
            return new ShellResult(ResultType.Error, "", new Exception(message.ToString().TrimEnd()));
        }

        var output = killedPids.Count == 1
            ? $"Killed process {killedPids[0]} listening on port {port}"
            : $"Killed {killedPids.Count} processes listening on port {port}";

        if (errors.Count == 0)
            return new ShellResult(ResultType.OsCommand, output, null);

        var sb = new StringBuilder();
        sb.AppendLine(output);
        foreach (var error in errors)
            sb.AppendLine(error);
        return new ShellResult(ResultType.OsCommand, sb.ToString().TrimEnd(), null);
    }

    private static bool TryParsePort(string value, out int port, out string error)
    {
        if (!int.TryParse(value, out port) || port is < 1 or > 65535)
        {
            error = $"kill: invalid port '{value}'";
            return false;
        }

        error = "";
        return true;
    }

    private static ShellResult KillProcessById(int pid, bool forceKill)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (forceKill)
                process.Kill(entireProcessTree: true);
            else if (!process.CloseMainWindow())
                process.Kill();

            return new ShellResult(ResultType.OsCommand, $"Killed process {pid}", null);
        }
        catch (ArgumentException)
        {
            return new ShellResult(ResultType.Error, "", new Exception($"kill: no process with pid {pid}"));
        }
        catch (Exception ex)
        {
            return new ShellResult(ResultType.Error, "", new Exception($"kill: {ex.Message}"));
        }
    }
}
