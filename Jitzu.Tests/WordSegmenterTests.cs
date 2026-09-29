using Jitzu.Shell.UI;
using Shouldly;

namespace Jitzu.Tests;

public class WordSegmenterTests
{
    private readonly WordSegmenter _segmenter = new();

    [Test]
    [Arguments("|", "|")]
    [Arguments("foo|", "|")]
    [Arguments("foo bar|", "foo |")]
    [Arguments("foo   |", "|")]
    [Arguments("my-file.txt|", "|")]
    [Arguments("git:per|sonal", "git:|sonal")]
    [Arguments("|git:personal", "|git:personal")]
    public void PreviousBoundary_SingleStep(string input, string expected)
    {
        Backspace(input).ShouldBe(expected);
    }

    [Test]
    [Arguments("git:personal|", "git:|;|")]
    [Arguments("simon@computer|", "simon@|;|")]
    [Arguments("ssh simon@host|", "ssh simon@|;ssh |;|")]
    [Arguments("simon@db.example.com|", "simon@db.example.|;simon@db.|;simon@|;|")]
    [Arguments("git@github.com:user/repo.git|", "git@github.com:user/|;git@github.com:|;git@github.|;git@|;|")]
    [Arguments("C:\\Users\\simon\\|", "C:\\Users\\|;C:\\|;|")]
    [Arguments("C:\\Users\\simon|", "C:\\Users\\|;C:\\|;|")]
    [Arguments("/usr/local/bin|", "/usr/local/|;/usr/|;/|;|")]
    [Arguments("\"hello world|", "\"hello |;\"|;|")]
    [Arguments("--conn=postgres://h|", "--conn=postgres://|;--conn=|;|")]
    [Arguments("--name=\"x|", "--name=\"|;|")]
    public void PreviousBoundary_RepeatedPresses(string input, string expectedSteps)
    {
        BackspaceUntilEmpty(input).ShouldBe(expectedSteps);
    }

    [Test]
    [Arguments(
        "postgres://postgres:postgres@localhost:5432/database|",
        "postgres://postgres:postgres@localhost:5432/|;postgres://postgres:postgres@localhost:|;postgres://postgres:postgres@|;postgres://postgres:|;postgres://|;|")]
    [Arguments(
        "postgres://postgres:pos|",
        "postgres://postgres:|;postgres://|;|")]
    [Arguments(
        "https://db.example.com/path|",
        "https://db.example.com/|;https://db.example.|;https://db.|;https://|;|")]
    [Arguments(
        "postgres://user@host1:5432,host2:5432|",
        "postgres://user@host1:5432,host2:|;postgres://user@host1:5432,|;postgres://user@host1:|;postgres://user@|;postgres://|;|")]
    public void PreviousBoundary_ConnectionStrings(string input, string expectedSteps)
    {
        BackspaceUntilEmpty(input).ShouldBe(expectedSteps);
    }

    [Test]
    [Arguments("|git:personal", "git:|personal")]
    [Arguments("git:|personal", "git:personal|")]
    [Arguments("gi|t:personal", "git:|personal")]
    [Arguments("|foo   bar", "foo   |bar")]
    [Arguments("foo bar|", "foo bar|")]
    [Arguments("|foo", "foo|")]
    [Arguments("|postgres://user:pw@host", "postgres://|user:pw@host")]
    [Arguments("postgres://|user:pw@host", "postgres://user:|pw@host")]
    [Arguments("|foo: bar", "foo: |bar")]
    public void NextBoundary_SingleStep(string input, string expected)
    {
        Forward(input).ShouldBe(expected);
    }

    private string Backspace(string input)
    {
        var (text, cursor) = Parse(input);
        var boundary = _segmenter.PreviousBoundary(text, cursor);
        return Render(text.Remove(boundary, cursor - boundary), boundary);
    }

    private string BackspaceUntilEmpty(string input)
    {
        var steps = new List<string>();
        var current = input;
        while (current != "|")
        {
            current = Backspace(current);
            steps.Add(current);
        }

        return string.Join(';', steps);
    }

    private string Forward(string input)
    {
        var (text, cursor) = Parse(input);
        return Render(text, _segmenter.NextBoundary(text, cursor));
    }

    private static (string Text, int Cursor) Parse(string input)
    {
        var cursor = input.IndexOf('|');
        return (input.Remove(cursor, 1), cursor);
    }

    private static string Render(string text, int cursor) => text.Insert(cursor, "|");
}
