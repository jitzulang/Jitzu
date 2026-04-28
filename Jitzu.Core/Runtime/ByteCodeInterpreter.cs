using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jitzu.Core.Logging;
using Jitzu.Core.Types;

namespace Jitzu.Core.Runtime;

public ref struct ByteCodeInterpreter
{
    private readonly ProgramStack _programStack;
    private int _ip;
    private CallFrame[] _frames = new CallFrame[64];
    private int _frameTop = -1;
    private UserFunction _currentFunction;
    private Closure? _currentClosure;
    private readonly bool _dumpStack;
    private Span<byte> _instructions;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PushFrame(in CallFrame frame)
    {
        if (_frameTop + 1 >= _frames.Length)
            Array.Resize(ref _frames, _frames.Length * 2);
        _frames[++_frameTop] = frame;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private CallFrame PopFrame()
    {
        var frame = _frames[_frameTop];
        _frameTop--;
        return frame;
    }

    public ByteCodeInterpreter(RuntimeProgram program, UserFunction script, string[] args, bool dumpStack)
    {
        _currentFunction = script;
        _dumpStack = dumpStack;

        _programStack = new ProgramStack();
        _programStack.SetGlobal(0, Value.FromRef(args));

        foreach (var (name, index) in program.GlobalSlotMap)
        {
            if (program.GlobalFunctions.TryGetValue(name, out var function))
                _programStack.SetGlobal(index, Value.FromRef(function));
            else if (program.Types.TryGetValue(name, out var type)
                     || program.SimpleTypeCache.TryGetValue(name, out type))
                _programStack.SetGlobal(index, Value.FromRef(type));
        }
    }

    // Constructor that accepts an existing ProgramStack (for REPL with persistent globals)
    public ByteCodeInterpreter(RuntimeProgram program, UserFunction script, ProgramStack existingStack, bool dumpStack)
    {
        _currentFunction = script;
        _dumpStack = dumpStack;
        _programStack = existingStack;

        // Update the stack with any new global functions or types from the program
        foreach (var (name, index) in program.GlobalSlotMap)
        {
            if (program.GlobalFunctions.TryGetValue(name, out var function))
                _programStack.SetGlobal(index, Value.FromRef(function));
            else if (program.Types.TryGetValue(name, out var type)
                     || program.SimpleTypeCache.TryGetValue(name, out type))
                _programStack.SetGlobal(index, Value.FromRef(type));
        }
    }

    public object Evaluate()
    {
        _instructions = CollectionsMarshal.AsSpan(_currentFunction.Chunk.Code);
        var lastIp = _ip;
        try
        {
            while (_ip < _instructions.Length)
            {
                DumpStack();

                lastIp = _ip; // Needed for keep a reference to the right DebugSpan
                var op = _instructions[_ip++];
                switch ((OpCode)op)
                {
                    case OpCode.Dup:
                    {
                        Dup();
                        break;
                    }

                    case OpCode.LoadConst:
                    {
                        LoadConst();
                        break;
                    }

                    case OpCode.SetLocal:
                    {
                        SetLocal();
                        break;
                    }

                    case OpCode.GetLocal:
                    {
                        GetLocal();
                        break;
                    }

                    case OpCode.SetGlobal:
                    {
                        SetGlobal();
                        break;
                    }

                    case OpCode.GetGlobal:
                    {
                        GetGlobal();
                        break;
                    }

                    case OpCode.Construct:
                    {
                        Construct();
                        break;
                    }

                    case OpCode.GetField:
                    {
                        GetProperty();
                        break;
                    }

                    case OpCode.SetField:
                    {
                        SetField();
                        break;
                    }

                    case OpCode.Call:
                    {
                        Call();
                        break;
                    }

                    case OpCode.Return:
                    {
                        if (Return(out var returnValue))
                            return returnValue;
                        break;
                    }

                    case OpCode.Swap:
                        Swap();
                        break;

                    case OpCode.Pop:
                        _programStack.Pop();
                        break;

                    case OpCode.JumpIfFalse:
                        JumpIfFalse();

                        break;

                    case OpCode.Jump:
                        Jump();
                        break;

                    case OpCode.Loop:
                        Loop();
                        break;

                    case OpCode.Inc:
                    {
                        Inc();
                        break;
                    }

                    case OpCode.Dec:
                    {
                        Dec();
                        break;
                    }

                    case OpCode.Add:
                    {
                        Add();
                        break;
                    }

                    case OpCode.Sub:
                    {
                        Sub();
                        break;
                    }

                    case OpCode.Mul:
                    {
                        Mul();
                        break;
                    }

                    case OpCode.Div:
                    {
                        Div();
                        break;
                    }

                    case OpCode.Mod:
                    {
                        Mod();
                        break;
                    }

                    case OpCode.Lt:
                    {
                        Lt();
                        break;
                    }

                    case OpCode.Lte:
                    {
                        Lte();
                        break;
                    }

                    case OpCode.Gt:
                    {
                        Gt();
                        break;
                    }

                    case OpCode.Gte:
                    {
                        Gte();
                        break;
                    }

                    case OpCode.TryUnwrap:
                    {
                        TryUnwrap();
                        break;
                    }

                    case OpCode.IndexGet:
                    {
                        EvaluateIndexGet();
                        break;
                    }

                    case OpCode.IndexSet:
                    {
                        EvaluateIndexSet();
                        break;
                    }

                    case OpCode.NewArray:
                        NewArray();
                        break;

                    case OpCode.Eq:
                    {
                        Eq();
                        break;
                    }

                    case OpCode.Not:
                    {
                        var v = _programStack.Pop();
                        if (v.Kind != ValueKind.Bool)
                            throw new InvalidOperationException(
                                $"Operator '!' requires a Bool operand, got {v.Kind}.");
                        _programStack.Push(Value.FromBool(!v.B));
                        break;
                    }

                    case OpCode.Compare:
                    {
                        EvaluateCompare();
                        break;
                    }

                    case OpCode.NewString:
                    {
                        NewString();
                        break;
                    }

                    case OpCode.NewInt:
                    {
                        NewInt();
                        break;
                    }

                    case OpCode.NewDouble:
                    {
                        NewDouble();
                        break;
                    }

                    case OpCode.UnwrapUnion:
                    {
                        UnwrapUnion();
                        break;
                    }

                    case OpCode.BitwiseOr:
                    {
                        BitwiseOr();
                        break;
                    }

                    case OpCode.GetUpvalue:
                    {
                        ExecuteGetUpvalue();
                        break;
                    }

                    case OpCode.SetUpvalue:
                    {
                        ExecuteSetUpvalue();
                        break;
                    }

                    case OpCode.GetCapturedLocal:
                    {
                        ExecuteGetCapturedLocal();
                        break;
                    }

                    case OpCode.SetCapturedLocal:
                    {
                        ExecuteSetCapturedLocal();
                        break;
                    }

                    case OpCode.MakeClosure:
                    {
                        ExecuteMakeClosure();
                        break;
                    }

                    case OpCode.IndexGetDirect:
                    {
                        IndexGetDirect();
                        break;
                    }

                    case OpCode.NewList:
                    {
                        NewList();
                        break;
                    }

                    case var other:
                        throw new ArgumentOutOfRangeException(other.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error occured at IP: {0}", _ip);
            Console.WriteLine("Stack:");
            if (_programStack.StackPointer > -1)
            {
                foreach (var item in _programStack.Stack[.._programStack.StackPointer])
                    Console.WriteLine($"- {item}");
            }

            Console.WriteLine($"FrameTop={_frameTop}");
            for (var i = 0; i <= _frameTop; i++)
                Console.WriteLine($"[{i}] {_frames[i].UserFunction.ToString()} IP={_frames[i].IP}");

            var debugSpan = _currentFunction.Chunk.DebugSpans[lastIp];
            throw new JitzuException(debugSpan, ex.Message);
        }

        return null!;
    }

    [Conditional("DEBUG")]
    private void DumpStack()
    {
        if (!_dumpStack)
            return;

        var sb = ObjectPools.StringBuilderPool.Rent();
        try
        {
            for (var i = _programStack.StackPointer; i >= 0; i--)
            {
                if (i > 0) sb.Append(' ');
                sb.Append($"[{ValueFormatter.Format(_programStack.Peek(i))}]");
            }

            Console.WriteLine($"{_ip:000}: {sb}");
        }
        finally
        {
            ObjectPools.StringBuilderPool.Return(sb);
        }
    }

    private void Dup()
    {
        _programStack.Push(_programStack.Peek());
    }

    private void LoadConst()
    {
        int constIdx = ReadInt();
        _programStack.Push(_currentFunction.Chunk.Constants[constIdx]);
    }

    private void SetLocal()
    {
        var localIdx = ReadInt();
        _programStack.SetLocal(localIdx, _programStack.Pop());
    }

    private void GetLocal()
    {
        var localIdx = ReadInt();
        var local = _programStack.GetLocal(localIdx);
        _programStack.Push(local);
    }

    private void SetGlobal()
    {
        var globalIdx = ReadInt();
        _programStack.SetGlobal(globalIdx, _programStack.Pop());
    }

    private void GetGlobal()
    {
        var globalIdx = ReadInt();
        var global = _programStack.GetGlobal(globalIdx);
        _programStack.Push(global);
    }

    private void ExecuteGetUpvalue()
    {
        var idx = ReadInt();
        _programStack.Push(_currentClosure!.Upvalues[idx].Value);
    }

    private void ExecuteSetUpvalue()
    {
        var idx = ReadInt();
        _currentClosure!.Upvalues[idx].Value = _programStack.Pop();
    }

    private void ExecuteGetCapturedLocal()
    {
        var slot = ReadInt();
        var localVal = _programStack.GetLocal(slot);
        if (localVal.Ref is UpvalueCell cell)
            _programStack.Push(cell.Value);
        else
            _programStack.Push(localVal);
    }

    private void ExecuteSetCapturedLocal()
    {
        var slot = ReadInt();
        var value = _programStack.Pop();
        var localVal = _programStack.GetLocal(slot);
        if (localVal.Ref is UpvalueCell cell)
        {
            cell.Value = value;
        }
        else
        {
            // First write — create the cell
            var newCell = new UpvalueCell { Value = value };
            _programStack.SetLocal(slot, Value.FromRef(newCell));
        }
    }

    private void ExecuteMakeClosure()
    {
        var funcConstIdx = ReadInt();
        var upvalueCount = ReadInt();
        var function = (UserFunction)_currentFunction.Chunk.Constants[funcConstIdx];
        var upvalues = new UpvalueCell[upvalueCount];

        for (var i = 0; i < upvalueCount; i++)
        {
            var isLocal = ReadInt() == 1;
            var index = ReadInt();

            if (isLocal)
            {
                // Capture from enclosing function's local slot
                var localVal = _programStack.GetLocal(index);
                if (localVal.Ref is UpvalueCell existingCell)
                {
                    upvalues[i] = existingCell;
                }
                else
                {
                    // Create a new cell and replace the local slot
                    var cell = new UpvalueCell { Value = localVal };
                    _programStack.SetLocal(index, Value.FromRef(cell));
                    upvalues[i] = cell;
                }
            }
            else
            {
                // Share from current closure's upvalues (transitive capture)
                upvalues[i] = _currentClosure!.Upvalues[index];
            }
        }

        _programStack.Push(Value.FromRef(new Closure(function, upvalues)));
    }

    private void Construct()
    {
        var globalIdx = ReadInt();
        var constructType = _currentFunction.Chunk.Constants[globalIdx] as Type
                            ?? throw new Exception("Construct must be of type 'Type'");
        var instance = Activator.CreateInstance(constructType)!;
        _programStack.Push(instance);
    }

    private void GetProperty()
    {
        var subject = _programStack.Pop().AsObject();
        var globalIdx = ReadInt();
        var fieldName = (string)_currentFunction.Chunk.Constants[globalIdx];
        var type = subject as Type ?? subject.GetType();
        var field = type.GetProperty(fieldName);
        var value = field!.GetValue(subject);
        _programStack.Push(value!);
    }

    private void SetField()
    {
        var value = _programStack.Pop().AsObject();
        var target = _programStack.Pop().AsObject();
        var fieldName = (string)_currentFunction.Chunk.Constants[ReadInt()];
        var targetType = target.GetType();
        if (targetType.GetProperty(fieldName) is { } field)
            field.SetValue(target, value);

        _programStack.Push(value);
    }

    private void Call()
    {
        var func = _programStack.Pop();
        int argCount = ReadInt();
        int returnIp = _ip; // Save IP after reading args but before processing call

        var args = new Value[argCount];
        for (var idx = argCount - 1; idx >= 0; idx--)
            args[idx] = _programStack.Pop();

        switch (func.Ref)
        {
            case ForeignFunction foreignFunction:
            {
                var result = foreignFunction.Invoke(args);
                _programStack.Push(result ?? Unit.Instance);
                break;
            }

            case MethodInfo methodInfo:
            {
                var result = ForeignFunction.InvokeMethodInfo(methodInfo, args);
                _programStack.Push(result ?? Unit.Instance);
                break;
            }

            case Closure closure:
            {
                // Save current frame state (including current closure)
                var frame = new CallFrame(
                    _currentFunction, returnIp, _programStack.StackPointer, _programStack.FrameBase, _currentClosure);
                PushFrame(frame);

                // Set up new function
                _currentFunction = closure.Function;
                _currentClosure = closure;
                _ip = 0;
                _instructions = CollectionsMarshal.AsSpan(closure.Function.Chunk.Code);

                // Create new frame
                _programStack.PushFrame(closure.Function.LocalCount);

                for (int j = 0; j < args.Length; j++)
                    _programStack.SetLocal(j, args[j]);

                break;
            }

            case UserFunction uf:
            {
                // Save current frame state
                var frame = new CallFrame(
                    _currentFunction, returnIp, _programStack.StackPointer, _programStack.FrameBase, _currentClosure);
                PushFrame(frame);

                // Set up new function
                _currentFunction = uf;
                _currentClosure = null;
                _ip = 0;
                _instructions = CollectionsMarshal.AsSpan(uf.Chunk.Code);

                // Create new frame
                _programStack.PushFrame(uf.LocalCount);

                for (int j = 0; j < args.Length; j++)
                    _programStack.SetLocal(j, args[j]);

                break;
            }

            case var _:
                throw new Exception($"Unhandled function type: {ValueFormatter.Format(func)}");
        }
    }

    private bool Return(out Value returnValue)
    {
        if (_programStack.StackPointer is -1)
        {
            returnValue = default;
            return false;
        }

        returnValue = _programStack.Pop();

        if (_frameTop < 0)
            return true;

        var caller = PopFrame();

        // Restore caller frame
        _programStack.SetPointer(caller.StackPointer);
        _programStack.SetFrameBase(caller.FrameBase);
        _currentFunction = caller.UserFunction;
        _currentClosure = caller.Closure;
        _ip = caller.IP;
        _instructions = CollectionsMarshal.AsSpan(_currentFunction.Chunk.Code);

        _programStack.Push(returnValue);
        return false;
    }

    private void Swap()
    {
        var pop1 = _programStack.Pop();
        var pop2 = _programStack.Pop();
        _programStack.Push(pop1);
        _programStack.Push(pop2);
    }

    private void JumpIfFalse()
    {
        int addr = ReadInt();
        var cond = _programStack.Pop();
        if (!IsTrue(cond))
        {
            _ip = addr;
        }
    }

    private void Jump()
    {
        _ip = ReadInt();
    }

    private void Loop()
    {
        int target = ReadInt();
        _ip = target;
    }

    private void Inc()
    {
        switch (_programStack.Peek())
        {
            case { Kind: ValueKind.Int } i:
                _programStack.Swap((int)i.I32 + 1);
                break;

            case { Kind: ValueKind.Double } i:
                _programStack.Swap((double)i.F64 + 1);
                break;
        }
    }

    private void Dec()
    {
        switch (_programStack.Peek())
        {
            case { Kind: ValueKind.Int } i:
                _programStack.Swap((int)i.I32 - 1);
                break;

            case { Kind: ValueKind.Double } i:
                _programStack.Swap((double)i.F64 - 1);
                break;
        }
    }

    private void Add()
    {
        var right = _programStack.Pop();
        var left = _programStack.Pop();
        var res = BinaryExpressionEvaluator.Add(left, right);
        _programStack.Push(res);
    }

    private void Sub()
    {
        var right = _programStack.Pop();
        var left = _programStack.Pop();
        var res = BinaryExpressionEvaluator.Sub(left, right);
        _programStack.Push(res);
    }

    private void Mul()
    {
        var right = _programStack.Pop();
        var left = _programStack.Pop();
        var res = BinaryExpressionEvaluator.Mul(left, right);
        _programStack.Push(res);
    }

    private void Div()
    {
        var right = _programStack.Pop();
        var left = _programStack.Pop();
        var res = BinaryExpressionEvaluator.Div(left, right);
        _programStack.Push(res);
    }

    private void Mod()
    {
        var right = _programStack.Pop();
        var left = _programStack.Pop();
        var res = BinaryExpressionEvaluator.Mod(left, right);
        _programStack.Push(res);
    }

    private void Lt()
    {
        var right = _programStack.Pop();
        var left = _programStack.Pop();
        var res = BinaryExpressionEvaluator.LessThan(left, right);
        _programStack.Push(res);
    }

    private void Lte()
    {
        var right = _programStack.Pop();
        var left = _programStack.Pop();
        var res = BinaryExpressionEvaluator.LessThanOrEqual(left, right);
        _programStack.Push(res);
    }

    private void Gt()
    {
        var right = _programStack.Pop();
        var left = _programStack.Pop();
        var res = BinaryExpressionEvaluator.GreaterThan(left, right);
        _programStack.Push(res);
    }

    private void Gte()
    {
        var right = _programStack.Pop();
        var left = _programStack.Pop();
        var res = BinaryExpressionEvaluator.GreaterThanOrEqual(left, right);
        _programStack.Push(res);
    }

    private void TryUnwrap()
    {
        switch (_programStack.Peek().Ref)
        {
            case ICanUnwrap unwrappable:
                _programStack.Swap(unwrappable.Unwrap());
                break;
        }
    }

    private void NewArray()
    {
        var type = _programStack.Pop().AsObject() as Type ?? throw new Exception("Type must be a type");
        var size = _programStack.Pop() is { Kind: ValueKind.Int, I32: var i }
            ? i
            : throw new Exception("Size must be an integer");
        _programStack.Push(Array.CreateInstance(type, size));
    }

    private void Eq()
    {
        var right = _programStack.Pop();
        var left = _programStack.Pop();
        _programStack.Push(BinaryExpressionEvaluator.Equal(left, right));
    }

    private void NewString()
    {
        var length = ReadInt();
        _programStack.Push(new string(' ', length));
    }

    private void NewInt()
    {
        var value = ReadInt();
        _programStack.Push(value);
    }

    private void NewDouble()
    {
        var value = (double)ReadInt();
        _programStack.Push(value);
    }

    private void UnwrapUnion()
    {
        var union = (IUnion)_programStack.Pop().AsObject();
        _programStack.Push(union.Value!);
    }

    private void BitwiseOr()
    {
        var right = _programStack.Pop();
        var left = _programStack.Pop();
        _programStack.Push(BinaryExpressionEvaluator.BitwiseOr(left, right));
    }

    private void IndexGetDirect()
    {
        var idx = _programStack.Pop();
        var subject = _programStack.Pop().Ref;
        switch (subject)
        {
            case Value[] arr:
                _programStack.Push(arr[idx.I32]);
                break;
            case IList list:
                _programStack.Push(list[idx.I32]!);
                break;
            case string str:
                _programStack.Push(str[idx.I32].ToString());
                break;
            default:
                throw new Exception($"IndexGetDirect: unsupported type {subject.GetType().Name}");
        }
    }

    private void NewList()
    {
        var count = ReadInt();
        var items = new Value[count];
        for (var i = count - 1; i >= 0; i--)
            items[i] = _programStack.Pop();
        _programStack.Push(items);
    }

    // TODO: Review all conditions
    private void EvaluateCompare()
    {
        var scrutinee = _programStack.Pop();
        var subject = _programStack.Pop();

        if (scrutinee.AsObject() is Type comparisonType)
        {
            var subjectType = subject.GetType();

            if (typeof(IUnion).IsAssignableFrom(subjectType))
            {
                var constructors = subjectType.GetConstructors();
                subjectType = ((IUnion)subject.AsObject()).Value!.GetType()!;

                foreach (var constructor in constructors)
                {
                    var firstType = constructor.GetParameters()[0].ParameterType;
                    if (firstType != subjectType && !firstType.IsAssignableFrom(subjectType))
                        continue;

                    _programStack.Push(true);
                    return;
                }
            }

            var isSame = comparisonType == subjectType || comparisonType.IsAssignableFrom(subjectType);
            _programStack.Push(isSame);
            return;
        }

        _programStack.Push(scrutinee.Equals(subject));
    }

    private void EvaluateIndexGet()
    {
        var idx = _programStack.Pop();
        var subject = _programStack.Peek().Ref;
        try
        {
            switch (subject)
            {
                case Value[] arr when idx.Kind == ValueKind.Int:
                {
                    var item = arr[idx.I32].AsObject();
                    var instance = MakeSome(item);
                    _programStack.Push(instance);
                    return;
                }

                case IList list:
                    switch (idx.Kind)
                    {
                        case ValueKind.Int:
                        {
                            var item = list[idx.I32]!;
                            var instance = MakeSome(item);
                            _programStack.Push(instance);
                            return;
                        }
                    }

                    break;

                case var _ when GetIndexer(subject.GetType(), idx.GetType()) is { } indexer:
                {
                    var getter = indexer.GetGetMethod() ?? throw new Exception("Indexer is write-only");
                    var item = getter.Invoke(subject, [idx]) ?? Unit.Instance;
                    var instance = MakeSome(item);

                    _programStack.Push(instance);
                    break;
                }

                case var _:
                    throw new Exception($"Object {subject.GetType().Name} does not have an indexer");
            }
        }
        catch (IndexOutOfRangeException)
        {
            var subjectType = subject.GetType();
            var elementType = subjectType.GetElementType() ?? subjectType;
            _programStack.Push(MakeNone(elementType));
        }
    }

    private void EvaluateIndexSet()
    {
        var idx = _programStack.Pop();
        var value = _programStack.Pop();
        try
        {
            var collection = _programStack.Peek().Ref;
            switch (collection)
            {
                case Value[] arr when idx.Kind == ValueKind.Int:
                    arr[idx.I32] = value;
                    return;

                case IList list:
                    switch (idx.Kind)
                    {
                        case ValueKind.Int:
                            list[idx.I32] = value.AsObject();
                            return;
                    }

                    break;

                case var subject when GetIndexer(subject.GetType(), idx.GetType()) is { } indexer:
                    var setter = indexer.GetSetMethod() ?? throw new Exception("Indexer is read only");
                    var item = setter.Invoke(subject, [idx]) ?? Unit.Instance;
                    _programStack.Push(item);
                    break;

                case var other:
                    throw new Exception($"Object {other.GetType().Name} does not have an indexer");
            }
        }
        catch (Exception ex)
        {
            _programStack.Push(MakeErr(ex.Message));
        }
    }

    // TODO: Handle missing indexer
    private static PropertyInfo? GetIndexer(Type subjectType, Type indexerType)
    {
        return subjectType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
    }

    private static bool IsTrue(Value cond)
    {
        return cond.Kind switch
        {
            ValueKind.Bool => cond.B,
            ValueKind.Int or ValueKind.Double => true,
            ValueKind.Ref when cond.Ref is ITruthy t => t.IsTruthy,
            ValueKind.Null => false,
            _ => throw new Exception($"How do I get a truthy value from {cond.GetType().Name}")
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private int ReadInt()
    {
        int localIdx = Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_instructions), _ip));
        _ip += 4;
        return localIdx;
    }

    private static object MakeSome(object item)
    {
        var indexerType = item.GetType();
        var someType = typeof(Some<>).MakeGenericType(indexerType);
        var some = Activator.CreateInstance(someType, item)!;

        var optionType = typeof(Option<>).MakeGenericType(indexerType);
        some = Activator.CreateInstance(optionType, some)!;

        return some;
    }

    private static object MakeNone(Type type)
    {
        var optionType = typeof(Option<>).MakeGenericType(type);
        return Activator.CreateInstance(optionType, None.Instance)!;
    }

    private static object MakeOk(object item)
    {
        var indexerType = item.GetType();
        var okType = typeof(Ok<>).MakeGenericType(indexerType);
        var instance = Activator.CreateInstance(okType, item)!;

        var resultType = typeof(Result<,>).MakeGenericType(okType);
        instance = Activator.CreateInstance(resultType)!;

        return instance;
    }

    private static object MakeErr(object item)
    {
        var indexerType = item.GetType();
        var errType = typeof(Err<>).MakeGenericType(indexerType);
        var instance = Activator.CreateInstance(errType, item)!;

        var resultType = typeof(Result<,>).MakeGenericType(errType);
        instance = Activator.CreateInstance(resultType)!;

        return instance;
    }
}
