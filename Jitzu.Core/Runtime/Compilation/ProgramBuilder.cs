using System.Reflection;
using System.Runtime.Loader;
using Jitzu.Core.Language;
using Jitzu.Core.Runtime.Extensions;
using Jitzu.Core.Runtime.Memory;
using Jitzu.Core.Types;
using NuGet.Frameworks;
using NuGet.Versioning;
using None = Jitzu.Core.Types.None;

namespace Jitzu.Core.Runtime.Compilation;

public static class ProgramBuilder
{
    public static readonly PackageResolver Resolver = new();
    public static readonly NuGetFramework Framework = NuGetFramework.Parse("net8.0");

    public static readonly Dictionary<string, Type> BaseTypes = new()
    {
        ["Int"] = typeof(int),
        ["String"] = typeof(string),
        ["Bool"] = typeof(bool),
        ["Double"] = typeof(double),
        ["Char"] = typeof(char),
        ["Date"] = typeof(DateOnly),
        ["Time"] = typeof(TimeOnly),
        ["DateTime"] = typeof(DateTime),
        ["Result"] = typeof(Result<,>),
        ["Ok"] = typeof(Ok<>),
        ["Err"] = typeof(Err<>),
        ["Option"] = typeof(Option<>),
        ["Some"] = typeof(Some<>),
        ["None"] = typeof(None),
        ["File"] = typeof(File),
        ["Path"] = typeof(Path),
    };

    // BCL types that callers commonly want available without an explicit #:package
    // declaration. Touching the type forces its containing assembly to load before we
    // walk AppDomain.CurrentDomain.GetAssemblies(), so the public types from System.IO,
    // System.Console, System.Diagnostics.Process, etc. all end up registered.
    private static readonly Type[] BclSeedTypes =
    [
        typeof(Environment),
        typeof(Console),
        typeof(Directory),
        typeof(FileInfo),
        typeof(DirectoryInfo),
        typeof(FileAttributes),
        typeof(System.Diagnostics.Process),
        typeof(System.Diagnostics.ProcessStartInfo),
        typeof(System.Text.Encoding),
        typeof(System.Text.StringBuilder),
        typeof(System.Text.RegularExpressions.Regex),
        typeof(System.Collections.Generic.List<>),
        typeof(System.Collections.Generic.Dictionary<,>),
        typeof(Uri),
        typeof(Convert),
        typeof(Math),
        typeof(Guid),
        typeof(TimeSpan),
    ];

    private static void RegisterBclTypes(Dictionary<string, Type> types)
    {
        // Force-load assemblies that hold the seed types, then walk every loaded
        // System.* assembly and register their public, non-nested types.
        var seedAssemblies = BclSeedTypes.Select(t => t.Assembly).Distinct().ToHashSet();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name is null) continue;
            if (!seedAssemblies.Contains(asm) && !name.StartsWith("System.", StringComparison.Ordinal))
                continue;

            Type[] exported;
            try { exported = asm.GetExportedTypes(); }
            catch { continue; }

