using System.Reflection;
using System.Runtime.CompilerServices;
using Jitzu.Core.Language;
using Jitzu.Core.Types;

namespace Jitzu.Core.Runtime.Compilation;

public class SemanticAnalyser(RuntimeProgram program)
{
    public ScriptExpression AnalyseScript(ScriptExpression expression)
    {
        // Phase 1: register headers
        foreach (var stmt in expression.Body)
        {
            switch (stmt)
            {
                case FunctionDefinitionExpression { ReturnType: { } returnType } fdef:
                {
                    fdef.FunctionReturnType = ResolveType(returnType);
                    if (program.GlobalFunctions.TryGetValue(fdef.Identifier.Name, out var f) && f is UserFunction uf)
                        uf.FunctionReturnType = fdef.FunctionReturnType; // null if needs inference

                    break;
                }
            }
        }

        // Second pass, resolve everything else
        for (var i = 0; i < expression.Body.Length; i++)
            expression.Body[i] = AnalyseExpression(expression.Body[i]);

        return expression;
    }

    private Expression AnalyseExpression(Expression expr)
    {
        switch (expr)
        {
            case ObjectInstantiationExpression objectInstantiationExpression:
            {
                objectInstantiationExpression.ObjectType = ResolveType(objectInstantiationExpression.Identifier);
                return objectInstantiationExpression;
            }

            case FunctionDefinitionExpression functionDefinition:
                return AnalyseFunctionDefinition(functionDefinition);

            case AssignmentExpression assignmentExpression:
            {
                assignmentExpression.Left = AnalyseExpression(assignmentExpression.Left);
                assignmentExpression.Right = AnalyseExpression(assignmentExpression.Right);
                return assignmentExpression;
            }

            case BlockBodyExpression blockBody:
            {
                for (var i = 0; i < blockBody.Expressions.Length; i++)
                    blockBody.Expressions[i] = AnalyseExpression(blockBody.Expressions[i]);

                return blockBody;
            }

            case BinaryExpression { Operator.Value: "|" } binOp:
            {
                var left = AnalyseExpression(binOp.Left);

                // Case 1: a | f(b, c) → f(a, b, c)
                if (binOp.Right is FunctionCallExpression existingCall)
                {
                    var newArgs = new Expression[existingCall.Arguments.Length + 1];
                    newArgs[0] = left;
                    Array.Copy(existingCall.Arguments, 0, newArgs, 1, existingCall.Arguments.Length);
                    var pipeCall = new FunctionCallExpression
                    {
                        Identifier = existingCall.Identifier,
                        Arguments = newArgs,
                        Location = binOp.Location
                    };
                    return AnalyseFunctionCall(pipeCall);
                }

                // Case 2: a | f → f(a)  (when f is a known function)
                if (binOp.Right is IIdentifierLiteral { Name: var rightName }
                    && program.GlobalFunctions.ContainsKey(rightName))
                {
                    var pipeCall = new FunctionCallExpression
                    {
                        Identifier = binOp.Right,
                        Arguments = [left],
                        Location = binOp.Location
                    };
                    return AnalyseFunctionCall(pipeCall);
                }

                // Case 3: Not a pipe — bitwise OR
                binOp.Left = left;
                binOp.Right = AnalyseExpression(binOp.Right);
                return binOp;
            }

            case BinaryExpression binOp:
            {
                binOp.Left = AnalyseExpression(binOp.Left);
                binOp.Right = AnalyseExpression(binOp.Right);
                return binOp;
            }

            case UnaryExpression unary:
            {
                unary.Operand = AnalyseExpression(unary.Operand);
                return unary;
            }

            case IIdentifierLiteral identifierLiteral:
                return AnalyseIdentifierLiteral(identifierLiteral);

            case GlobalSetExpression globalSetExpression:
            {
                globalSetExpression.ValueExpression = AnalyseExpression(globalSetExpression.ValueExpression);
                globalSetExpression.SetterType = ResolveType(globalSetExpression.ValueExpression);
                program.Globals[globalSetExpression.Identifier.Name] = globalSetExpression.SetterType;
                return globalSetExpression;
            }

            case LetExpression letExpression:
            {
                letExpression.Value = AnalyseExpression(letExpression.Value);
                return letExpression;
            }

            case IfExpression ifExpression:
            {
                ifExpression.Condition = AnalyseExpression(ifExpression.Condition);
                ifExpression.Then = AnalyseExpression(ifExpression.Then);
                if (ifExpression.Else is not null)
                    ifExpression.Else = AnalyseExpression(ifExpression.Else);
                return ifExpression;
            }

            case MatchExpression matchExpression:
                return AnalyseMatchExpression(matchExpression);

            case FunctionCallExpression call:
                return AnalyseFunctionCall(call);

            case TypeDefinitionExpression typeDefinitionExpression:
            {
                var type = typeDefinitionExpression.Descriptor?.CreatedType;
                foreach (var method in typeDefinitionExpression.Methods)
                    AnalyseFunctionDefinition(method.FunctionDefinition, type);

                return typeDefinitionExpression;
            }

            case InplaceIncrementExpression inc:
            {
                inc.Subject = AnalyseExpression(inc.Subject);
                return inc;
            }

            case InplaceDecrementExpression dec:
            {
                dec.Subject = AnalyseExpression(dec.Subject);
                return dec;
            }

            case TryExpression tryExpression:
            {
                tryExpression.Body = AnalyseExpression(tryExpression.Body);
                tryExpression.ReturnType = ResolveType(tryExpression.Body);
                return tryExpression;
            }

            case WhileExpression whileExpression:
                return AnalyseWhileExpression(whileExpression);

            case QuickArrayInitialisationExpression arrayInit:
            {
                for (var i = 0; i < arrayInit.Expressions.Length; i++)
                    arrayInit.Expressions[i] = AnalyseExpression(arrayInit.Expressions[i]);
                return arrayInit;
            }

            case ForExpression forExpression:
                forExpression.Range = AnalyseExpression(forExpression.Range);
                for (var i = 0; i < forExpression.Body.Expressions.Length; i++)
                    forExpression.Body.Expressions[i] = AnalyseExpression(forExpression.Body.Expressions[i]);
                return forExpression;

            case InterpolatedStringExpression interpolatedStringExpression:
            {
                foreach (var t in interpolatedStringExpression.Parts)
                    t.Expression = AnalyseExpression(t.Expression);

                return interpolatedStringExpression;
            }

            case IndexerExpression indexerExpression:
            {
                indexerExpression.Identifier = AnalyseExpression(indexerExpression.Identifier);
                var identifierType = ResolveType(indexerExpression.Identifier);

                var returnType = indexerExpression.Index switch
                {
                    IntLiteral => identifierType switch
                    {
                        { IsArray: true } => identifierType.GetElementType()!,
                        _ => throw new Exception(
                            $"Don't know how to iterate {identifierType.Name} with {identifierType.Name}")
                    },
                    var other => throw new Exception($"TODO: implement indexer {other.GetType().Name}")
                };

                indexerExpression.ReturnType = typeof(Option<>).MakeGenericType(returnType);
                return indexerExpression;
            }

            case SimpleMemberAccessExpression simpleMemberAccessExpression:
            {
                simpleMemberAccessExpression.Object = AnalyseExpression(simpleMemberAccessExpression.Object);
                simpleMemberAccessExpression.Property = AnalyseExpression(simpleMemberAccessExpression.Property);
                return simpleMemberAccessExpression;
            }

            case LambdaExpression lambda:
                return AnalyseLambdaExpression(lambda);

            case LocalSetExpression localSet:
            {
                localSet = localSet with { ValueExpression = AnalyseExpression(localSet.ValueExpression) };
                return localSet;
            }

            case UpvalueSetExpression upvalueSet:
            {
                upvalueSet = upvalueSet with { ValueExpression = AnalyseExpression(upvalueSet.ValueExpression) };
                return upvalueSet;
            }

            case CapturedLocalSetExpression capturedSet:
            {
                capturedSet = capturedSet with { ValueExpression = AnalyseExpression(capturedSet.ValueExpression) };
                return capturedSet;
            }

            default:
                return expr;
        }
    }

