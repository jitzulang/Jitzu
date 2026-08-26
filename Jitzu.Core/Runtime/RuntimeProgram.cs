using System.Reflection;
using Jitzu.Core.Runtime.Memory;

namespace Jitzu.Core.Runtime;

public record RuntimeProgram
{
    private Dictionary<string, Type> _types = null!;
    private Dictionary<string, Type> _simpleTypeCache = null!;
    private Dictionary<string, HashSet<string>> _typeNameConflicts = null!;

    public required Dictionary<string, Type> Types
    {
        get => _types;
        set
        {
            _types = value;
            TypeRegistry = null;
        }
    }

    // Type resolution caches for namespace support
    public required Dictionary<string, Type> SimpleTypeCache
    {
        get => _simpleTypeCache;
        set
        {
            _simpleTypeCache = value;
            TypeRegistry = null;
        }
    }

    public required Dictionary<string, HashSet<string>> TypeNameConflicts
    {
        get => _typeNameConflicts;
        set
        {
            _typeNameConflicts = value;
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
    // the runtime pipeline; the dictionaries above remain public for compatibility.
    internal TypeRegistry? TypeRegistry { get; set; }

    internal TypeRegistry EnsureTypeRegistry()
    {
        return TypeRegistry ??= new TypeRegistry(Types, SimpleTypeCache, TypeNameConflicts);
    }

    internal void RegisterType(string fullQualifiedName, Type type)
    {
        EnsureTypeRegistry().RegisterType(fullQualifiedName, type);
    }

    internal bool TryRegisterType(string fullQualifiedName, Type type)
    {
        return EnsureTypeRegistry().TryRegisterType(fullQualifiedName, type);
    }
}
