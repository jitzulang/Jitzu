using Shouldly;

namespace Jitzu.Tests;

public class HandoffTests
{
    [Test]
    public async Task Handoff_RunsActionWithPayloadWrittenByCommand()
    {
        using var workspace = new HandoffWorkspace();
        await using var harness = new ShellTestHarness(Path.GetTempPath());
        await harness.StartAsync(ShellTestHarness.GetShellPath());
        await harness.SendCommandAsync($"path \"{workspace.BinDirectory}\"");
        await harness.SendCommandAsync("handoff writer HANDOFF_TARGET cd");

        await harness.SendCommandAsync($"writer \"{workspace.TargetDirectory}\"");
        var output = await harness.SendCommandAsync("pwd");

        output.ShouldContain(workspace.TargetDirectory);
    }

    [Test]
    public async Task Handoff_SkipsActionWhenCommandWritesNothing()
    {
        using var workspace = new HandoffWorkspace();
        await using var harness = new ShellTestHarness(Path.GetTempPath());
        await harness.StartAsync(ShellTestHarness.GetShellPath());
        await harness.SendCommandAsync($"path \"{workspace.BinDirectory}\"");
        await harness.SendCommandAsync("handoff writer HANDOFF_TARGET echo HANDOFF_RAN");

        var output = await harness.SendCommandAsync("writer");

        output.ShouldContain("WRITER_RAN");
        output.ShouldNotContain("HANDOFF_RAN");
    }

    private sealed class HandoffWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"jitzu-handoff-{Guid.NewGuid():N}");

        public HandoffWorkspace()
        {
            Directory.CreateDirectory(BinDirectory);
            Directory.CreateDirectory(TargetDirectory);
            WriteWriter();
        }

        public string BinDirectory => Path.Combine(_root, "bin");
        public string TargetDirectory => Path.Combine(_root, "target dir");

        private void WriteWriter()
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(BinDirectory, "writer.cmd"),
                    "@echo off\r\necho WRITER_RAN\r\nif not \"%~1\"==\"\" >\"%HANDOFF_TARGET%\" echo %~1\r\n");
                return;
            }

            var script = Path.Combine(BinDirectory, "writer");
            File.WriteAllText(script,
                "#!/bin/sh\necho WRITER_RAN\nif [ -n \"$1\" ]; then printf '%s\\n' \"$1\" > \"$HANDOFF_TARGET\"; fi\n");
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
