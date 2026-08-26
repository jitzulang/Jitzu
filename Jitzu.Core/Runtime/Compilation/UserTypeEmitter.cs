using System.Reflection;
using System.Reflection.Emit;
using Jitzu.Core.Language;

namespace Jitzu.Core.Runtime.Compilation;

public static class UserTypeEmitter
{
    public static void RegisterUserTypes(
        RuntimeProgram program,
        IEnumerable<TypeDefinitionExpression> typeDefs,
        string sourceFilePath = "",
        string projectRoot = "")
    {
        // Derive namespace from file path if provided
        var namespacePrefix = DeriveNamespaceFromFilePath(sourceFilePath, projectRoot);
        var typeDefList = typeDefs.ToList();

        // PHASE 1: Build a set of user type names for reference resolution
        var userTypeNames = new HashSet<string>();
        var nodeToFullName = new Dictionary<TypeDefinitionExpression, string>();
        foreach (var node in typeDefList)
        {
            var simpleName = node.Identifier.Name;
            var fullName = string.IsNullOrEmpty(namespacePrefix) ? simpleName : $"{namespacePrefix}.{simpleName}";
            nodeToFullName[node] = fullName;
            userTypeNames.Add(fullName);
        }

        // PHASE 2: Create descriptors with field type names (not CLR types yet)
        // We'll resolve to actual CLR types after creating them
        var descriptors = new List<(UserTypeDescriptor descriptor, List<(string fieldName, Expression fieldTypeExpr)> fieldTypeExprs)>();
        foreach (var node in typeDefList)
        {
            var fullName = nodeToFullName[node];
            var simpleName = node.Identifier.Name;
            var fieldTypeExprs = new List<(string, Expression)>();

            foreach (var f in node.Fields)
            {
                fieldTypeExprs.Add((f.Identifier.Name, f.Type));
            }

            var descriptor = new UserTypeDescriptor
            {
                Name = simpleName,
                FullName = fullName,
                Fields = Array.Empty<UserFieldDescriptor>(), // Will be filled after types are created
            };

            node.Descriptor = descriptor;
            descriptors.Add((descriptor, fieldTypeExprs));
        }

        // PHASE 3: Create TypeBuilders first (before resolving field types)
        var factory = new DynamicTypeFactory("UserTypes.Dynamic", namespacePrefix);
        var descriptorList = descriptors.Select(d => d.descriptor).ToList();
        factory.ReserveTypes(descriptorList);

        // PHASE 4: Resolve field types using an extended type dictionary that includes TypeBuilders
        // This allows user-defined types to reference each other
        var extendedTypeDict = new Dictionary<string, Type>(program.Types);

        // Add TypeBuilders for all user types being defined
        var typeBuilderMap = factory.GetTypeBuilderMap();
        foreach (var (fullName, typeBuilder) in typeBuilderMap)
        {
            extendedTypeDict[fullName] = typeBuilder;
        }

        // Now resolve field types and create updated descriptors
        var updatedDescriptors = new List<UserTypeDescriptor>();
        foreach (var (descriptor, fieldTypeExprs) in descriptors)
        {
            var resolvedFields = new List<UserFieldDescriptor>();

            foreach (var (fieldName, fieldTypeExpr) in fieldTypeExprs)
            {
                // Resolve the type - it could be a built-in, a user type (TypeBuilder), or a qualified name
                var resolvedType = ResolveType(fieldTypeExpr, extendedTypeDict);

                resolvedFields.Add(
                    new UserFieldDescriptor
                    {
                        Name = fieldName,
                        ClrType = resolvedType,
                        IsPublic = true, // TODO: get from AST
                        IsMutable = false, // TODO: get from AST
                        DefaultValue = null
                    });
            }

            var newDescriptor = new UserTypeDescriptor
            {
                Name = descriptor.Name,
                FullName = descriptor.FullName,
                Fields = resolvedFields.ToArray(),
            };

            updatedDescriptors.Add(newDescriptor);
        }

        // Now define members with resolved field types (can use TypeBuilders directly)
        foreach (var d in updatedDescriptors)
        {
            factory.DefineMembers(d);
        }

        var created = factory.CreateAll();

        // PHASE 5: Register actual created types in program
        foreach (var (fullName, type) in created)
        {
            // Keep the program's incremental type indexes in sync. A user type can
            // intentionally replace an existing simple registration (for example,
            // defining `Path`), so this is a replace operation rather than TryAdd.
            program.RegisterType(fullName, type);
            if (!string.IsNullOrEmpty(sourceFilePath))
                program.FileNamespaces[sourceFilePath] = namespacePrefix;
        }
    }