    private FunctionDefinitionExpression AnalyseFunctionDefinition(
        FunctionDefinitionExpression functionDefinition,
        Type? instanceType = null)
    {
        var paramTypes = new List<UserFunctionParameter>();

        if (functionDefinition.Parameters.Self is not null
            && instanceType is not null)
        {
            paramTypes.Add(new UserFunctionParameter("self", instanceType, IsSelf: true));
        }

        foreach (var functionParam in functionDefinition.Parameters.Parameters)
        {
            functionParam.Type = AnalyseExpression(functionParam.Type);
            var paramType = ResolveType(functionParam.Type);
            paramTypes.Add(new UserFunctionParameter(functionParam.Identifier.Name, paramType));
        }

        for (var i = 0; i < functionDefinition.Body.Length; i++)
            functionDefinition.Body[i] = AnalyseExpression(functionDefinition.Body[i]);

        if (functionDefinition.ReturnType is not null)
            functionDefinition.ReturnType = AnalyseExpression(functionDefinition.ReturnType);

        var functionName = functionDefinition.Identifier.Name;
        var returnType = ResolveFunctionDefinitionReturnType(functionDefinition);
        functionDefinition.FunctionReturnType = returnType;

        if (program.GlobalFunctions.TryGetValue(functionName, out var f) && f is UserFunction uf)
        {
            uf.Parameters = paramTypes.ToArray();
            uf.FunctionReturnType = returnType;
        }
        else if (instanceType is not null
                 && program.MethodTable.TryGetValue(instanceType, out var methods)
                 && methods.TryGetValue(functionName, out var m)
                 && m is UserFunction um)
        {
            um.Parameters = paramTypes.ToArray();
            um.FunctionReturnType = returnType;
        }

        return functionDefinition;
    }

