using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jitzu.Core.Formatting;
using Jitzu.Core.Language;
using Jitzu.Core.Logging;

namespace Jitzu.Core;

public ref struct Parser(ReadOnlySpan<Token> tokens)
{
    private readonly ReadOnlySpan<Token> _tokens = tokens;
    private readonly long _maxIndex = tokens.Length - 1;
    private int _index;
    private Token? _current = tokens[0];

    public Parser(List<Token> tokens) : this(CollectionsMarshal.AsSpan(tokens))
    {
    }

    public static Expression[] Parse(string filePath, ReadOnlySpan<char> sourceCode)
    {
        var lexer = new Lexer(filePath, sourceCode);
        var tokens = lexer.Lex();
        return new Parser(tokens).Parse();
    }

    public Expression[] Parse()
    {
        var expressions = ImmutableArray.CreateBuilder<Expression>();

        while (true)
        {
            if (_index > _maxIndex)
                break;

            var expression = ParseExpression();
            if (expression is not CommentExpression)
                expressions.Add(expression);

            if (_current is { Type: TokenType.Punctuation, Value: ";" })
                MoveNext();
        }

        return expressions.ToArray();
    }

    private readonly Token? Peek(int offset)
    {
        var nextIndex = _index + offset;
        return nextIndex <= _maxIndex ? _tokens[nextIndex] : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MoveNext()
    {
        _index++;
        _current = _index <= _maxIndex ? _tokens[_index] : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private Token Take()
    {
        var token = _current;
        if (token is null) throw new Exception("End of stream");
        MoveNext();
        return token.Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private bool TryConsume(char predicate)
    {
        if (!IsNext(predicate))
            return false;

        MoveNext();
        return true;
    }

    private bool TryConsume(char predicate, [NotNullWhen(true)] out Token? token)
    {
        token = _current;
        if (token is null || predicate != token.Value.Value[0])
            return false;

        MoveNext();
        return true;
    }

    private bool TryConsume(ReadOnlySpan<char> predicate, out Token token)
    {
        if (_current is null || !predicate.Equals(_current.Value.Value, StringComparison.Ordinal))
        {
            token = default;
            return false;
        }

        token = _current.Value;
        MoveNext();
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private bool IsNext(char predicate)
    {
        return _current is { } token && token.Value[0] == predicate;
    }

    private bool IsNext(ReadOnlySpan<char> predicate)
    {
        return _current is { } token && predicate.Equals(token.Value, StringComparison.Ordinal);
    }

    private readonly Token Expect(
        Func<Token, bool> predicate,
        string predicateText = "Unknown")
    {
        var token = _current ?? throw new UnexpectedEndOfFileException();
        if (predicate(token)) return token;
        throw new UnexpectedSyntaxException(token.Span, predicateText, TokenFormatter.Format(token, false));
    }

    private readonly Token Expect(char predicate)
    {
        var token = _current ?? throw new Exception("Unexpected end of file");
        if (token.Value[0] == predicate) return token;
        throw new UnexpectedSyntaxException(token.Span, predicate.ToString(), TokenFormatter.Format(token, false));
    }

    private readonly Token Expect(ReadOnlySpan<char> predicate)
    {
        var token = _current ?? throw new Exception("Unexpected end of file");
        if (predicate.Equals(token.Value, StringComparison.Ordinal)) return token;
        throw new UnexpectedSyntaxException(token.Span, predicate.ToString(), TokenFormatter.Format(token, false));
    }

    private readonly Token Expect(TokenType tokenType)
    {
        var token = _current ?? throw new UnexpectedEndOfFileException();
        if (token.Type == tokenType) return token;
        throw new UnexpectedSyntaxException(token.Span, tokenType.ToString(), TokenFormatter.Format(token, false));
    }

    private Token ExpectAndConsume(params ReadOnlySpan<TokenType> tokenTypes)
    {
        var current = _current ?? throw new UnexpectedEndOfFileException();
        foreach (var tokenType in tokenTypes)
        {
            if (current.Type != tokenType)
                continue;

            MoveNext();
            return current;
        }

        var names = new string[tokenTypes.Length];
        for (var i = 0; i < tokenTypes.Length; i++)
            names[i] = tokenTypes[i].ToStringFast();

        throw new UnexpectedSyntaxException(
            current.Span,
            string.Join(" or ", names),
            current.Type.ToString());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private Token ExpectAndConsume(char predicate)
    {
        var token = Expect(predicate);
        MoveNext();
        return token;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private Token ExpectAndConsume(ReadOnlySpan<char> predicate)
    {
        var token = Expect(predicate);
        MoveNext();
        return token;
    }

    private Expression ParseExpression(int parentPrecedence = 0)
    {
        switch (_current)
        {
            case { Value: "open" }:
                return ParseOpenExpression();
        }

        var expression = ParsePrimaryExpression();

        switch (expression)
        {
            case IdentifierLiteral identifier when char.IsUpper(identifier.Name[0]) && _current is { Value: "{" }:
                return ParseObjectInstantiationExpression(identifier);

            case IdentifierLiteral or SimpleMemberAccessExpression
                when _current is { Type: TokenType.Operator, Value: "=" } assignmentOperator:
                MoveNext();
                var right = ParseExpression();
                return new AssignmentExpression
                {
                    Left = (IIdentifierLiteral)expression,
                    Right = right,
                    Operator = assignmentOperator,
                    Location = expression.Location.Extend(right.Location)
                };
        }

        if (_current is { Type: TokenType.RangeOperator })
            return ParseRangeExpression(expression);

        while (true)
        {
            if (TryConsume('?', out var question))
            {
                expression = new InlineTryExpression
                {
                    QuestionMark = question.Value,
                    Body = expression,
                    Location = expression.Location.Extend(question.Value.Span)
                };
            }

            if (TryConsume("++", out var inplaceIncrement))
            {
                expression = new InplaceIncrementExpression
                {
                    Subject = expression,
                    Location = expression.Location.Extend(inplaceIncrement.Span),
                };
            }
            else if (TryConsume("--", out var inplaceDecrement))
            {
                expression = new InplaceDecrementExpression
                {
                    Subject = expression,
                    Location = expression.Location.Extend(inplaceDecrement.Span),
                };
            }
            else if (TryConsume("=", out var equals))
            {
                var right = ParseExpression();
                return new AssignmentExpression
                {
                    Left = expression,
                    Right = right,
                    Operator = equals,
                    Location = expression.Location.Extend(right.Location)
                };
            }
            else if (TryConsume('.'))
            {
                // Parse the next primary expression after the dot (typically an identifier)
                var property = ParsePrimaryExpression();

                // Construct a SimpleMemberAccessExpression with the current expression as the object
                expression = new SimpleMemberAccessExpression
                {
                    Object = expression,
                    Property = property,
                    Location = expression.Location.Extend(property.Location)
                };
            }
            else if (TryConsume("(", out var openBracket))
            {
                var args = ParseFunctionArguments();
                var closingBracket = Take();

                expression = new FunctionCallExpression
                {
                    Identifier = expression,
                    OpeningBracket = openBracket,
                    Arguments = args,
                    ClosingBracket = closingBracket,
                    Location = expression.Location.Extend(closingBracket.Span)
                };
            }
            else if (IsNext('['))
            {
                var squareBracketOpen = Take();
                var indexExpression = ParsePrimaryExpression();
                var squareBracketClose = ExpectAndConsume(']');

                expression = new IndexerExpression
                {
                    Identifier = expression,
                    SquareBracketOpen = squareBracketOpen,
                    Index = indexExpression,
                    SquareBracketClose = squareBracketClose,
                    Location = expression.Location.Extend(squareBracketClose.Span),
                };
            }
            else
            {
                if (_current is var opToken && opToken is not { Type: TokenType.Operator })
                    break;

                var precedence = GetPrecedence(opToken.Value.Value);
                if (precedence <= parentPrecedence)
                    break;

                MoveNext();
                var right = ParseExpression(precedence);

                if (opToken.Value.Value is "+=" or "-=")
                {
                    var operatorChar = opToken.Value.Value[0].ToString();
                    var binaryOp = new Token
                    {
                        Type = TokenType.Operator,
                        Value = operatorChar,
                        Span = opToken.Value.Span
                    };

                    var assignOp = new Token
                    {
                        Type = TokenType.Operator,
                        Value = "=",
                        Span = opToken.Value.Span
                    };

                    expression = new AssignmentExpression
                    {
                        Left = expression,
                        Operator = assignOp,
                        Right = new BinaryExpression
                        {
                            Left = expression,
                            Right = right,
                            Operator = binaryOp,
                            Location = expression.Location.Extend(right.Location)
                        },
                        Location = expression.Location.Extend(right.Location)
                    };
                    continue;
                }

                expression = new BinaryExpression
                {
                    Left = expression,
                    Right = right,
                    Operator = opToken.Value,
                    Location = expression.Location.Extend(right.Location)
                };
            }
        }

        return expression;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPrecedence(ReadOnlySpan<char> op) => op switch
    {
        "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "<<=" or ">>=" or "&=" or "^=" or "|=" => 1,
        "||" => 2,
        "&&" => 3,
        "|" => 4,
        "^" => 5,
        "&" => 6,
        "==" or "!=" => 7,
        "<" or "<=" or ">" or ">=" => 8,
        ">>" or "<<" => 9,
        "+" or "-" => 10,
        "*" or "/" or "%" => 11,
        "is" => 12,
        _ => 0,
    };

    private Expression ParsePrimaryExpression()
    {
        if (_current is { Type: TokenType.Operator, Value: "!" or "-" } prefixToken)
        {
            MoveNext();
            var operand = ParsePrimaryExpression();
            return new UnaryExpression
            {
                Operator = prefixToken.Value,
                Operand = operand,
                Location = prefixToken.Span.Extend(operand.Location),
            };
        }

        switch (_current)
        {
            case { Type: TokenType.Comment } token:
                MoveNext();
                return new CommentExpression
                {
                    Token = token,
                    Location = token.Span
                };

            case { Type: TokenType.Int }:
                return ParseIntLiteral();

            case { Type: TokenType.Double }:
                return ParseDoubleLiteral();

            case { Type: TokenType.String }:
                return ParseStringLiteral();

            case { Type: TokenType.Char }:
                return ParseCharLiteral();

            case { Type: TokenType.InterpolationStringStart } token:
                return ParseInterpolationString(token);

            case { Type: TokenType.Boolean } token:
                MoveNext();
                return new BooleanLiteral
                {
                    Token = token,
                    Location = token.Span,
                    Bool = token.Value is "true"
                };

            case { Type: TokenType.Operator, Value: "(" }:
                var openBracket = ExpectAndConsume('(');
                var expression = ParseExpression();

                if (expression is IdentifierLiteral firstName && IsNext(','))
                {
                    var parts = ImmutableArray.CreateBuilder<IIdentifierLiteral>();
                    parts.Add(firstName);

                    while (TryConsume(','))
                        parts.Add(ParseIdentifierLiteral());

                    var closeBracket = ExpectAndConsume(')');

                    return new TupleExpression
                    {
                        Name = $"Tuple({string.Join(", ", parts.Select(e => e.ToString()))})",
                        OpenBracket = openBracket,
                        Parts = parts.ToArray(),
                        CloseBracket = closeBracket,
                        Location = openBracket.Span.Extend(closeBracket.Span)
                    };
                }

                ExpectAndConsume(')');
                return expression;

            case { Type: TokenType.Operator } token:
                MoveNext();
                return new OperatorLiteral
                {
                    Token = token,
                    Location = token.Span
                };

            case { Type: TokenType.Keyword, Value: "return" }:
                return ParseReturnExpression();

            case { Type: TokenType.Keyword, Value: "try" }:
                return ParseTryExpression();

            case { Type: TokenType.Keyword, Value: "let" }:
                return ParseLetExpression();

            case { Type: TokenType.Keyword, Value: "if" }:
                return ParseIfExpression();

            case { Type: TokenType.Keyword, Value: "use" }:
                return ParseUseExpression();

            case { Type: TokenType.Keyword, Value: "mod" }:
                return ParseModuleExpression();

            case { Type: TokenType.Keyword, Value: "fun" }:
                return ParseFunctionDefinitionExpression();

            case { Type: TokenType.Keyword, Value: "type" }:
                return ParseTypeExpression();

            case { Type: TokenType.Keyword, Value: "trait" }:
                return ParseTraitExpression();

            case { Type: TokenType.Keyword, Value: "impl" }:
                return ParseImplExpression();

            case { Type: TokenType.Keyword, Value: "for" }:
                return ParseForExpression();

            case { Type: TokenType.Keyword, Value: "union" }:
                return ParseUnionExpression();

            case { Type: TokenType.Keyword, Value: "match" }:
                return ParseMatchExpression();

            case { Type: TokenType.Keyword, Value: "while" }:
                return ParseWhileExpression();

            case { Type: TokenType.Keyword, Value: "open" }:
                return ParseOpenExpression();

            case { Type: TokenType.Keyword, Value: "defer" }:
                return ParseDeferExpression();

            case { Type: TokenType.Keyword, Value: "clear" }:
                return ParseClearExpression();

            case { Type: TokenType.Keyword, Value: "new" }:
                return ParseNewKeywordExpression();

            case { Type: TokenType.Keyword, Value: "continue" } continueToken:
                MoveNext();
                return new ContinueExpression { Location = continueToken.Span };

            case { Type: TokenType.Keyword, Value: "break" } breakToken:
                MoveNext();
                return new BreakExpression { Location = breakToken.Span };

            case { Type: TokenType.Keyword } keyword:
                throw new NotImplementedException($"Keyword {TokenFormatter.Format(keyword)} not implemented");

            case { Type: TokenType.Identifier }:
                return ParseIdentifierLiteral();

            case { Type: TokenType.Punctuation, Value: "#" }:
                return ParseTagExpression();

            case { Type: TokenType.Punctuation, Value: "[" }:
                return ParseQuickArrayInitialisationExpression();

            case { Type: TokenType.Punctuation, Value: "{" }
                when Peek(1) is { Type: TokenType.Keyword }:
                return ParseBlockBodyExpression();

            case { Type: TokenType.Punctuation, Value: "{" }:
                return ParseNewDynamicExpression();

            case { Type: TokenType.Punctuation, Value: "." } token:
                MoveNext();
                return new DotLiteral
                {
                    Token = token,
                    Location = token.Span
                };

            case { Type: TokenType.RangeOperator }:
                return ParseRangeExpression(null);

            case { Type: TokenType.Tag }:
                return ParseTagExpression();

            case { } other:
                throw new UnexpectedTokenException(other);

            default:
                throw new Exception("Unexpected end of file");
        }
    }

    private IntLiteral ParseIntLiteral()
    {
        // Lets trust the lexer has done its thing
        var token = Take();
        token.Value.AsSpan().FastIntParse(out var output);

        return new IntLiteral
        {
            Token = token,
            Integer = output!.Value,
            Location = token.Span
        };
    }

    private DoubleLiteral ParseDoubleLiteral()
    {
        var token = Take();
        return new DoubleLiteral
        {
            Token = token,
            Double = token.Value.AsSpan().FastParseDouble(),
            Location = token.Span,
        };
    }

    private CharLiteral ParseCharLiteral()
    {
        var token = Take();
        return new CharLiteral
        {
            Token = token,
            Location = token.Span,
            Char = token.Value[1],
        };
    }

    private StringLiteral ParseStringLiteral()
    {
        var token = Take();
        return new StringLiteral
        {
            Token = token,
            String = token.Value.Trim('"'),
            Location = token.Span,
        };
    }

    private IfExpression ParseIfExpression()
    {
        var ifToken = Take(); // Consume 'if'
        var condition = ParseExpression();

        Expect(static t => t.Value is "{", "Expected bracket to start an if body");

        var thenBody = IsNext('{') ? ParseBlockBodyExpression() : ParseExpression();

        if (!IsNext("else"))
        {
            return new IfExpression
            {
                Condition = condition,
                Then = thenBody,
                Location = ifToken.Span.Extend(thenBody.Location)
            };
        }

        MoveNext();

        Expression elseBody;
        switch (_current)
        {
            case { Type: TokenType.Keyword, Value: "if" }:
                elseBody = ParseIfExpression();
                break;
            case { Type: TokenType.Punctuation, Value: "{" }:
                elseBody = IsNext('{') ? ParseBlockBodyExpression() : ParseExpression();
                break;
            default:
                throw new Exception(
                    $"Expected 'if' or '{{' after 'else', but found {TokenFormatter.Format(_current!.Value)}");
        }

        return new IfExpression
        {
            Condition = condition,
            Then = thenBody,
            Else = elseBody,
            Location = ifToken.Span.Extend(elseBody.Location)
        };
    }

    private UseExpression ParseUseExpression()
    {
        var useToken = ExpectAndConsume("use");
        var identifier = ParseIdentifierLiteral();

        return new UseExpression
        {
            Identifier = identifier,
            Location = useToken.Span.Extend(identifier.Location)
        };
    }

    private ModuleExpression ParseModuleExpression()
    {
        var modToken = ExpectAndConsume("mod");
        var identifier = ParseIdentifierLiteral();

        return new ModuleExpression
        {
            Identifier = identifier,
            Location = modToken.Span.Extend(identifier.Location)
        };
    }

    private IIdentifierLiteral ParseIdentifierLiteral()
    {
        return ExpectAndConsume(TokenType.Identifier, TokenType.Keyword) switch
        {
            { Value: "self" } identifier => new KeywordLiteral
            {
                Token = identifier,
                Name = identifier.Value,
                Location = identifier.Span,
            },
            var identifier => new IdentifierLiteral
            {
                Token = identifier,
                Name = identifier.Value,
                Location = identifier.Span,
            }
        };
    }

    /// <summary>
    /// Parses a type annotation, which can be a simple identifier or a qualified name (e.g., System.Collections.List).
    /// This supports dot notation for namespace qualification.
    /// </summary>
    private Expression ParseTypeAnnotation()
    {
        var expr = (Expression)ParseIdentifierLiteral();

        // Handle qualified names (e.g., System.Collections.List)
        while (_current is { Type: TokenType.Operator, Value: "." })
        {
            ExpectAndConsume('.');
            var property = ParseIdentifierLiteral();

            expr = new SimpleMemberAccessExpression
            {
                Object = expr,
                Property = property,
                Location = expr.Location.Extend(property.Location)
            };
        }

        // Handle generic type arguments (e.g., Result<JObject, String>)
        if (_current is { Type: TokenType.Operator, Value: "<" } && expr is IdentifierLiteral baseIdent)
        {
            var lessThan = ExpectAndConsume('<');

            var typeArgs = ImmutableArray.CreateBuilder<Expression>();
            typeArgs.Add(ParseTypeAnnotation());
            while (TryConsume(','))
                typeArgs.Add(ParseTypeAnnotation());

            var greaterThan = ExpectAndConsume('>');

            expr = new GenericNameLiteral
            {
                Identifier = baseIdent.Token,
                LessThanBracket = lessThan,
                TypeArgumentList = [.. typeArgs],
                GreaterThanBracket = greaterThan,
                Name = baseIdent.Name,
                Location = baseIdent.Location.Extend(greaterThan.Span)
            };
        }

        return expr;
    }

    private ReturnExpression ParseReturnExpression()
    {
        var returnKeyword = ExpectAndConsume("return");
        var keyword = new KeywordLiteral
        {
            Token = returnKeyword,
            Name = "return",
            Location = returnKeyword.Span
        };

        var returnValue = _current switch
        {
            { Value: ";" or "}" } or null => null,
            { Value: "{" } => ParseBlockBodyExpression(),
            _ => ParseExpression(),
        };

        return new ReturnExpression
        {
            ReturnKeyword = keyword,
            ReturnValue = returnValue,
            Location = returnValue is not null
                ? keyword.Location.Extend(returnValue.Location)
                : keyword.Location
        };
    }

    private TryExpression ParseTryExpression()
    {
        var tryKeyword = ExpectAndConsume("try");
        var tryKeywordExpr = new KeywordLiteral
        {
            Token = tryKeyword,
            Name = "try",
            Location = tryKeyword.Span,
        };

        var body = IsNext('{') ? ParseBlockBodyExpression() : ParseExpression();

        return new TryExpression
        {
            TryKeyword = tryKeywordExpr,
            Body = body,
            Location = tryKeyword.Span.Extend(body.Location)
        };
    }

    private BlockBodyExpression ParseBlockBodyExpression()
    {
        var openBracket = ExpectAndConsume('{');

        var body = ImmutableArray.CreateBuilder<Expression>();
        while (!IsNext('}'))
        {
            body.Add(IsNext('{') ? ParseBlockBodyExpression() : ParseExpression());
            if (_current is { Type: TokenType.Punctuation, Value: ";" })
                MoveNext();
        }

        var closeBracket = ExpectAndConsume('}');

        return new BlockBodyExpression
        {
            OpenBracket = openBracket,
            Expressions = body.ToArray(),
            CloseBracket = closeBracket,
            Location = openBracket.Span.Extend(closeBracket.Span)
        };
    }

    private LetExpression ParseLetExpression()
    {
        var letToken = Take(); // Consume 'let'

        KeywordLiteral? mutable = null;
        if (IsNext("mut"))
        {
            var token = Take();
            mutable = new KeywordLiteral
            {
                Token = token,
                Name = token.Value,
                Location = token.Span
            };
        }

        var identifier = ParseIdentifierLiteral();
        IIdentifierLiteral? type = null;
        if (TryConsume(':'))
        {
            type = ParseIdentifierLiteral();
        }

        ExpectAndConsume('=');
        var value = ParseExpression();
        TryConsume(';');

        return new LetExpression
        {
            Mutable = mutable,
            Identifier = (IdentifierLiteral)identifier,
            TypeIdentifier = type,
            Value = value,
            Location = letToken.Span.Extend(value.Location)
        };
    }

    private FunctionDefinitionExpression ParseFunctionDefinitionExpression()
    {
        var funToken = ExpectAndConsume("fun");
        var identifier = ParseIdentifierLiteral() switch
        {
            IdentifierLiteral literal => literal,
            { } other => throw new Exception("Expected single identifier, got " + ExpressionFormatter.Format(other))
        };

        var parameters = ParseFunctionParameters();
        var returnType = ParseFunctionReturnType();
        var body = ParseBlockBodyExpression();

        return new FunctionDefinitionExpression
        {
            Identifier = identifier,
            Parameters = parameters,
            ReturnType = returnType,
            Body = body.Expressions,
            Location = funToken.Span.Extend(body.Location)
        };
    }

    private TypeDefinitionExpression ParseTypeExpression()
    {
        var typeKeyword = ExpectAndConsume("type");
        var identifier = ParseIdentifierLiteral() as IdentifierLiteral ?? throw new Exception("Expected identifier");

        if (char.IsLower(identifier.Name[0]))
            throw new Exception("Type names must start with an uppercase letter");

        if (!TryConsume('{'))
        {
            return new TypeDefinitionExpression
            {
                TypeKeyword = typeKeyword,
                Identifier = identifier,
                Fields = [],
                Methods = [],
                Location = typeKeyword.Span.Extend(identifier.Location)
            };
        }

        var fields = ImmutableArray.CreateBuilder<FieldDefinitionExpression>();

        while (!IsNext('}'))
        {
            AccessModifierExpression? accessModifier = null;
            if (IsNext("pub"))
            {
                var pubToken = Take();
                accessModifier = new PublicAccessModifier
                {
                    Location = pubToken.Span
                };
            }

            if (IsNext("fun"))
            {
                // Functions are always defined after the fields
                break;
            }

            KeywordLiteral? mutable = null;
            if (IsNext("mut"))
            {
                var mutToken = Take();
                mutable = new KeywordLiteral
                {
                    Token = mutToken,
                    Name = "mut",
                    Location = mutToken.Span
                };
            }

            var fieldIdentifier = ParseIdentifierLiteral() as IdentifierLiteral ??
                                  throw new Exception("Expected identifier");

            ExpectAndConsume(':');

            var type = ParseTypeAnnotation();
            var defaultValue = TryConsume('=')
                ? ParseExpression()
                : null;

            if (_current is { Type: TokenType.Punctuation, Value: "[" } openBracket
                && Peek(1) is { Type: TokenType.Punctuation, Value: "]" } closeBracket)
            {
                MoveNext(); // Consume '['
                MoveNext(); // Consume ']'
                type = new VecIdentifier
                {
                    OpenBracket = openBracket,
                    CloseBracket = closeBracket,
                    Type = type,
                    Name = identifier.Name,
                    Location = type.Location.Extend(closeBracket.Span)
                };
            }

            var fieldLocation = (accessModifier?.Location ?? mutable?.Location ?? fieldIdentifier.Location)
                .Extend(defaultValue?.Location ?? type.Location);

            fields.Add(
                new FieldDefinitionExpression
                {
                    AccessModifier = accessModifier,
                    Mutable = mutable,
                    Identifier = fieldIdentifier,
                    Type = type,
                    DefaultValue = defaultValue,
                    Location = fieldLocation
                });

            if (!TryConsume(','))
                break;
        }

        var functions = ImmutableArray.CreateBuilder<MemberFunctionDefinitionExpression>();
        while (!IsNext('}'))
        {
            AccessModifierExpression? accessModifier = null;
            if (TryConsume("pub", out var pubToken))
            {
                accessModifier = new PublicAccessModifier
                {
                    Location = pubToken.Span
                };
            }

            functions.Add(new MemberFunctionDefinitionExpression
            {
                AccessModifierExpression = accessModifier,
                FunctionDefinition = ParseFunctionDefinitionExpression()
            });
        }

        var closeBrace = ExpectAndConsume('}');

        return new TypeDefinitionExpression
        {
            TypeKeyword = typeKeyword,
            Identifier = identifier,
            Fields = fields.ToArray(),
            Methods = functions.ToArray(),
            Location = typeKeyword.Span.Extend(closeBrace.Span)
        };
    }

    private TraitDefinitionExpression ParseTraitExpression()
    {
        var traitToken = Take(); // Consume 'trait'

        var identifier = ParseIdentifierLiteral() as IdentifierLiteral ?? throw new Exception("Expected identifier");

        if (char.IsLower(identifier.Name[0]))
            throw new Exception("Type names must start with an uppercase letter");

        ExpectAndConsume('{');

        var associatedTypes = ImmutableArray.CreateBuilder<AssociatedTypeExpression>();
        var functions = ImmutableArray.CreateBuilder<TraitFunctionSignature>();
        while (!IsNext('}'))
        {
            switch (_current)
            {
                case { Value: "fun" } funToken:
                    var funKeyword = new KeywordLiteral
                    {
                        Token = funToken,
                        Name = "fun",
                        Location = funToken.Span
                    };

                    MoveNext();
                    var funName = ParseIdentifierLiteral();
                    var parameters = ParseFunctionParameters();
                    var returnType = ParseFunctionReturnType();

                    functions.Add(
                        new TraitFunctionSignature
                        {
                            FuncKeyword = funKeyword,
                            Identifier = funName,
                            Parameters = parameters,
                            ReturnType = returnType,
                            Location = funKeyword.Location.Extend(returnType?.Location ?? parameters.Location)
                        });
                    break;

                case { Value: "type" } typeToken:
                    var typeKeyword = new KeywordLiteral
                    {
                        Token = typeToken,
                        Name = "type",
                        Location = typeToken.Span
                    };
                    MoveNext();
                    var alias = ParseIdentifierLiteral();

                    associatedTypes.Add(
                        new AssociatedTypeExpression
                        {
                            TypeLiteral = typeKeyword,
                            TypeAlias = alias,
                            Location = typeKeyword.Location.Extend(alias.Location)
                        });
                    break;
            }

            TryConsume(',');
        }

        if (_current is not { Value: "}" })
            throw new Exception(
                $"Expected bracket to end the trait body, but found {TokenFormatter.Format(_current!.Value)}");

        var closeBrace = Take(); // Consume '}'

        return new TraitDefinitionExpression
        {
            Identifier = identifier,
            AssociatedTypes = associatedTypes.ToArray(),
            FunctionSignatures = functions.ToArray(),
            Location = traitToken.Span.Extend(closeBrace.Span)
        };
    }

    private ImplExpression ParseImplExpression()
    {
        var implToken = Take(); // Consume 'impl';

        var identifier = ParseIdentifierLiteral() as IdentifierLiteral ?? throw new Exception("Expected identifier");

        if (char.IsLower(identifier.Name[0]))
            throw new Exception("Type names must start with an uppercase letter");

        ExpectAndConsume("for");

        var type = ParseIdentifierLiteral();

        ExpectAndConsume('{');

        var associatedTypes = ImmutableArray.CreateBuilder<AssociatedTypeImplementationExpression>();
        var functions = ImmutableArray.CreateBuilder<FunctionDefinitionExpression>();

        while (_current is { } token and not { Value: "}" })
        {
            switch (token)
            {
                case { Value: "fun" }:
                    var function = ParseFunctionDefinitionExpression();
                    functions.Add(function);
                    break;

                case { Value: "type" } typeToken:
                    var typeLiteral = new KeywordLiteral
                    {
                        Token = typeToken,
                        Name = "type",
                        Location = typeToken.Span
                    };
                    MoveNext(); // consume 'type';

                    var typeAlias = ParseIdentifierLiteral() ?? throw new Exception("Expected type alias");
                    var equalsOperator = ExpectAndConsume('=');
                    var typeExpression = ParseIdentifierLiteral();

                    associatedTypes.Add(
                        new AssociatedTypeImplementationExpression
                        {
                            TypeLiteral = typeLiteral,
                            TypeAlias = typeAlias,
                            EqualsOperator = new OperatorLiteral
                            {
                                Token = equalsOperator,
                                Location = equalsOperator.Span
                            },
                            TypeExpression = typeExpression,
                            Location = typeLiteral.Location.Extend(typeExpression.Location)
                        });
                    break;
            }

            TryConsume(',');
        }

        var closeBrace = ExpectAndConsume('}');

        return new ImplExpression
        {
            TraitIdentifier = identifier,
            TypeIdentifier = type,
            AssociatedTypes = associatedTypes.ToArray(),
            Functions = functions.ToArray(),
            Location = implToken.Span.Extend(closeBrace.Span)
        };
    }

    private ForExpression ParseForExpression()
    {
        var forToken = Take(); // Consume 'for'

        var identifier = ParseIdentifierLiteral();
        ExpectAndConsume("in");

        var range = ParseExpression();

        var openBrace = ExpectAndConsume('{');

        var body = ImmutableArray.CreateBuilder<Expression>();
        while (!IsNext('}'))
            body.Add(ParseExpression());

        var closeBrace = Take(); // Consume }

        var blockBody = new BlockBodyExpression
        {
            OpenBracket = openBrace,
            Expressions = body.ToArray(),
            CloseBracket = closeBrace,
            Location = openBrace.Span.Extend(closeBrace.Span)
        };

        return new ForExpression
        {
            Identifier = identifier,
            Range = range,
            Body = blockBody,
            Location = forToken.Span.Extend(blockBody.Location)
        };
    }

    private UnionDefinitionExpression ParseUnionExpression()
    {
        var unionToken = ExpectAndConsume("union");
        var identifier = ParseIdentifierLiteral() as IdentifierLiteral ?? throw new Exception("Expected identifier");
        var variants = ImmutableArray.CreateBuilder<EnumVariantExpression>();

        Token closingBrace = default;
        while (!TryConsume("}", out closingBrace))
        {
            var variantIdentifier = ParseIdentifierLiteral() as IdentifierLiteral
                                    ?? throw new Exception("Expected identifier");

            var fields = ImmutableArray.CreateBuilder<IIdentifierLiteral>();

            Token closeParen = default;
            if (TryConsume('('))
            {
                while (!TryConsume(")", out closeParen))
                {
                    var fieldIdentifier = ParseIdentifierLiteral() ?? throw new Exception("Expected identifier");
                    fields.Add(fieldIdentifier);
                    TryConsume(',');
                }
            }

            TryConsume(',');

            var variantLocation = variantIdentifier.Location;
            if (fields.Count > 0)
                variantLocation = variantLocation.Extend(closeParen.Span);

            variants.Add(
                new EnumVariantExpression
                {
                    Identifier = variantIdentifier,
                    Fields = fields.ToArray(),
                    Location = variantLocation
                });
        }

        return new UnionDefinitionExpression
        {
            Identifier = identifier,
            Variants = variants.ToArray(),
            Location = unionToken.Span.Extend(closingBrace.Span)
        };
    }

    private MatchExpression ParseMatchExpression()
    {
        var matchToken = ExpectAndConsume("match");
        var expression = ParseExpression();
        ExpectAndConsume('{');

        var arms = ImmutableArray.CreateBuilder<MatchArm>();

        Token closeBrace;
        while (!TryConsume("}", out closeBrace))
        {
            Expression pattern;
            switch (_current)
            {
                case { Value: "_" }:
                    pattern = new DiscardExpression
                    {
                        Token = _current,
                        Location = _current.Value.Span,
                    };
                    MoveNext();
                    break;

                case { Type: TokenType.Identifier }:
                    var identifier = ParseIdentifierLiteral();
                    var positionalPattern = ParsePositionalPattern();
                    pattern = new VariantExpression
                    {
                        Identifier = identifier, 
                        PositionalPattern = positionalPattern,
                        Location =  positionalPattern is not null
                            ? identifier.Location.Extend(positionalPattern.Location)
                            : identifier.Location
                    };
                    break;

                default:
                    var expr = ParseExpression();
                    pattern = new ConstantExpression
                    {
                        Expression = expr,
                        Location = expr.Location
                    };
                    break;
            }

            ExpectAndConsume("=>");
            var body = IsNext('{') ? ParseBlockBodyExpression() : ParseExpression();

            arms.Add( new MatchArm
            {
                Pattern = pattern,
                Body = body,
                Location = pattern.Location.Extend(body.Location)
            });

            TryConsume(',');
        }

        return new MatchExpression
        {
            Expression = expression,
            Cases = arms.ToArray(),
            Location = matchToken.Span.Extend(closeBrace.Span)
        };
    }

    private TuplePatternExpression? ParsePositionalPattern()
    {
        if (!TryConsume('(', out var openBracket))
            return null;

        var parts = ImmutableArray.CreateBuilder<Expression>();

        Token? closeBracket;
        while (!TryConsume(')', out closeBracket))
        {
            parts.Add(_current?.Type switch
            {
                TokenType.String => ParseStringLiteral(),
                TokenType.Int => ParseIntLiteral(),
                TokenType.Double => ParseDoubleLiteral(),
                TokenType.Char => ParseCharLiteral(),
                TokenType.Identifier => ParseIdentifierLiteral(),
                var other => throw new JitzuException(_current!.Value.Span, $"Unsupported tuple pattern type {other}")
            });
        }

        return new TuplePatternExpression
        {
            OpenBracket = openBracket.Value,
            Parts = parts.ToArray(),
            CloseBracket = closeBracket.Value,
        };
    }

    private WhileExpression ParseWhileExpression()
    {
        var whileToken = ExpectAndConsume("while");
        var condition = ParseExpression();
        var body = IsNext('{') ? ParseBlockBodyExpression() : ParseExpression();

        return new WhileExpression
        {
            WhileToken = whileToken,
            Condition = condition,
            Body = body,
            Location = whileToken.Span.Extend(body.Location)
        };
    }

    private Expression ParseOpenExpression()
    {
        var openToken = ExpectAndConsume("open");
        switch (_current)
        {
            case { Type: TokenType.String }:
                var expr = ParseExpression() as StringLiteral ?? throw new Exception("Expected string literal");
                return new OpenExpression
                {
                    Path = expr,
                    Location = openToken.Span.Extend(expr.Location)
                };
            default:
                return new IdentifierLiteral
                {
                    Token = openToken,
                    Name = openToken.Value,
                    Location = openToken.Span
                };
        }
    }

    private DeferExpression ParseDeferExpression()
    {
        var deferToken = Take();
        var expression = ParseExpression();

        return new DeferExpression
        {
            Keyword = deferToken,
            Expression = expression,
            Location = deferToken.Span.Extend(expression.Location)
        };
    }

    private KeywordLiteral ParseClearExpression()
    {
        var token = Take();
        return new KeywordLiteral
        {
            Token = token,
            Name = token.Value,
            Location = token.Span
        };
    }

    private KeywordLiteral ParseNewKeywordExpression()
    {
        var token = Take();
        return new KeywordLiteral
        {
            Token = token,
            Name = token.Value,
            Location = token.Span
        };
    }

    private FunctionParametersExpression ParseFunctionParameters()
    {
        var openParen = ExpectAndConsume('(');
        var builder = ImmutableArray.CreateBuilder<FunctionParameterExpression>();

        KeywordLiteral? self = null;

        Token closeParen;
        while (!TryConsume(")", out closeParen))
        {
            var nameIdentifier = ExpectAndConsume(TokenType.Identifier, TokenType.Keyword);

            if (nameIdentifier.Value is "self")
            {
                self = new KeywordLiteral
                {
                    Token = nameIdentifier,
                    Name = "self",
                    Location = nameIdentifier.Span
                };
                TryConsume(',');
                continue;
            }

            ExpectAndConsume(':');

            var type = ParseTypeAnnotation();

            builder.Add(
                new FunctionParameterExpression
                {
                    Identifier = new IdentifierLiteral
                    {
                        Token = nameIdentifier,
                        Name = nameIdentifier.Value,
                        Location = nameIdentifier.Span
                    },
                    Type = type,
                    Location = nameIdentifier.Span.Extend(type.Location)
                });

            TryConsume(',');
        }

        return new FunctionParametersExpression
        {
            Self = self,
            Parameters = [.. builder],
            Location = openParen.Span.Extend(closeParen.Span)
        };
    }

    private Expression? ParseFunctionReturnType()
    {
        return TryConsume(':') ? ParseTypeAnnotation() : null;
    }

    private Expression[] ParseFunctionArguments()
    {
        var builder = ImmutableArray.CreateBuilder<Expression>();

        while (!IsNext(')'))
        {
            var expression = ParseExpression();

            if (TryConsume("=>", out _))
            {
                var body = IsNext('{') ? ParseBlockBodyExpression() : ParseExpression();
                builder.Add(
                    new LambdaExpression
                    {
                        Parameters = [expression],
                        Body = body,
                        Location = expression.Location.Extend(body.Location)
                    });
            }
            else
            {
                builder.Add(expression);
            }

            if (!TryConsume(','))
                break;
        }

        return builder.ToArray();
    }

    private NewDynamicExpression ParseNewDynamicExpression()
    {
        var openBrace = ExpectAndConsume('{');
        var fields = ImmutableArray.CreateBuilder<ObjectFieldInstantiationExpression>();

        while (!IsNext('}'))
        {
            var fieldIdentifier = ParseIdentifierLiteral();
            Expression? value = null;
            if (TryConsume('='))
                value = ParseExpression();

            var fieldLocation = value != null
                ? fieldIdentifier.Location.Extend(value.Location)
                : fieldIdentifier.Location;

            fields.Add(
                new ObjectFieldInstantiationExpression
                {
                    Identifier = fieldIdentifier,
                    Value = value,
                    Location = fieldLocation
                });

            TryConsume(',');
        }

        var closeBrace = ExpectAndConsume('}');

        return new NewDynamicExpression
        {
            OpenBrace = openBrace,
            Fields = fields.ToArray(),
            CloseBrace = closeBrace,
            Location = openBrace.Span.Extend(closeBrace.Span)
        };
    }

    private ObjectInstantiationExpression ParseObjectInstantiationExpression(IIdentifierLiteral typeIdentifier)
    {
        var body = ParseNewDynamicExpression();
        return new ObjectInstantiationExpression
        {
            Identifier = typeIdentifier,
            Body = body,
            Location = typeIdentifier.Location.Extend(body.Location)
        };
    }

    private QuickArrayInitialisationExpression ParseQuickArrayInitialisationExpression()
    {
        var openBracket = ExpectAndConsume('[');
        var expressions = ImmutableArray.CreateBuilder<Expression>();

        while (!IsNext(']'))
        {
            expressions.Add(ParseExpression());
            TryConsume(',');
        }

        var closeBracket = ExpectAndConsume(']');

        return new QuickArrayInitialisationExpression
        {
            SquareBracketOpen = openBracket,
            Expressions = expressions.ToArray(),
            SquareBracketClose = closeBracket,
            Location = openBracket.Span.Extend(closeBracket.Span)
        };
    }

    private InterpolatedStringExpression ParseInterpolationString(Token token)
    {
        var parts = ImmutableArray.CreateBuilder<IInterpolatedStringPart>();
        MoveNext(); // Consume '`'

        while (true)
        {
            switch (_current)
            {
                case { Type: TokenType.InterpolationStringEnd } endToken:
                    MoveNext(); // Consume '`'
                    return new InterpolatedStringExpression
                    {
                        StartToken = token,
                        Parts = parts.ToArray(),
                        EndToken = endToken,
                        Location = token.Span.Extend(endToken.Span)
                    };

                case { Type: TokenType.InterpolationTextToken } textToken:
                    parts.Add(
                        new InterpolatedStringText(
                            new StringLiteral
                            {
                                Token = textToken,
                                String = textToken.Value.Trim('"'),
                                Location = textToken.Span
                            }));
                    MoveNext();
                    break;

                case { Type: TokenType.Interpolation } interpolation:
                    var lexer = new Lexer(
                        token.Span.FilePath,
                        interpolation.Value.AsSpan()[1..^1], // removes {} around the value
                        interpolation.Span.Start.Line,
                        interpolation.Span.Start.Column + 1);
                    var parser = new Parser(lexer.Lex());
                    var expressions = parser.Parse();
                    parts.Add(new Interpolation(expressions[0]));
                    MoveNext();
                    break;

                case var other:
                    throw new Exception($"Not expecting this in an interpolated string {other}");
            }
        }
    }

    private RangeExpression ParseRangeExpression(Expression? left)
    {
        var operatorToken = ExpectAndConsume(TokenType.RangeOperator);
        var right = _current switch
        {
            { Value: "{" } => null,
            _ => ParseExpression()
        };

        var startLocation = left?.Location ?? operatorToken.Span;
        var endLocation = right?.Location ?? operatorToken.Span;

        return new RangeExpression
        {
            Left = left,
            Operator = operatorToken,
            Right = right,
            Location = startLocation.Extend(endLocation)
        };
    }

    private TagExpression ParseTagExpression()
    {
        var token = ExpectAndConsume('#');
        ExpectAndConsume(':');

        switch (Take())
        {
            case { Value: "package" }:
                var identifier = ParseExpression().ToString();
                var version = TryConsume('@')
                    ? Take().Value
                    : null;

                while (TryConsume('-'))
                    version = $"{version}-{Take().Value}";

                return new TagExpression
                {
                    TagToken = token,
                    Identifier = identifier,
                    Version = version,
                    Location = token.Span,
                };

            case var other:
                throw new NotImplementedException(other.Type.ToString());
        }
    }
}

public class UnexpectedTokenException(
    Token token
) : JitzuException(token.Span, $"S000: Syntax Error - Unexpected token {token.Type.ToString()} {token.Value}");

public class UnexpectedEndOfFileException()
    : JitzuException(SourceSpan.Empty, "S001: Syntax Error - Unexpected end of file");

public class UnexpectedSyntaxException(
    SourceSpan location,
    ReadOnlySpan<char> expected,
    ReadOnlySpan<char> received
) : JitzuException(location, $"S002: Syntax Error - Expected {expected} but got {received}");