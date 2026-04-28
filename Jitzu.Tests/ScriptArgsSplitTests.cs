using Jitzu.Shell.Models;
using Shouldly;

namespace Jitzu.Tests;

public class ScriptArgsSplitTests
{
    [Test]
    public void NoScript_AllArgsGoToHost()
    {
        var (host, script) = JitzuOptions.SplitArgs(["-d", "--telemetry"]);
        host.ShouldBe(["-d", "--telemetry"]);
        script.ShouldBeEmpty();
    }

    [Test]
    public void ScriptPath_TrailingArgsGoToScript()
    {
        var (host, script) = JitzuOptions.SplitArgs(["-d", "script.jz", "--skip-clean", "foo"]);
        host.ShouldBe(["-d", "script.jz"]);
        script.ShouldBe(["--skip-clean", "foo"]);
    }

    [Test]
    public void DashDashSeparator_SplitsThere()
    {
        var (host, script) = JitzuOptions.SplitArgs(["-d", "script.jz", "--", "--skip-clean"]);
        host.ShouldBe(["-d", "script.jz"]);
        script.ShouldBe(["--skip-clean"]);
    }

    [Test]
    public void DashDashSeparator_BeforeScriptPath()
    {
        var (host, script) = JitzuOptions.SplitArgs(["--", "--anything", "goes"]);
        host.ShouldBeEmpty();
        script.ShouldBe(["--anything", "goes"]);
    }

    [Test]
    public void ScriptArgsWithoutLeadingDashes_StillCaptured()
    {
        var (host, script) = JitzuOptions.SplitArgs(["script.jz", "arg1", "arg2"]);
        host.ShouldBe(["script.jz"]);
        script.ShouldBe(["arg1", "arg2"]);
    }
}
