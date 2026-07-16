using Jitzu.Core;
using Jitzu.Core.Language;
using Jitzu.Core.Runtime;
using Jitzu.Core.Runtime.Compilation;
using Jitzu.Shell.Core.Completions;

namespace Jitzu.Shell.Core;

/// <summary>
/// Maintains persistent state across REPL iterations.
/// This is the KEY to stateful execution - we DON'T recreate RuntimeProgram each time.
/// </summary>
public class ShellSession
{
    // Persistent compilation state
    private ProgramStack _stack = null!;

    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private RuntimeProgram? _program;

    // Runtime-aware commands pay the initialization cost on first use. Keeping
    // this lazy lets the prompt and native builtins start without scanning every
    // loaded BCL assembly.
    public RuntimeProgram Program => EnsureInitializedAsync().GetAwaiter().GetResult();

    public static Task<ShellSession> CreateAsync() => Task.FromResult(new ShellSession());

    private async Task<RuntimeProgram> EnsureInitializedAsync()
    {
        if (_program is not null)
            return _program;

        await _initializationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_program is null)
                await Initialize().ConfigureAwait(false);
            return _program!;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task Initialize()
    {
        // Initialize with built-in types and functions
        _program = await InitializeBaseProgram();

        // Create persistent stack and initialize with global functions and types
        _stack = new ProgramStack();
        _stack.SetGlobal(0, Value.FromRef(Array.Empty<string>())); // args slot

        InitializeGlobalStack();
    }

    private static async Task<RuntimeProgram> InitializeBaseProgram()
    {
        // Use ProgramBuilder.Build() with empty AST
        // This gives us all built-in types and global functions
        var emptyScript = ScriptExpression.Empty;
        return await ProgramBuilder.Build(emptyScript);
    }

    private void InitializeGlobalStack()
    {
        // Initialize global slots with types and functions
        var program = _program!;
        foreach (var (name, index) in program.GlobalSlotMap)
        {
            if (program.GlobalFunctions.TryGetValue(name, out var function))
                _stack.SetGlobal(index, Value.FromRef(function));
            else if (program.Types.TryGetValue(name, out var type)
                     || program.SimpleTypeCache.TryGetValue(name, out type))
                _stack.SetGlobal(index, Value.FromRef(type));
        }
    }

    /// <summary>
    /// Execute a single line or block incrementally.
    /// Returns: (success: bool, result: object?, error: Exception?)
    /// </summary>
    public async Task<ExecutionResult> ExecuteAsync(string input)
    {
        try
        {
            var program = await EnsureInitializedAsync().ConfigureAwait(false);
            // Parse the new input
            var newAst = Parser.Parse("<repl>", input);
            var scriptExpression = new ScriptExpression
            {
                Body = newAst,
                Location = SourceSpan.Empty
            };

            _program = program = await ProgramBuilder.PatchProgram(program, scriptExpression);

            // Run semantic analysis (type resolution, function registration)
            var analyser = new SemanticAnalyser(program);
            scriptExpression = analyser.AnalyseScript(scriptExpression);

            // Update global stack with new program state (types, functions, updated SlotMap)
            InitializeGlobalStack();

            // Extract and compile only the new expressions
            var script = new ByteCodeCompiler(program).Compile(scriptExpression.Body);

            // Execute using persistent stack to maintain global variables
            var interpreter = new ByteCodeInterpreter(program, script, _stack, false);
            var result = interpreter.Evaluate();

            return new ExecutionResult(true, result, null);
        }
        catch (Exception ex)
        {
            return new ExecutionResult(false, null, ex);
        }
    }

    public async Task ResetAsync()
    {
        await _initializationLock.WaitAsync().ConfigureAwait(false);
        try { await Initialize().ConfigureAwait(false); }
        finally { _initializationLock.Release(); }
    }

    public List<Completion> GetCompletionSuggestions(string partial)
    {
        // Return variable names, function names, type names
        var suggestions = new List<Completion>();

        // Global functions
        suggestions.AddRange(
            Program.GlobalFunctions
                .Where(f => f.Key.StartsWith(partial))
                .Select(f => new RuntimeFunctionCompletion(f.Key)));

        // Types (simple names)
        suggestions.AddRange(
            Program.SimpleTypeCache
                .Where(f => f.Key.StartsWith(partial))
                .Select(f => new RuntimeFunctionCompletion(f.Key)));

        // Keywords
        var keywords = new[] { "let", "fun", "type", "if", "else", "match", "return", "true", "false", "pub" };
        suggestions.AddRange(
            keywords
                .Where(k => k.StartsWith(partial))
                .Select(k => new KeywordCompletion(k)));

        return suggestions;
    }

    
}

public record ExecutionResult(bool Success, object? Result, Exception? Error);