    private IIdentifierLiteral AnalyseIdentifierLiteral(IIdentifierLiteral identifierLiteral)
    {
        switch (identifierLiteral)
        {
            case GlobalGetExpression globalGet:
            {
                AnalyseExpression(globalGet.Identifier);
                globalGet.VariableType = ResolveType(globalGet);
                return globalGet;
            }

            case LocalGetExpression localGet:
            {
                AnalyseExpression(localGet.Identifier);
                localGet.VariableType = ResolveType(localGet);
                return localGet;
            }

            case UpvalueGetExpression upvalueGet:
                return upvalueGet;

            case CapturedLocalGetExpression capturedGet:
                return capturedGet;

            default:
                return identifierLiteral;
        }
    }

    private LambdaExpression AnalyseLambdaExpression(LambdaExpression lambda)
    {
        return lambda with { Body = AnalyseExpression(lambda.Body) };
    }

    private WhileExpression AnalyseWhileExpression(WhileExpression whileExpression)
    {
        whileExpression.Condition = AnalyseExpression(whileExpression.Condition);
        whileExpression.Body = AnalyseExpression(whileExpression.Body);
        return whileExpression;
    }

    private FunctionCallExpression AnalyseFunctionCall(FunctionCallExpression call)
    {
        // Analyse arguments first
        for (var i = 0; i < call.Arguments.Length; i++)
        {
            var argument = call.Arguments[i];
            call.Arguments[i] = AnalyseExpression(argument);
        }

        switch (call.Identifier)
        {
            case GlobalGetExpression ident when program.GlobalFunctions.TryGetValue(ident.Name, out var function):
            {
                call.CachedFunction = function;
                call.ReturnType = function switch
                {
                    ForeignFunction ff => ff.MethodInfo.ReturnType,
                    UserFunction uf => uf.FunctionReturnType,
                    _ => call.ReturnType
                };
                return call;
            }

            case IdentifierLiteral ident when program.GlobalFunctions.TryGetValue(ident.Name, out var function):
                call.CachedFunction = function;
                call.ReturnType = function switch
                {
                    ForeignFunction ff => ff.MethodInfo.ReturnType,
                    UserFunction uf => uf.FunctionReturnType,
                    _ => call.ReturnType
                };
                return call;

            case SimpleMemberAccessExpression member:
            {
                member.Object = AnalyseExpression(member.Object);
                var targetType = ResolveType(member.Object);

                var argTypes = call.Arguments
                    .Select(ResolveType)
                    .ToArray();

                // BCL boundary: Option<T> args bind to parameters typed T (or T?).
                // Pre-unwrap before overload resolution; the runtime ForeignFunction will
                // unwrap actual values at the call site.
                var resolvedArgTypes = argTypes.Select(UnwrapOptionType).ToArray();

                var methodName = member.Property.ToString();

                // Resolve matching overload
                if (methodName is "or")
                {
                    call.CachedFunction = new ForeignFunction(GlobalFunctions.Or);
                    call.ReturnType = ResolveType(call.Arguments[0]);
                    return call;
                }

                if (program.MethodTable.TryGetValue(targetType, out var functions)
                    && functions.TryGetValue(methodName, out var customMethod))
                {
                    call.CachedFunction = customMethod;
                    call.ReturnType = customMethod switch
                    {
                        ForeignFunction ff => ff.MethodInfo.ReturnType,
                        UserFunction uf => uf.FunctionReturnType,
                        _ => null
                    };
                    return call;
                }

                var csharpName = CSharpMethodName(methodName);
                if (ResolveOverload(targetType, csharpName, resolvedArgTypes) is { } method)
                {
                    call.CachedFunction = new ForeignFunction(method);
                    AssignNullableReturn(call, method.ReturnType, method.ReturnParameter);
                    return call;
                }

                if (GetExtensionMethod(targetType, csharpName) is { } extensionMethod)
                {
                    call.CachedFunction = new ForeignFunction(extensionMethod);
                    AssignNullableReturn(call, extensionMethod.ReturnType, extensionMethod.ReturnParameter);
                    return call;
                }

                var args = argTypes.Select(t => t.Name).Join(", ").Trim();
                throw new JitzuException(
                    call.Location,
                    args.Length is 0
                        ? $"Cannot find function '{methodName}' on '{targetType.Name}'"
                        : $"Cannot find function '{methodName}' on '{targetType.Name}' with argument types: {args}");
            }

            case LocalGetExpression:
            case UpvalueGetExpression:
            case CapturedLocalGetExpression:
                // Dynamic call — closure stored in a variable
                return call;

            default:
                // Collect argument types
                return call;
        }
    }

