using Jitzu.Shell.UI;
using Shouldly;

namespace Jitzu.Tests;

public class TerminalIntegrationTests
{
    [Test]
    public void WriteTitle_WhenOutputRedirected_WritesNothing()
    {
        using var writer = new StringWriter();

        TerminalIntegration.WriteTitle(writer, "jz", isOutputRedirected: true);

        writer.ToString().ShouldBe("");
    }

    [Test]
    public void FormatTitleSequence_UsesOsc2TitleFormat()
    {
        TerminalIntegration.FormatTitleSequence("repo/src").ShouldBe("\e]2;repo/src\e\\");
    }

    [Test]
    public void FormatTitleSequence_RemovesControlCharacters()
    {
        TerminalIntegration.FormatTitleSequence("repo\e]6;1;bg;red\a\nsrc")
            .ShouldBe("\e]2;repo]6;1;bg;redsrc\e\\");
    }
}
