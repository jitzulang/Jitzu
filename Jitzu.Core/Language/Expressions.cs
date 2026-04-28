using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Jitzu.Core.Runtime;

namespace Jitzu.Core.Language;

public abstract record Expression
{
    public SourceSpan Location { get; init; }
}

public sealed record UnitExpression : Expression;

public record ScriptExpression : Expression
{
    public static readonly ScriptExpression Empty = new()
    {
        Body = [],
        Location = SourceSpan.Empty,
    };

    public required Expression[] Body { get; init; }
}

public record ModuleExpression : Expression
{
    public required Expression Identifier { get; init; }
}

public record LetExpression : Expression
{
    public KeywordLiteral? Mutable { get; init; }
    public required IdentifierLiteral Identifier { get; init; }
    public IIdentifierLiteral? TypeIdentifier { get; init; }
    public required Expression Value { get; set; }
}

public record ReturnExpression : Expression
{
    public required KeywordLiteral ReturnKeyword { get; init; }
    public required Expression? ReturnValue { get; set; }
}

public record ContinueExpression : Expression;
public record BreakExpression : Expression;

public record TryExpression : Expression
{
    public required KeywordLiteral TryKeyword { get; init; }
    public required Expression Body { get; set; }
    public Type? ReturnType { get; set; }

    public override string ToString()
    {
        return $"try {Body.ToString()}";
    }
}

public record InlineTryExpression : Expression
{
    public required Token QuestionMark { get; init; }
    public required Expression Body { get; init; }
}

public record MemberFunctionDefinitionExpression : Expression
{
    public required AccessModifierExpression? AccessModifierExpression { get; init; }
    public required FunctionDefinitionExpression FunctionDefinition { get; init; }
    public string Name => FunctionDefinition.Identifier.Name;
}

public record FunctionDefinitionExpression : Expression
{
    public required IdentifierLiteral Identifier { get; init; }
    public required FunctionParametersExpression Parameters { get; init; }
    public Expression? ReturnType { get; set; }
    public required Expression[] Body { get; init; }
    public Type? FunctionReturnType { get; set; }
    public UpvalueDescriptor[]? CapturedUpvalues { get; set; }
    public int LocalCount { get; set; }
}

public record FunctionParametersExpression : Expression
{
    public KeywordLiteral? Self { get; init; }
    public required FunctionParameterExpression[] Parameters { get; init; }
}

public record FunctionParameterExpression : Expression
{
    public required IdentifierLiteral Identifier { get; init; }
    public required Expression Type { get; set; }
}

public record TypeDefinitionExpression : Expression
{
    public Token TypeKeyword { get; set; }
    public required IdentifierLiteral Identifier { get; init; }
    public required FieldDefinitionExpression[] Fields { get; init; }
    public required MemberFunctionDefinitionExpression[] Methods { get; set; }
    public UserTypeDescriptor? Descriptor { get; set; }
}

public abstract record AccessModifierExpression : Expression;
public sealed record PublicAccessModifier : AccessModifierExpression;
public sealed record PrivateAccessModifier : AccessModifierExpression;

public record FieldDefinitionExpression : Expression
{
    public AccessModifierExpression? AccessModifier { get; set; }
    public KeywordLiteral? Mutable { get; init; }
    public required IdentifierLiteral Identifier { get; init; }
    public required Expression Type { get; init; }  // Can be IdentifierLiteral or SimpleMemberAccessExpression for qualified names
    public Expression? DefaultValue { get; init; }
}

public record AssociatedTypeExpression : Expression
{
    public required KeywordLiteral TypeLiteral { get; init; }
    public required IIdentifierLiteral TypeAlias { get; init; }
}

public record AssociatedTypeImplementationExpression : AssociatedTypeExpression
{
    public required OperatorLiteral EqualsOperator { get; init; }
    public required Expression TypeExpression { get; init; }
}

public record TraitDefinitionExpression : Expression
{
    public required IdentifierLiteral Identifier { get; init; }
    public required TraitFunctionSignature[] FunctionSignatures { get; init; }
    public required AssociatedTypeExpression[] AssociatedTypes { get; set; }
}

