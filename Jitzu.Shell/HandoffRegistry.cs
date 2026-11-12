namespace Jitzu.Shell;

public sealed record Handoff(string Command, string Variable, string[] Action);

public class HandoffRegistry
{
    private static readonly StringComparer NameComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly List<Handoff> _handoffs = [];

    public IReadOnlyList<Handoff> Handoffs => _handoffs;

    public void Set(Handoff handoff)
    {
        _handoffs.RemoveAll(existing =>
            NameComparer.Equals(existing.Command, handoff.Command)
            && NameComparer.Equals(existing.Variable, handoff.Variable));
        _handoffs.Add(handoff);
    }

    public bool Remove(string command) =>
        _handoffs.RemoveAll(existing => NameComparer.Equals(existing.Command, command)) > 0;

    public Handoff[] For(string executable)
    {
        var fileName = Path.GetFileName(executable);
        var stem = Path.GetFileNameWithoutExtension(executable);
        return _handoffs
            .Where(handoff => NameComparer.Equals(handoff.Command, fileName) || NameComparer.Equals(handoff.Command, stem))
            .ToArray();
    }
}
