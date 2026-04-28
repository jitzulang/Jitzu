using System.Runtime.CompilerServices;
using Jitzu.Core.Formatting;
using Jitzu.Core.Language;

namespace Jitzu.Core;

public ref struct Lexer(ReadOnlySpan<char> filePath, ReadOnlySpan<char> input, int line = 1, int column = 1)
{
    private readonly ReadOnlySpan<char> _filePath = filePath;
    private readonly ReadOnlySpan<char> _input = input;
    private readonly long _maxIndex = input.Length - 1;
    private int _index;
    private Location _currentLocation = new(column, line);

    public List<Token> Lex()
    {
        if (_index > _maxIndex)
            return [];

        var builder = new List<Token>();
        while (true)
        {
            SkipWhitespace();

            if (_index > _maxIndex)
                return builder;

            var next = Peek(1);
            switch (_input[_index])
            {
                case '\0':
                    break;

                case '1' or '2' or '3' or '4' or '5' or '6' or '7' or '8' or '9' or '0':
                {
                    var token = ParseNumber();
                    builder.Add(token);
                    break;
                }

                case '_' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'):
                case var c when char.IsLetter(c):
                {
                    var token = ParseIdentifier();
                    builder.Add(token);
                    break;
                }

                case '/' when next is '/' or '*':
                {
                    var token = ParseComment();
                    builder.Add(token);
                    break;
                }

                case '(' or ')':
                case '+' or '-' or '/' or '*' or '!' or '=' or '>' or '<' or '&' or '|' or '^' or '%' or '?':
                case '.' when next is '.':
                case ':' when next is ':':
                {
                    var token = ParseOperator();
                    builder.Add(token);
                    break;
                }

                case ',' or '.' or ';' or ':' or '{' or '}' or '[' or ']' or '@' or '#':
                {
                    var start = _currentLocation;
                    _currentLocation.AdvanceBy(1);

                    var token = Token.Create(
                        new SourceSpan(_filePath, 1, start, _currentLocation),
                        _input[_index++.._index],
                        TokenType.Punctuation);
                    builder.Add(token);
                    break;
                }

                case '\'':
                {
                    var token = ParseChar();
                    builder.Add(token);
                    break;
                }

                case '\"':
                {
                    var token = ParseString();
                    builder.Add(token);
                    break;
                }

                case '`':
                {
                    var tokens = ParseinterpolatedString();
                    builder.AddRange(tokens);
                    break;
                }

                case var current:
                    throw new Exception(
                        $"Unexpected character: {current} at {_index} ({_filePath}:{_currentLocation.Line}:{_currentLocation.Column})");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private readonly char Peek(int offset = 0)
    {
        var nextIndex = _index + offset;
        return nextIndex <= _maxIndex ? _input[nextIndex] : '\0';
    }

    private void SkipWhitespace()
    {
        while (_index <= _maxIndex)
        {
            switch (_input[_index])
            {
                case '\n':
                    _currentLocation.NewLine();
                    break;

                case ' ' or '\t' or '\r':
                    _currentLocation.AdvanceBy(1);
                    break;

                default:
                    return;
            }

            _index++;
        }
    }

    private void SkipWhitespaceToNextLine()
    {
        while (_index <= _maxIndex && _input[_index] is var current and (' ' or '\t' or '\n' or '\r'))
        {
            _index++;
            if (current is not '\n') continue;

            _currentLocation = new Location(1, _currentLocation.Line + 1);
            return;
        }
    }

    private Token ParseNumber()
    {
        var start = _currentLocation;
        var index = _index;
        var decimalCount = 0;
        var endOfNumber = false;

        while (!endOfNumber && _index <= _maxIndex)
        {
            switch (_input[_index])
            {
                case '.' when char.IsDigit(Peek(1)):
                    decimalCount++;
                    _currentLocation.AdvanceBy(1);
                    _index++;
                    break;

                case '_' when decimalCount == 0:
                case '0' or '1' or '2' or '3' or '4' or '5' or '6' or '7' or '8' or '9':
                    _currentLocation.AdvanceBy(1);
                    _index++;
                    break;

                default:
                    endOfNumber = true;
                    break;
            }
        }

        var tokenType = decimalCount switch
        {
            0 => TokenType.Int,
            1 => TokenType.Double,
            _ => TokenType.Version
        };

        return Token.Create(
            new SourceSpan(_filePath, _index - index, start, _currentLocation),
            _input[index.._index].RemoveChars('_'),
            tokenType);
    }

    private Token ParseTag()
    {
        var start = _currentLocation;
        var index = _index;
        _index++; // We know it's a #
        _currentLocation.AdvanceBy(1);

        while (_index <= _maxIndex 
               && _input[_index] is var current
               && (current is '.' || char.IsLetterOrDigit(_input[_index])))
        {
            _currentLocation.AdvanceBy(1);
            _index++;
        }

        if (Peek() is '@')
        {
            _currentLocation.AdvanceBy(1);
            _index++;

            // parse version as string
            while (_index <= _maxIndex
                   && _input[_index] is var current
                   && (current is '.' or '-' || char.IsLetterOrDigit(_input[_index])))
            {
                _currentLocation.AdvanceBy(1);
                _index++;
            }
        }

        return Token.Create(
            new SourceSpan(_filePath, _index - index, start, _currentLocation),
            _input[index.._index],
            TokenType.Tag);
    }

    private Token ParseIdentifier()
    {
        var start = _currentLocation;
        var index = _index;
        var endOfIdentifer = false;

        while (!endOfIdentifer && _index <= _maxIndex)
        {
            switch (_input[_index])
            {
                case '_':
                case var c when char.IsLetterOrDigit(c):
                    _currentLocation.AdvanceBy(1);
                    _index++;
                    break;

                default:
                    endOfIdentifer = true;
                    break;
            }
        }

        var identifier = _input[index.._index];
        return Token.Create(
            new SourceSpan(_filePath, _index - index, start, _currentLocation),
            identifier,
            identifier switch
            {
                "true" or "false" => TokenType.Boolean,
                "is" => TokenType.Operator,
                "use" or "mod" or "type" or "trait" or "impl" or "for" or "pub" or "fun" or "mut"
                    or "if" or "else" or "while" or "match" or "union" or "let" or "new" or "open" or "try"
                    or "defer" or "clear" or "return" or "continue" or "break" => TokenType.Keyword,
                _ => TokenType.Identifier
            });
    }

    private Token ParseChar()
    {
        var start = _currentLocation;
        var index = _index;

        _index++; // We know it's an open quote at this point
        _currentLocation.AdvanceBy(1);

        var current = Peek();
        switch (current)
        {
            case '\'':
                _currentLocation.AdvanceBy(1);
                throw new JitzuException(
                    new SourceSpan(_filePath, _index - index, start, _currentLocation), "Empty char literal");

            case '\\':
                switch (Peek(1))
                {
                    case '\'': // single quote, required for character literals.
                    case '"': // double quote, required for string literals.
                    case '\\': // backslash.
                    case '0': // null character (Unicode character 0).
                    case 'a': // alert (bell) character (Unicode character 7).
                    case 'b': // backspace character (Unicode character 8).
                    case 'f': // form feed character (Unicode character 12).
                    case 'n': // new line character (Unicode character 10).
                    case 'r': // carriage return character (Unicode character 13).
                    case 't': // horizontal tab character (Unicode character 9).
                    case 'v': // vertical tab character (Unicode character 11).
                        _index += 2;
                        _currentLocation.AdvanceBy(2);
                        break;

                    default:
                        start.AdvanceBy(1);
                        _currentLocation.AdvanceBy(2);
                        throw new JitzuException(
                            new SourceSpan(_filePath, _index - index, start, _currentLocation),
                            "Invalid escape character sequence");
                }

                break;
            default:
                _currentLocation.AdvanceBy(1);
                _index++;
                break;
        }

        if (Peek() is not '\'')
        {
            start.AdvanceBy(1);
            throw new JitzuException(
                new SourceSpan(_filePath, _index - index, start, _currentLocation),
                "Expected closing single quote for char literal");
        }

        _currentLocation.AdvanceBy(1);
        _index++;
        var token = _input[index.._index];
        return Token.Create(new SourceSpan(_filePath, token.Length, start, _currentLocation), token, TokenType.Char);
    }

    private Token ParseString()
    {
        var start = _currentLocation;
        var index = _index;
        _index++; // We know it's an open quote at this point
        _currentLocation.AdvanceBy(1);

        while (true)
        {
            switch (Peek())
            {
                case '\\':
                    _index += 2;
                    _currentLocation.AdvanceBy(2);
                    continue;

                case '\0':
                    throw new Exception("String terminated abnormally");

                case '"':
                    _index++;
                    _currentLocation.AdvanceBy(1);
                    return Token.Create(
                        new SourceSpan(_filePath, _index - index, start, _currentLocation),
                        _input[index.._index].ReplaceControlCharacters(),
                        TokenType.String);

                case '\n':
                    _currentLocation.NewLine();
                    _index++;
                    continue;

                default:
                    _index++;
                    _currentLocation.AdvanceBy(1);
                    continue;
            }
        }
    }

    private List<Token> ParseinterpolatedString()
    {
        var tokens = new List<Token>();

        var lastLocation = _currentLocation;
        var lastIndex = _index;

        _index++;
        tokens.Add(
            Token.Create(
                new SourceSpan(_filePath, 0, lastLocation, _currentLocation),
                _input[lastIndex.._index], TokenType.InterpolationStringStart));
        _currentLocation.AdvanceBy(1);

        // This allows for multi-line Interpolations,
        // we need the token to start at column 1 if it is on a new line
        SkipWhitespaceToNextLine();
        lastIndex = _index;
        lastLocation = _currentLocation;

        while (true)
        {
            switch (Peek())
            {
                case '\n':
                    _currentLocation.NewLine();
                    _index++;
                    continue;

                case '\\':
                    _index += 2;
                    _currentLocation.AdvanceBy(2);
                    continue;

                case '\0':
                    throw new Exception("String terminated abnormally");

                case '`':
                {
                    // gather the text up to to now
                    var text = _input[lastIndex.._index];
                    if (!text.IsEmpty)
                    {
                        tokens.Add(
                            Token.Create(
                                new SourceSpan(_filePath, text.Length, lastLocation, _currentLocation), text,
                                TokenType.InterpolationTextToken));
                        lastIndex = _index;
                        lastLocation = _currentLocation;
                    }

                    _index++;
                    tokens.Add(
                        Token.Create(
                            new SourceSpan(_filePath, 1, lastLocation, _currentLocation),
                            _input[lastIndex.._index],
                            TokenType.InterpolationStringEnd));

                    return tokens;
                }

                case '$' when Peek(1) is '{':
                {
                    var start = _currentLocation;
                    var dollarIndex = _index;

                    // Scan the inner expression to echo it back in the error (best-effort).
                    var scan = _index + 2;
                    var depth = 0;
                    while (scan <= _maxIndex)
                    {
                        var ch = _input[scan];
                        if (ch == '{') depth++;
                        else if (ch == '}')
                        {
                            if (depth == 0) break;
                            depth--;
                        }
                        scan++;
                    }
                    var inner = scan <= _maxIndex
                        ? _input[(_index + 2)..scan].ToString()
                        : "expr";

                    _currentLocation.AdvanceBy(1);
                    _index++;
                    throw new JitzuException(
                        new SourceSpan(_filePath, _index - dollarIndex, start, _currentLocation),
                        $"Did you mean `{{{inner}}}` instead of `${{{inner}}}`? Jitzu uses `{{expr}}` for string interpolation; drop the `$`.");
                }

                case '{':
                {
                    // gather the text up to to now
                    var text = _input[lastIndex.._index];
                    if (!text.IsEmpty)
                    {
                        tokens.Add(
                            Token.Create(
                                new SourceSpan(_filePath, text.Length, lastLocation, _currentLocation), text,
                                TokenType.InterpolationTextToken));
                        lastIndex = _index;
                        lastLocation = _currentLocation;
                    }

                    _index++;

                    var depth = 0;
                    while (depth is not -1)
                    {
                        switch (Peek())
                        {
                            case '{':
                                depth++;
                                break;

                            case '}':
                                depth--;
                                break;
                        }

                        _index++;
                    }

                    // Consume expression
                    var expressionSpan = _input[lastIndex.._index];
                    tokens.Add(
                        Token.Create(
                            new SourceSpan(_filePath, expressionSpan.Length, lastLocation, _currentLocation),
                            expressionSpan,
                            TokenType.Interpolation));
                    lastIndex = _index;
                    lastLocation = _currentLocation;
                    continue;
                }

                default:
                    _index++;
                    _currentLocation.AdvanceBy(1);
                    continue;
            }
        }
    }

    private Token ParseComment()
    {
        var start = _currentLocation;
        var index = _index;
        _index++; // We know it's either // or /* here
        _currentLocation.AdvanceBy(1);
        var closingChar = Peek() is '/' ? '\n' : '/';
        _index++;
        _currentLocation.AdvanceBy(1);

        while (true)
        {
            switch (Peek())
            {
                case '\0' when closingChar is '\n':
                    return Token.Create(
                        new SourceSpan(_filePath, _index - index, start, _currentLocation),
                        _input[index.._index],
                        TokenType.Comment);

                case '\n' when closingChar is '\n':
                    _currentLocation.NewLine();
                    _index++;
                    return Token.Create(
                        new SourceSpan(_filePath, _index - index, start, _currentLocation),
                        _input[index.._index],
                        TokenType.Comment);

                case '\0' when closingChar is '/':
                    throw new Exception("Non-terminated comment");

                case '*' when closingChar is '/' && Peek(1) is '/':
                    _index += 2;
                    _currentLocation.AdvanceBy(2);
                    return Token.Create(
                        new SourceSpan(_filePath, _index - index, start, _currentLocation),
                        _input[index.._index],
                        TokenType.Comment);

                case '\n':
                    _currentLocation.NewLine();
                    _index++;
                    continue;

                default:
                    _index++;
                    _currentLocation.AdvanceBy(1);
                    continue;
            }
        }
    }

    private Token ParseOperator()
    {
        string op = (_input[_index], Peek(1)) switch
        {
            ('<', '=') => "<=",
            ('<', '>') => "<>",
            ('>', '=') => ">=",
            ('!', '=') => "!=",
            ('+', '+') => "++",
            ('+', '=') => "+=",
            ('-', '-') => "--",
            ('-', '=') => "-=",
            ('=', '>') => "=>",
            ('=', '=') => "==",
            (':', ':') => "::",
            ('.', '.') => Peek(2) == '=' ? "..=" : "..",
            ('(', _) => "(",
            (')', _) => ")",
            var (current, _) => current.ToString()
        };

        var location = _currentLocation;
        _index += op.Length;
        _currentLocation.AdvanceBy(op.Length);
        var tokenType = op is ".." or "..=" ? TokenType.RangeOperator : TokenType.Operator;
        return Token.Create(
            new SourceSpan(_filePath, op.Length, location, _currentLocation), op, tokenType);
    }
}