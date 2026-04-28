using System.Runtime.CompilerServices;
using Jitzu.Core.Common;
using Jitzu.Core.Language;
using Jitzu.Core.Types;

namespace Jitzu.Core.Runtime.Compilation;

public enum ContextItem
{
    TryExpression,
}

public sealed class ByteCodeCompiler(RuntimeProgram program)
{
    private Chunk _currentChunk = null!;
    private readonly Stack<ContextItem> _context = new();

    public UserFunction Compile(Expression[] expressions)
    {
        _currentChunk = new Chunk();

        // This is the args that are injected at runtime
        _currentChunk.AddOrGetConstant(Array.Empty<string>());

        foreach (var thing in program.GlobalFunctions.Values)
            _currentChunk.AddOrGetConstant(thing);

        foreach (var expr in expressions)
            EmitExpression(expr);

        _currentChunk.Emit(OpCode.Return, SourceSpan.Empty);
        return new UserFunction("<script>", _currentChunk);
    }

    private Chunk CompileFunction(FunctionDefinitionExpression decl)
    {
        var parentChunk = _currentChunk;
        _currentChunk = new Chunk();

        // Parameters are already bound to local slots by AstTransformer, so we just push code for the body.
        foreach (var stmt in decl.Body)
            EmitExpression(stmt);

        // Ensure there is always a return at the end.
        // If user didn't explicitly return, return Unit.Instance by default.
        if (_currentChunk.Code.Count == 0 || _currentChunk.Code[^1] != (byte)OpCode.Return)
        {
            if (decl.FunctionReturnType == null
                || decl.FunctionReturnType == typeof(void)
                || decl.FunctionReturnType == typeof(Unit))
            {
                // Void function: implicit return Unit
                EmitConstant(Unit.Instance, decl.Location);
            }

            _currentChunk.Emit(OpCode.Return, decl.Body.Last().Location);
        }

        var functionChunk = _currentChunk;
        _currentChunk = parentChunk;
        return functionChunk;
    }

    private Chunk CompileLambdaBody(Expression body, SourceSpan location)
    {
        var parentChunk = _currentChunk;
        _currentChunk = new Chunk();

        EmitExpression(body);

        // Lambda implicitly returns the value of its body expression
        if (_currentChunk.Code.Count == 0 || _currentChunk.Code[^1] != (byte)OpCode.Return)
            _currentChunk.Emit(OpCode.Return, location);

        var lambdaChunk = _currentChunk;
        _currentChunk = parentChunk;
        return lambdaChunk;
    }

