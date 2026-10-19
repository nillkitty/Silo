using System.Globalization;
using System.Text;

namespace Silo.Expressions;

/// <summary>Reads expression text into an <see cref="Expr" /> tree.</summary>
/// <remarks>
///     <para>
///         The grammar: <c>expression := sum ("??" sum)*</c>; <c>sum := postfix ("+" postfix)*</c>; <c>postfix := primary ("." name | "[" expression "]")*</c>;
///         <c>primary := number | text | true | false | null | name "(" arguments ")" | [prefix ":"] name | "(" expression ")"</c>.
///         Text is in single or double quotes with the escapes <c>\\ \' \" \n \r \t \0 \uXXXX \u{X...}</c>; numbers are decimal or hexadecimal (<c>0x1F7E2</c>).
///     </para>
///     <para>A name is a letter or underscore followed by letters, digits and underscores. <c>your:blue</c> is the variable <c>blue</c> of the namespace <c>your</c>.</para>
/// </remarks>
public static class ExpressionParser
{
    /// <summary>Reads the whole of <paramref name="source" /> as one expression.</summary>
    /// <exception cref="ExpressionException">The text is not an expression; the exception says where.</exception>
    public static Expr Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var p = new Parser(source, 0, inTemplate: false);
        return p.ParseAll(out _);
    }

    /// <summary>Reads the expression of a template that starts at <paramref name="start" /> (just after <c>${</c>) and ends at the next unquoted <c>}</c>.</summary>
    internal static Expr ParseInTemplate(string source, int start, out int end)
    {
        var p = new Parser(source, start, inTemplate: true);
        return p.ParseAll(out end);
    }

    private enum Kind { End, Name, Number, Text, Plus, Coalesce, LParen, RParen, Comma, Dot, LBracket, RBracket, Colon, RBrace }

    private readonly record struct Token(Kind Kind, int Position, int Length, string Text, object? Value);

    private sealed class Parser
    {
        private readonly string      _s;
        private readonly bool        _inTemplate;
        private readonly List<Token> _tokens = [];
        private int                  _next;

        public Parser(string source, int start, bool inTemplate)
        {
            _s          = source;
            _inTemplate = inTemplate;
            Lex(start);
        }

        public Expr ParseAll(out int end)
        {
            if (Peek.Kind == (_inTemplate ? Kind.RBrace : Kind.End)) throw new ExpressionException("Write an expression here.", Peek.Position);

            var expr = ParseCoalesce();
            var last = Peek;
            if (_inTemplate)
            {
                if (last.Kind != Kind.RBrace)
                    throw last.Kind == Kind.End
                              ? new ExpressionException("The ${ is never closed: add a }.", last.Position)
                              : new ExpressionException($"Unexpected {Describe(last)}.", last.Position, last.Length);
                end = last.Position + 1;
            }
            else
            {
                if (last.Kind != Kind.End) throw new ExpressionException($"Unexpected {Describe(last)}.", last.Position, last.Length);
                end = _s.Length;
            }

            return expr;
        }

        // ---- grammar -----------------------------------------------------------------------------

        private Token Peek => _tokens[_next];

        private Token Take() => _tokens[_next < _tokens.Count - 1 ? _next++ : _next];

        private Expr ParseCoalesce()
        {
            var left = ParseSum();
            while (Peek.Kind == Kind.Coalesce)
            {
                Take();
                left = new BinaryExpr(left.Position, "??", left, ParseSum());
            }

            return left;
        }

        private Expr ParseSum()
        {
            var left = ParsePostfix();
            while (Peek.Kind == Kind.Plus)
            {
                Take();
                left = new BinaryExpr(left.Position, "+", left, ParsePostfix());
            }

            return left;
        }

        private Expr ParsePostfix()
        {
            var expr = ParsePrimary();
            while (true)
            {
                if (Peek.Kind == Kind.Dot)
                {
                    Take();
                    var name = Take();
                    if (name.Kind != Kind.Name) throw new ExpressionException("A name is expected after the dot.", name.Position, name.Length);
                    expr = new MemberExpr(expr.Position, expr, name.Text);
                }
                else if (Peek.Kind == Kind.LBracket)
                {
                    Take();
                    var index = ParseCoalesce();
                    Expect(Kind.RBracket, "]");
                    expr = new IndexExpr(expr.Position, expr, index);
                }
                else
                {
                    return expr;
                }
            }
        }

        private Expr ParsePrimary()
        {
            var t = Take();
            switch (t.Kind)
            {
                case Kind.Number:
                case Kind.Text:
                    return new LiteralExpr(t.Position, t.Value);

                case Kind.LParen:
                {
                    var inner = ParseCoalesce();
                    Expect(Kind.RParen, ")");
                    return inner;
                }

                case Kind.Name:
                    switch (t.Text)
                    {
                        case "true":  return new LiteralExpr(t.Position, true);
                        case "false": return new LiteralExpr(t.Position, false);
                        case "null":  return new LiteralExpr(t.Position, null);
                    }

                    if (Peek.Kind == Kind.LParen)
                    {
                        Take();
                        var args = new List<Expr>();
                        if (Peek.Kind != Kind.RParen)
                        {
                            args.Add(ParseCoalesce());
                            while (Peek.Kind == Kind.Comma)
                            {
                                Take();
                                args.Add(ParseCoalesce());
                            }
                        }

                        Expect(Kind.RParen, ")");
                        return new CallExpr(t.Position, t.Text, args);
                    }

                    if (Peek.Kind == Kind.Colon && _tokens[Math.Min(_next + 1, _tokens.Count - 1)].Kind == Kind.Name)
                    {
                        Take();
                        var name = Take();
                        return new VariableExpr(t.Position, t.Text, name.Text);
                    }

                    return new VariableExpr(t.Position, null, t.Text);

                case Kind.End:
                case Kind.RBrace:
                    throw new ExpressionException("The expression ends too soon: a value is expected.", t.Position);

                default:
                    throw new ExpressionException($"Unexpected {Describe(t)}: a value is expected.", t.Position, t.Length);
            }
        }

        private void Expect(Kind kind, string what)
        {
            var t = Take();
            if (t.Kind != kind)
                throw new ExpressionException(t.Kind is Kind.End or Kind.RBrace ? $"A {what} is missing." : $"Expected {what} but found {Describe(t)}.", t.Position, t.Length);
        }

        private static string Describe(Token t) => t.Kind switch
        {
            Kind.End    => "end of text",
            Kind.Name   => $"the name \"{t.Text}\"",
            Kind.Number => $"the number {t.Text}",
            Kind.Text   => "a piece of text",
            _           => $"\"{t.Text}\""
        };

        // ---- lexer ------------------------------------------------------------------------------

        private void Lex(int pos)
        {
            while (true)
            {
                while (pos < _s.Length && char.IsWhiteSpace(_s[pos])) pos++;
                if (pos >= _s.Length)
                {
                    _tokens.Add(new Token(Kind.End, _s.Length, 0, string.Empty, null));
                    return;
                }

                char c = _s[pos];
                if (c == '}' && _inTemplate)
                {
                    _tokens.Add(new Token(Kind.RBrace, pos, 1, "}", null));
                    return;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = pos;
                    while (pos < _s.Length && (char.IsLetterOrDigit(_s[pos]) || _s[pos] == '_')) pos++;
                    _tokens.Add(new Token(Kind.Name, start, pos - start, _s[start..pos], null));
                    continue;
                }

                if (char.IsDigit(c))
                {
                    _tokens.Add(LexNumber(ref pos));
                    continue;
                }

                if (c is '\'' or '"')
                {
                    _tokens.Add(LexText(ref pos));
                    continue;
                }

                if (c == '?' && pos + 1 < _s.Length && _s[pos + 1] == '?')
                {
                    _tokens.Add(new Token(Kind.Coalesce, pos, 2, "??", null));
                    pos += 2;
                    continue;
                }

                var kind = c switch
                {
                    '+' => Kind.Plus,
                    '(' => Kind.LParen,
                    ')' => Kind.RParen,
                    ',' => Kind.Comma,
                    '.' => Kind.Dot,
                    '[' => Kind.LBracket,
                    ']' => Kind.RBracket,
                    ':' => Kind.Colon,
                    _   => Kind.End
                };

                if (kind == Kind.End) throw new ExpressionException($"The character \"{c}\" is not part of the expression syntax.", pos);

                _tokens.Add(new Token(kind, pos, 1, c.ToString(), null));
                pos++;
            }
        }

        private Token LexNumber(ref int pos)
        {
            int start = pos;
            if (_s[pos] == '0' && pos + 1 < _s.Length && (_s[pos + 1] is 'x' or 'X'))
            {
                pos += 2;
                int digits = pos;
                while (pos < _s.Length && Uri.IsHexDigit(_s[pos])) pos++;
                if (pos == digits || !long.TryParse(_s.AsSpan(digits, pos - digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long hex))
                    throw new ExpressionException("A hexadecimal number needs digits after 0x (and has to fit in 63 bits).", start, pos - start);
                return new Token(Kind.Number, start, pos - start, _s[start..pos], hex);
            }

            while (pos < _s.Length && char.IsDigit(_s[pos])) pos++;
            bool isFraction = pos + 1 < _s.Length && _s[pos] == '.' && char.IsDigit(_s[pos + 1]);
            if (isFraction)
            {
                pos++;
                while (pos < _s.Length && char.IsDigit(_s[pos])) pos++;
            }

            string text = _s[start..pos];
            if (isFraction) return new Token(Kind.Number, start, pos - start, text, double.Parse(text, CultureInfo.InvariantCulture));
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long n)) throw new ExpressionException("The number is too large.", start, pos - start);
            return new Token(Kind.Number, start, pos - start, text, n);
        }

        private Token LexText(ref int pos)
        {
            int  start = pos;
            char quote = _s[pos++];
            var  sb    = new StringBuilder();
            while (true)
            {
                if (pos >= _s.Length) throw new ExpressionException("This piece of text is never closed.", start, _s.Length - start);

                char c = _s[pos++];
                if (c == quote) break;
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (pos >= _s.Length) throw new ExpressionException("This piece of text is never closed.", start, _s.Length - start);
                char e = _s[pos++];
                switch (e)
                {
                    case '\\': sb.Append('\\'); break;
                    case '\'': sb.Append('\''); break;
                    case '"':  sb.Append('"'); break;
                    case 'n':  sb.Append('\n'); break;
                    case 'r':  sb.Append('\r'); break;
                    case 't':  sb.Append('\t'); break;
                    case '0':  sb.Append('\0'); break;
                    case 'u':
                        sb.Append(LexUnicodeEscape(ref pos, pos - 2));
                        break;
                    default:
                        throw new ExpressionException($"The escape \\{e} is not known (use \\\\ \\' \\\" \\n \\r \\t \\0 \\uXXXX \\u{{X...}}).", pos - 2, 2);
                }
            }

            return new Token(Kind.Text, start, pos - start, _s[start..pos], sb.ToString());
        }

        private string LexUnicodeEscape(ref int pos, int escapeStart)
        {
            if (pos < _s.Length && _s[pos] == '{')
            {
                int close = _s.IndexOf('}', pos);
                string digits = close < 0 ? string.Empty : _s[(pos + 1)..close];
                if (close < 0 || digits.Length is 0 or > 6 || !int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp) || cp > 0x10FFFF)
                    throw new ExpressionException("\\u{...} needs 1 to 6 hex digits that make a code point.", escapeStart, (close < 0 ? pos : close) - escapeStart + 1);

                pos = close + 1;
                return (cp is >= 0xD800 and <= 0xDFFF) ? ((char)cp).ToString() : char.ConvertFromUtf32(cp);
            }

            if (pos + 4 > _s.Length || !int.TryParse(_s.AsSpan(pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int unit))
                throw new ExpressionException("\\u needs four hex digits (or {X...}).", escapeStart, 2);

            pos += 4;
            return ((char)unit).ToString();
        }
    }
}
