using System.Text;
using Jitzu.Shell;
using Shouldly;

namespace Jitzu.Tests;

public class ThemeConfigTests
{
    [Test]
    public void Utf8_Overrides_Are_Flattened_And_Applied()
    {
        var colours = new Dictionary<string, string>();
        var json = Encoding.UTF8.GetBytes("""
            {
              "prompt": { "arrow": "#010203" },
              "selection": { "selected": { "bg": "#a0b0c0" } }
            }
            """);

        ThemeConfig.ApplyUserOverrides(json, colours);

        colours["prompt.arrow"].ShouldBe("\e[38;2;1;2;3m");
        colours["selection.selected.bg"].ShouldBe("\e[48;2;160;176;192m");
    }

    [Test]
    public void Invalid_Colour_Values_Are_Ignored()
    {
        var colours = new Dictionary<string, string>
        {
            ["prompt.arrow"] = "existing"
        };
        var json = """{ "prompt": { "arrow": "red", "error": 42 } }"""u8;

        ThemeConfig.ApplyUserOverrides(json, colours);

        colours["prompt.arrow"].ShouldBe("existing");
        colours.ShouldNotContainKey("prompt.error");
    }
}