            foreach (var type in exported)
            {
                if (type.IsNested || type.FullName is null)
                    continue;
                types.TryAdd(type.FullName, type);
            }
        }
    }

    public static readonly Dictionary<string, IShellFunction> BuiltInFunctions = new()
    {
        ["print"] = new ForeignFunction(GlobalFunctions.PrintStatic),
        ["rand"] = new ForeignFunction(GlobalFunctions.RandStatic),
        ["first"] = new ForeignFunction(GlobalFunctions.FirstStatic),
        ["last"] = new ForeignFunction(GlobalFunctions.LastStatic),
        ["nth"] = new ForeignFunction(GlobalFunctions.NthStatic),
        ["grep"] = new ForeignFunction(GlobalFunctions.GrepStatic),
        ["run"] = new ForeignFunction(GlobalFunctions.RunStatic),
    };

    public static async Task<RuntimeProgram> Build(ScriptExpression ast)
    {
        var slotBuilder = new SlotMapBuilder(null, LocalKind.Global);
        var globalSlotMap = slotBuilder.PushScope();
        slotBuilder.Add("args");

        var types = BaseTypes.ToDictionary();
        RegisterBclTypes(types);

        var program = new RuntimeProgram
        {
            Types = types,
            SimpleTypeCache = new Dictionary<string, Type>(),
            TypeNameConflicts = new Dictionary<string, HashSet<string>>(),
            FileNamespaces = new Dictionary<string, string>(),
            Globals = new Dictionary<string, Type>
            {
                ["args"] = typeof(string[]),
            },
            SlotBuilder = slotBuilder,
            GlobalSlotMap = globalSlotMap,
            GlobalFunctions = BuiltInFunctions.ToDictionary(),
            MethodTable = new MethodTable
            {
                [typeof(DateOnly)] = new Dictionary<string, IShellFunction>
                {
                    ["today"] = new ForeignFunction(DateOnlyExtensions.Today)
                }
            },
        };

        foreach (var function in program.GlobalFunctions)
            slotBuilder.Add(function.Key);

        // Build the type indexes once for the initial BCL/base type universe. REPL
        // patches update this registry incrementally rather than rebuilding these
        // dictionaries from every known type.
        program.EnsureTypeRegistry();

        return await PatchProgram(program, ast);
    }

    public static async Task<RuntimeProgram> PatchProgram(RuntimeProgram program, ScriptExpression ast)
    {
        var typeRegistry = program.EnsureTypeRegistry();

        foreach (var expression in ast.Body.OfType<TagExpression>())
        {
            // No version → assume the namespace is in an already-loaded BCL assembly
            // (e.g. `#:package System.Diagnostics.Process`). Skip NuGet resolution.
            if (string.IsNullOrEmpty(expression.Version))
                continue;

            var paths = await Resolver.ResolveAsync(
                expression.Identifier,
                new NuGetVersion(expression.Version),
                Framework);

            foreach (var path in paths)
            {
                var assembly = LoadAssemblySafe(path);
                if (assembly == null)
                    continue;

                program.LoadedAssemblies.Add(assembly);

                foreach (var type in assembly.ExportedTypes)
                    typeRegistry.TryRegisterType(type.FullName ?? type.Name, type);
            }
        }

        AllocateSlots(ast, program.SlotBuilder);

        UserTypeEmitter.RegisterUserTypes(program, ast.Body.OfType<TypeDefinitionExpression>());

        var transformer = new AstTransformer(program);
        transformer.TransformScriptExpression(ast, program.SlotBuilder);

        foreach (var node in ast.Body.OfType<TypeDefinitionExpression>())
        {
            var type = node.Descriptor?.CreatedType ?? typeof(void);
            if (!program.MethodTable.TryGetValue(type, out var methodTable))
            {
                methodTable = [];
                program.MethodTable.Add(type, methodTable);
            }

            foreach (var method in node.Methods)
            {
                var funcDef = method.FunctionDefinition;
                methodTable[funcDef.Identifier.Name] = CreateUserFunction(funcDef, program.SlotBuilder, transformer, type);
            }
        }

        foreach (var node in ast.Body.OfType<FunctionDefinitionExpression>())
            program.GlobalFunctions.Add(node.Identifier.Name, CreateUserFunction(node, program.SlotBuilder, transformer));

        return program;
    }

    private static void AllocateSlots(ScriptExpression ast, SlotMapBuilder slotBuilder)
    {
        foreach (var node in ast.Body)
        {
            switch (node)
            {
                case FunctionDefinitionExpression funcDef:
                    slotBuilder.Add(funcDef.Identifier.Name);
                    break;
            }
        }
    }

    private static UserFunction CreateUserFunction(
        FunctionDefinitionExpression funcDef,
        SlotMapBuilder globalSlotBuilder,
        AstTransformer transformer,
        Type? parentType = null)
    {
        var (slotMap, _) = transformer.TransformFunctionBody(funcDef, globalSlotBuilder);
        return new UserFunction(funcDef.Identifier.Name, null!) // placeholder, no bytecode yet
        {
            ParentType = parentType,
            LocalCount = slotMap.Values.Count
        };
    }

    private static Assembly? LoadAssemblySafe(string path)
    {
        var assemblyName = AssemblyName.GetAssemblyName(path);

        // Check if already loaded
        var existing = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a =>
            {
                try
                {
                    return AssemblyName.ReferenceMatchesDefinition(a.GetName(), assemblyName);
                }
                catch
                {
                    return false;
                }
            });

        if (existing != null)
            return existing;

        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        catch (FileLoadException)
        {
            // Already loaded with different path, try to find it
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
        }
    }
}