public record TraitFunctionSignature : Expression
{
    public required KeywordLiteral FuncKeyword { get; set; }
    public required IIdentifierLiteral Identifier { get; init; }
    public required FunctionParametersExpression Parameters { get; init; }
    public Expression? ReturnType { get; init; }
}

public record ImplExpression : Expression
{
    public required IdentifierLiteral TraitIdentifier { get; init; }
    public required IIdentifierLiteral TypeIdentifier { get; init; }
    public required AssociatedTypeImplementationExpression[] AssociatedTypes { get; set; }
    public required FunctionDefinitionExpression[] Functions { get; init; }

    public override string ToString()
    {
        return $"impl {TraitIdentifier.ToString()} for {TypeIdentifier.ToString()} {{ x{Functions.Length} funcs }}";
    }
}

public record BlockBodyExpression : Expression
{
    public static readonly BlockBodyExpression Empty = new()
    {
        Expressions = [],
        Location = SourceSpan.Empty
    };

    public Token OpenBracket { get; init; }
    public required Expression[] Expressions { get; init; }
    public Token CloseBracket { get; init; }
    public Dictionary<string, int> Scope { get; set; } = new();

    public override string ToString()
    {
        return $"{{ {Expressions.Select(e => e.GetType().Name).Join(", ")} }}";
    }
}

public record CommentExpression : Expression
{
    public Token Token { get; init; }
}

public record UseExpression : Expression
{
    public required Expression Identifier { get; init; }
}

public record DotLiteral : ExpressionLiteral
{
    public Token Token { get; init; }
    public override string ToString() => ".";
}

public abstract record ExpressionLiteral : Expression;

public record IntLiteral : ExpressionLiteral
{
    public Token Token { get; init; }
    public required int Integer { get; init; }
    public override string ToString() => Integer.ToString();
}

public record DoubleLiteral : ExpressionLiteral
{
    public Token Token { get; init; }
    public required double Double { get; init; }
    public override string ToString() => Double.ToString(CultureInfo.InvariantCulture);
}

public record StringLiteral : ExpressionLiteral
{
    /// <summary>
    ///  This is the token inc. the surrounding string
    /// </summary>
    public Token Token { get; init; }

    /// <summary>
    /// This is the value inside the token string
    /// </summary>
    public required string String { get; init; }

    public override string ToString() => Token.Value;
}

public record CharLiteral : ExpressionLiteral
{
    public Token Token { get; init; }
    public required char Char { get; init; }
}

public record InterpolatedStringExpression : Expression
{
    public Token StartToken { get; init; }
    public required IInterpolatedStringPart[] Parts { get; init; }
    public Token EndToken { get; init; }

    public override string ToString()
    {
        return $"`{Parts.Select(p => p.ToString()!).Join("")}`";
    }
}

public interface IInterpolatedStringPart
{
    Expression Expression { get; set; }
}

public record InterpolatedStringText(StringLiteral StringLiteral) : IInterpolatedStringPart
{
    public Expression Expression { get; set; } = StringLiteral;

    public override string ToString()
    {
        return StringLiteral.String;
    }
}

public record Interpolation(Expression Expression) : IInterpolatedStringPart
{
    public Expression Expression { get; set; } = Expression;
    public override string ToString() => $"{{{Expression.ToString()}}}";
}

public record BooleanLiteral : ExpressionLiteral
{
    public required Token Token { get; init; }
    public required bool Bool { get; set; }
    public override string ToString() => Bool ? "true" : "false";
}

public record OperatorLiteral : ExpressionLiteral
{
    public required Token Token { get; init; }
    public override string ToString() => Token.Value;
}

public record PunctuationLiteral : ExpressionLiteral
{
    public Token Token { get; init; }
}

public abstract record IIdentifierLiteral : ExpressionLiteral
{
    public abstract string Name { get; init; }
}

public record KeywordLiteral : IIdentifierLiteral
{
    public Token Token { get; init; }
    public override required string Name { get; init; }
    public override string ToString() => Name;
}