    private static string CSharpMethodName(ReadOnlySpan<char> name)
    {
        Span<char> output = stackalloc char[name.Length];
        var written = 0;
        while (name.IndexOf('_') is var index and > -1)
        {
            var part = name[..index];
            part.CopyTo(output[written..]);
            output[written] = char.ToUpper(output[written]);
            written += part.Length;
            name = name[(index + 1)..];
        }

        name.CopyTo(output[written..]);
        output[written] = char.ToUpper(output[written]);
        written += name.Length;
        return output[..written].ToString();
    }

    private MethodInfo? GetExtensionMethod(Type extendedType, string methodName)
    {
        // Search explicitly loaded assemblies first, then fall back to all loaded
        var assemblies = program.LoadedAssemblies
            .Concat(AppDomain.CurrentDomain.GetAssemblies())
            .Distinct();

        foreach (var assembly in assemblies)
        {
            try
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Use the types that did load
                    types = ex.Types.Where(t => t != null).ToArray()!;
                }

                foreach (var type in types)
                {
                    if (!type.IsSealed || !type.IsAbstract || type.IsGenericType)
                        continue;

                    if (!type.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))
                        continue;

                    foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public))
                    {
                        if (method.Name != methodName)
                            continue;

                        if (!method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))
                            continue;

                        var parameters = method.GetParameters();
                        if (parameters.Length == 0)
                            continue;

                        var thisParam = parameters[0].ParameterType;

                        if (IsExtensionMethodCompatible(thisParam, extendedType))
                            return method;
                    }
                }
            }
            catch (Exception)
            {
                // Skip problematic assemblies
            }
        }

        return null;
    }

    private static bool IsExtensionMethodCompatible(Type thisParam, Type extendedType)
    {
        // Direct match
        if (thisParam == extendedType)
            return true;

        // Assignable (includes inheritance)
        if (thisParam.IsAssignableFrom(extendedType))
            return true;

        // Generic parameter (unconstrained)
        if (thisParam.IsGenericParameter)
            return true;

        // Generic type matching
        if (!thisParam.IsGenericType)
            return thisParam.IsInterface && thisParam.IsAssignableFrom(extendedType);

        var genericDef = thisParam.GetGenericTypeDefinition();

        // Check if extendedType implements/inherits the generic type
        if (extendedType.IsGenericType &&
            extendedType.GetGenericTypeDefinition() == genericDef)
            return true;

        // Check interfaces
        foreach (var iface in extendedType.GetInterfaces())
        {
            if (iface == thisParam)
                return true;

            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == genericDef)
                return true;
        }

        // Check base types
        var baseType = extendedType.BaseType;
        while (baseType != null)
        {
            if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == genericDef)
                return true;
            baseType = baseType.BaseType;
        }

        // Non-generic interface check
        return thisParam.IsInterface && thisParam.IsAssignableFrom(extendedType);
    }

    private Type ResolveType(Expression expr)
    {
        return expr switch
        {
            StringLiteral => typeof(string),
            InterpolatedStringExpression => typeof(string),
            CharLiteral => typeof(char),
            IntLiteral => typeof(int),
            DoubleLiteral => typeof(double),
            BooleanLiteral => typeof(bool),
            BinaryExpression e => ResolveBinaryExpressionReturnType(e),
            UnaryExpression { Operator: "!" } => typeof(bool),
            UnaryExpression { Operator: "-" } e => ResolveType(e.Operand),
            MatchExpression e => ResolveMatchExpressionReturnType(e),
            BlockBodyExpression e => ResolveBlockBodyReturnType(e),
            TryExpression e => e.ReturnType ?? ResolveType(e.Body),
            FunctionDefinitionExpression e => ResolveFunctionDefinitionReturnType(e),
            FunctionCallExpression { ReturnType: { } returnType } => returnType,
            SimpleMemberAccessExpression { ReturnType: { } returnType } => returnType,
            // Qualified name resolution (e.g., System.Collections.List)
            SimpleMemberAccessExpression sma when IsQualifiedTypeName(sma) => ResolveQualifiedTypeName(sma),
            // Simple name resolution with namespace support
            IdentifierLiteral i => ResolveSimpleName(i.Name),
            GenericNameLiteral g => ResolveGenericTypeName(g),
            GlobalSetExpression { SetterType: { } setterType } => setterType,
            GlobalGetExpression { VariableType: { } varType } => varType,
            GlobalGetExpression g when program.SimpleTypeCache.TryGetValue(g.Name, out var type) => type,
            GlobalGetExpression g when program.Globals.TryGetValue(g.Name, out var type) => type,
            IndexerExpression i => ResolveType(i.Identifier) switch
            {
                { IsArray: true } arrayType => MakeSomeType(arrayType.GetElementType()!),
                var other => other,
            },
            ObjectInstantiationExpression e => ResolveType(e.Identifier),
            SimpleMemberAccessExpression s => ResolveTypeMember(s),
            _ => typeof(void)
        };
    }

    private Type ResolveGenericTypeName(GenericNameLiteral g)
    {
        var baseType = ResolveSimpleName(g.Name);
        if (baseType == typeof(void))
            throw new JitzuException(g.Location, $"Unknown type '{g.Name}'");

        if (!baseType.IsGenericTypeDefinition)
            throw new JitzuException(g.Location, $"Type '{g.Name}' is not generic");

        var expectedArity = baseType.GetGenericArguments().Length;
        if (g.TypeArgumentList.Length != expectedArity)
            throw new JitzuException(
                g.Location,
                $"Type '{g.Name}' expects {expectedArity} type argument(s), got {g.TypeArgumentList.Length}");

        var typeArgs = new Type[g.TypeArgumentList.Length];
        for (var i = 0; i < g.TypeArgumentList.Length; i++)
            typeArgs[i] = ResolveType(g.TypeArgumentList[i]);

        return baseType.MakeGenericType(typeArgs);
    }

    private Type ResolveSimpleName(string name)
    {
        // Try simple name cache first (unambiguous)
        if (program.SimpleTypeCache.TryGetValue(name, out var type))
            return type;

        // Check for ambiguous reference
        if (program.TypeNameConflicts.TryGetValue(name, out var fullNames))
        {
            var suggestions = string.Join(", ", fullNames.OrderBy(x => x));
            throw new Exception($"Type '{name}' is ambiguous. Did you mean: {suggestions}?");
        }

        return typeof(void);
    }

    private bool IsQualifiedTypeName(SimpleMemberAccessExpression expr)
    {
        // Check if this looks like a type name (e.g., System.Collections.List)
        // rather than member access on an object
        return FlattenMemberAccessToQualifiedName(expr, out _);
    }

    private Type ResolveQualifiedTypeName(SimpleMemberAccessExpression expr)
    {
        if (FlattenMemberAccessToQualifiedName(expr, out var qualifiedName))
        {
            if (program.Types.TryGetValue(qualifiedName, out var type))
                return type;

            // Type not found - could be member access on value, not type name
            // Fall through to normal resolution
        }

        // If we get here, it's not a qualified type name, so resolve normally
        return ResolveTypeMember(expr);
    }

    private bool FlattenMemberAccessToQualifiedName(Expression expr, out string qualifiedName)
    {
        qualifiedName = "";

        if (expr is SimpleMemberAccessExpression sma)
        {
            var parts = new List<string>();
            var current = (Expression)sma;

            while (current is SimpleMemberAccessExpression memberAccess)
            {
                if (memberAccess.Property is IdentifierLiteral prop)
                {
                    parts.Insert(0, prop.Name);
                    current = memberAccess.Object;
                }
                else
                {
                    return false;
                }
            }

            if (current is IdentifierLiteral id)
            {
                parts.Insert(0, id.Name);
                qualifiedName = string.Join(".", parts);
                return true;
            }
        }

        return false;
    }

    private Type ResolveTypeMember(SimpleMemberAccessExpression s)
    {
        var type = ResolveType(s.Object);

        switch (s.Property)
        {
            case IdentifierLiteral identifierLiteral:
            {
                var member = type
                    .GetMember(identifierLiteral.Name)
                    .FirstOrDefault();

                switch (member)
                {
                    case PropertyInfo p:
                        AssignNullableMember(s, p.PropertyType, p);
                        return s.ReturnType!;
                    case FieldInfo f:
                        AssignNullableMember(s, f.FieldType, f);
                        return s.ReturnType!;
                    default:
                        return s.ReturnType = typeof(void);
                }
            }

            default:
                throw new NotImplementedException();
        }
    }

    private static void AssignNullableMember(SimpleMemberAccessExpression s, Type declaredType, ICustomAttributeProvider site)
    {
        if (OptionBridge.IsNullableSite(declaredType, site))
        {
            var element = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            s.WrapReturnElement = element;
            s.ReturnType = typeof(Option<>).MakeGenericType(element);
        }
        else
        {
            s.ReturnType = declaredType;
        }
    }

    private static Type SurfaceNullableAsOption(Type declaredType, ICustomAttributeProvider site)
    {
        if (!OptionBridge.IsNullableSite(declaredType, site))
            return declaredType;

        var element = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        return typeof(Option<>).MakeGenericType(element);
    }

    private static void AssignNullableReturn(FunctionCallExpression call, Type returnType, ICustomAttributeProvider returnSite)
    {
        if (OptionBridge.IsNullableSite(returnType, returnSite))
        {
            var element = Nullable.GetUnderlyingType(returnType) ?? returnType;
            call.WrapReturnElement = element;
            call.ReturnType = typeof(Option<>).MakeGenericType(element);
        }
        else
        {
            call.ReturnType = returnType;
        }
    }

    private static Type UnwrapOptionType(Type t)
    {
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Option<>))
            return t.GetGenericArguments()[0];
        return t;
    }

    private Type ResolveFunctionDefinitionReturnType(FunctionDefinitionExpression functionDefinition)
    {
        if (functionDefinition.FunctionReturnType is not null)
            return functionDefinition.FunctionReturnType;

        if (functionDefinition.ReturnType is { } returnType)
            return ResolveType(returnType);

        var types = new HashSet<Type>();

        foreach (var expr in functionDefinition.Body)
        {
            if (expr is ReturnExpression returnExpression)
                types.Add(ResolveType(returnExpression));
        }

        types.Add(ResolveType(functionDefinition.Body.Last()));

        return types.Count > 1
            ? throw new Exception("Couldn't resolve match to a single return type")
            : types.First();
    }

    private static Type MakeSomeType(Type valueType)
    {
        return typeof(Option<>).MakeGenericType(valueType);
    }

    private static Type ResolveBinaryExpressionReturnType(BinaryExpression e)
    {
        // TODO: Actually do the logic for binary expressions
        return typeof(object);
    }

    private Type ResolveMatchExpressionReturnType(MatchExpression expression)
    {
        var types = new HashSet<Type>();

        foreach (var branch in expression.Cases)
        {
            branch.Body = AnalyseExpression(branch.Body);
            types.Add(ResolveType(branch.Body));
        }

        return types.Count > 1
            ? throw new Exception("Couldn't resolve match to a single return type")
            : types.First();
    }

    private Type ResolveBlockBodyReturnType(BlockBodyExpression blockBody)
    {
        var types = new HashSet<Type>();

        for (var index = 0; index < blockBody.Expressions.Length; index++)
        {
            blockBody.Expressions[index] = AnalyseExpression(blockBody.Expressions[index]);
            if (blockBody.Expressions[index] is ReturnExpression returnExpression)
                types.Add(ResolveType(returnExpression));
        }

        types.Add(ResolveType(blockBody.Expressions.Last()));

        return types.Count > 1
            ? throw new Exception("Couldn't resolve match to a single return type")
            : types.First();
    }

    private static MethodInfo? ResolveOverload(Type targetType, string methodName, Type[] argTypes)
    {
        // Try exact match first (fast path)
        if (targetType.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.IgnoreCase,
                argTypes) is { } exact)
            return exact;

        // Try generic methods
        var candidates = targetType
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.IgnoreCase)
            .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase) && m.IsGenericMethodDefinition)
            .ToArray();

        foreach (var method in candidates)
        {
            if (TryBindGenericMethod(method, argTypes) is { } bound)
                return bound;
        }

        return null;
    }

    private static MethodInfo? TryBindGenericMethod(MethodInfo method, Type[] argTypes)
    {
        var parameters = method.GetParameters();
        var genericArgs = method.GetGenericArguments();
        var resolved = new Type?[genericArgs.Length];

        // Simple case: single generic param used as first arg (covers Serialize<T>(T value, ...))
        for (var i = 0; i < parameters.Length && i < argTypes.Length; i++)
        {
            var paramType = parameters[i].ParameterType;
            if (paramType.IsGenericParameter)
            {
                var position = paramType.GenericParameterPosition;
                if (resolved[position] is null)
                    resolved[position] = argTypes[i];
                else if (resolved[position] != argTypes[i])
                    return null; // Conflicting inference
            }
        }

        // Check all generic args were resolved
        var genericParams = resolved.OfType<Type>().ToArray();
        if (genericParams.Length != genericArgs.Length)
            return null;

        try
        {
            return method.MakeGenericMethod(genericParams);
        }
        catch (ArgumentException)
        {
            return null; // Constraint violation
        }
    }

    private Expression AnalyseMatchExpression(MatchExpression matchExpression)
    {
        matchExpression.Expression = AnalyseExpression(matchExpression.Expression);

        foreach (var c in matchExpression.Cases)
        {
            c.Pattern = c.Pattern switch
            {
                VariantExpression e => AnalyseVariant(e, ResolveType(matchExpression.Expression)),
                _ => AnalyseExpression(c.Pattern)
            };
            c.Body = AnalyseExpression(c.Body);
        }

        return matchExpression;
    }

    private VariantExpression AnalyseVariant(VariantExpression variantExpression, Type resolvedType)
    {
        variantExpression.Identifier = AnalyseIdentifierLiteral(variantExpression.Identifier);
        var variantType = ResolveType(variantExpression.Identifier);

        if (typeof(IUnion).IsAssignableFrom(resolvedType))
        {
            foreach (var constructor in resolvedType.GetConstructors())
            {
                var firstType = constructor.GetParameters()[0].ParameterType;
                if (!firstType.IsGenericType)
                {
                    if (firstType == variantType)
                        variantExpression.VariantType = firstType;
                }
                else if (variantType.IsGenericType
                         && firstType.GetGenericTypeDefinition() == variantType.GetGenericTypeDefinition())
                    variantExpression.VariantType = firstType;
            }
        }

        variantExpression.VariantType ??= variantType;

        if (variantExpression.PositionalPattern is null)
            return variantExpression;

        if (variantExpression.VariantType.GetMethod("Deconstruct") is not { } deconstructor)
            throw new JitzuException(
                variantExpression.Location,
                $"Type {variantExpression.VariantType.Name} does not have a constructor");

        variantExpression.Deconstructor = deconstructor;
        var parameters = deconstructor.GetParameters();
        var parts = variantExpression.PositionalPattern.Parts;
        for (var i = 0; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case GlobalGetExpression globalGetExpression:
                    globalGetExpression.VariableType = parameters[i].ParameterType.GetElementType();
                    break;

                case LocalGetExpression localGetExpression:
                    localGetExpression.VariableType = parameters[i].ParameterType.GetElementType();
                    break;
            }
        }

        return variantExpression;
    }
}