namespace Jitzu.Core.Runtime;

/// <summary>
/// Manages type registration and resolution with full namespace support.
/// Supports both simple names (for backward compatibility) and qualified names.
/// </summary>
internal class TypeRegistry
{
    private readonly Dictionary<string, Type> _types;
    private readonly Dictionary<string, Type> _simpleTypeCache;
    private readonly Dictionary<string, HashSet<string>> _typeNameConflicts;
    private readonly Dictionary<string, HashSet<string>> _simpleNameToFullNames;
    private readonly Dictionary<string, Type> _indexedTypes;

    public IReadOnlyDictionary<string, Type> Types => _types.AsReadOnly();
    public IReadOnlyDictionary<string, Type> SimpleTypeCache => _simpleTypeCache.AsReadOnly();
    public IReadOnlyDictionary<string, HashSet<string>> TypeNameConflicts => _typeNameConflicts.AsReadOnly();

    public TypeRegistry()
    {
        _types = new Dictionary<string, Type>(StringComparer.Ordinal);
        _simpleTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        _typeNameConflicts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        _simpleNameToFullNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        _indexedTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Creates a registry over the dictionaries owned by a <see cref="RuntimeProgram"/>.
    /// The dictionaries remain mutable for compatibility, but registrations should go
    /// through this registry so the simple-name indexes can be updated incrementally.
    /// </summary>
    public TypeRegistry(
        Dictionary<string, Type> types,
        Dictionary<string, Type> simpleTypeCache,
        Dictionary<string, HashSet<string>> typeNameConflicts)
    {
        _types = types;
        _simpleTypeCache = simpleTypeCache;
        _typeNameConflicts = typeNameConflicts;
        _simpleNameToFullNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        _indexedTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        BuildCaches();
    }

    /// <summary>
    /// Registers or replaces a type with its full qualified name.
    ///
    /// Only the simple-name bucket affected by this registration is recomputed. This
    /// matters for the REPL, where the BCL type universe is stable across expressions.
    /// </summary>
    public void RegisterType(string fullQualifiedName, Type type)
    {
        if (_indexedTypes.TryGetValue(fullQualifiedName, out var previous)
            && ReferenceEquals(previous, type))
            return;

        var simpleName = ExtractSimpleName(fullQualifiedName);
        if (_indexedTypes.ContainsKey(fullQualifiedName))
            RemoveFullName(simpleName, fullQualifiedName);

        _types[fullQualifiedName] = type;
        _indexedTypes[fullQualifiedName] = type;
        AddFullName(simpleName, fullQualifiedName);
        RebuildSimpleName(simpleName);
    }

    /// <summary>
    /// Registers a type only when its full name is not already present.
    /// Returns <see langword="true"/> when the type was added.
    /// </summary>
    public bool TryRegisterType(string fullQualifiedName, Type type)
    {
        if (_types.ContainsKey(fullQualifiedName))
            return false;

        RegisterType(fullQualifiedName, type);
        return true;
    }

    /// <summary>
    /// Builds the simple type cache and conflict tracking after all types are registered.
    /// RuntimeProgram uses this during initialization; later registrations update only
    /// their affected simple-name bucket through <see cref="RegisterType"/>.
    /// </summary>
    public void BuildCaches()
    {
        _simpleTypeCache.Clear();
        _typeNameConflicts.Clear();
        _simpleNameToFullNames.Clear();
        _indexedTypes.Clear();

        // Build a map of simple names to full qualified names
        foreach (var (fullName, type) in _types)
        {
            _indexedTypes[fullName] = type;
            AddFullName(ExtractSimpleName(fullName), fullName);
        }

        // Populate cache and conflicts
        foreach (var simpleName in _simpleNameToFullNames.Keys)
            RebuildSimpleName(simpleName);
    }

    /// <summary>
    /// Picks up direct mutations to the public type dictionary without doing any
    /// work for the normal no-op REPL patch. Internal registrations already update
    /// the index and therefore take the fast path here.
    /// </summary>
    public void Synchronize()
    {
        if (_indexedTypes.Count == _types.Count)
            return;

        foreach (var (fullName, type) in _types)
        {
            if (!_indexedTypes.TryGetValue(fullName, out var indexedType)
                || !ReferenceEquals(indexedType, type))
                RegisterType(fullName, type);
        }

        foreach (var fullName in _indexedTypes.Keys.Where(name => !_types.ContainsKey(name)).ToArray())
        {
            var simpleName = ExtractSimpleName(fullName);
            RemoveFullName(simpleName, fullName);
            _indexedTypes.Remove(fullName);
            RebuildSimpleName(simpleName);
        }
    }

    private void AddFullName(string simpleName, string fullName)
    {
        if (!_simpleNameToFullNames.TryGetValue(simpleName, out var fullNames))
        {
            fullNames = new HashSet<string>(StringComparer.Ordinal);
            _simpleNameToFullNames[simpleName] = fullNames;
        }

        fullNames.Add(fullName);
    }

    private void RemoveFullName(string simpleName, string fullName)
    {
        if (!_simpleNameToFullNames.TryGetValue(simpleName, out var fullNames)
            || !fullNames.Remove(fullName))
            return;

        if (fullNames.Count == 0)
            _simpleNameToFullNames.Remove(simpleName);
    }

    private void RebuildSimpleName(string simpleName)
    {
        _simpleTypeCache.Remove(simpleName);
        _typeNameConflicts.Remove(simpleName);

        if (!_simpleNameToFullNames.TryGetValue(simpleName, out var fullNames))
            return;

        // Distinct full names can refer to the same CLR type (for example, the
        // BaseTypes alias "Path" and System.IO.Path). Those aliases are not a
        // conflict and should resolve to the shared type.
        Type? uniqueType = null;
        foreach (var fullName in fullNames)
        {
            var type = _types[fullName];
            if (uniqueType is null)
            {
                uniqueType = type;
                continue;
            }

            if (!ReferenceEquals(uniqueType, type))
            {
                _typeNameConflicts[simpleName] = new HashSet<string>(fullNames, StringComparer.Ordinal);
                return;
            }
        }

        if (uniqueType is not null)
            _simpleTypeCache[simpleName] = uniqueType;
    }

    /// <summary>
    /// Resolves a simple type name. Returns the type if unambiguous, throws if ambiguous.
    /// </summary>
    public Type? ResolveSimpleName(string simpleName)
    {
        // Fast path: check cache
        if (_simpleTypeCache.TryGetValue(simpleName, out var type))
        {
            return type;
        }

        // Check for conflicts
        if (_typeNameConflicts.TryGetValue(simpleName, out var fullNames))
        {
            var suggestions = string.Join(", ", fullNames.OrderBy(x => x));
            throw new InvalidOperationException(
                $"Type '{simpleName}' is ambiguous. Did you mean: {suggestions}?");
        }

        return null;
    }

    /// <summary>
    /// Resolves a qualified type name.
    /// </summary>
    public Type? ResolveQualifiedName(string qualifiedName)
    {
        return _types.TryGetValue(qualifiedName, out var type) ? type : null;
    }

    /// <summary>
    /// Extracts the simple name from a fully qualified name.
    /// E.g., "System.Text.Json.JsonSerializer" -> "JsonSerializer"
    /// </summary>
    private static string ExtractSimpleName(string fullQualifiedName)
    {
        var lastDot = fullQualifiedName.LastIndexOf('.');
        return lastDot < 0 ? fullQualifiedName : fullQualifiedName[(lastDot + 1)..];
    }

    /// <summary>
    /// Flattens a member access expression chain into a qualified name string.
    /// </summary>
    public static string FlattenMemberAccess(IEnumerable<string> parts)
    {
        return string.Join(".", parts);
    }
}