public record IdentifierLiteral : IIdentifierLiteral
{
    public Token Token { get; init; }
    public override required string Name { get; init; }
    public override string ToString() => Name;
}

public record TupleExpression : Expression
{
    public required Token OpenBracket { get; init; }
    public required IIdentifierLiteral[] Parts { get; init; }
    public required Token CloseBracket { get; init; }
    public required string Name { get; init; }
}

public record GenericNameLiteral : IIdentifierLiteral
{
    public Token Identifier { get; init; }
    public Token LessThanBracket { get; init; }
    public required Expression[] TypeArgumentList { get; init; }
    public Token GreaterThanBracket { get; init; }
    public override required string Name { get; init; }
}

public record SimpleMemberAccessExpression : Expression
{
    public required Expression Object { get; set; }
    public required Expression Property { get; set; }
    public Type? ReturnType { get; set; }
    /// <summary>Set when the BCL boundary surfaced a nullable member as Option&lt;T&gt;.
    /// Holds the inner T; the bytecode compiler emits a WrapOption instruction.</summary>
    public Type? WrapReturnElement { get; set; }

    public override string ToString() => $"{Object.ToString()}.{Property.ToString()}";
}

public record VecIdentifier : IIdentifierLiteral
{
    public required Expression Type { get; init; }
    public Token OpenBracket { get; set; }
    public Token CloseBracket { get; set; }
    public override required string Name { get; init; }
}

public record IfExpression : Expression
{
    public required Expression Condition { get; set; }
    public required Expression Then { get; set; }

    /// <summary>
    /// Could be <see cref="IfExpression"/>, <see cref="BlockBodyExpression"/>, or <see cref="Expression"/>
    /// </summary>
    public Expression? Else { get; set; }

    public override string ToString()
    {
        return Else is not null
            ? $"if {Condition.ToString()} {Then.ToString()} else {Else.ToString()}"
            : $"if {Condition.ToString()} {Then.ToString()} ";
    }
}

public record BinaryExpression : Expression
{
    public required Expression Left { get; set; }
    public required Expression Right { get; set; }
    public Token Operator { get; init; }

    public override string ToString()
    {
        return $"{Left.ToString()} {Operator.Value} {Right.ToString()}";
    }
}

public record AssignmentExpression : Expression
{
    public required Expression Left { get; set; }
    public required Expression Right { get; set; }
    public Token Operator { get; init; }
}

public record UnaryExpression : Expression
{
    public required string Operator { get; init; }
    public required Expression Operand { get; set; }
    public override string ToString() => $"{Operator}{Operand}";
}

public record InplaceIncrementExpression : Expression
{
    public required Expression Subject { get; set; }
    public Token Operator { get; init; }
}

public record InplaceDecrementExpression : Expression
{
    public required Expression Subject { get; set; }
    public Token Operator { get; init; }
}

public record ForExpression : Expression
{
    public required IIdentifierLiteral Identifier { get; set; }
    public required Expression Range { get; set; }
    public required BlockBodyExpression Body { get; set; }
    public int LimitSlot { get; set; }
    public int CounterSlot { get; set; }
    public int CollectionSlot { get; set; }
    public bool IsCollection { get; set; }

    public override string ToString()
    {
        return $"for {Identifier.ToString()} in {Range.ToString()} {{ ... }}";
    }
}

public record RangeExpression : Expression
{
    public Expression? Left { get; set; }
    public Token Operator { get; init; }
    public Expression? Right { get; set; }

    public override string ToString()
    {
        return $"{Left?.ToString()}..{Right?.ToString()}";
    }
}

public record UnionDefinitionExpression : Expression
{
    public required IdentifierLiteral Identifier { get; init; }
    public required EnumVariantExpression[] Variants { get; init; }
}

public record EnumVariantExpression : Expression
{
    public required IdentifierLiteral Identifier { get; init; }
    public required IIdentifierLiteral[] Fields { get; init; }
}

public record WhileExpression : Expression
{
    public Token WhileToken { get; set; }
    public required Expression Condition { get; set; }
    public required Expression Body { get; set; }
}

