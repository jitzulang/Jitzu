namespace Jitzu.Shell;

public class AliasManager
{
    private readonly string _aliasFile;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _persist;
    private int _saveDeferralDepth;
    private bool _savePending;
    private readonly Infrastructure.PersistentFileGuard _fileGuard;

    public IReadOnlyDictionary<string, string> Aliases => _aliases;
    public string? PersistenceWarning => _fileGuard.DegradedReason;

    public AliasManager(bool persist = true) : this(persist, persist
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jitzu", "aliases.txt")
        : "")
    {
    }

    internal AliasManager(bool persist, string aliasFile, Action<string>? beforeAtomicReplace = null,
        Action<string>? afterAtomicReplace = null, Action<string>? afterSuccessfulCommit = null,
        Func<string, ReadOnlyMemory<byte>, Task>? temporaryWriter = null)
    {
        _persist = persist;
        _aliasFile = aliasFile;
        _fileGuard = new Infrastructure.PersistentFileGuard(
            aliasFile, persist, beforeAtomicReplace, afterAtomicReplace, afterSuccessfulCommit, temporaryWriter);
    }

    public void Initialise()
    {
        if (!_persist)
        {
            Infrastructure.Logging.StartupProfiler.Mark("aliases-loaded");
            return;
        }

        // This file is small and required before config execution. Avoid initializing
        // asynchronous file I/O infrastructure only to await it immediately at startup.
        string[] lines;
        try
        {
            lines = Infrastructure.StartupFileReader.ReadAllLines(
                _aliasFile, Infrastructure.StartupFileReader.AliasMaxBytes);
        }
        catch (FileNotFoundException)
        {
            Infrastructure.Logging.StartupProfiler.Mark("aliases-loaded");
            return;
        }
        catch (DirectoryNotFoundException)
        {
            Infrastructure.Logging.StartupProfiler.Mark("aliases-loaded");
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            _fileGuard.Degrade(ex.Message);
            Infrastructure.Logging.StartupProfiler.Mark("aliases-loaded");
            return;
        }
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
        Infrastructure.Logging.StartupProfiler.Mark("aliases-loaded");
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

        var content = string.Concat(lines.Select(line => line + Environment.NewLine));
        await _fileGuard.ReplaceAtomicallyAsync(System.Text.Encoding.UTF8.GetBytes(content));
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