    private void EmitExpression(Expression expr)
    {
        switch (expr)
        {
            case IntLiteral intLit:
                EmitConstant(intLit.Integer, expr.Location);
                break;

            case DoubleLiteral doubleLit:
                EmitConstant(doubleLit.Double, expr.Location);
                break;

            case StringLiteral strLit:
                EmitConstant(strLit.String, expr.Location);
                break;

            case BooleanLiteral boolLit:
                EmitConstant(boolLit.Bool, expr.Location);
                break;

            case IdentifierLiteral identifierLiteral:
                EmitConstant(identifierLiteral.Name, identifierLiteral.Location);
                break;

            case GlobalGetExpression g:
                _currentChunk.Emit(OpCode.GetGlobal, expr.Location, g.SlotIndex);
                break;

            case LocalGetExpression l:
                _currentChunk.Emit(OpCode.GetLocal, expr.Location, l.SlotIndex);
                break;

            case UpvalueGetExpression u:
                _currentChunk.Emit(OpCode.GetUpvalue, expr.Location, u.UpvalueIndex);
                break;

            case CapturedLocalGetExpression cl:
                _currentChunk.Emit(OpCode.GetCapturedLocal, expr.Location, cl.SlotIndex);
                break;

            case KeywordLiteral { Name: "self" }:
                throw new NotImplementedException();

            case GlobalSetExpression gs:
                EmitExpression(gs.ValueExpression);
                _currentChunk.Emit(OpCode.SetGlobal, gs.Location, gs.SlotIndex);
                break;

            case LocalSetExpression ls:
                EmitExpression(ls.ValueExpression);
                _currentChunk.Emit(OpCode.SetLocal, ls.Location, ls.SlotIndex);
                break;

            case UpvalueSetExpression us:
                EmitExpression(us.ValueExpression);
                _currentChunk.Emit(OpCode.SetUpvalue, us.Location, us.UpvalueIndex);
                break;

            case CapturedLocalSetExpression cls:
                EmitExpression(cls.ValueExpression);
                _currentChunk.Emit(OpCode.SetCapturedLocal, cls.Location, cls.SlotIndex);
                break;

            case FunctionCallExpression call:
                EmitFunctionCallExpression(call);
                break;

            case ObjectInstantiationExpression obj:
                CompileObjectInstantiation(obj);
                break;

            case AssignmentExpression ass:
            {
                EmitExpression(ass.Right);

                switch (ass.Left)
                {
                    case SimpleMemberAccessExpression member:
                    {
                        EmitExpression(member.Object);   // Push object
                        _currentChunk.Emit(OpCode.Dup, ass.Location); // Keep a copy
                        EmitExpression(ass.Right);       // Push value
                        switch (member.Property)
                        {
                            case IIdentifierLiteral identifierLiteral:
                                var idx = _currentChunk.AddOrGetConstant(identifierLiteral.Name);
                                _currentChunk.Emit(OpCode.SetField, ass.Location, idx);
                                break;
                        }
                        _currentChunk.Emit(OpCode.Pop, ass.Location); // Clean up dup
                        break;
                    }
                }

                break;
            }

            case BinaryExpression binOp:
            {
                EmitExpression(binOp.Left);
                EmitExpression(binOp.Right);
                var op = binOp.Operator.Value switch
                {
                    "+" => OpCode.Add,
                    "-" => OpCode.Sub,
                    "*" => OpCode.Mul,
                    "/" => OpCode.Div,
                    "<" => OpCode.Lt,
                    "<=" => OpCode.Lte,
                    ">" => OpCode.Gt,
                    ">=" => OpCode.Gte,
                    "is" => OpCode.Compare,
                    "==" => OpCode.Eq,
                    "%" => OpCode.Mod,
                    "|" => OpCode.BitwiseOr,
                    _ => throw new UnsupportedExpressionException(binOp),
                };
                _currentChunk.Emit(op, binOp.Location);
                break;
            }

            case InplaceIncrementExpression inc:
            {
                switch (inc.Subject)
                {
                    case GlobalGetExpression global:
                    {
                        EmitExpression(inc.Subject);
                        _currentChunk.Emit(OpCode.Inc, inc.Location);
                        _currentChunk.Emit(OpCode.SetGlobal, inc.Location, global.SlotIndex);
                        break;
                    }

                    case LocalGetExpression local:
                    {
                        EmitExpression(inc.Subject);
                        _currentChunk.Emit(OpCode.Inc, inc.Location);
                        _currentChunk.Emit(OpCode.SetLocal, inc.Location, local.SlotIndex);
                        break;
                    }

                    case CapturedLocalGetExpression captured:
                    {
                        EmitExpression(inc.Subject);
                        _currentChunk.Emit(OpCode.Inc, inc.Location);
                        _currentChunk.Emit(OpCode.SetCapturedLocal, inc.Location, captured.SlotIndex);
                        break;
                    }

                    case UpvalueGetExpression upvalue:
                    {
                        EmitExpression(inc.Subject);
                        _currentChunk.Emit(OpCode.Inc, inc.Location);
                        _currentChunk.Emit(OpCode.SetUpvalue, inc.Location, upvalue.UpvalueIndex);
                        break;
                    }

                    default:
                        _currentChunk.Emit(OpCode.Inc, inc.Location);
                        break;
                }

                break;
            }

            case InplaceDecrementExpression dec:
            {
                switch (dec.Subject)
                {
                    case GlobalGetExpression global:
                    {
                        EmitExpression(dec.Subject);
                        _currentChunk.Emit(OpCode.Dec, dec.Location);
                        _currentChunk.Emit(OpCode.SetGlobal, dec.Location, global.SlotIndex);
                        break;
                    }

                    case LocalGetExpression local:
                    {
                        EmitExpression(dec.Subject);
                        _currentChunk.Emit(OpCode.Dec, dec.Location);
                        _currentChunk.Emit(OpCode.SetLocal, dec.Location, local.SlotIndex);
                        break;
                    }

                    case CapturedLocalGetExpression captured:
                    {
                        EmitExpression(dec.Subject);
                        _currentChunk.Emit(OpCode.Dec, dec.Location);
                        _currentChunk.Emit(OpCode.SetCapturedLocal, dec.Location, captured.SlotIndex);
                        break;
                    }

                    case UpvalueGetExpression upvalue:
                    {
                        EmitExpression(dec.Subject);
                        _currentChunk.Emit(OpCode.Dec, dec.Location);
                        _currentChunk.Emit(OpCode.SetUpvalue, dec.Location, upvalue.UpvalueIndex);
                        break;
                    }

                    default:
                        EmitExpression(dec.Subject);
                        _currentChunk.Emit(OpCode.Dec, dec.Location);
                        break;
                }

                break;
            }

            case TryExpression tryExpression:
                _context.Push(ContextItem.TryExpression);
                EmitExpression(tryExpression.Body);
                _currentChunk.Emit(OpCode.TryUnwrap, tryExpression.Location);
                _context.Pop();
                break;

            case SimpleMemberAccessExpression memberExpression:
            {
                EmitExpression(memberExpression.Object);
                int constIndex = _currentChunk.AddOrGetConstant(memberExpression.Property.ToString());
                _currentChunk.Emit(OpCode.GetField, memberExpression.Property.Location, constIndex);
                break;
            }

            case IndexerExpression indexerExpression:
                EmitExpression(indexerExpression.Identifier);
                EmitExpression(indexerExpression.Index);
                _currentChunk.Emit(OpCode.IndexGet, indexerExpression.Location);
                break;

            case QuickArrayInitialisationExpression arrayInit:
                foreach (var element in arrayInit.Expressions)
                    EmitExpression(element);
                _currentChunk.Emit(OpCode.NewList, arrayInit.Location, arrayInit.Expressions.Length);
                break;

            case WhileExpression whileExpression:
                CompileWhileExpression(whileExpression);
                break;

            case ForExpression forExpression:
                CompileForExpression(forExpression);
                break;

            case InterpolatedStringExpression interpolatedString:
                foreach (var part in interpolatedString.Parts)
                {
                    switch (part.Expression)
                    {
                        case StringLiteral stringLiteral:
                            EmitConstant(stringLiteral.String, stringLiteral.Location);
                            break;

                        default:
                            EmitExpression(part.Expression);
                            EmitConstant(typeof(object).GetMethod("ToString")!, part.Expression.Location);
                            _currentChunk.Emit(OpCode.Call, part.Expression.Location, 1);
                            break;
                    }
                }

                EmitConstant(
                    typeof(string).GetMethod(nameof(string.Concat), [typeof(object[])])!, interpolatedString.Location);
                _currentChunk.Emit(OpCode.Call, interpolatedString.Location, interpolatedString.Parts.Length);
                break;

            case MatchExpression matchExpression:
            {
                EmitExpression(matchExpression.Expression);
                var expressionType = matchExpression.Expression switch
                {
                    GlobalGetExpression g => g.VariableType,
                    LocalGetExpression g => g.VariableType,
                    CapturedLocalGetExpression g => g.VariableType,
                    UpvalueGetExpression g => g.VariableType,
                    _ => typeof(object)
                };

                var isUnion = typeof(IUnion).IsAssignableFrom(expressionType);
                if (isUnion)
                {
                    // Hopefully Option<T>(Some<T>(..)) should unwrap to just Some<T>(..)
                    _currentChunk.Emit(OpCode.UnwrapUnion, matchExpression.Expression.Location);
                }

                var endLabel = Chunk.NewLabel();
                foreach (var c in matchExpression.Cases)
                {
                    var endOfCase = Chunk.NewLabel();

                    switch (c.Pattern)
                    {
                        case DiscardExpression:
                            break;

                        case VariantExpression variant:
                        {
                            // Duplicate the subject for comparison
                            _currentChunk.Emit(OpCode.Dup, c.Pattern.Location);

                            // Do a type check before equality check
                            EmitConstant(variant.VariantType!, c.Pattern.Location);
                            _currentChunk.Emit(OpCode.Compare, c.Pattern.Location);
                            _currentChunk.EmitJump(OpCode.JumpIfFalse, c.Pattern.Location, endOfCase);

                            if (variant.Deconstructor is not { } deconstructor)
                                break;

                            // Get a the fields of the deconstructor
                            var fields = deconstructor.GetParameters();
                            var expectedIndex = fields.Length;
                            var parts = variant.PositionalPattern?.Parts ?? [];
                            for (var i = 0; i < Math.Min(parts.Length, expectedIndex); i++)
                            {
                                var field = fields[i];
                                var part = parts[i];

                                var location = part.Location;
                                int constIndex = _currentChunk.AddOrGetConstant(field.Name!);
                                _currentChunk.Emit(OpCode.GetField, location, constIndex);

                                switch (part)
                                {
                                    case LocalGetExpression getExpression:
                                    {
                                        _currentChunk.Emit(OpCode.SetLocal, location, getExpression.SlotIndex);
                                        break;
                                    }

                                    case GlobalGetExpression getExpression:
                                    {
                                        _currentChunk.Emit(OpCode.GetField, location, constIndex);
                                        _currentChunk.Emit(OpCode.SetGlobal, location, getExpression.SlotIndex);
                                        break;
                                    }

                                    case StringLiteral e:
                                    {
                                        EmitExpression(e);
                                        _currentChunk.Emit(OpCode.Compare, c.Pattern.Location);
                                        _currentChunk.EmitJump(OpCode.JumpIfFalse, c.Pattern.Location, endOfCase);
                                        break;
                                    }

                                    case IntLiteral e:
                                    {
                                        EmitExpression(e);
                                        _currentChunk.Emit(OpCode.Compare, c.Pattern.Location);
                                        _currentChunk.EmitJump(OpCode.JumpIfFalse, c.Pattern.Location, endOfCase);
                                        break;
                                    }

                                    case DoubleLiteral e:
                                    {
                                        EmitExpression(e);
                                        _currentChunk.Emit(OpCode.Compare, c.Pattern.Location);
                                        _currentChunk.EmitJump(OpCode.JumpIfFalse, c.Pattern.Location, endOfCase);
                                        break;
                                    }

                                    case CharLiteral e:
                                    {
                                        EmitExpression(e);
                                        _currentChunk.Emit(OpCode.Compare, c.Pattern.Location);
                                        _currentChunk.EmitJump(OpCode.JumpIfFalse, c.Pattern.Location, endOfCase);
                                        break;
                                    }
                                }
                            }
                            break;
                        }

                        case ConstantExpression constantExpression:
                        {
                            _currentChunk.Emit(OpCode.Dup, c.Pattern.Location);
                            EmitExpression(constantExpression);
                            _currentChunk.Emit(OpCode.Compare, c.Pattern.Location);
                            _currentChunk.EmitJump(OpCode.JumpIfFalse, c.Pattern.Location, endOfCase);
                            _currentChunk.Emit(OpCode.Pop, c.Pattern.Location);
                            break;
                        }
                    }

                    EmitExpression(c.Body);

                    _currentChunk.EmitJump(OpCode.Jump, c.Location, endLabel);
                    _currentChunk.MarkLabel(endOfCase);
                }

                EmitConstant(Unit.Instance, matchExpression.Location);
                _currentChunk.MarkLabel(endLabel);
                break;
            }

            case BlockBodyExpression blockBody:
                EmitBlockBody(blockBody);
                break;

            case IfExpression ifExpression:
                CompileIfExpression(ifExpression);
                break;

            case ReturnExpression returnExpression:
                if (returnExpression.ReturnValue is not null)
                {
                    EmitExpression(returnExpression.ReturnValue);
                }
                else
                {
                    EmitConstant(Unit.Instance, returnExpression.Location);
                }

                _currentChunk.Emit(OpCode.Return, returnExpression.Location);
                break;

            case ConstantExpression constantExpression:
                EmitExpression(constantExpression.Expression);
                break;

            case FunctionDefinitionExpression funcDef:
                CompileFunctionDefinition(funcDef);
                break;

            case LambdaExpression lambda:
                CompileLambdaExpression(lambda);
                break;

            case TypeDefinitionExpression typeDef:
                CompileTypeDefExpression(typeDef);
                break;

            case TagExpression:
                break;

            default:
                throw new NotSupportedException($"Unhandled AST node in bytecode compiler: {expr.GetType().Name}");
        }
    }

