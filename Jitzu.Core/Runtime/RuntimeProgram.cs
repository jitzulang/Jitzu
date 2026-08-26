using System.Collections.ObjectModel;
using System.Reflection;
using Jitzu.Core.Runtime.Memory;

namespace Jitzu.Core.Runtime;

public record RuntimeProgram
{
    private Dictionary<string, Type> _types = null!;
    private Dictionary<string, Type> _simpleTypeCache = null!;
    private Dictionary<string, HashSet<string>> _typeNameConflicts = null!;
    private IReadOnlyDictionary<string, Type> _typesView = null!;
    private IReadOnlyDictionary<string, Type> _simpleTypeCacheView = null!;
    private IReadOnlyDictionary<string, HashSet<string>> _typeNameConflictsView = null!;

    public required IReadOnlyDictionary<string, Type> Types
    {
        get => _typesView;
        set
        {
            _types = value.ToDictionary(StringComparer.Ordinal);
            _typesView = new ReadOnlyDictionary<string, Type>(_types);
            TypeRegistry = null;
        }
    }

    // Type resolution caches for namespace support
    public required IReadOnlyDictionary<string, Type> SimpleTypeCache
    {
        get => _simpleTypeCacheView;
        set
        {
            _simpleTypeCache = value.ToDictionary(StringComparer.Ordinal);
            _simpleTypeCacheView = new ReadOnlyDictionary<string, Type>(_simpleTypeCache);
            TypeRegistry = null;
        }
    }

    public required IReadOnlyDictionary<string, HashSet<string>> TypeNameConflicts
    {
        get => _typeNameConflictsView;
        set
        {
            _typeNameConflicts = value.ToDictionary(StringComparer.Ordinal);
            _typeNameConflictsView = new ReadOnlyDictionary<string, HashSet<string>>(_typeNameConflicts);
            TypeRegistry = null;
        }
    }

    public required Dictionary<string, string> FileNamespaces { get; set; }

    public required Dictionary<string, Type> Globals { get; set; }
    public required MethodTable MethodTable { get; init; }
    public required Dictionary<string, IShellFunction> GlobalFunctions { get; init; }
    public required Dictionary<string, int> GlobalSlotMap { get; init; }
    public required SlotMapBuilder SlotBuilder { get; set; }
    public HashSet<Assembly> LoadedAssemblies { get; init; } = [];

    // The registry owns the incremental indexes behind SimpleTypeCache and
    // TypeNameConflicts. It is initialized by ProgramBuilder and kept private to
    // the runtime pipeline; read-only views remain public for compatibility.
    internal TypeRegistry? TypeRegistry { get; set; }

    internal TypeRegistry EnsureTypeRegistry()
    {
        return TypeRegistry ??= new TypeRegistry(_types, _simpleTypeCache, _typeNameConflicts);
    }

    public void RegisterType(string fullQualifiedName, Type type)
    {
        EnsureTypeRegistry().RegisterType(fullQualifiedName, type);
    }

    public bool TryRegisterType(string fullQualifiedName, Type type)
    {
        return EnsureTypeRegistry().TryRegisterType(fullQualifiedName, type);
    }

    public bool RemoveType(string fullQualifiedName)
    {
        return EnsureTypeRegistry().RemoveType(fullQualifiedName);
    }
}