    private static string DeriveNamespaceFromFilePath(string filePath, string projectRoot)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(projectRoot))
            return "";

        var relativePath = Path.GetRelativePath(projectRoot, filePath);
        var directory = Path.GetDirectoryName(relativePath) ?? "";

        if (string.IsNullOrEmpty(directory) || directory == ".")
            return ""; // Root namespace

        return directory.Replace(Path.DirectorySeparatorChar, '.')
                       .Replace(Path.AltDirectorySeparatorChar, '.');
    }

    private static Type ResolveType(Expression typeExpr, Dictionary<string, Type> knownTypes)
    {
        // Handles:
        // - Simple identifiers: "Int", "String", "MyType"
        // - Qualified names: "System.Collections.List"
        // - Generic: "Option<Int>"
        // - Arrays: "String[]"

        // Extract the name from expression (could be simple identifier or qualified name)
        var name = ExtractTypeNameFromExpression(typeExpr);

        // First try direct lookup (handles both simple names and fully qualified names if already registered)
        if (knownTypes.TryGetValue(name, out var t))
            return t;

        // Handle simple array suffix
        if (name.EndsWith("[]", StringComparison.Ordinal))
        {
            var elemName = name[..^2];
            if (!knownTypes.TryGetValue(elemName, out var elemType))
                throw new Exception($"Unknown element type: {elemName}");
            return elemType.MakeArrayType();
        }

        // Handle generic like Result<Int, String> etc. if your AST provides parts
        // Parse and resolve type arguments, then construct: genericType.MakeGenericType(args)
        throw new Exception($"Unknown type identifier: {name}");
    }

    private static string ExtractTypeNameFromExpression(Expression expr)
    {
        return expr switch
        {
            IdentifierLiteral id => id.Name,
            SimpleMemberAccessExpression sma => FlattenMemberAccessToName(sma),
            _ => throw new Exception($"Invalid type expression: {expr.GetType().Name}")
        };
    }

    private static string FlattenMemberAccessToName(SimpleMemberAccessExpression expr)
    {
        var parts = new List<string>();
        var current = (Expression)expr;

        while (current is SimpleMemberAccessExpression memberAccess)
        {
            if (memberAccess.Property is IdentifierLiteral prop)
            {
                parts.Insert(0, prop.Name);
                current = memberAccess.Object;
            }
            else
            {
                throw new Exception($"Invalid qualified name");
            }
        }

        if (current is IdentifierLiteral id)
        {
            parts.Insert(0, id.Name);
            return string.Join(".", parts);
        }

        throw new Exception($"Invalid qualified name");
    }
}

public sealed class DynamicTypeFactory
{
    private readonly ModuleBuilder _moduleBuilder;
    private readonly string _namespacePrefix;
    private readonly Dictionary<string, (UserTypeDescriptor, TypeBuilder)> _builders = new();
    private readonly Dictionary<string, Type> _created = new();
    private const MethodAttributes GetSetAttr = MethodAttributes.Public
                                                | MethodAttributes.HideBySig
                                                | MethodAttributes.SpecialName;

    public DynamicTypeFactory(string assemblyName = "UserTypes.Dynamic", string namespacePrefix = "")
    {
        _namespacePrefix = namespacePrefix;
        var an = new AssemblyName(assemblyName);
        var ab = AssemblyBuilder.DefineDynamicAssembly(an, AssemblyBuilderAccess.Run);
        _moduleBuilder = ab.DefineDynamicModule(assemblyName);
    }

    public void ReserveTypes(IEnumerable<UserTypeDescriptor> types)
    {
        foreach (var td in types)
        {
            if (_builders.ContainsKey(td.FullName))
                continue;

            // Build the full type name including namespace
            var fullTypeName = string.IsNullOrEmpty(_namespacePrefix)
                ? td.Name
                : $"{_namespacePrefix}.{td.Name}";

            var tb = _moduleBuilder.DefineType(
                fullTypeName,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
            _builders[td.FullName] = (td, tb);
        }
    }

    /// <summary>
    /// Gets a dictionary mapping full type names to their TypeBuilders.
    /// Useful for resolving references to types being defined.
    /// </summary>
    public IReadOnlyDictionary<string, Type> GetTypeBuilderMap()
    {
        var result = new Dictionary<string, Type>();
        foreach (var (fullName, (_, typeBuilder)) in _builders)
        {
            result[fullName] = typeBuilder;
        }
        return result;
    }

    public void DefineMembers(UserTypeDescriptor td)
    {
        var tb = _builders[td.FullName].Item2;
        // Parameterless ctor (for JSON and default values flow)
        tb.DefineDefaultConstructor(MethodAttributes.Public);

        foreach (var f in td.Fields)
        {
            DefineAutoProperty(tb, f.Name, f.ClrType);
        }
    }

    private static void DefineAutoProperty(TypeBuilder tb, string name, Type type)
    {
        var field = tb.DefineField($"<{name}>k__BackingField", type, FieldAttributes.Private);
        var prop = tb.DefineProperty(name, PropertyAttributes.None, type, null);

        var getter = tb.DefineMethod($"get_{name}",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            type, Type.EmptyTypes);
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        prop.SetGetMethod(getter);

        var setter = tb.DefineMethod($"set_{name}",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            null, [type]);
        il = setter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        prop.SetSetMethod(setter);
    }

    public IReadOnlyDictionary<string, Type> CreateAll()
    {
        foreach (var (fullName, (td, tb)) in _builders)
        {
            if (!_created.ContainsKey(fullName))
                _created[fullName] = td.CreatedType = tb.CreateType();
        }
        return _created;
    }
}