    private void CompileFunctionDefinition(FunctionDefinitionExpression funcDef)
    {
        var funcChunk = CompileFunction(funcDef);

        if (funcDef.CapturedUpvalues is { } upvalues)
        {
            // Nested function that captures — emit as closure (pushes onto stack)
            var uf = new UserFunction(funcDef.Identifier.Name, funcChunk)
            {
                LocalCount = funcDef.LocalCount,
            };
            var funcConstIdx = _currentChunk.AddOrGetConstant(uf);
            EmitMakeClosure(funcConstIdx, upvalues, funcDef.Location);
        }
        else if (program.GlobalFunctions.TryGetValue(funcDef.Identifier.Name, out var f) && f is UserFunction uf)
        {
            // Global function — patch the placeholder
            uf.Chunk = funcChunk;
        }
        else
        {
            // Nested function without captures — push as constant (for local storage)
            var uf2 = new UserFunction(funcDef.Identifier.Name, funcChunk)
            {
                LocalCount = funcDef.LocalCount,
            };
            EmitConstant(uf2, funcDef.Location);
        }
    }

    private void CompileLambdaExpression(LambdaExpression lambda)
    {
        var lambdaChunk = CompileLambdaBody(lambda.Body, lambda.Location);
        var uf = new UserFunction("<lambda>", lambdaChunk)
        {
            LocalCount = lambda.LocalCount,
        };

        if (lambda.CapturedUpvalues is { } upvalues)
        {
            var funcConstIdx = _currentChunk.AddOrGetConstant(uf);
            EmitMakeClosure(funcConstIdx, upvalues, lambda.Location);
        }
        else
        {
            // No captures — just push the function as a constant
            EmitConstant(uf, lambda.Location);
        }
    }

