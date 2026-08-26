using Jitzu.Core;
using Jitzu.Core.Language;
using Jitzu.Core.Runtime.Compilation;
using Jitzu.Shell.Core;
using Shouldly;

namespace Jitzu.Tests;

public class TypeResolutionCacheTests
{
    private static ScriptExpression ParseScript(string source) => new()
    {
        Body = string.IsNullOrWhiteSpace(source)
            ? []
            : Parser.Parse("<type-cache-test>", source),
        Location = SourceSpan.Empty,
    };

    [Test]
    public async Task PatchWithoutTypes_ReusesResolutionCaches()
    {
        var program = await ProgramBuilder.Build(ScriptExpression.Empty);
        var simpleTypeCache = program.SimpleTypeCache;
        var typeNameConflicts = program.TypeNameConflicts;

        await ProgramBuilder.PatchProgram(program, ParseScript(string.Empty));

        ReferenceEquals(simpleTypeCache, program.SimpleTypeCache).ShouldBeTrue();
        ReferenceEquals(typeNameConflicts, program.TypeNameConflicts).ShouldBeTrue();
        program.SimpleTypeCache["Path"].ShouldBe(typeof(Path));
    }

    [Test]
    public async Task AddingUserType_UpdatesCacheWithoutRebuildingBclEntries()
    {
        var program = await ProgramBuilder.Build(ScriptExpression.Empty);
        var simpleTypeCache = program.SimpleTypeCache;
        var pathType = simpleTypeCache["Path"];

        var script = ParseScript("type ReplWidget { pub value: Int }");
        await ProgramBuilder.PatchProgram(program, script);

        ReferenceEquals(simpleTypeCache, program.SimpleTypeCache).ShouldBeTrue();
        program.SimpleTypeCache["Path"].ShouldBe(pathType);
        var createdType = script.Body[0].ShouldBeOfType<TypeDefinitionExpression>().Descriptor!.CreatedType;
        program.SimpleTypeCache["ReplWidget"].ShouldBe(createdType);
        program.TypeNameConflicts.ContainsKey("ReplWidget").ShouldBeFalse();
    }

    [Test]
    public async Task DirectTypeDictionaryAddition_IsPickedUpOnNextPatch()
    {
        var program = await ProgramBuilder.Build(ScriptExpression.Empty);
        program.Types.Add("DirectlyAddedType", typeof(Uri));

        await ProgramBuilder.PatchProgram(program, ParseScript(string.Empty));

        program.SimpleTypeCache["DirectlyAddedType"].ShouldBe(typeof(Uri));
    }

    [Test]
    public async Task ReplacingExistingSimpleName_PreservesConflictAcrossIncrementalPatches()
    {
        var program = await ProgramBuilder.Build(ScriptExpression.Empty);
        var simpleTypeCache = program.SimpleTypeCache;
        var typeNameConflicts = program.TypeNameConflicts;

        // BaseTypes registers the simple alias Path and the BCL walk registers
        // System.IO.Path. Defining a user Path replaces the alias with a distinct
        // CLR type, so the name must become ambiguous.
        var definition = ParseScript("type Path { pub value: Int }");
        await ProgramBuilder.PatchProgram(program, definition);

        ReferenceEquals(simpleTypeCache, program.SimpleTypeCache).ShouldBeTrue();
        ReferenceEquals(typeNameConflicts, program.TypeNameConflicts).ShouldBeTrue();
        program.SimpleTypeCache.ContainsKey("Path").ShouldBeFalse();
        program.TypeNameConflicts["Path"].SetEquals(["Path", "System.IO.Path"]).ShouldBeTrue();

        // A subsequent expression with no type registrations must retain the
        // conflict so incremental REPL state resolves exactly like a full rebuild.
        await ProgramBuilder.PatchProgram(program, ParseScript("let n = 1"));
        program.SimpleTypeCache.ContainsKey("Path").ShouldBeFalse();
        program.TypeNameConflicts["Path"].SetEquals(["Path", "System.IO.Path"]).ShouldBeTrue();
    }

    [Test]
    public async Task ReplState_ResolvesUserTypeAddedByEarlierExpression()
    {
        var session = new ShellSession();

        var definition = await session.ExecuteAsync("type ReplStateWidget { pub value: Int }");
        definition.Success.ShouldBeTrue(definition.Error?.ToString());

        var use = await session.ExecuteAsync("let widget = ReplStateWidget { value = 42 }");
        use.Success.ShouldBeTrue(use.Error?.ToString());
        session.Program.SimpleTypeCache.ContainsKey("ReplStateWidget").ShouldBeTrue();
    }
}
