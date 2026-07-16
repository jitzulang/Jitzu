using Jitzu.Core.Language;
using Jitzu.Core.Runtime.Memory;

namespace Jitzu.Core.Runtime.Compilation;

/// <summary>
/// Transforms AST expressions to use LocalGet and LocalSet expressions for local variable access.
/// This enables efficient local variable storage using slot indices instead of dictionary lookups.
/// </summary>
public class AstTransformer(RuntimeProgram program)
{
    private bool _insideFunction;

    public void TransformScriptExpression(
        ScriptExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        // Don't add a scope here, this should be extending the program scope
        for (int i = 0; i < expression.Body.Length; i++)
            expression.Body[i] = TransformExpression(expression.Body[i], slotMapBuilder);
    }

    public (Dictionary<string, int> SlotMap, SlotMapBuilder ScopedSlotMap) TransformFunctionBody(
        FunctionDefinitionExpression fun,
        SlotMapBuilder? slotMapBuilder)
    {
        var wasInsideFunction = _insideFunction;
        _insideFunction = true;

        var scopedSlotMap = new SlotMapBuilder(slotMapBuilder, LocalKind.Local);
        var slotMap = scopedSlotMap.PushScope();

        // Reserve slots for parameters (including self if present)
        if (fun.Parameters.Self != null)
            scopedSlotMap.Add("self");

        foreach (var param in fun.Parameters.Parameters)
            scopedSlotMap.Add(param.Identifier.Name);

        for (int i = 0; i < fun.Body.Length; i++)
            fun.Body[i] = TransformExpression(fun.Body[i], scopedSlotMap);

        // Post-pass: replace LocalGet/Set with CapturedLocalGet/Set for captured slots
        if (scopedSlotMap.CapturedSlots.Count > 0)
        {
            for (int i = 0; i < fun.Body.Length; i++)
                fun.Body[i] = RewriteCapturedLocals(fun.Body[i], scopedSlotMap.CapturedSlots);
        }

        // Attach upvalue descriptors if any inner scope captures from this function
        var upvalues = scopedSlotMap.Upvalues;
        if (upvalues.Length > 0)
            fun.CapturedUpvalues = upvalues;

        fun.LocalCount = scopedSlotMap.LocalCount;

        _insideFunction = wasInsideFunction;
        return (slotMap, scopedSlotMap);
    }

    /// <summary>
    /// Transforms an expression to use locals where appropriate.
    /// </summary>
    private Expression TransformExpression(
        Expression expr,
        SlotMapBuilder slotMapBuilder)
    {
        return expr switch
        {
            LetExpression e => TransformLetExpression(e, slotMapBuilder),
            AssignmentExpression e => TransformAssignmentExpression(e, slotMapBuilder),
            // Check for qualified type name before treating as normal member access
            SimpleMemberAccessExpression e when TryTransformQualifiedTypeName(e, slotMapBuilder, out var result) => result,
            SimpleMemberAccessExpression e => TransformSimpleMemberAccess(e, slotMapBuilder),
            BlockBodyExpression e => TransformBlockBody(e, slotMapBuilder),
            FunctionCallExpression e => TransformFunctionCallExpression(e, slotMapBuilder),
            BinaryExpression e => TransformBinaryExpression(e, slotMapBuilder),
            UnaryExpression e => e with { Operand = TransformExpression(e.Operand, slotMapBuilder) },
            IfExpression e => TransformIfExpression(e, slotMapBuilder),
            IndexerExpression e => TransformIndexerExpression(e, slotMapBuilder),
            TryExpression e => TransformTryExpression(e, slotMapBuilder),
            ObjectInstantiationExpression e => TransformObjectInstantiationExpression(e, slotMapBuilder),
            NewDynamicExpression e => TransformNewDynamicExpression(e, slotMapBuilder),
            WhileExpression e => TransformWhileExpression(e, slotMapBuilder),
            MatchExpression e => TransformMatchExpression(e, slotMapBuilder),
            InterpolatedStringExpression e => TransformInterpolatedStringExpression(e, slotMapBuilder),
            QuickArrayInitialisationExpression e => TransformQuickArrayInitialisation(e, slotMapBuilder),
            ForExpression e => TransformForExpression(e, slotMapBuilder),
            ReturnExpression e => TransformReturnExpression(e, slotMapBuilder),
            RangeExpression e => TransformRangeExpression(e, slotMapBuilder),
            InplaceIncrementExpression e => TransformInplaceIncrementExpression(e, slotMapBuilder),
            InplaceDecrementExpression e => TransformInplaceDecrementExpression(e, slotMapBuilder),
            VariantExpression e => TransformVariantExpression(e, slotMapBuilder),
            LambdaExpression e => TransformLambdaExpression(e, slotMapBuilder),
            FunctionDefinitionExpression e when _insideFunction => TransformNestedFunctionDefinition(e, slotMapBuilder),
            IIdentifierLiteral e => TransformIdentifierExpression(e, slotMapBuilder),
            _ => expr,
        };
    }