    private void EmitMakeClosure(int funcConstIdx, UpvalueDescriptor[] upvalues, SourceSpan location)
    {
        // MakeClosure format: funcConstIdx, upvalueCount, then pairs of (isLocal, index)
        var operands = new int[2 + upvalues.Length * 2];
        operands[0] = funcConstIdx;
        operands[1] = upvalues.Length;
        for (var i = 0; i < upvalues.Length; i++)
        {
            operands[2 + i * 2] = upvalues[i].IsLocal ? 1 : 0;
            operands[2 + i * 2 + 1] = upvalues[i].Index;
        }

        _currentChunk.Emit(OpCode.MakeClosure, location, operands);
    }

    private void CompileTypeDefExpression(TypeDefinitionExpression typeDef)
    {
        var type = program.Types[typeDef.Identifier.Name];
        var methodTable = program.MethodTable.GetValueOrDefault(type, []);
        foreach (var method in typeDef.Methods)
        {
            var funcDef = method.FunctionDefinition;
            if (methodTable.TryGetValue(method.FunctionDefinition.Identifier.Name, out var f) && f is UserFunction uf)
                uf.Chunk = CompileFunction(funcDef);
        }
    }

    private void EmitFunctionCallExpression(FunctionCallExpression call)
    {
        var argCount = 0;

        switch (call.CachedFunction)
        {
            case ForeignFunction ff:
            {
                if (!ff.MethodInfo.IsStatic || ff.MethodInfo.IsDefined(typeof(ExtensionAttribute), false))
                {
                    if (call.Identifier is SimpleMemberAccessExpression e)
                        EmitExpression(e.Object);
                    else
                        EmitExpression(call.Identifier);

                    argCount++;
                }

                foreach (var arg in call.Arguments)
                {
                    EmitExpression(arg);
                    argCount++;
                }

                EmitConstant(ff.MethodInfo, call.Location);
                break;
            }

            case UserFunction function:
            {
                if (function.Parameters.FirstOrDefault() is { IsSelf: true })
                {
                    switch (call.Identifier)
                    {
                        case IdentifierLiteral i:
                            EmitExpression(i);
                            argCount++;
                            break;

                        case SimpleMemberAccessExpression simpleAccess:
                            EmitExpression(simpleAccess.Object);
                            argCount++;
                            break;
                    }
                }

                foreach (var arg in call.Arguments)
                {
                    EmitExpression(arg);
                    argCount++;
                }

                EmitConstant(function, call.Location);
                break;
            }

            case null:
            {
                // Dynamic call — callee is a closure/function stored in a variable
                foreach (var arg in call.Arguments)
                {
                    EmitExpression(arg);
                    argCount++;
                }

                // Emit the callee expression (loads the closure/function onto stack)
                EmitExpression(call.Identifier);
                break;
            }
        }

        _currentChunk.Emit(OpCode.Call, call.Location, argCount);
        if ((!_context.TryPeek(out var context) || context != ContextItem.TryExpression)
            && (call.ReturnType == typeof(void) || call.ReturnType == typeof(Unit)))
            _currentChunk.Emit(OpCode.Pop, call.Location);
    }

