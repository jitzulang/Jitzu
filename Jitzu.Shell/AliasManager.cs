namespace Jitzu.Shell;

public class AliasManager
{
    private readonly string _aliasFile;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _persist;
    private int _saveDeferralDepth;
    private bool _savePending;

    public IReadOnlyDictionary<string, string> Aliases => _aliases;

    public AliasManager(bool persist = true) : this(
        persist,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jitzu", "aliases.txt"))
    {
    }

    internal AliasManager(bool persist, string aliasFile)
    {
        _persist = persist;
        if (_persist)
            Directory.CreateDirectory(Path.GetDirectoryName(aliasFile)!);
        _aliasFile = aliasFile;
    }

    public async Task InitialiseAsync()
    {
        if (!_persist || !File.Exists(_aliasFile))
            return;

        var lines = await File.ReadAllLinesAsync(_aliasFile);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var eqIndex = line.IndexOf('=');
            if (eqIndex <= 0)
                continue;

            var name = line[..eqIndex].Trim();
            var value = line[(eqIndex + 1)..].Trim();
            _aliases[name] = value;
        }
    }

    public bool Set(string name, string value)
    {
        if (_aliases.TryGetValue(name, out var existing) && existing == value)
            return false;

        _aliases[name] = value;
        return true;
    }

    public bool Remove(string name)
    {
        return _aliases.Remove(name);
    }

    public bool TryExpand(string firstWord, out string expanded)
    {
        return _aliases.TryGetValue(firstWord, out expanded!);
    }

    public async Task SaveAsync()
    {
        if (!_persist)
            return;

        if (_saveDeferralDepth > 0)
        {
            _savePending = true;
            return;
        }

        var lines = new List<string>(_aliases.Count);
        foreach (var (name, value) in _aliases.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            lines.Add($"{name}={value}");

        await File.WriteAllLinesAsync(_aliasFile, lines);
    }

    public void BeginSaveBatch() => _saveDeferralDepth++;

    public async Task EndSaveBatchAsync()
    {
        if (_saveDeferralDepth == 0)
            return;

        _saveDeferralDepth--;
        if (_saveDeferralDepth == 0 && _savePending)
        {
            _savePending = false;
            await SaveAsync();
        }
    }
}
