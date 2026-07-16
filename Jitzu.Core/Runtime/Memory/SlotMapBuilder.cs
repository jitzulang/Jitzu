using Jitzu.Core.Language;

namespace Jitzu.Core.Runtime.Memory;

public record struct Local(LocalKind LocalKind, int SlotIndex);

public class SlotMapBuilder(SlotMapBuilder? parentBuilder, LocalKind localKind)
{
    private Dictionary<string, int>[] _scopes = new Dictionary<string, int>[32];
    private int _scopeIndex = -1;
    private int _slotIndex;
    private readonly List<UpvalueDescriptor> _upvalues = [];
    private readonly Dictionary<string, int> _upvalueIndexByName = new();

    /// <summary>
    /// Slots in this builder that are captured by inner functions/lambdas.
    /// </summary>
    public HashSet<int> CapturedSlots { get; } = [];

    /// <summary>
    /// The upvalue descriptors for this function/lambda scope.
    /// </summary>
    public UpvalueDescriptor[] Upvalues => _upvalues.ToArray();

    /// <summary>
    /// Push a new lexical scope (block or function body).
    /// Does NOT reset slot index — slots are unique per function.
    /// </summary>
    public Dictionary<string, int> PushScope()
    {
        if (++_scopeIndex >= _scopes.Length)
            Array.Resize(ref _scopes, _scopes.Length * 2);

        return _scopes[_scopeIndex] = new Dictionary<string, int>();
    }

    public void PopScope()
    {
        --_scopeIndex;
    }

    public Local Add()
    {
        var local = new Local(localKind, _slotIndex);
        _slotIndex++;
        return local;
    }

    /// <summary>
    /// Add a new local variable in the current scope.
    /// Always increments slot index — no reuse within a function.
    /// </summary>
    public Local Add(string name)
    {
        var scope = _scopes[_scopeIndex];
        if (scope.TryGetValue(name, out var slot))
            return new Local(localKind, slot);

        var local = new Local(localKind, _slotIndex);
        scope.Add(name, _slotIndex++);
        return local;
    }

    /// <summary>
    /// Adds a name to the root/global slot map, even when called while binding a
    /// function or lambda body. Runtime type objects live in global slots and must
    /// never be allocated as uninitialised function locals.
    /// </summary>
    public Local AddGlobal(string name) => parentBuilder?.AddGlobal(name) ?? Add(name);

    /// <summary>
    /// Try to find a local in the current function or enclosing functions.
    /// </summary>
    public bool TryGetLocal(string name, out Local local)
    {
        // Search current function's scopes
        for (var index = _scopeIndex; index >= 0; index--)
        {
            if (!_scopes[index].TryGetValue(name, out var slot))
                continue;

            local = new Local(localKind, slot);
            return true;
        }

        // Search enclosing functions — capture as upvalue
        if (parentBuilder != null)
        {
            if (parentBuilder.TryGetLocal(name, out var enclosingLocal))
            {
                // Only capture if it's a local or upvalue in the parent (not global)
                if (enclosingLocal.LocalKind is LocalKind.Global)
                {
                    local = enclosingLocal;
                    return true;
                }

                // Deduplicate upvalues by name
                if (_upvalueIndexByName.TryGetValue(name, out var existingIdx))
                {
                    local = new Local(LocalKind.Upvalue, existingIdx);
                    return true;
                }

                var upvalueIndex = _upvalues.Count;

                if (enclosingLocal.LocalKind is LocalKind.Local)
                {
                    // Direct capture from parent's local slot
                    parentBuilder.CapturedSlots.Add(enclosingLocal.SlotIndex);
                    _upvalues.Add(new UpvalueDescriptor(IsLocal: true, enclosingLocal.SlotIndex, name));
                }
                else
                {
                    // Transitive capture — parent already has it as an upvalue
                    _upvalues.Add(new UpvalueDescriptor(IsLocal: false, enclosingLocal.SlotIndex, name));
                }

                _upvalueIndexByName[name] = upvalueIndex;
                local = new Local(LocalKind.Upvalue, upvalueIndex);
                return true;
            }
        }

        local = default;
        return false;
    }

    /// <summary>
    /// Get the total number of locals allocated in this function.
    /// </summary>
    public int LocalCount => _slotIndex;
}