    private IIdentifierLiteral TransformIdentifierExpression(
        IIdentifierLiteral ident,
        SlotMapBuilder slotMapBuilder)
    {
        return ident switch
        {
            IdentifierLiteral l when slotMapBuilder.TryGetLocal(l.Name, out var local) =>
                TransformIdentifierToLocalGet(l, local),
            // Simple type name using simple cache (unambiguous)
            IdentifierLiteral l when program.SimpleTypeCache.TryGetValue(l.Name, out var type) =>
                AddTypeAndTransformIdentifier(l, type, slotMapBuilder),
            KeywordLiteral { Name: "self" } k when slotMapBuilder.TryGetLocal("self", out var local) =>
                TransformIdentifierToLocalGet(k, local),
            GenericNameLiteral => throw new NotImplementedException(),
            _ => throw new JitzuException(ident.Location, $"Unable to bind identifier '{ident.Name}'"),
        };
    }

    private Expression CreateGetExpression(Local local, IIdentifierLiteral identifier)
    {
        return local.LocalKind switch
        {
            LocalKind.Local => new LocalGetExpression(identifier)
            {
                SlotIndex = local.SlotIndex,
                Location = identifier.Location
            },
            LocalKind.Global => new GlobalGetExpression(identifier)
            {
                SlotIndex = local.SlotIndex,
                Location = identifier.Location,
            },
            LocalKind.Upvalue => new UpvalueGetExpression(identifier)
            {
                UpvalueIndex = local.SlotIndex,
                Location = identifier.Location,
            },
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    /// <summary>
    /// Transforms a let expression to use LocalSet.
    /// </summary>
    private Expression TransformLetExpression(
        LetExpression letExpr,
        SlotMapBuilder slotMapBuilder)
    {
        // Always add a new local in this case for var shadowing.
        var local = slotMapBuilder.Add(letExpr.Identifier.Name);
        return CreateSetExpression(local, letExpr.Identifier, letExpr.Value, slotMapBuilder);
    }

    private Expression CreateSetExpression(
        Local local,
        IIdentifierLiteral identifierLiteral,
        Expression value,
        SlotMapBuilder slotMapBuilder)
    {
        return local.LocalKind switch
        {
            LocalKind.Local => new LocalSetExpression
            {
                SlotIndex = local.SlotIndex,
                ValueExpression = TransformExpression(value, slotMapBuilder),
                Location = identifierLiteral.Location,
                Identifier = identifierLiteral,
            },
            LocalKind.Global => new GlobalSetExpression(identifierLiteral)
            {
                SlotIndex = local.SlotIndex,
                ValueExpression = TransformExpression(value, slotMapBuilder),
                Location = identifierLiteral.Location,
            },
            LocalKind.Upvalue => new UpvalueSetExpression
            {
                UpvalueIndex = local.SlotIndex,
                ValueExpression = TransformExpression(value, slotMapBuilder),
                Location = identifierLiteral.Location,
                Identifier = identifierLiteral,
            },
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    /// <summary>
    /// Transforms an assignment expression to use LocalSet if the target is a local variable.
    /// </summary>
    private Expression TransformAssignmentExpression(
        AssignmentExpression assignExpr,
        SlotMapBuilder slotMapBuilder)
    {
        if (assignExpr.Left is IIdentifierLiteral ident && slotMapBuilder.TryGetLocal(ident.Name, out var local))
        {
            return local.LocalKind switch
            {
                LocalKind.Local => new LocalSetExpression
                {
                    SlotIndex = local.SlotIndex,
                    ValueExpression = TransformExpression(assignExpr.Right, slotMapBuilder),
                    Location = assignExpr.Location,
                    Identifier = ident,
                },
                LocalKind.Global => new GlobalSetExpression(ident)
                {
                    SlotIndex = local.SlotIndex,
                    ValueExpression = TransformExpression(assignExpr.Right, slotMapBuilder),
                    Location = assignExpr.Location,
                },
                LocalKind.Upvalue => new UpvalueSetExpression
                {
                    UpvalueIndex = local.SlotIndex,
                    ValueExpression = TransformExpression(assignExpr.Right, slotMapBuilder),
                    Location = assignExpr.Location,
                    Identifier = ident,
                },
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        // For non-local assignments, transform children but keep the structure
        return new AssignmentExpression
        {
            Left = TransformExpression(assignExpr.Left, slotMapBuilder),
            Right = TransformExpression(assignExpr.Right, slotMapBuilder),
            Location = assignExpr.Location
        };
    }

    /// <summary>
    /// Transforms an identifier literal to use LocalGet if it's a local variable.
    /// </summary>
    private SimpleMemberAccessExpression TransformSimpleMemberAccess(
        SimpleMemberAccessExpression ident,
        SlotMapBuilder slotMapBuilder)
    {
        ident.Object = TransformExpression(ident.Object, slotMapBuilder);
        return ident;
    }

    /// <summary>
    /// Transforms an identifier literal to use LocalGet if it's a local variable.
    /// </summary>
    private IIdentifierLiteral TransformIdentifierToLocalGet(IIdentifierLiteral ident, Local local)
    {
        return local.LocalKind switch
        {
            LocalKind.Local => new LocalGetExpression(ident)
            {
                SlotIndex = local.SlotIndex,
                Location = ident.Location,
            },
            LocalKind.Global => new GlobalGetExpression(ident)
            {
                SlotIndex = local.SlotIndex,
                Location = ident.Location,
            },
            LocalKind.Upvalue => new UpvalueGetExpression(ident)
            {
                UpvalueIndex = local.SlotIndex,
                Location = ident.Location,
            },
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private IIdentifierLiteral AddTypeAndTransformIdentifier(
        IdentifierLiteral ident,
        Type type,
        SlotMapBuilder slotMapBuilder)
    {
        var local = slotMapBuilder.AddGlobal(ident.Name);
        return TransformIdentifierToLocalGet(ident, local);
    }

    private bool TryTransformQualifiedTypeName(
        SimpleMemberAccessExpression expr,
        SlotMapBuilder slotMapBuilder,
        out Expression result)
    {
        result = expr;

        // Try to flatten the member access expression into a qualified name
        if (FlattenMemberAccessToQualifiedName(expr, out var qualifiedName))
        {
            // Check if this is a type
            if (program.Types.TryGetValue(qualifiedName, out var type))
            {
                // Create a synthetic identifier for the type
                var syntheticIdent = new IdentifierLiteral
                {
                    Name = qualifiedName,
                    Location = expr.Location
                };

                result = AddTypeAndTransformIdentifier(syntheticIdent, type, slotMapBuilder);
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// Transforms a block body expression by transforming all its child expressions.
    /// </summary>
    private BlockBodyExpression TransformBlockBody(
        BlockBodyExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        expression.Scope = slotMapBuilder.PushScope();
        for (var index = 0; index < expression.Expressions.Length; index++)
        {
            var expr = expression.Expressions[index];
            expression.Expressions[index] = TransformExpression(expr, slotMapBuilder);
        }

        slotMapBuilder.PopScope();
        return expression;
    }

    /// <summary>
    /// Transforms a binary expression by transforming its left and right operands.
    /// </summary>
    private BinaryExpression TransformBinaryExpression(
        BinaryExpression binExpr,
        SlotMapBuilder slotMapBuilder)
    {
        binExpr.Left = TransformExpression(binExpr.Left, slotMapBuilder);
        binExpr.Right = TransformExpression(binExpr.Right, slotMapBuilder);
        return binExpr;
    }

    private IfExpression TransformIfExpression(
        IfExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        expression.Condition = TransformExpression(expression.Condition, slotMapBuilder);
        expression.Then = TransformExpression(expression.Then, slotMapBuilder);
        expression.Else = expression.Else is not null
            ? TransformExpression(expression.Else, slotMapBuilder)
            : null;

        return expression;
    }

    private IndexerExpression TransformIndexerExpression(
        IndexerExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        expression.Identifier = TransformExpression(expression.Identifier, slotMapBuilder);
        expression.Index = TransformExpression(expression.Index, slotMapBuilder);
        return expression;
    }

    private TryExpression TransformTryExpression(
        TryExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        expression.Body = TransformExpression(expression.Body, slotMapBuilder);
        return expression;
    }

    private ObjectInstantiationExpression TransformObjectInstantiationExpression(
        ObjectInstantiationExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        expression.Identifier = (IIdentifierLiteral)TransformExpression(expression.Identifier, slotMapBuilder);
        expression.Body = (NewDynamicExpression)TransformExpression(expression.Body, slotMapBuilder);
        return expression;
    }

    private NewDynamicExpression TransformNewDynamicExpression(
        NewDynamicExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        foreach (var field in expression.Fields)
        {
            field.Value = field.Value is not null
                ? TransformExpression(field.Value, slotMapBuilder)
                : TransformExpression(field.Identifier, slotMapBuilder);
        }

        return expression;
    }

    private WhileExpression TransformWhileExpression(
        WhileExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        expression.Condition = TransformExpression(expression.Condition, slotMapBuilder);
        expression.Body = TransformExpression(expression.Body, slotMapBuilder);
        return expression;
    }

    private BlockBodyExpression TransformMatchExpression(
        MatchExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        var identifier = new IdentifierLiteral
        {
            Name = Guid.NewGuid().ToString(),
            Location = expression.Expression.Location,
        };

        var local = slotMapBuilder.Add(identifier.Name);
        var tempExpression = CreateSetExpression(local, identifier, expression.Expression, slotMapBuilder);

        expression.Expression = CreateGetExpression(local, identifier);
        foreach (var matchCase in expression.Cases)
        {
            slotMapBuilder.PushScope();
            matchCase.Pattern = TransformExpression(matchCase.Pattern, slotMapBuilder);
            matchCase.Body = TransformExpression(matchCase.Body, slotMapBuilder);
            slotMapBuilder.PopScope();
        }

        return new BlockBodyExpression
        {
            Expressions = [tempExpression, expression]
        };
    }

    private InterpolatedStringExpression TransformInterpolatedStringExpression(
        InterpolatedStringExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        foreach (var part in expression.Parts)
            part.Expression = TransformExpression(part.Expression, slotMapBuilder);

        return expression;
    }

    private QuickArrayInitialisationExpression TransformQuickArrayInitialisation(
        QuickArrayInitialisationExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        for (var i = 0; i < expression.Expressions.Length; i++)
            expression.Expressions[i] = TransformExpression(expression.Expressions[i], slotMapBuilder);
        return expression;
    }

    private ForExpression TransformForExpression(ForExpression expression, SlotMapBuilder slotMapBuilder)
    {
        // Reject reassignment of the collection variable inside the loop body.
        // Check the *untransformed* body so that `let` (shadow) is not confused with `=` (mutation).
        if (expression.Range is not RangeExpression && expression.Range is IIdentifierLiteral ident
            && ContainsReassignmentOf(expression.Body, ident.Name))
            throw new JitzuException(expression.Location, $"Cannot assign to '{ident.Name}' while iterating over it");

        slotMapBuilder.PushScope();
        // Hidden counter and limit slots drive the loop — never captured by closures.
        var counterLocal = slotMapBuilder.Add();
        var limitLocal = slotMapBuilder.Add();
        expression.CounterSlot = counterLocal.SlotIndex;
        expression.LimitSlot = limitLocal.SlotIndex;

        // Check if the range is a collection (not a RangeExpression)
        if (expression.Range is not RangeExpression)
        {
            var collectionLocal = slotMapBuilder.Add();
            expression.CollectionSlot = collectionLocal.SlotIndex;
            expression.IsCollection = true;
        }

        // User-visible iterator gets a fresh copy each iteration (per-iteration scoping).
        var iteratorLocal = slotMapBuilder.Add(expression.Identifier.Name);
        expression.Identifier = (IIdentifierLiteral)CreateGetExpression(iteratorLocal, expression.Identifier);
        expression.Range = TransformExpression(expression.Range, slotMapBuilder);
        expression.Body = TransformBlockBody(expression.Body, slotMapBuilder);

        slotMapBuilder.PopScope();
        return expression;
    }

    /// <summary>
    /// Walks the untransformed AST looking for <see cref="AssignmentExpression"/> nodes
    /// whose left-hand side is an identifier matching <paramref name="name"/>.
    /// <see cref="LetExpression"/> (new binding / shadow) is intentionally excluded.
    /// </summary>
    private static bool ContainsReassignmentOf(Expression expr, string name)
    {
        return expr switch
        {
            AssignmentExpression { Left: IIdentifierLiteral id } when id.Name == name => true,
            AssignmentExpression e => ContainsReassignmentOf(e.Right, name),
            BlockBodyExpression e => e.Expressions.Any(x => ContainsReassignmentOf(x, name)),
            IfExpression e => ContainsReassignmentOf(e.Condition, name)
                || ContainsReassignmentOf(e.Then, name)
                || (e.Else is not null && ContainsReassignmentOf(e.Else, name)),
            WhileExpression e => ContainsReassignmentOf(e.Condition, name) || ContainsReassignmentOf(e.Body, name),
            ForExpression e => ContainsReassignmentOf(e.Range, name) || ContainsReassignmentOf(e.Body, name),
            // Don't recurse into lambdas/nested functions — they have their own scope
            LambdaExpression or FunctionDefinitionExpression => false,
            _ => false,
        };
    }

    private ReturnExpression TransformReturnExpression(
        ReturnExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        if (expression.ReturnValue is { } returnValue)
            expression.ReturnValue = TransformExpression(returnValue, slotMapBuilder);
        return expression;
    }

    private RangeExpression TransformRangeExpression(
        RangeExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        if (expression.Left is not null)
            expression.Left = TransformExpression(expression.Left, slotMapBuilder);

        if (expression.Right is not null)
            expression.Right = TransformExpression(expression.Right, slotMapBuilder);

        return expression;
    }

    private InplaceIncrementExpression TransformInplaceIncrementExpression(
        InplaceIncrementExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        expression.Subject = TransformExpression(expression.Subject, slotMapBuilder);
        return expression;
    }

    private InplaceDecrementExpression TransformInplaceDecrementExpression(
        InplaceDecrementExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        expression.Subject = TransformExpression(expression.Subject, slotMapBuilder);
        return expression;
    }

    /// <summary>
    /// Transforms a function call expression by transforming its identifier and arguments.
    /// </summary>
    private FunctionCallExpression TransformFunctionCallExpression(
        FunctionCallExpression expression,
        SlotMapBuilder slotMapBuilder)
    {
        expression.Identifier = TransformExpression(expression.Identifier, slotMapBuilder);

        for (int i = 0; i < expression.Arguments.Length; i++)
        {
            var arg = expression.Arguments[i];
            expression.Arguments[i] = TransformExpression(arg, slotMapBuilder);
        }

        return expression;
    }

    private VariantExpression TransformVariantExpression(
        VariantExpression variantExpression,
        SlotMapBuilder slotMapBuilder)
    {
        variantExpression.Identifier = TransformIdentifierExpression(variantExpression.Identifier, slotMapBuilder);
        if (variantExpression.PositionalPattern != null)
        {
            var parts = variantExpression.PositionalPattern.Parts;
            for (var i = 0; i < variantExpression.PositionalPattern.Parts.Length; i++)
            {
                switch (parts[i])
                {
                    case IdentifierLiteral identifier:
                        var local = slotMapBuilder.Add(identifier.Name);
                        parts[i] = CreateGetExpression(local, identifier);
                        break;
                }
            }
        }

        return variantExpression;
    }

    private Expression TransformLambdaExpression(
        LambdaExpression lambda,
        SlotMapBuilder slotMapBuilder)
    {
        var scopedSlotMap = new SlotMapBuilder(slotMapBuilder, LocalKind.Local);
        scopedSlotMap.PushScope();

        // Reserve slots for parameters
        foreach (var param in lambda.Parameters)
        {
            if (param is IdentifierLiteral ident)
                scopedSlotMap.Add(ident.Name);
        }

        // Transform body
        lambda = lambda with { Body = TransformExpression(lambda.Body, scopedSlotMap) };

        // Post-pass for captured locals
        if (scopedSlotMap.CapturedSlots.Count > 0)
            lambda = lambda with { Body = RewriteCapturedLocals(lambda.Body, scopedSlotMap.CapturedSlots) };

        lambda.LocalCount = scopedSlotMap.LocalCount;

        var upvalues = scopedSlotMap.Upvalues;
        if (upvalues.Length > 0)
            lambda.CapturedUpvalues = upvalues;

        return lambda;
    }

    private Expression TransformNestedFunctionDefinition(
        FunctionDefinitionExpression funcDef,
        SlotMapBuilder slotMapBuilder)
    {
        // Allocate a local slot for the nested function so it can be called by name
        var local = slotMapBuilder.Add(funcDef.Identifier.Name);

        // TransformFunctionBody handles attaching CapturedUpvalues to funcDef
        TransformFunctionBody(funcDef, slotMapBuilder);

        // Wrap the function definition in a local set so it can be referenced
        return new LocalSetExpression
        {
            SlotIndex = local.SlotIndex,
            ValueExpression = funcDef,
            Location = funcDef.Location,
            Identifier = funcDef.Identifier,
        };
    }

    /// <summary>
    /// Post-pass: replace LocalGetExpression/LocalSetExpression with CapturedLocal variants
    /// for slots that are captured by inner closures.
    /// </summary>
    private static Expression RewriteCapturedLocals(Expression expr, HashSet<int> capturedSlots)
    {
        return expr switch
        {
            LocalGetExpression lg when capturedSlots.Contains(lg.SlotIndex) =>
                new CapturedLocalGetExpression(lg.Identifier)
                {
                    SlotIndex = lg.SlotIndex,
                    Location = lg.Location,
                    VariableType = lg.VariableType,
                },
            LocalSetExpression ls when capturedSlots.Contains(ls.SlotIndex) =>
                new CapturedLocalSetExpression
                {
                    SlotIndex = ls.SlotIndex,
                    ValueExpression = RewriteCapturedLocals(ls.ValueExpression, capturedSlots),
                    Location = ls.Location,
                    Identifier = ls.Identifier,
                },
            // Recurse into compound expressions
            BlockBodyExpression bb => RewriteBlockBody(bb, capturedSlots),
            IfExpression ie => RewriteIf(ie, capturedSlots),
            WhileExpression we => RewriteWhile(we, capturedSlots),
            ForExpression fe => RewriteFor(fe, capturedSlots),
            BinaryExpression be => be with
            {
                Left = RewriteCapturedLocals(be.Left, capturedSlots),
                Right = RewriteCapturedLocals(be.Right, capturedSlots),
            },
            UnaryExpression ue => ue with
            {
                Operand = RewriteCapturedLocals(ue.Operand, capturedSlots),
            },
            FunctionCallExpression fce => RewriteFunctionCall(fce, capturedSlots),
            ReturnExpression re => re with
            {
                ReturnValue = re.ReturnValue != null ? RewriteCapturedLocals(re.ReturnValue, capturedSlots) : null,
            },
            InplaceIncrementExpression inc => inc with
            {
                Subject = RewriteCapturedLocals(inc.Subject, capturedSlots),
            },
            InplaceDecrementExpression dec => dec with
            {
                Subject = RewriteCapturedLocals(dec.Subject, capturedSlots),
            },
            InterpolatedStringExpression ise => RewriteInterpolatedString(ise, capturedSlots),
            IndexerExpression idx => idx with
            {
                Identifier = RewriteCapturedLocals(idx.Identifier, capturedSlots),
                Index = RewriteCapturedLocals(idx.Index, capturedSlots),
            },
            TryExpression te => te with { Body = RewriteCapturedLocals(te.Body, capturedSlots) },
            RangeExpression re => re with
            {
                Left = re.Left != null ? RewriteCapturedLocals(re.Left, capturedSlots) : null,
                Right = re.Right != null ? RewriteCapturedLocals(re.Right, capturedSlots) : null,
            },
            AssignmentExpression ae => ae with
            {
                Left = RewriteCapturedLocals(ae.Left, capturedSlots),
                Right = RewriteCapturedLocals(ae.Right, capturedSlots),
            },
            GlobalSetExpression gs => gs with
            {
                ValueExpression = RewriteCapturedLocals(gs.ValueExpression, capturedSlots),
            },
            UpvalueSetExpression us => us with
            {
                ValueExpression = RewriteCapturedLocals(us.ValueExpression, capturedSlots),
            },
            SimpleMemberAccessExpression sma => sma with
            {
                Object = RewriteCapturedLocals(sma.Object, capturedSlots),
            },
            MatchExpression me => RewriteMatch(me, capturedSlots),
            ObjectInstantiationExpression oi => oi with
            {
                Body = (NewDynamicExpression)RewriteCapturedLocals(oi.Body, capturedSlots),
            },
            NewDynamicExpression nde => RewriteNewDynamic(nde, capturedSlots),
            VariantExpression ve => RewriteVariant(ve, capturedSlots),
            ConstantExpression ce => ce with
            {
                Expression = RewriteCapturedLocals(ce.Expression, capturedSlots),
            },
            // Don't recurse into nested functions/lambdas — they have their own scope
            LambdaExpression => expr,
            FunctionDefinitionExpression => expr,
            // Leaf nodes (literals, identifiers without captured slots, discards, etc.)
            _ => expr,
        };
    }

    private static BlockBodyExpression RewriteBlockBody(BlockBodyExpression bb, HashSet<int> capturedSlots)
    {
        for (var i = 0; i < bb.Expressions.Length; i++)
            bb.Expressions[i] = RewriteCapturedLocals(bb.Expressions[i], capturedSlots);
        return bb;
    }

    private static IfExpression RewriteIf(IfExpression ie, HashSet<int> capturedSlots)
    {
        ie.Condition = RewriteCapturedLocals(ie.Condition, capturedSlots);
        ie.Then = RewriteCapturedLocals(ie.Then, capturedSlots);
        if (ie.Else != null)
            ie.Else = RewriteCapturedLocals(ie.Else, capturedSlots);
        return ie;
    }

    private static WhileExpression RewriteWhile(WhileExpression we, HashSet<int> capturedSlots)
    {
        we.Condition = RewriteCapturedLocals(we.Condition, capturedSlots);
        we.Body = RewriteCapturedLocals(we.Body, capturedSlots);
        return we;
    }

    private static ForExpression RewriteFor(ForExpression fe, HashSet<int> capturedSlots)
    {
        fe.Identifier = (IIdentifierLiteral)RewriteCapturedLocals(fe.Identifier, capturedSlots);
        fe.Range = RewriteCapturedLocals(fe.Range, capturedSlots);
        fe.Body = RewriteBlockBody(fe.Body, capturedSlots);
        return fe;
    }

    private static FunctionCallExpression RewriteFunctionCall(FunctionCallExpression fce, HashSet<int> capturedSlots)
    {
        fce.Identifier = RewriteCapturedLocals(fce.Identifier, capturedSlots);
        for (var i = 0; i < fce.Arguments.Length; i++)
            fce.Arguments[i] = RewriteCapturedLocals(fce.Arguments[i], capturedSlots);
        return fce;
    }

    private static InterpolatedStringExpression RewriteInterpolatedString(InterpolatedStringExpression ise, HashSet<int> capturedSlots)
    {
        foreach (var part in ise.Parts)
            part.Expression = RewriteCapturedLocals(part.Expression, capturedSlots);
        return ise;
    }

    private static MatchExpression RewriteMatch(MatchExpression me, HashSet<int> capturedSlots)
    {
        me.Expression = RewriteCapturedLocals(me.Expression, capturedSlots);
        foreach (var c in me.Cases)
        {
            c.Pattern = RewriteCapturedLocals(c.Pattern, capturedSlots);
            c.Body = RewriteCapturedLocals(c.Body, capturedSlots);
        }
        return me;
    }

    private static NewDynamicExpression RewriteNewDynamic(NewDynamicExpression nde, HashSet<int> capturedSlots)
    {
        foreach (var field in nde.Fields)
        {
            if (field.Value != null)
                field.Value = RewriteCapturedLocals(field.Value, capturedSlots);
            else
                field.Identifier = (IIdentifierLiteral)RewriteCapturedLocals(field.Identifier, capturedSlots);
        }
        return nde;
    }

    private static VariantExpression RewriteVariant(VariantExpression ve, HashSet<int> capturedSlots)
    {
        ve.Identifier = (IIdentifierLiteral)RewriteCapturedLocals(ve.Identifier, capturedSlots);
        if (ve.PositionalPattern != null)
        {
            var parts = ve.PositionalPattern.Parts;
            for (var i = 0; i < parts.Length; i++)
                parts[i] = RewriteCapturedLocals(parts[i], capturedSlots);
        }
        return ve;
    }
}
