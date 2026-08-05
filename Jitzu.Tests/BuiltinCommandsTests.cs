using Jitzu.Shell;
using Jitzu.Shell.Core;
using Shouldly;

namespace Jitzu.Tests;

public class BuiltinCommandsTests
{
    [Test]
    public async Task QuitRequestsOrderlyReplShutdown()
    {
        var builtins = new BuiltinCommands(new ShellSession(), ThemeConfig.CreateDefault());

        var result = await builtins.ExecuteAsync("quit", ReadOnlyMemory<string>.Empty);

        builtins.ExitRequested.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    [Test]
    public void EveryRegisteredCommand_HasALazyFactory()
    {
        var builtins = new BuiltinCommands(new ShellSession(), ThemeConfig.CreateDefault());

        foreach (var command in builtins.CommandNames.Where(command => command != "sudo"))
            builtins.GetOrCreate(command).ShouldNotBeNull();
    }

    [Test]
    public void Aliases_ReuseTheSameCommandInstance()
    {
        var builtins = new BuiltinCommands(new ShellSession(), ThemeConfig.CreateDefault());

        builtins.GetOrCreate("quit").ShouldBeSameAs(builtins.GetOrCreate("exit"));
        builtins.GetOrCreate("less").ShouldBeSameAs(builtins.GetOrCreate("more"));
    }
}