    private void CompileObjectInstantiation(ObjectInstantiationExpression obj)
    {
        if (obj.ObjectType is { } targetType)
        {
            var objIdx = _currentChunk.AddOrGetConstant(targetType);
            _currentChunk.Emit(OpCode.Construct, obj.Location, objIdx);

            foreach (var field in obj.Body.Fields)
            {
                _currentChunk.Emit(OpCode.Dup, SourceSpan.Empty);
                EmitExpression(field.Value ?? field.Identifier);
                var idx = _currentChunk.AddOrGetConstant(field.Identifier.Name);
                _currentChunk.Emit(OpCode.SetField, field.Location, idx);
                _currentChunk.Emit(OpCode.Pop, SourceSpan.Empty);
            }
        }
        else
        {
            throw new InvalidOperationException("Expected global get for type ctor.");
        }
    }

    private void EmitConstant(object value, SourceSpan span)
    {
        var idx = _currentChunk.AddOrGetConstant(value);
        _currentChunk.Emit(OpCode.LoadConst, span, idx);
    }

    private void CompileIfExpression(IfExpression ifExpression)
    {
        EmitExpression(ifExpression.Condition);
        int jumpIfFalseAddress = _currentChunk.Code.Count;
        _currentChunk.Emit(OpCode.JumpIfFalse, ifExpression.Condition.Location, 0);

        EmitExpression(ifExpression.Then);

        if (ifExpression.Else is null)
        {
            PatchJump(jumpIfFalseAddress, _currentChunk.Code.Count);
            return;
        }

        // Skip over the else branch when the then branch was taken.
        int jumpOverElseAddress = _currentChunk.Code.Count;
        _currentChunk.Emit(OpCode.Jump, ifExpression.Location, 0);

        PatchJump(jumpIfFalseAddress, _currentChunk.Code.Count);
        EmitExpression(ifExpression.Else);
        PatchJump(jumpOverElseAddress, _currentChunk.Code.Count);
    }

