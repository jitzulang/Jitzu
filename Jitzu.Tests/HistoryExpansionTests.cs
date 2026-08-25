using Jitzu.Shell;
using Shouldly;

namespace Jitzu.Tests;

public class HistoryExpansionTests
{
    [Arguments("^a0", "arg1")]
    [Arguments("^a1", "arg2")]
    [Arguments("^a2", "arg3")]
    [Arguments("^l", "arg3")]
    [Arguments("^p", "thing.exe arg1 arg2 arg3")]
    [Test]
    public void PreviousCommandDesignators_Expand(string designator, string expected)
    {
        var history = HistoryWith("thing.exe arg1 arg2 arg3");

        var success = HistoryExpansion.TryExpand(designator, history, out var expanded, out var error);

        success.ShouldBeTrue();
        error.ShouldBeNull();
        expanded.ShouldBe(expected);
    }

    [Test]
    public void ArgumentDesignator_PreservesQuotedArgument()
    {
        var history = HistoryWith("thing.exe arg1 \"arg two\"");

        HistoryExpansion.TryExpand("other.exe ^a1", history, out var expanded, out _).ShouldBeTrue();

        expanded.ShouldBe("other.exe \"arg two\"");
    }

    [Test]
    public void PreviousCommand_CanBeEmbeddedAsACommandToken()
    {
        var history = HistoryWith("thing.exe arg1");

        HistoryExpansion.TryExpand("sudo ^p", history, out var expanded, out _).ShouldBeTrue();

        expanded.ShouldBe("sudo thing.exe arg1");
    }

    [Test]
    public void DesignatorsInsideSingleQuotes_AreNotExpanded()
    {
        var history = HistoryWith("thing.exe arg1");

        HistoryExpansion.TryExpand("echo '^a0' '^l' '^p'", history, out var expanded, out _).ShouldBeTrue();

        expanded.ShouldBe("echo '^a0' '^l' '^p'");
    }

    [Test]
    public void DesignatorMustBeACompleteToken()
    {
        var history = HistoryWith("thing.exe arg1");

        HistoryExpansion.TryExpand("echo ^label file^a0", history, out var expanded, out _).ShouldBeTrue();

        expanded.ShouldBe("echo ^label file^a0");
    }

    [Test]
    public void MissingArgument_ReturnsAnError()
    {
        var history = HistoryWith("thing.exe arg1");

        var success = HistoryExpansion.TryExpand("echo ^a1", history, out _, out var error);

        success.ShouldBeFalse();
        error.ShouldBe("history expansion: argument 1 not found");
    }

    [Test]
    public void DesignatorWithoutHistory_ReturnsAnError()
    {
        var history = new HistoryManager(persist: false);

        var success = HistoryExpansion.TryExpand("echo ^l", history, out _, out var error);

        success.ShouldBeFalse();
        error.ShouldBe("history expansion: no previous command");
    }

    private static HistoryManager HistoryWith(string command)
    {
        var history = new HistoryManager(persist: false);
        history.Record(command);
        return history;
    }
}
