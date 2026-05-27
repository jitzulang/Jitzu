using System.Net;
using System.Net.Sockets;
using Jitzu.Shell;
using Jitzu.Shell.Core;
using Jitzu.Shell.Core.Commands;
using Shouldly;

namespace Jitzu.Tests;

public class KillCommandTests
{
    [Test]
    public async Task PortFlag_KillsProcessListeningOnPort()
    {
        var killedPid = 0;
        var forceKill = true;
        var cmd = CreateCommand(
            port => port == 3000 ? [1234] : [],
            (pid, force) =>
            {
                killedPid = pid;
                forceKill = force;
                return new ShellResult(ResultType.OsCommand, $"Killed process {pid}", null);
            });

        var result = await cmd.ExecuteAsync(new[] { "--port", "3000" });

        result.Type.ShouldBe(ResultType.OsCommand);
        result.Output.ShouldBe("Killed process 1234 listening on port 3000");
        killedPid.ShouldBe(1234);
        forceKill.ShouldBeFalse();
    }

    [Test]
    public async Task PortFlag_RespectsForceFlag()
    {
        var forceKill = false;
        var cmd = CreateCommand(
            _ => [1234],
            (pid, force) =>
            {
                forceKill = force;
                return new ShellResult(ResultType.OsCommand, $"Killed process {pid}", null);
            });

        var result = await cmd.ExecuteAsync(new[] { "-9", "-p", "3000" });

        result.Type.ShouldBe(ResultType.OsCommand);
        forceKill.ShouldBeTrue();
    }

    [Test]
    public async Task PortFlag_WhenNoListener_ReturnsError()
    {
        var cmd = CreateCommand(_ => [], (_, _) => throw new InvalidOperationException("Should not kill"));

        var result = await cmd.ExecuteAsync(new[] { "--port", "3000" });

        result.Type.ShouldBe(ResultType.Error);
        result.Error!.Message.ShouldBe("kill: no process listening on port 3000");
    }

    [Test]
    public async Task PortFlag_WithInvalidPort_ReturnsError()
    {
        var cmd = CreateCommand(_ => [], (_, _) => throw new InvalidOperationException("Should not kill"));

        var result = await cmd.ExecuteAsync(new[] { "--port", "70000" });

        result.Type.ShouldBe(ResultType.Error);
        result.Error!.Message.ShouldBe("kill: invalid port '70000'");
    }

    [Test]
    public async Task PortProcessFinder_FindsCurrentProcessTcpListener()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return;

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        IReadOnlyList<int> pids = [];
        for (var i = 0; i < 20; i++)
        {
            pids = PortProcessFinder.FindTcpListenerPids(port);
            if (pids.Contains(Environment.ProcessId))
                break;

            await Task.Delay(50);
        }

        pids.ShouldContain(Environment.ProcessId);
    }

    private static KillCommand CreateCommand(
        Func<int, IReadOnlyList<int>> findPidsByPort,
        Func<int, bool, ShellResult> killProcessById)
    {
        var context = new CommandContext(new ShellSession(), ThemeConfig.CreateDefault());
        return new KillCommand(context, findPidsByPort, killProcessById);
    }
}