    private void EmitBlockBody(BlockBodyExpression body)
    {
        foreach (var expression in body.Expressions)
            EmitExpression(expression);
    }

    private void CompileForExpression(ForExpression forExpr)
    {
        if (forExpr.IsCollection)
        {
            CompileCollectionForExpression(forExpr);
            return;
        }

        if (forExpr.Range is not RangeExpression { Left: not null, Right: not null } range)
            throw new InvalidOperationException("For loop requires a bounded range (e.g. 0..10)");

        var (iterSlot, iterSetOp) = forExpr.Identifier switch
        {
            CapturedLocalGetExpression c => (c.SlotIndex, OpCode.SetCapturedLocal),
            LocalGetExpression l => (l.SlotIndex, OpCode.SetLocal),
            GlobalGetExpression g => (g.SlotIndex, OpCode.SetGlobal),
            _ => throw new NotSupportedException()
        };

        // Counter and limit are hidden slots — always same scope kind, never captured.
        var isGlobal = forExpr.Identifier is GlobalGetExpression;
        var getOp = isGlobal ? OpCode.GetGlobal : OpCode.GetLocal;
        var setOp = isGlobal ? OpCode.SetGlobal : OpCode.SetLocal;
        var counterSlot = forExpr.CounterSlot;
        var limitSlot = forExpr.LimitSlot;

        // Initialise hidden counter = range.Left, limit = range.Right
        EmitExpression(range.Left);
        _currentChunk.Emit(setOp, forExpr.Location, counterSlot);
        EmitExpression(range.Right);
        _currentChunk.Emit(setOp, forExpr.Location, limitSlot);

        // Loop condition: counter < limit
        int loopStart = _currentChunk.Code.Count;
        _currentChunk.Emit(getOp, forExpr.Location, counterSlot);
        _currentChunk.Emit(getOp, forExpr.Location, limitSlot);
        _currentChunk.Emit(OpCode.Lt, forExpr.Location);

        int jumpAddress = _currentChunk.Code.Count;
        _currentChunk.Emit(OpCode.JumpIfFalse, forExpr.Location, 0);

        // Per-iteration: copy counter → user-visible iterator (fresh binding)
        _currentChunk.Emit(getOp, forExpr.Location, counterSlot);
        _currentChunk.Emit(iterSetOp, forExpr.Location, iterSlot);

        // Body
        EmitBlockBody(forExpr.Body);

        // Increment hidden counter
        _currentChunk.Emit(getOp, forExpr.Location, counterSlot);
        _currentChunk.Emit(OpCode.Inc, forExpr.Location);
        _currentChunk.Emit(setOp, forExpr.Location, counterSlot);

        _currentChunk.Emit(OpCode.Loop, forExpr.Location, loopStart);
        var endOfLoop = _currentChunk.Code.Count;

        PatchJump(jumpAddress, endOfLoop);
    }