public record OpenExpression : Expression
{
    public required StringLiteral Path { get; init; }
}

public record DeferExpression : Expression
{
    public Token Keyword { get; init; }
    public required Expression Expression { get; init; }
}

public record MatchExpression : Expression
{
    public required Expression Expression { get; set; }
    public required MatchArm[] Cases { get; init; }

    public override string ToString()
    {
        return $"match {Expression.ToString()}";
    }
}

public record MatchArm : Expression
{
    public required Expression Pattern { get; set; }

    /// <summary>
    /// Could be anything, a number, a block body, etc.
    /// </summary>
    public required Expression Body { get; set; }

    public int LocalCount { get; set; }

    public override string ToString() => $"{Pattern.ToString()} => {Body.ToString()}";
}

public record TuplePatternExpression : Expression
{
    public required Token OpenBracket { get; init; }
    public required Expression[] Parts { get; init; }
    public required Token CloseBracket { get; init; }

    public override string ToString()
    {
        return $"({Parts.Select(p => p.ToString()!).Join(", ")})";
    }
}

public record DiscardExpression : Expression
{
    public required Token? Token { get; set; }
    public override string ToString() => "_";
}

public record VariantExpression : Expression
{
    public required IIdentifierLiteral Identifier { get; set; }
    public required TuplePatternExpression? PositionalPattern { get; set; }
    public Type? VariantType { get; set; }
    public MethodInfo? Deconstructor { get; set; }

    public override string ToString()
    {
        return PositionalPattern is null
            ? $"{Identifier.ToString()}"
            : $"{Identifier.ToString()}{PositionalPattern.ToString()}";
    }
}

public record ConstantExpression : Expression
{
    public required Expression Expression { get; set; }
}

public record FunctionCallExpression : Expression
{
    public required Expression Identifier { get; set; }
    public Token OpeningBracket { get; init; }
    public required Expression[] Arguments { get; init; }
    public Token ClosingBracket { get; init; }
    public IShellFunction? CachedFunction { get; set; }
    public Type? ReturnType { get; set; }
    /// <summary>Set when the BCL boundary surfaced a nullable return as Option&lt;T&gt;.
    /// Holds the inner T; the bytecode compiler emits a WrapOption instruction.</summary>
    public Type? WrapReturnElement { get; set; }

    public override string ToString()
    {
        return $"{Identifier.ToString()}({Arguments.Select(a => a.ToString()).Join(", ")})";
    }
}

public record ObjectInstantiationExpression : Expression
{
    public required IIdentifierLiteral Identifier { get; set; }
    public required NewDynamicExpression Body { get; set; }
    public Type? ObjectType { get; set; }

    public override string ToString() => $"{Identifier.ToString()} {Body.ToString()}";
}

public record NewDynamicExpression : Expression
{
    public required Token OpenBrace { get; init; }
    public required ObjectFieldInstantiationExpression[] Fields { get; init; }
    public required Token CloseBrace { get; init; }

    public override string ToString()
    {
        return $"{{ {Fields.Select(f => f.ToString()).Join(", ")} }}";
    }
}

// cord struct ArrayInitialisationExpression : Expression
// {
//     public override string Name {get;init; =nameof(ArrayInitialisationExpression);
//     public Expression ArrayType { get; init; }
//     public Token SquareBracketOpen { get; init; }
//     public IntLiteral? Length { get; init; }
//     public Token SquareBracketClose { get; init; }
//     public override LocationRange Location { get;init;  
// }

public record LikenessExpression : Expression
{
    public required Expression Left { get; set; }
    public required Token IsToken { get; set; }
    public required Expression Right { get; set; }
}

public record IndexerExpression : Expression
{
    public required Expression Identifier { get; set; }
    public Token SquareBracketOpen { get; init; }
    public required Expression Index { get; set; }
    public Token SquareBracketClose { get; init; }
    public Type? ReturnType { get; set; }

    public override string ToString() => $"{Identifier.ToString()}[{Index.ToString()}]";
}