    private void CompileCollectionForExpression(ForExpression forExpr)
    {
        var (iterSlot, iterSetOp) = forExpr.Identifier switch
        {
            CapturedLocalGetExpression c => (c.SlotIndex, OpCode.SetCapturedLocal),
            LocalGetExpression l => (l.SlotIndex, OpCode.SetLocal),
            GlobalGetExpression g => (g.SlotIndex, OpCode.SetGlobal),
            _ => throw new NotSupportedException()
        };

        var isGlobal = forExpr.Identifier is GlobalGetExpression;
        var getOp = isGlobal ? OpCode.GetGlobal : OpCode.GetLocal;
        var setOp = isGlobal ? OpCode.SetGlobal : OpCode.SetLocal;
        var counterSlot = forExpr.CounterSlot;
        var limitSlot = forExpr.LimitSlot;
        var collectionSlot = forExpr.CollectionSlot;

        // Init: collection = expr, counter = 0, limit = collection.Length
        EmitExpression(forExpr.Range);
        _currentChunk.Emit(setOp, forExpr.Location, collectionSlot);

        EmitConstant(0, forExpr.Location);
        _currentChunk.Emit(setOp, forExpr.Location, counterSlot);

        _currentChunk.Emit(getOp, forExpr.Location, collectionSlot);
        int lengthIdx = _currentChunk.AddOrGetConstant("Length");
        _currentChunk.Emit(OpCode.GetField, forExpr.Location, lengthIdx);
        _currentChunk.Emit(setOp, forExpr.Location, limitSlot);

        // Loop condition: counter < limit
        int loopStart = _currentChunk.Code.Count;
        _currentChunk.Emit(getOp, forExpr.Location, counterSlot);
        _currentChunk.Emit(getOp, forExpr.Location, limitSlot);
        _currentChunk.Emit(OpCode.Lt, forExpr.Location);

        int jumpAddress = _currentChunk.Code.Count;
        _currentChunk.Emit(OpCode.JumpIfFalse, forExpr.Location, 0);

        // Per-iteration: item = collection[counter]
        _currentChunk.Emit(getOp, forExpr.Location, collectionSlot);
        _currentChunk.Emit(getOp, forExpr.Location, counterSlot);
        _currentChunk.Emit(OpCode.IndexGetDirect, forExpr.Location);
        _currentChunk.Emit(iterSetOp, forExpr.Location, iterSlot);

        // Body
        EmitBlockBody(forExpr.Body);

        // Increment counter
        _currentChunk.Emit(getOp, forExpr.Location, counterSlot);
        _currentChunk.Emit(OpCode.Inc, forExpr.Location);
        _currentChunk.Emit(setOp, forExpr.Location, counterSlot);

        _currentChunk.Emit(OpCode.Loop, forExpr.Location, loopStart);
        var endOfLoop = _currentChunk.Code.Count;

        PatchJump(jumpAddress, endOfLoop);
    }

    private void CompileWhileExpression(WhileExpression whileExpr)
    {
        int loopStart = _currentChunk.Code.Count;
        EmitExpression(whileExpr.Condition);

        int jumpAddress = _currentChunk.Code.Count;
        _currentChunk.Emit(OpCode.JumpIfFalse, whileExpr.Condition.Location, 0);

        EmitExpression(whileExpr.Body);

        _currentChunk.Emit(OpCode.Loop, whileExpr.Location, loopStart);
        var endOfLoop = _currentChunk.Code.Count;

        PatchJump(jumpAddress, endOfLoop);
    }

    private void PatchJump(int jumpAddress, int endOfLoopAddress)
    {
        byte[] bytes = BitConverter.GetBytes(endOfLoopAddress);
        for (int i = 0; i < 4; i++)
            _currentChunk.Code[jumpAddress + i + 1] = bytes[i];
    }
}