public record QuickArrayInitialisationExpression : Expression
{
    public required Token SquareBracketOpen { get; init; }
    public required Expression[] Expressions { get; init; }
    public required Token SquareBracketClose { get; init; }
}

public record ObjectFieldInstantiationExpression : Expression
{
    public required IIdentifierLiteral Identifier { get; set; }
    public required Expression? Value { get; set; }

    public override string ToString()
    {
        return $"{Identifier.ToString()} = {Value?.ToString() ?? Identifier.ToString()}";
    }
}

public record TagExpression : Expression
{
    public required Token TagToken { get; init; }
    public required string Identifier { get; init; }
    public required string? Version { get; init; }
}

public record LambdaExpression : Expression
{
    public required Expression[] Parameters { get; init; }
    public required Expression Body { get; init; }
    public UpvalueDescriptor[]? CapturedUpvalues { get; set; }
    public int LocalCount { get; set; }
}

public record GlobalGetExpression(IIdentifierLiteral Identifier) : IIdentifierLiteral
{
    public required int SlotIndex { get; init; }
    public override string Name { get; init; } = Identifier.Name;
    public Type? VariableType { get; set; }
    public override string ToString() => Identifier.Name;
}

public record LocalGetExpression(IIdentifierLiteral Identifier) : IIdentifierLiteral
{
    public required int SlotIndex { get; init; }
    public override string Name { get; init; } = Identifier.Name;
    public Type? VariableType { get; set; }

    public override string ToString() => Identifier.Name;
}

public record GlobalSetExpression(IIdentifierLiteral Identifier) : Expression
{
    public required int SlotIndex { get; init; }
    public required Expression ValueExpression { get; set; }
    public Type? SetterType { get; set; }

    public override string ToString()
    {
        return $"{Identifier.ToString()}[{SlotIndex}] = {ValueExpression.ToString()}";
    }
}

public record MatchSetExpression(IIdentifierLiteral Identifier) : Expression
{
    public required int SlotIndex { get; init; }
    public LocalKind LocalKind { get; set; }

    public override string ToString()
    {
        return $"{Identifier.ToString()}[{SlotIndex}]";
    }
}

public record LocalSetExpression : Expression
{
    public required int SlotIndex { get; init; }
    public required Expression ValueExpression { get; init; }
    public required IIdentifierLiteral? Identifier { get; init; }

    public override string ToString()
    {
        return $"local[{SlotIndex}] ({Identifier?.ToString()})  = {ValueExpression.ToString()}";
    }
}

public record UpvalueGetExpression(IIdentifierLiteral Identifier) : IIdentifierLiteral
{
    public required int UpvalueIndex { get; init; }
    public override string Name { get; init; } = Identifier.Name;
    public Type? VariableType { get; set; }
    public override string ToString() => $"upvalue[{UpvalueIndex}] ({Identifier.Name})";
}

public record UpvalueSetExpression : Expression
{
    public required int UpvalueIndex { get; init; }
    public required Expression ValueExpression { get; init; }
    public required IIdentifierLiteral? Identifier { get; init; }

    public override string ToString()
    {
        return $"upvalue[{UpvalueIndex}] ({Identifier?.ToString()}) = {ValueExpression.ToString()}";
    }
}

public record CapturedLocalGetExpression(IIdentifierLiteral Identifier) : IIdentifierLiteral
{
    public required int SlotIndex { get; init; }
    public override string Name { get; init; } = Identifier.Name;
    public Type? VariableType { get; set; }
    public override string ToString() => $"captured_local[{SlotIndex}] ({Identifier.Name})";
}

public record CapturedLocalSetExpression : Expression
{
    public required int SlotIndex { get; init; }
    public required Expression ValueExpression { get; init; }
    public required IIdentifierLiteral? Identifier { get; init; }

    public override string ToString()
    {
        return $"captured_local[{SlotIndex}] ({Identifier?.ToString()}) = {ValueExpression.ToString()}";
    }
}

public readonly record struct UpvalueDescriptor(bool IsLocal, int Index, string Name);